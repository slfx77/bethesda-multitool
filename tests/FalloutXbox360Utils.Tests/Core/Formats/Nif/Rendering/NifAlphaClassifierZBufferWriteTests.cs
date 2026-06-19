using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Covers the BSShaderFlags2 ZBuffer_Write handling in <see cref="NifAlphaClassifier" />: a shape
///     authored to occlude (the engine writes depth for it) must be routed to the depth-writing
///     opaque/cutout pass, not the depth-write-off blend pass where its interior / anything behind it
///     shows through. Two real cases drive this — MobileHomeASNV's cabin shell (alpha-blend + alpha-test
///     opaque hull with window cutouts) and NV_McCarran-Wall03's tower body (alpha-blend + ZBuffer_Write,
///     NO alpha-test) — while genuine FX (smoke / glass / decals) stay in the blend pass for soft edges.
/// </summary>
public sealed class NifAlphaClassifierZBufferWriteTests
{
    [Fact]
    public void Classify_BlendWithZBufferWriteAndAlphaTest_RoutesToCutoutAndWritesDepth()
    {
        var submesh = CreateSubmesh(hasAlphaBlend: true, hasAlphaTest: true, shaderFlags2: 0x1u);

        var state = NifAlphaClassifier.Classify(submesh, diffuseTexture: null);

        Assert.Equal(NifAlphaRenderMode.Cutout, state.RenderMode);
        Assert.True(state.WritesDepth);
        Assert.False(state.HasAlphaBlend);
        Assert.True(state.HasAlphaTest);
    }

    [Fact]
    public void Classify_NonFxBlendWithZBufferWriteNoAlphaTest_WritesDepth()
    {
        // The McCarran tower body: alpha-blend + ZBuffer_Write, NO alpha-test, on a solid architectural
        // mesh (not FX). The engine writes depth for it; leaving it in the blend pass let the railing
        // render through the metal ring. With no see-through diffuse it becomes a depth-writing Opaque.
        var submesh = CreateSubmesh(hasAlphaBlend: true, hasAlphaTest: false, shaderFlags2: 0x1u,
            shapeName: "tower03:0", diffusePath: @"textures\architecture\mccarran\mccarranTower.dds");

        var state = NifAlphaClassifier.Classify(submesh, diffuseTexture: null);

        Assert.Equal(NifAlphaRenderMode.Opaque, state.RenderMode);
        Assert.True(state.WritesDepth);
        Assert.False(state.HasAlphaBlend);
    }

    [Fact]
    public void Classify_NonFxOccluderWithSeeThroughDiffuse_RoutesToCutoutKeepingHoles()
    {
        // Same occluder but the diffuse has see-through texels (window glass): keep them as an alpha-test
        // cutout (so the holes survive) while still writing depth — rather than filling them in solid.
        var submesh = CreateSubmesh(hasAlphaBlend: true, hasAlphaTest: false, shaderFlags2: 0x1u,
            shapeName: "tower03:0", diffusePath: @"textures\architecture\mccarran\mccarranTower.dds");

        var state = NifAlphaClassifier.Classify(submesh, diffuseTexture: HalfTransparentTexture());

        Assert.Equal(NifAlphaRenderMode.Cutout, state.RenderMode);
        Assert.True(state.WritesDepth);
        Assert.False(state.HasAlphaBlend);
        Assert.True(state.HasAlphaTest);
    }

    [Fact]
    public void Classify_FxBlendWithZBufferWrite_StaysBlend()
    {
        // Genuine FX (smoke / sandstorm / glass / decals) sometimes quirkily set ZBuffer_Write but are
        // real transparency — they must stay in the blend pass for soft edges, NOT be forced to a
        // depth-writing opaque/cutout (which gives the reported "sharp edges" on smoke/sandstorms).
        var smoke = CreateSubmesh(hasAlphaBlend: true, hasAlphaTest: false, shaderFlags2: 0x1u,
            shapeName: "SmokeQuad", diffusePath: @"textures\effects\smoke01.dds");
        var glassWithTest = CreateSubmesh(hasAlphaBlend: true, hasAlphaTest: true, shaderFlags2: 0x1u,
            shapeName: "iceShard", diffusePath: @"textures\effects\glassbreak.dds");

        var smokeState = NifAlphaClassifier.Classify(smoke, diffuseTexture: null);
        var glassState = NifAlphaClassifier.Classify(glassWithTest, diffuseTexture: null);

        Assert.Equal(NifAlphaRenderMode.Blend, smokeState.RenderMode);
        Assert.False(smokeState.WritesDepth);
        Assert.Equal(NifAlphaRenderMode.Blend, glassState.RenderMode);
        Assert.False(glassState.WritesDepth);
    }

    [Fact]
    public void Classify_BlendWithoutZBufferWrite_StaysBlendAndSkipsDepth()
    {
        // Genuine transparency (glass, decals, smoke) leaves the ZBuffer_Write bit clear.
        var submesh = CreateSubmesh(hasAlphaBlend: true, hasAlphaTest: false, shaderFlags2: 0x0u);

        var state = NifAlphaClassifier.Classify(submesh, diffuseTexture: null);

        Assert.Equal(NifAlphaRenderMode.Blend, state.RenderMode);
        Assert.False(state.WritesDepth);
        Assert.True(state.HasAlphaBlend);
    }

    [Fact]
    public void Classify_BlendWithZBufferWriteButNoShaderMetadata_StaysBlend()
    {
        var submesh = CreateSubmesh(hasAlphaBlend: true, hasAlphaTest: false, shaderFlags2: null);

        var state = NifAlphaClassifier.Classify(submesh, diffuseTexture: null);

        Assert.Equal(NifAlphaRenderMode.Blend, state.RenderMode);
        Assert.False(state.WritesDepth);
    }

    private static DecodedTexture HalfTransparentTexture()
    {
        // 4×4 RGBA where half the texels are fully transparent → HasSignificantAlpha() is true.
        var pixels = new byte[4 * 4 * 4];
        for (var i = 0; i < 16; i++)
        {
            var o = i * 4;
            pixels[o] = 200;
            pixels[o + 1] = 200;
            pixels[o + 2] = 200;
            pixels[o + 3] = (byte)(i % 2 == 0 ? 255 : 0);
        }

        return DecodedTexture.FromBaseLevel(pixels, 4, 4, generateMipChain: false);
    }

    private static RenderableSubmesh CreateSubmesh(
        bool hasAlphaBlend, bool hasAlphaTest, uint? shaderFlags2,
        string? shapeName = null, string? diffusePath = null)
        => new()
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
                    PropertyType = "BSShaderPPLightingProperty",
                    ShaderFlags2 = shaderFlags2
                }
        };
}
