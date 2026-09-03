using System;
using System.Linq;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in sweeps of the Daggerfall IMG and CIF/RCI decoders over the retail ARENA2
///     (<c>RUN_BUCKET_B=1</c>). The counts pinned here were established twice over — by the
///     porting agents' surveys and again by an independent Python re-implementation of the
///     reference during adversarial review (2026-09-02) — so a drift means the data or the decoder
///     changed, never the test.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class DaggerfallImageRetailTests
{
    private static string RequireArena2()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Daggerfall();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Daggerfall (ARENA2)"));
        return root!;
    }

    [Fact]
    public void EveryImg_DecodesAndTheHeaderlessAndPalettizedSetsMatchTheirTables()
    {
        var root = RequireArena2();
        var files = Directory.EnumerateFiles(root, "*.IMG")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Equal(263, files.Count);

        var decoded = 0;
        var headerless = 0;
        var embeddedPalettes = 0;
        var refused = 0;
        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            var bytes = File.ReadAllBytes(path);

            if (DaggerfallImgFile.IsUnsupported(name))
            {
                // The three 12-byte all-zero FMAP stubs.
                Assert.Equal(12, bytes.Length);
                Assert.Throws<NotSupportedException>(() => DaggerfallImgFile.Parse(bytes, name));
                refused++;
                continue;
            }

            var img = DaggerfallImgFile.Parse(bytes, name);
            decoded++;

            Assert.Equal(img.Bitmap.Width * img.Bitmap.Height, img.Bitmap.Indices.Length);
            Assert.True(img.Bitmap.Width > 0 && img.Bitmap.Height > 0, $"{name} has empty geometry.");

            if (DaggerfallImgFile.HeaderlessDimensionsBySize.ContainsKey(bytes.Length))
            {
                headerless++;
            }

            if (img.EmbeddedPalette is not null)
            {
                embeddedPalettes++;
                Assert.True(DaggerfallImgFile.HasEmbeddedPalette(name), $"{name} yielded a palette it should not carry.");
                Assert.Equal(64_768, bytes.Length);
            }
        }

        Assert.Equal(3, refused);
        Assert.Equal(260, decoded);
        Assert.Equal(72, headerless);

        // Exactly six fullscreens carry their own 6-bit palette, keyed by name.
        Assert.Equal(6, embeddedPalettes);
    }

    [Fact]
    public void EveryCifAndRci_DecodesToTheIndependentlyCountedRecordsAndFrames()
    {
        var root = RequireArena2();
        var files = Directory.EnumerateFiles(root, "*.CIF")
            .Concat(Directory.EnumerateFiles(root, "*.RCI"))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Equal(76, files.Count);

        var records = 0;
        var frames = 0;
        var weaponRecords = 0;
        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            var cif = DaggerfallCifRciFile.Parse(File.ReadAllBytes(path), name);

            Assert.NotEmpty(cif.Records);
            foreach (var record in cif.Records)
            {
                records++;
                Assert.NotEmpty(record.Frames);
                frames += record.Frames.Count;
                if (record.IsWeaponAnimation)
                {
                    weaponRecords++;
                }

                foreach (var frame in record.Frames)
                {
                    Assert.Equal(frame.Width * frame.Height, frame.Indices.Length);
                    Assert.True(frame.Width > 0 && frame.Height > 0, $"{name} has an empty frame.");
                }
            }
        }

        // 1,172 records / 1,610 frames across the 76 files. The 19 weapon CIFs (WEAPON00-11 plus
        // WEAPO101-108) hold 109 animation records — six per file, except the bow's single one —
        // with 547 RLE frames between them. Both figures come from an independent Python walk that
        // tiles every file exactly to EOF; an earlier review estimate of 115 was a miscount.
        Assert.Equal(1172, records);
        Assert.Equal(1610, frames);
        Assert.Equal(109, weaponRecords);
    }

    [Fact]
    public void WeaponBow_HasNoWieldImage_AndOneSevenFrameAnimation()
    {
        var root = RequireArena2();
        var path = Path.Combine(root, "WEAPON09.CIF");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("WEAPON09.CIF"));

        // The bow is the one weapon file that opens straight into an animation header.
        var cif = DaggerfallCifRciFile.Parse(File.ReadAllBytes(path), "WEAPON09.CIF");

        var record = Assert.Single(cif.Records);
        Assert.True(record.IsWeaponAnimation);
        Assert.Equal(7, record.Frames.Count);
        Assert.All(record.Frames, f =>
        {
            Assert.Equal(204, f.Width);
            Assert.Equal(179, f.Height);
        });
    }
}
