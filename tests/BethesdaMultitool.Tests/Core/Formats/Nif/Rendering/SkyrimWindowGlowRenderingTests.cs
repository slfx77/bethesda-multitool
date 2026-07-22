using System.Numerics;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Installed-asset probe for Skyrim's Whiterun LOD window-glow mesh. The property is the exact
///     retail BSEffect Vc/Tex/Additive (0x883) permutation decompiled from shaders001.fxp: RGB masking
///     lives in the texture, BaseColorScale is 2, and LightingInfluence blends toward the external
///     XEMI emittance color. The available Lit permutation is gated off by retail's unwritten static.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public sealed class SkyrimWindowGlowRenderingTests
{
    public SkyrimWindowGlowRenderingTests()
    {
        BucketBTestGuard.SkipUnlessEnabled();
    }

    private const string ArchivePath = @"E:\SteamLibrary\SteamApps\common\Skyrim\Data\Skyrim - Meshes.bsa";
    private const string AssetPath =
        @"meshes\architecture\whiterun\wrbuildings\wrlodwindowglow01.nif";

    [Fact]
    public void WrLodWindowGlow_InlineEffect_IsAdditiveUnlitAndScaled()
    {
        var archivePath = Environment.GetEnvironmentVariable("SKYRIM_MESHES_BSA") ?? ArchivePath;
        Assert.SkipWhen(!File.Exists(archivePath),
            "Skyrim LE Meshes BSA not installed (set SKYRIM_MESHES_BSA to run this probe)");

        using var archive = ArchiveReader.Open(archivePath);
        var data = archive.ReadFile(AssetPath);
        Assert.NotNull(data);

        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data!));
        var model = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(data!, nif));
        var glow = Assert.Single(model.Submeshes);

        Assert.Equal("BSEffectShaderProperty", glow.ShaderMetadata?.PropertyType);
        Assert.Equal(0xAC000000u, glow.ShaderMetadata?.ShaderFlags);
        Assert.Equal(0x40000029u, glow.ShaderMetadata?.ShaderFlags2);
        Assert.Equal(1f, glow.ShaderMetadata?.EffectLightingInfluence);
        Assert.Equal(2f, glow.EffectTint.R, 3);
        Assert.Equal(2f, glow.EffectTint.G, 3);
        Assert.Equal(2f, glow.EffectTint.B, 3);

        // Structural hour-invariance guard: reference.frag bypasses AtmosphereLight whenever this
        // bit is true. Retail TESV likewise stays on unlit 0x883 because bLightingEnabled is zero.
        Assert.True(glow.IsEmissive);
        Assert.True(glow.IsDecal);
        Assert.True(glow.HasAlphaBlend);
        Assert.False(glow.HasAlphaTest);
        Assert.Equal((byte)6, glow.SrcBlendMode); // SRC_ALPHA
        Assert.Equal((byte)0, glow.DstBlendMode); // ONE (additive)

        var externalColor = new Vector3(0.75f, 0.5f, 0.25f);
        var externallyTinted = Assert.IsType<NifRenderableModel>(
            NifGeometryExtractor.Extract(data!, nif, externalEmittanceColor: externalColor));
        var tintedGlow = Assert.Single(externallyTinted.Submeshes);
        Assert.Equal(1.5f, tintedGlow.EffectTint.R, 3);
        Assert.Equal(1f, tintedGlow.EffectTint.G, 3);
        Assert.Equal(0.5f, tintedGlow.EffectTint.B, 3);
    }
}
