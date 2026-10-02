using BethesdaMultitool.Core.Media.Sprite;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Tactics;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Tactics;

/// <summary>
///     Opt-in (<c>RUN_BUCKET_B=1</c>) exact-tiling checks of every <c>&lt;zar&gt;</c> in the retail
///     Fallout Tactics install. Every pinned number below was measured 2026-09-07 by an INDEPENDENT
///     Python transcription of the same decompiled functions (scratchpad <c>zar_decode.py</c> /
///     <c>til_scan.py</c>), not by the reader under test.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class TacticsZarRetailTests
{
    private static string RequireArchive(string fileName)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var core = RealAssetPaths.Classics.FalloutTactics();
        Assert.SkipWhen(core is null, RealAssetPaths.SkipMessage("Fallout Tactics"));
        var path = Path.Combine(core, fileName);
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage(fileName));
        return path;
    }

    private static (long Opaque, double MeanR, double MeanG, double MeanB, int Partial) OpaqueStatistics(
        ReadOnlySpan<byte> rgba)
    {
        long opaque = 0, r = 0, g = 0, b = 0;
        var partial = 0;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            switch (rgba[i + 3])
            {
                case 255:
                    opaque++;
                    r += rgba[i];
                    g += rgba[i + 1];
                    b += rgba[i + 2];
                    break;
                case > 0:
                    partial++;
                    break;
            }
        }

        return (opaque, (double)r / opaque, (double)g / opaque, (double)b / opaque, partial);
    }

    [Fact]
    public void GuiArchive_Every839ZarTilesExactly_WithTheMeasuredRunCensus()
    {
        var path = RequireArchive("gui_0.bos");
        using var archive = ArchiveReader.Open(path);

        var count = 0;
        var plates = 0;
        var shadowed = 0;
        long dataBytes = 0;
        int transparent = 0, opaque = 0, translucent = 0, shadow = 0;
        var maxWidth = 0;
        var maxHeight = 0;
        var largest = (Width: 0, Height: 0);
        var failures = new List<string>();

        foreach (var entry in archive.ListFiles())
        {
            if (!entry.Name.EndsWith(".zar", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var bytes = archive.ReadFile(entry.FullPath);
            Assert.NotNull(bytes);
            count++;
            try
            {
                // Parse requires the record to END the file; Decode requires width*height pixels
                // out AND the block consumed to its last byte.
                var image = TacticsZarImage.Parse(bytes, entry.FullPath);
                var texture = image.Decode();
                Assert.Equal(image.Width * image.Height * 4, texture.Pixels.Length);
                Assert.Equal(4, image.Version);
                Assert.Equal(256, image.PaletteEntries);

                var census = image.CountRuns();
                transparent += census.Transparent;
                opaque += census.Opaque;
                translucent += census.Translucent;
                shadow += census.Shadow;
                dataBytes += image.PixelBlock.Length;
                if (image.ShadowIndex != 0)
                {
                    shadowed++;
                    // ⚑ A non-zero shadow index occurs ONLY in files that use mode 3 (48/48).
                    Assert.True(census.Shadow > 0,
                        $"{entry.FullPath}: shadow index {image.ShadowIndex} but no mode-3 run");
                }

                if (image is { Width: 800, Height: 600 })
                {
                    plates++;
                }

                maxWidth = Math.Max(maxWidth, image.Width);
                maxHeight = Math.Max(maxHeight, image.Height);
                if (image.Width * image.Height > largest.Width * largest.Height)
                {
                    largest = (image.Width, image.Height);
                }
            }
            catch (InvalidDataException e)
            {
                failures.Add(e.Message);
            }
        }

        Assert.Empty(failures);
        Assert.Equal(839, count);
        Assert.Equal(12, plates);
        // The widest files are the two 1024x128 bars (speech.zar, game_bar_long.zar); the tallest
        // and largest by area are the twelve 800x600 plates.
        Assert.Equal((1024, 600), (maxWidth, maxHeight));
        Assert.Equal((800, 600), largest);
        Assert.Equal(48, shadowed);
        Assert.Equal(17_122_127, dataBytes);

        // 804,624 control bytes: mode 0 191,002 / 1 363,360 / 2 14,258 / 3 236,004. A reader with
        // the wrong payload width for any mode desynchronises and fails tiling long before here.
        Assert.Equal((191_002, 363_360, 14_258, 236_004), (transparent, opaque, translucent, shadow));
    }

    [Fact]
    public void GuiArchive_NamedFiles_PinDimensionsLengthsAndRunMix()
    {
        var path = RequireArchive("gui_0.bos");
        using var archive = ArchiveReader.Open(path);

        var barter = TacticsZarImage.Parse(archive.ReadFile("gui/back/BarterNew.zar")!, "BarterNew.zar");
        Assert.Equal((800, 600), (barter.Width, barter.Height));
        Assert.Equal(487_800, barter.PixelBlock.Length);
        Assert.Equal(488_850, barter.RecordLength);
        Assert.Equal(TacticsZarImage.RetailPixelBlockOffset + 487_800, barter.RecordLength);
        // 800 px = 12 runs of 63 + one of 44 => 13 opaque runs per row, 7,800 for 600 rows, nothing else.
        Assert.Equal(new TacticsZarImage.RunCensus(0, 7_800, 0, 0), barter.CountRuns());

        var slider = TacticsZarImage.Parse(archive.ReadFile("gui/misc/slider.zar")!, "slider.zar");
        Assert.Equal((3, 14), (slider.Width, slider.Height));
        Assert.Equal(109, slider.PixelBlock.Length);
        Assert.Equal(new TacticsZarImage.RunCensus(0, 11, 25, 0), slider.CountRuns());

        var equip = TacticsZarImage.Parse(archive.ReadFile("gui/back/equip.zar")!, "equip.zar");
        Assert.Equal((800, 600), (equip.Width, equip.Height));
        Assert.Equal(487_921, equip.PixelBlock.Length);
        Assert.Equal(new TacticsZarImage.RunCensus(6, 7_787, 133, 9), equip.CountRuns());

        var death = TacticsZarImage.Parse(archive.ReadFile("gui/pip/deathSense.zar")!, "deathSense.zar");
        Assert.Equal((120, 127), (death.Width, death.Height));
        Assert.Equal(9_970, death.PixelBlock.Length);
        Assert.Equal(new TacticsZarImage.RunCensus(1_444, 1_035, 0, 2_260), death.CountRuns());
    }

    [Fact]
    public void GuiArchive_ColourOrderOracle_BrassPlateGreenSliderBlackStrokes()
    {
        // ⚑ The B,G,R order is settled by colour, not by tiling (tiling is blind to it): the
        // barter plate is BRASS (mean R > G > B over its 480,000 opaque pixels), the slider handle
        // is GREEN, and deathSense's 1,694 opaque pixels are all black with the icon drawn in
        // mode-3 partial alpha. A B/R swap turns the plate blue and fails the first assertion.
        var path = RequireArchive("gui_0.bos");
        using var archive = ArchiveReader.Open(path);

        var barter = TacticsZarImage.Parse(archive.ReadFile("gui/back/BarterNew.zar")!, "BarterNew.zar").Decode();
        var (opaque, meanR, meanG, meanB, partial) = OpaqueStatistics(barter.Pixels);
        Assert.Equal(480_000, opaque);
        Assert.Equal(0, partial);
        Assert.True(meanR > meanG && meanG > meanB, $"BarterNew means R {meanR:F1} G {meanG:F1} B {meanB:F1}");
        Assert.Equal((55, 47, 33), ((int)Math.Round(meanR), (int)Math.Round(meanG), (int)Math.Round(meanB)));

        var slider = TacticsZarImage.Parse(archive.ReadFile("gui/misc/slider.zar")!, "slider.zar").Decode();
        // Pixel (0,0) is a translucent run: entry 0 = B 0, G 22, R 2 at alpha 36. Pixel (1,1) is
        // opaque (103, 229, 79). Both measured independently.
        Assert.Equal(new byte[] { 2, 22, 0, 36 }, slider.Pixels[..4]);
        const int pixelOneOne = (1 * 3 + 1) * 4;
        Assert.Equal(new byte[] { 103, 229, 79, 255 }, slider.Pixels[pixelOneOne..(pixelOneOne + 4)]);
        var (sliderOpaque, sliderR, sliderG, sliderB, _) = OpaqueStatistics(slider.Pixels);
        Assert.Equal(11, sliderOpaque);
        Assert.True(sliderG > sliderR && sliderG > sliderB,
            $"slider means R {sliderR:F1} G {sliderG:F1} B {sliderB:F1}");

        var death = TacticsZarImage.Parse(archive.ReadFile("gui/pip/deathSense.zar")!, "deathSense.zar").Decode();
        var (deathOpaque, deathR, deathG, deathB, deathPartial) = OpaqueStatistics(death.Pixels);
        Assert.Equal(1_694, deathOpaque);
        Assert.Equal((0.0, 0.0, 0.0), (deathR, deathG, deathB));
        Assert.True(deathPartial > 0, "deathSense should carry partial-alpha shadow strokes");
    }

    [Fact]
    public void SpritePipeline_DecodesAZarStraightOutOfTheBos()
    {
        var path = RequireArchive("gui_0.bos");

        var decoded = SpriteRenderPipeline.Decode(path, "gui/misc/slider.zar");

        Assert.Equal(ClassicSpriteGame.Tactics, decoded.Game);
        Assert.Equal("embedded", decoded.PaletteSource);
        Assert.Equal("slider", decoded.BaseName);
        var frame = Assert.Single(decoded.Frames);
        Assert.Equal((3, 14), (frame.Texture.Width, frame.Texture.Height));
        Assert.Equal(new byte[] { 103, 229, 79, 255 }, frame.Texture.Pixels[16..20]);

        // The info half reports the game's own index plane.
        var frames = SpriteRenderPipeline.Inspect(path, "gui/misc/slider.zar", ClassicSpriteGame.Auto, out var name,
            out var game);
        Assert.Equal("slider.zar", name);
        Assert.Equal(ClassicSpriteGame.Tactics, game);
        Assert.Equal((3, 14), (Assert.Single(frames).Width, frames[0].Height));
    }

    [Fact]
    public void TileArchive_All31127EmbeddedZarsTile_AndOnlyVersion4CarriesTheShadowByte()
    {
        // ⚑ The version gate's retail control: the 4,534 v3 streams have NO shadow byte (reading
        // one scores 0/4,534) while the 26,593 v4 streams do. 1,249 of the v3 streams use mode 3,
        // which then shadows through palette[0]. Streams are located by their '<zar>' tag; what
        // frames them inside a tile is the TIL item's business.
        var path = RequireArchive("tiles_0.bos");
        using var archive = ArchiveReader.Open(path);

        var tag = "<zar>\0"u8;
        var tiles = 0;
        var streams = 0;
        var v3 = 0;
        var v4 = 0;
        var v3Shadowed = 0;
        var noPalette = 0;
        var failures = new List<string>();

        foreach (var entry in archive.ListFiles())
        {
            if (!entry.Name.EndsWith(".til", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var bytes = archive.ReadFile(entry.FullPath);
            Assert.NotNull(bytes);
            tiles++;

            var search = 0;
            while (true)
            {
                var hit = bytes.AsSpan(search).IndexOf(tag);
                if (hit < 0)
                {
                    break;
                }

                var at = search + hit;
                streams++;
                try
                {
                    var cursor = new TacticsCursor(bytes, entry.FullPath, at);
                    var image = TacticsZarImage.Read(cursor);
                    _ = image.Decode();
                    if (!image.HasPalette)
                    {
                        noPalette++;
                    }

                    switch (image.Version)
                    {
                        case 3:
                            v3++;
                            Assert.Equal(0, image.ShadowIndex);
                            if (image.CountRuns().Shadow > 0)
                            {
                                v3Shadowed++;
                            }

                            break;
                        case 4:
                            v4++;
                            break;
                        default:
                            failures.Add($"{entry.FullPath} @{at}: version {image.Version}");
                            break;
                    }

                    search = at + image.RecordLength;
                }
                catch (InvalidDataException e)
                {
                    failures.Add(e.Message);
                    search = at + tag.Length;
                }
            }
        }

        Assert.Equal(29_957, tiles);
        Assert.Empty(failures.Take(10));
        Assert.Equal(31_127, streams);
        Assert.Equal((26_593, 4_534), (v4, v3));
        Assert.Equal(1_249, v3Shadowed);
        Assert.Equal(0, noPalette);
    }
}