using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class SkyrimSkyCloudBindingTests
{
    private const string DefaultArchivePath =
        @"E:\SteamLibrary\SteamApps\common\Skyrim\Data\Skyrim - Meshes.bsa";

    [Fact]
    public void SparseTextureSlotsResolveToMatchingSourceShapes()
    {
        var layers = Enumerable.Range(0, 21)
            .Select(index => new WeatherCloudLayer
            {
                SourceIndex = index,
                Texture = index switch
                {
                    15 => @"sky\layer15.dds",
                    19 => @"sky\layer19.dds",
                    20 => @"sky\layer20.dds",
                    _ => null,
                },
            })
            .ToArray();
        var weather = new WeatherRecord { CloudLayers = layers };

        Assert.Null(weather.FindCloudLayerBySourceIndex(0)?.Texture);
        Assert.Equal(@"sky\layer15.dds", weather.FindCloudLayerBySourceIndex(15)?.Texture);
        Assert.Equal(@"sky\layer19.dds", weather.FindCloudLayerBySourceIndex(19)?.Texture);
        Assert.Equal(@"sky\layer20.dds", weather.FindCloudLayerBySourceIndex(20)?.Texture);
    }

    [Fact]
    public void InstalledCloudsNifRootChildrenProveSourceShapeOrder()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        var archivePath = Environment.GetEnvironmentVariable("SKYRIM_MESHES_BSA") ?? DefaultArchivePath;
        Assert.SkipWhen(!File.Exists(archivePath),
            "Skyrim LE Meshes BSA not installed (set SKYRIM_MESHES_BSA to run this probe)");

        using var archive = ArchiveReader.Open(archivePath);
        var data = Assert.IsType<byte[]>(archive.ReadFile(@"meshes\sky\clouds.nif"));
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        var root = nif.Blocks[0];
        var children = Assert.IsType<List<int>>(NifBlockParsers.ParseNodeChildren(
            data, root, nif.BsVersion, nif.BinaryVersion, nif.IsBigEndian, nif.HasInlineStrings));

        // All 29 cloud shapes are direct root children. The sparse WTHR source index is therefore the
        // child/shape slot, not a dense ordinal among only the texture subrecords that happened to exist.
        Assert.Equal(29, children.Count);
        Assert.Equal("12_CDHorizon_1", Name(children[15]));
        Assert.Equal("13_CDHorizon_1_E", Name(children[19]));
        Assert.Equal("13_CDHorizon_1_W", Name(children[20]));

        string? Name(int blockIndex) => NifBlockParsers.ReadBlockName(data, nif.Blocks[blockIndex], nif);
    }
}
