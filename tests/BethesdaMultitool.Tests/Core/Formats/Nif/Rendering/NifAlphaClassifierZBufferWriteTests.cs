using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Covers the engine-accurate alpha classification in <see cref="NifAlphaClassifier" />, decompiled from
///     BSShader::SetupGeometryAlphaBlending + BSShader::SetupGeometryRenderStates (MemDebug XEX —
///     tools/GhidraProject/shader_zwrite_decompiled.txt):
///     <list type="bullet">
///         <item>Alpha BLEND is enabled by NiAlphaProperty bit 0.</item>
///         <item>
///             Depth WRITE in the alpha pass follows the alpha-TEST bit: alpha-tested geometry writes depth;
///             plain alpha-blend does not.
///         </item>
///         <item>
///             BSShaderFlags2 ZBuffer_Write does NOT drive the per-draw Z-write and does NOT demote
///             alpha-blend to opaque — that earlier rule was a workaround for a hull leak the engine actually
///             avoids by back-to-front sorting + back-face culling (both of which the renderer already does).
///         </item>
///     </list>
/// </summary>
public sealed class NifAlphaClassifierZBufferWriteTests
{
    [Fact]
    public void Classify_PlainBlend_NoTest_StaysBlendAndDoesNotWriteDepth()
    {
        // Alpha-blend, no alpha-test → plain Blend, Z-write OFF (the engine sorts it back-to-front).
        var submesh = CreateSubmesh(true, false, 0x0u);

        var state = NifAlphaClassifier.Classify(submesh, null);

        Assert.Equal(NifAlphaRenderMode.Blend, state.RenderMode);
        Assert.True(state.HasAlphaBlend);
        Assert.False(state.DepthWritingBlend);
        Assert.False(state.WritesDepth);
    }

    [Fact]
    public void Classify_BlendPlusAlphaTest_IsDepthWritingBlend()
    {
        // Engine: Z-write = alpha-TEST bit. A shape that BOTH blends and alpha-tests writes depth, while
        // staying a blend (its kept cutout texels are opaque). NVSeaPlant02 foliage / window-cutout hulls.
        var submesh = CreateSubmesh(true, true, 0x1u,
            "NVSeaPlant02:0", @"textures\effects\nv\NVSeaPlant02.dds");

        var state = NifAlphaClassifier.Classify(submesh, null);

        Assert.Equal(NifAlphaRenderMode.Blend, state.RenderMode);
        Assert.True(state.HasAlphaBlend);
        Assert.True(state.HasAlphaTest);
        Assert.True(state.DepthWritingBlend);
        Assert.True(state.WritesDepth);
    }

    [Fact]
    public void Classify_BlendPlusTrivialAlphaTest_StaysNonDepthWritingBlend()
    {
        // FXMistLow01Long: blend + alpha-test at threshold 1 — the test keeps near-invisible mist
        // texels, so a depth-writing hoist would lay a full-quad depth footprint that punches
        // transparent holes in the water pass drawn after it. Trivial thresholds stay a plain
        // sorted blend (Z-write off); only real cutout thresholds (NVSeaPlant02: 124) earn the
        // depth-writing pre-water hoist.
        var submesh = CreateSubmesh(true, true, 0x1u,
            "FXMistLow01Long:1", @"textures\effects\fxwastelandmist01.dds");
        submesh.AlphaTestThreshold = 1;

        var state = NifAlphaClassifier.Classify(submesh, null);

        Assert.Equal(NifAlphaRenderMode.Blend, state.RenderMode);
        Assert.True(state.HasAlphaBlend);
        Assert.True(state.HasAlphaTest);
        Assert.False(state.DepthWritingBlend);
        Assert.False(state.WritesDepth);
    }

    [Fact]
    public void Classify_AlphaTestOnly_IsCutoutAndWritesDepth()
    {
        var submesh = CreateSubmesh(false, true, 0x1u);

        var state = NifAlphaClassifier.Classify(submesh, null);

        Assert.Equal(NifAlphaRenderMode.Cutout, state.RenderMode);
        Assert.False(state.HasAlphaBlend);
        Assert.True(state.WritesDepth);
    }

    [Fact]
    public void Classify_ZBufferWriteFlag_DoesNotDemoteBlendToOpaque()
    {
        // The decisive behavioral change: a plain alpha-blend shape whose shader sets BSShaderFlags2
        // ZBuffer_Write (the McCarran-tower / blood-decal case) is NO LONGER demoted to opaque/cutout — the
        // engine renders it as a sorted blend. It stays Blend with Z-write off; the renderer's back-to-front
        // sort + single-sided back-face culling handle any closed-hull see-through, as the engine does.
        var solidHullStyle = CreateSubmesh(true, false, 0x1u,
            "tower03:0", @"textures\architecture\mccarran\tower.dds");

        var state = NifAlphaClassifier.Classify(solidHullStyle, null);

        Assert.Equal(NifAlphaRenderMode.Blend, state.RenderMode);
        Assert.True(state.HasAlphaBlend);
        Assert.False(state.DepthWritingBlend);
        Assert.False(state.WritesDepth);
    }

    [Fact]
    public void Classify_UnlitDecalWithZBufferWrite_StaysBlend()
    {
        // Unlit decals/glows/halos (BSShaderNoLightingProperty: ground-blend skirts, neon, radioactive glow)
        // keep their authored blend — they were the meshes the old ZBuffer_Write demotion painted opaque.
        var submesh = CreateSubmesh(true, false, 0x1u,
            "RadioactiveGlow", @"textures\clutter\radioactive.dds", "BSShaderNoLightingProperty");

        var state = NifAlphaClassifier.Classify(submesh, null);

        Assert.Equal(NifAlphaRenderMode.Blend, state.RenderMode);
        Assert.True(state.HasAlphaBlend);
        Assert.False(state.WritesDepth);
    }

    [Fact]
    public void Classify_Opaque_WhenNoBlendNoTest()
    {
        var submesh = CreateSubmesh(false, false, 0x1u);

        var state = NifAlphaClassifier.Classify(submesh, null);

        Assert.Equal(NifAlphaRenderMode.Opaque, state.RenderMode);
        Assert.True(state.WritesDepth);
    }

    [Fact]
    public void Classify_BlendWithoutShaderMetadata_StaysBlend()
    {
        var submesh = CreateSubmesh(true, false, null);

        var state = NifAlphaClassifier.Classify(submesh, null);

        Assert.Equal(NifAlphaRenderMode.Blend, state.RenderMode);
        Assert.False(state.WritesDepth);
    }

    private static RenderableSubmesh CreateSubmesh(
        bool hasAlphaBlend, bool hasAlphaTest, uint? shaderFlags2,
        string? shapeName = null, string? diffusePath = null, string propertyType = "BSShaderPPLightingProperty")
    {
        return new RenderableSubmesh
        {
            Positions = [],
            Triangles = [],
            ShapeName = shapeName,
            DiffuseTexturePath = diffusePath,
            HasAlphaBlend = hasAlphaBlend,
            HasAlphaTest = hasAlphaTest,
            AlphaTestThreshold = 50,
            AlphaTestFunction = 4,
            MaterialAlpha = 1f,
            ShaderMetadata = shaderFlags2 is null
                ? null
                : new NifShaderTextureMetadata
                {
                    PropertyType = propertyType,
                    ShaderFlags2 = shaderFlags2
                }
        };
    }
}