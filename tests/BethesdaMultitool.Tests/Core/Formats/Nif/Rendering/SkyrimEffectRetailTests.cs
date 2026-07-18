using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>Installed Skyrim LE effect regressions for the named snow and fire assets.</summary>
public sealed class SkyrimEffectRetailTests
{
    private const string ArchivePath = @"E:\SteamLibrary\SteamApps\common\Skyrim\Data\Skyrim - Meshes.bsa";

    [Fact]
    public void BlowingSnow_ComposesInlineEffectOpacityWithMaterialAlpha()
    {
        var model = Extract(@"meshes\effects\fxambsnowblowingplane.nif");

        Assert.Equal(4, model.Submeshes.Count);
        Assert.Equal(new[] { 0.6f, 0.4f, 0.4f, 0.6f },
            model.Submeshes.Select(static sub => sub.MaterialAlpha).ToArray());
        Assert.All(model.Submeshes, static sub =>
        {
            Assert.Equal("BSEffectShaderProperty", sub.ShaderMetadata?.PropertyType);
            Assert.Equal(sub.ShaderMetadata?.EffectBaseColor?.A, sub.MaterialAlpha);
            Assert.True(sub.IsEmissive); // TESV's effect-lighting static remains disabled.
        });
    }

    [Fact]
    public void FireFlameCards_RetainAlwaysFaceCenterMode()
    {
        var model = Extract(@"meshes\effects\fxfirewithembers01.nif");
        var flameCards = model.Submeshes
            .Where(static sub => sub.IsBillboard)
            .ToArray();

        Assert.Equal(4, flameCards.Length);
        Assert.All(flameCards, static sub =>
            Assert.Equal(NifBillboardMode.AlwaysFaceCenter, sub.BillboardMode));
        Assert.Contains(flameCards, static sub => sub.ShapeName?.StartsWith("L2_Flames", StringComparison.Ordinal) == true);
        Assert.Contains(flameCards, static sub => sub.ShapeName?.StartsWith("Flames", StringComparison.Ordinal) == true);
        Assert.Contains(flameCards, static sub => sub.ShapeName?.StartsWith("glow", StringComparison.Ordinal) == true);
    }

    private static NifRenderableModel Extract(string assetPath)
    {
        var archivePath = Environment.GetEnvironmentVariable("SKYRIM_MESHES_BSA") ?? ArchivePath;
        Assert.SkipWhen(!File.Exists(archivePath),
            "Skyrim LE Meshes BSA not installed (set SKYRIM_MESHES_BSA to run this probe)");

        using var archive = ArchiveReader.Open(archivePath);
        var data = Assert.IsType<byte[]>(archive.ReadFile(assetPath));
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        return Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(
            data, nif, bindPoseOnly: false, skipSkinning: true,
            treatRootsAsIdentity: true, collectBillboards: true));
    }
}
