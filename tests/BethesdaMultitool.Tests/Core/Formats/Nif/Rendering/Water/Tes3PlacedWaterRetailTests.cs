using System.Text;
using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Bsa.Parsing;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     Optional retail boundary for the synthetic classifier tests. The nearby animated Vivec
///     waterfall is a deliberate negative: UV motion and water-themed names alone are insufficient.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public sealed class Tes3PlacedWaterRetailTests
{
    private const string Bsa = @"E:\SteamLibrary\SteamApps\common\Morrowind\Data Files\Morrowind.bsa";
    private static readonly string[] RetailBsas =
    [
        Bsa,
        @"E:\SteamLibrary\SteamApps\common\Morrowind\Data Files\Tribunal.bsa",
        @"E:\SteamLibrary\SteamApps\common\Morrowind\Data Files\Bloodmoon.bsa"
    ];

    [Fact]
    public void VivecPlacedWater_IsDivertedButAnimatedWaterfallIsNot()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(File.Exists(Bsa), "Morrowind.bsa not present (dev-machine-only asset).");

        var water = ExtractModel(@"meshes\x\ex_vivec_p_water_01.nif");
        Assert.Equal(2, water.Submeshes.Count);
        Assert.All(water.Submeshes, static submesh =>
            Assert.Equal(RenderableSubmesh.WaterSurfaceTexturePath, submesh.DiffuseTexturePath));

        var waterfall = ExtractModel(@"meshes\x\ex_vivec_waterfall_05.nif");
        Assert.NotEmpty(waterfall.Submeshes);
        Assert.DoesNotContain(waterfall.Submeshes, static submesh =>
            string.Equals(
                submesh.DiffuseTexturePath,
                RenderableSubmesh.WaterSurfaceTexturePath,
                StringComparison.Ordinal));
    }

    [Fact]
    public void CanonicalWaterTextureIdentity_IsUniqueAcrossInstalledRetailNifs()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(File.Exists(Bsa), "Morrowind.bsa not present (dev-machine-only asset).");

        var matches = new List<(string Archive, string Mesh)>();
        foreach (var archivePath in RetailBsas.Where(File.Exists))
        {
            using var extractor = new BsaExtractor(archivePath);
            var archive = BsaParser.Parse(archivePath);
            foreach (var file in archive.AllFiles.Where(static file =>
                         file.FullPath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)))
            {
                var text = Encoding.ASCII.GetString(extractor.ExtractFile(file));
                if (text.Contains("Tx_V_water_01.tga", StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add((Path.GetFileName(archivePath), file.FullPath));
                }
            }
        }

        var match = Assert.Single(matches);
        Assert.Equal("Morrowind.bsa", match.Archive, ignoreCase: true);
        Assert.Equal(@"meshes\x\ex_vivec_p_water_01.nif", match.Mesh, ignoreCase: true);
    }

    private static NifRenderableModel ExtractModel(string meshPath)
    {
        using var extractor = new BsaExtractor(Bsa);
        var archive = BsaParser.Parse(Bsa);
        var file = archive.AllFiles.First(record =>
            string.Equals(record.FullPath, meshPath, StringComparison.OrdinalIgnoreCase));
        var data = extractor.ExtractFile(file);
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        return Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(
            data,
            nif,
            treatRootsAsIdentity: true));
    }
}
