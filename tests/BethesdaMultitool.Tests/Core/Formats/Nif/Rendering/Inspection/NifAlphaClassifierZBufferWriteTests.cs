using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Inspection;

/// <summary>
///     Covers the alpha classification in <see cref="NifAlphaClassifier" /> — BOTH bits:
///     <list type="bullet">
///         <item>Alpha BLEND is enabled by NiAlphaProperty bit 0 (decompiled from
///             BSShader::SetupGeometryAlphaBlending).</item>
///         <item>
///             LEGACY <c>DepthWritingBlend</c> (blend + test + cutting threshold) — the viewer
///             heuristic serving every stream-off route. NOTE the 2026-08-04 RE round proved the
///             old "z-write follows the alpha-test bit" reading FALSE (that call site sets cull +
///             alpha test); the heuristic is retained as the legacy fallback, not as engine truth.
///         </item>
///         <item>
///             ENGINE <c>EngineZWriteOff</c> / <c>DepthTestOff</c> (memory note
///             fnv_alpha_zwrite_order_re_2026_08_04): z-write is ambient-ON minus decals, the hair
///             flag (bit 18 + blend), and NoLighting honoring ShaderFlags2 bit 0 "zbuffer write"
///             (CLEAR ⇒ OFF); NoLighting also honors ShaderFlags bit 31 "zbuffer test" (CLEAR ⇒
///             test OFF). Lit geometry IGNORES the flag pair — blended LIT shapes keep z-write.
///             Shapes with no flag evidence MIRROR the legacy bit.
///         </item>
///         <item>
///             ZBuffer_Write never demotes alpha-blend to opaque — the engine renders sorted
///             blends; back-to-front sorting + back-face culling handle closed-hull see-through.
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

    [Fact]
    public void Classify_EngineRule_LitBlendWithTest_KeepsZWrite()
    {
        // The WhiteHorseNettle pin: a blended LIT shape that ALSO alpha-tests keeps engine
        // z-write — lit geometry ignores the zbuffer-write/test flag pair entirely, even with
        // flags2 bit 0 authored either way — and it does so at ANY threshold, which is the whole
        // point of replacing the old texel-histogram heuristic (the nettle authors threshold 0).
        var submesh = CreateSubmesh(true, true, 0x1u, shaderFlags: 0x82000000u);
        submesh.AlphaTestThreshold = 0;

        var state = NifAlphaClassifier.Classify(submesh, null);

        Assert.False(state.EngineZWriteOff);
        Assert.False(state.DepthTestOff);
    }

    [Fact]
    public void Classify_EngineRule_PureBlendCannotWriteDepth()
    {
        // VIEWER SAFETY RESTRICTION, deliberately NOT the engine's rule: the depth-writing blend
        // PSO's only discard is the alpha test, so a pure blend that wrote depth would deposit it
        // across fully transparent texels. Measured at render distance 32, letting these write
        // depth differed from the legacy pass order by 328,701 of 328,704 px; restricting them
        // brings the same frame back to 1,093 px. Lift only with a shader-side alpha discard.
        var submesh = CreateSubmesh(true, false, 0x1u, shaderFlags: 0x82000000u);

        var state = NifAlphaClassifier.Classify(submesh, null);

        Assert.True(state.EngineZWriteOff);
        // The legacy bit is UNCHANGED (plain blend stays non-depth-writing on stream-off routes).
        Assert.False(state.DepthWritingBlend);
    }

    [Fact]
    public void Classify_EngineRule_NoLightingBit0Clear_TurnsZWriteOff()
    {
        // FXMistLow01Long's SmokeWispsTile sheet, byte-verified from the shipped NIF: NoLighting
        // with ShaderFlags 0xA2000148 (bit 31 SET ⇒ z-test stays ON) and ShaderFlags2 0x0 (bit 0
        // CLEAR ⇒ z-write OFF). This is why retail mist never punches water.
        var submesh = CreateSubmesh(true, false, 0x0u,
            propertyType: "BSShaderNoLightingProperty", shaderFlags: 0xA2000148u);

        var state = NifAlphaClassifier.Classify(submesh, null);

        Assert.True(state.EngineZWriteOff);
        Assert.False(state.DepthTestOff);
    }

    [Fact]
    public void Classify_EngineRule_NoLightingBit0Set_KeepsZWrite()
    {
        // The authored NoLighting DEFAULT (0x82000000 / flags2 bit 0 SET) writes depth — the
        // engine honors the flag in BOTH directions, so bit0-SET sheets are depth-writing blends.
        // Carries an alpha test so the viewer's pure-blend safety restriction does not apply.
        var submesh = CreateSubmesh(true, true, 0x1u,
            propertyType: "BSShaderNoLightingProperty", shaderFlags: 0x82000000u);

        var state = NifAlphaClassifier.Classify(submesh, null);

        Assert.False(state.EngineZWriteOff);
        Assert.False(state.DepthTestOff);
    }

    [Fact]
    public void Classify_EngineRule_ZBufferTestBit31_IsNoLightingOnlyAndClearMeansOff()
    {
        // Polarity is load-bearing: bit 31 CLEAR disables the depth test (the authored default
        // 0x82000000 has it SET — an inverted read would kill the test for nearly every
        // NoLighting sheet in the game). And it is honored for the NoLighting family ONLY.
        var noLighting = CreateSubmesh(true, false, 0x1u,
            propertyType: "BSShaderNoLightingProperty", shaderFlags: 0x00000148u);
        Assert.True(NifAlphaClassifier.Classify(noLighting, null).DepthTestOff);

        var lit = CreateSubmesh(true, false, 0x1u, shaderFlags: 0x00000148u);
        Assert.False(NifAlphaClassifier.Classify(lit, null).DepthTestOff);
    }

    [Fact]
    public void Classify_EngineRule_DecalAndHairFlag_TurnZWriteOff()
    {
        // Decals (ShaderFlags bits 26/27, pre-folded into IsDecal) and the hair flag (bit 18,
        // 0x40000, with blend) are the remaining engine off-switches.
        var decal = CreateSubmesh(true, false, 0x1u, shaderFlags: 0x82000000u);
        decal.IsDecal = true;
        Assert.True(NifAlphaClassifier.Classify(decal, null).EngineZWriteOff);

        var hair = CreateSubmesh(true, false, 0x1u, shaderFlags: 0x82040000u);
        Assert.True(NifAlphaClassifier.Classify(hair, null).EngineZWriteOff);

        // The hair leg is blend-gated (the engine's tech-6/8 hair pass only exists for blends).
        var hairNoBlend = CreateSubmesh(false, true, 0x1u, shaderFlags: 0x82040000u);
        Assert.False(NifAlphaClassifier.Classify(hairNoBlend, null).EngineZWriteOff);
    }

    [Fact]
    public void Classify_EngineRule_MissingEvidence_MirrorsLegacyBit()
    {
        // No authored flags = no engine evidence: particle-system submeshes never populate
        // ShaderMetadata, and NiTexturing-era games yield null. The engine bit MIRRORS the legacy
        // classification instead of guessing a lit default — a feathered pure-blend particle
        // cloud must not gain a full-quad depth footprint, and Morrowind's authored write-OFF
        // channel (NiZBufferProperty) is not parsed yet.
        var mirrorOff = CreateSubmesh(true, false, null);
        var mirrorOffState = NifAlphaClassifier.Classify(mirrorOff, null);
        Assert.False(mirrorOffState.DepthWritingBlend);
        Assert.True(mirrorOffState.EngineZWriteOff);

        var mirrorOn = CreateSubmesh(true, true, null,
            "NVSeaPlant02:0", @"textures\effects\nv\NVSeaPlant02.dds");
        var mirrorOnState = NifAlphaClassifier.Classify(mirrorOn, null);
        Assert.True(mirrorOnState.DepthWritingBlend);
        Assert.False(mirrorOnState.EngineZWriteOff);

        // Metadata WITHOUT the flag words (the ShaderFlags-null case) is equally evidence-less.
        var flagsMissing = CreateSubmesh(true, false, 0x1u);
        Assert.True(NifAlphaClassifier.Classify(flagsMissing, null).EngineZWriteOff);
    }

    private static RenderableSubmesh CreateSubmesh(
        bool hasAlphaBlend, bool hasAlphaTest, uint? shaderFlags2,
        string? shapeName = null, string? diffusePath = null,
        string propertyType = "BSShaderPPLightingProperty", uint? shaderFlags = null)
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
                    ShaderFlags = shaderFlags,
                    ShaderFlags2 = shaderFlags2
                }
        };
    }
}