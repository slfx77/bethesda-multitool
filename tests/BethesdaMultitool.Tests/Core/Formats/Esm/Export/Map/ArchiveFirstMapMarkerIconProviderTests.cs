using BethesdaMultitool.Core.Formats.Esm.Export.Map;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Export.Map;

public sealed class ArchiveFirstMapMarkerIconProviderTests
{
    [Theory]
    [InlineData(BethesdaGame.Fallout3, "Fallout - Textures.bsa", true,
        @"textures\interface\icons\world map\{iconKey}.dds")]
    [InlineData(BethesdaGame.FalloutNewVegas, "Fallout - Textures2.bsa", true,
        @"textures\interface\icons\world map\{iconKey}.dds")]
    [InlineData(BethesdaGame.Oblivion, "Oblivion - Textures - Compressed.bsa", true,
        @"textures\menus\map\world\world_map_*.dds")]
    [InlineData(BethesdaGame.Skyrim, "Skyrim - Interface.bsa", false, @"interface\map.swf")]
    [InlineData(BethesdaGame.Fallout4, "Fallout4 - Interface.ba2", false,
        @"Interface\Pipboy_MapPage.swf")]
    [InlineData(BethesdaGame.Fallout76, "SeventySix - Interface.ba2", false,
        @"interface\mapmarkerslibrary.swf")]
    public void ArchivePlans_LockInstalledContainerAndSourceEvidence(
        BethesdaGame game,
        string archive,
        bool direct,
        string sourceAsset)
    {
        var plan = Assert.IsType<MapMarkerArchiveAssetPlan>(MapMarkerArchiveAssetCatalog.For(game));

        Assert.Equal(archive, plan.ArchiveFileName);
        Assert.Equal(direct, plan.HasDirectPerIconAssets);
        Assert.Contains(sourceAsset, plan.SourceAssetPaths);
    }

    [Fact]
    public void FindExistingArchive_UsesLoadOrderDataPath_WhenPrimaryIsADump()
    {
        var dump = Path.GetFullPath(Path.Combine("captures", "scene.dmp"));
        var master = Path.GetFullPath(Path.Combine("games", "Fallout New Vegas", "Data", "FalloutNV.esm"));
        var plugin = Path.Combine(Path.GetDirectoryName(master)!, "SomeMod.esp");
        var expected = Path.Combine(Path.GetDirectoryName(master)!, "Fallout - Textures2.bsa");

        var found = MapMarkerArchiveAssetCatalog.FindExistingArchive(
            BethesdaGame.FalloutNewVegas,
            [dump, master, plugin],
            path => path.Equals(expected, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(expected, found);
        Assert.Equal(2, MapMarkerArchiveAssetCatalog.BuildArchiveCandidates(
            BethesdaGame.FalloutNewVegas, [dump, master, plugin]).Count);
    }

    [Theory]
    [InlineData(BethesdaGame.Fallout3, 1, @"textures\interface\icons\world map\icon_map_city.dds")]
    [InlineData(BethesdaGame.FalloutNewVegas, 14, @"textures\interface\icons\world map\icon_map_vault.dds")]
    [InlineData(BethesdaGame.Oblivion, 1, @"textures\menus\map\world\world_map_icon_camp.dds")]
    [InlineData(BethesdaGame.Oblivion, 2, @"textures\menus\map\world\world_map_icon_cave.dds")]
    [InlineData(BethesdaGame.Oblivion, 3, @"textures\menus\map\world\world_map_city_icon.dds")]
    [InlineData(BethesdaGame.Oblivion, 4, @"textures\menus\map\world\world_map_icon_elven_ruin.dds")]
    [InlineData(BethesdaGame.Oblivion, 5, @"textures\menus\map\world\world_map_icon_fort_ruin.dds")]
    [InlineData(BethesdaGame.Oblivion, 6, @"textures\menus\map\world\world_map_icon_mine.dds")]
    [InlineData(BethesdaGame.Oblivion, 7, @"textures\menus\map\world\world_map_icon_mountain_peak.dds")]
    [InlineData(BethesdaGame.Oblivion, 8, @"textures\menus\map\world\world_map_icon_tavern.dds")]
    [InlineData(BethesdaGame.Oblivion, 9, @"textures\menus\map\world\world_map_icon_settlement.dds")]
    [InlineData(BethesdaGame.Oblivion, 10, @"textures\menus\map\world\world_map_daedric_shrine_icon.dds")]
    [InlineData(BethesdaGame.Oblivion, 11, @"textures\menus\map\world\world_map_icon_daedric_shrine.dds")]
    public void DirectIconPath_MapsVerifiedDdsAssets(BethesdaGame game, int rawValue, string expected)
    {
        Assert.Equal(expected,
            MapMarkerArchiveAssetCatalog.DirectIconPath(game, MapMarkerCatalog.Resolve(game, rawValue)));
    }

    [Theory]
    [InlineData(BethesdaGame.Oblivion, 12)] // misleading goblet/door DDS intentionally rejected
    [InlineData(BethesdaGame.Skyrim, 1)]
    [InlineData(BethesdaGame.Fallout4, 0)]
    [InlineData(BethesdaGame.Fallout76, 0)]
    public void DirectIconPath_ReturnsNull_WhenArtworkIsMissingOrSwfOnly(BethesdaGame game, int rawValue)
    {
        Assert.Null(MapMarkerArchiveAssetCatalog.DirectIconPath(game, MapMarkerCatalog.Resolve(game, rawValue)));
    }

    [Fact]
    public void Resolve_UsesArchiveFirst_AndCachesExtraction()
    {
        var archiveCalls = 0;
        var embeddedCalls = 0;
        var archiveBytes = new byte[] { 1, 2, 3 };
        using var provider = new ArchiveFirstMapMarkerIconProvider(
            BethesdaGame.FalloutNewVegas,
            path =>
            {
                archiveCalls++;
                Assert.Equal(@"textures\interface\icons\world map\icon_map_city.dds", path);
                return archiveBytes;
            },
            _ =>
            {
                embeddedCalls++;
                return new byte[] { 9 };
            });
        var entry = MapMarkerCatalog.Resolve(BethesdaGame.FalloutNewVegas, 1);

        var first = provider.Resolve(entry, payload => payload.Source.ToString());
        var second = provider.Resolve(entry, payload => payload.Source.ToString());

        Assert.Equal(nameof(MapMarkerIconPayloadSource.GameArchive), first);
        Assert.Equal(first, second);
        Assert.Equal(1, archiveCalls);
        Assert.Equal(0, embeddedCalls);
    }

    [Fact]
    public void Resolve_FallsBackToEmbedded_WhenArchiveDdsCannotMaterialize()
    {
        var order = new List<MapMarkerIconPayloadSource>();
        var embeddedCalls = 0;
        using var provider = new ArchiveFirstMapMarkerIconProvider(
            BethesdaGame.Fallout3,
            _ => new byte[] { 1 },
            _ =>
            {
                embeddedCalls++;
                return new byte[] { 2 };
            });
        var entry = MapMarkerCatalog.Resolve(BethesdaGame.Fallout3, 1);

        var result = provider.Resolve(entry, payload =>
        {
            order.Add(payload.Source);
            return payload.Source == MapMarkerIconPayloadSource.GameArchive ? null : "loaded";
        });

        Assert.Equal("loaded", result);
        Assert.Equal(
            [MapMarkerIconPayloadSource.GameArchive, MapMarkerIconPayloadSource.EmbeddedFallback],
            order);
        Assert.Equal(1, embeddedCalls);
    }

    [Fact]
    public void Resolve_Fo76UsesColoredEmbeddedPayload_WithoutArchiveRead()
    {
        var archiveCalls = 0;
        using var provider = new ArchiveFirstMapMarkerIconProvider(
            BethesdaGame.Fallout76,
            _ =>
            {
                archiveCalls++;
                return new byte[] { 1 };
            },
            _ => new byte[] { 2 });

        var payload = provider.Resolve(
            MapMarkerCatalog.Resolve(BethesdaGame.Fallout76, 0),
            candidate => candidate);

        Assert.NotNull(payload);
        Assert.Equal(MapMarkerIconPayloadSource.EmbeddedFallback, payload.Source);
        Assert.Equal(MapMarkerIconPayloadFormat.Png, payload.Format);
        Assert.Equal(0, archiveCalls);
        Assert.False(GameProfiles.For(BethesdaGame.Fallout76).MarkersAreTinted);
    }

    [Fact]
    public void PremultiplyRgba_CopiesAndRoundsEachColorByAlpha()
    {
        var straight = new byte[]
        {
            200, 100, 50, 128,
            9, 8, 7, 0,
            1, 2, 3, 255
        };

        var premultiplied = MapMarkerIconPixels.PremultiplyRgba(straight);

        Assert.Equal(new byte[]
        {
            100, 50, 25, 128,
            0, 0, 0, 0,
            1, 2, 3, 255
        }, premultiplied);
        Assert.Equal(200, straight[0]);
    }

    /// <summary>
    ///     The retail FO3/FNV geometry, measured across all 30 icons: a 64×64 DDS whose ink is a centred
    ///     35×35 box at (14,14), fill 0.547. Normalizing the drawn height over the full 64 shrank the
    ///     visible glyph to 0.547× linear — the reported "markers are tiny" regression.
    /// </summary>
    [Fact]
    public void CropToOpaqueBounds_TrimsRetailFnvPaddingToTheInkBox()
    {
        var rgba = TransparentRgba(64, 64);
        FillOpaque(rgba, 64, 14, 14, 35, 35);

        var (pixels, width, height) = MapMarkerIconPixels.CropToOpaqueBounds(rgba, 64, 64);

        Assert.Equal(35, width);
        Assert.Equal(35, height);
        Assert.Equal(35 * 35 * 4, pixels.Length);
        Assert.All(Alphas(pixels), a => Assert.Equal(255, a));
    }

    [Fact]
    public void CropToOpaqueBounds_IsAReferenceEqualNoOpWhenInkTouchesEveryEdge()
    {
        var rgba = TransparentRgba(8, 8);
        FillOpaque(rgba, 8, 0, 0, 8, 8);

        var (pixels, width, height) = MapMarkerIconPixels.CropToOpaqueBounds(rgba, 8, 8);

        Assert.Same(rgba, pixels);
        Assert.Equal(8, width);
        Assert.Equal(8, height);
    }

    /// <summary>A fully transparent icon must never crop to zero — callers would fail to create a bitmap.</summary>
    [Fact]
    public void CropToOpaqueBounds_ReturnsTheSourceWhenNothingClearsTheThreshold()
    {
        var rgba = TransparentRgba(8, 8);

        var (pixels, width, height) = MapMarkerIconPixels.CropToOpaqueBounds(rgba, 8, 8);

        Assert.Same(rgba, pixels);
        Assert.Equal(8, width);
        Assert.Equal(8, height);
    }

    /// <summary>A DXT1 punch-through fringe below the cutoff must not defeat the trim.</summary>
    [Fact]
    public void CropToOpaqueBounds_IgnoresSubThresholdFringe()
    {
        var rgba = TransparentRgba(10, 10);
        FillOpaque(rgba, 10, 3, 3, 4, 4);
        rgba[(0 * 10 + 0) * 4 + 3] = 7; // one nearly-transparent corner texel, below the threshold of 8

        var (_, width, height) = MapMarkerIconPixels.CropToOpaqueBounds(rgba, 10, 10);

        Assert.Equal(4, width);
        Assert.Equal(4, height);
    }

    /// <summary>Channel order and row stride must survive the copy — a transposed crop is silently wrong.</summary>
    [Fact]
    public void CropToOpaqueBounds_PreservesChannelOrderAndRowStride()
    {
        var rgba = TransparentRgba(4, 4);
        // Two opaque texels on different rows and columns, with distinct colours.
        SetTexel(rgba, 4, 1, 1, 10, 20, 30, 255);
        SetTexel(rgba, 4, 2, 2, 40, 50, 60, 255);

        var (pixels, width, height) = MapMarkerIconPixels.CropToOpaqueBounds(rgba, 4, 4);

        Assert.Equal(2, width);
        Assert.Equal(2, height);
        // (1,1) becomes (0,0) and (2,2) becomes (1,1); the off-diagonal texels stay transparent.
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, pixels[..4]);
        Assert.Equal(new byte[] { 40, 50, 60, 255 }, pixels[12..16]);
        Assert.Equal(0, pixels[7]);
        Assert.Equal(0, pixels[11]);
    }

    private static byte[] TransparentRgba(int width, int height)
    {
        return new byte[width * height * 4];
    }

    private static void FillOpaque(byte[] rgba, int stride, int x0, int y0, int w, int h)
    {
        for (var y = y0; y < y0 + h; y++)
        {
            for (var x = x0; x < x0 + w; x++)
            {
                SetTexel(rgba, stride, x, y, 255, 255, 255, 255);
            }
        }
    }

    private static void SetTexel(byte[] rgba, int stride, int x, int y, byte r, byte g, byte b, byte a)
    {
        var i = (y * stride + x) * 4;
        rgba[i] = r;
        rgba[i + 1] = g;
        rgba[i + 2] = b;
        rgba[i + 3] = a;
    }

    private static IEnumerable<byte> Alphas(byte[] rgba)
    {
        for (var i = 3; i < rgba.Length; i += 4) yield return rgba[i];
    }
}