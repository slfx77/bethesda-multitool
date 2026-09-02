using System;
using System.Linq;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in sweep of <see cref="DaggerfallTextureFile" /> over the retail ARENA2
///     (<c>RUN_BUCKET_B=1</c>): every decodable TEXTURE archive must parse, and every frame must
///     come out at exactly its declared geometry. The corpus is what makes this strong — 469
///     files and 6,713 records exercising all three storage forms, including compression words
///     the reference's enum never named (0x0900 alone covers 4,359 records).
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class DaggerfallTextureRetailTests
{
    private static string RequireArena2()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Daggerfall();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Daggerfall (ARENA2)"));
        return root!;
    }

    [Fact]
    public void EveryDecodableTextureArchive_ParsesWithFullFrameGeometry()
    {
        var root = RequireArena2();
        var files = Directory.EnumerateFiles(root, "TEXTURE.*")
            .Where(p => !DaggerfallTextureFile.IsUnsupported(Path.GetFileName(p)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 472 ship; 3 are the documented malformed ones.
        Assert.True(files.Count >= 469, $"Expected 469 decodable TEXTURE archives, found {files.Count}.");

        var records = 0;
        var frames = 0;
        var rleRecords = 0;
        var multiFrameRecords = 0;
        var emptyRecords = 0;
        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            var texture = DaggerfallTextureFile.Parse(File.ReadAllBytes(path), name);

            Assert.NotEmpty(texture.Records);
            foreach (var record in texture.Records)
            {
                records++;
                if (record.Frames.Count == 0)
                {
                    // Authored-empty placeholders; the retail data has exactly three.
                    emptyRecords++;
                    continue;
                }

                frames += record.Frames.Count;

                if (record.Compression is 0x1108 or 0x0108)
                {
                    rleRecords++;
                }

                if (record.Frames.Count > 1)
                {
                    multiFrameRecords++;
                }

                // Every frame's pixel buffer must be exactly its declared geometry — the
                // arithmetic proof that stride, run and RLE decoding all consumed correctly.
                foreach (var frame in record.Frames)
                {
                    Assert.Equal(frame.Width * frame.Height, frame.Indices.Length);
                    Assert.True(frame.Width > 0 && frame.Height > 0, $"{name} has an empty frame.");
                }
            }
        }

        Assert.Equal(6713, records);
        Assert.Equal(3, emptyRecords);
        Assert.True(frames >= records - emptyRecords, "Frame count cannot be below populated record count.");

        // All three storage forms must actually have been exercised by the sweep.
        Assert.True(rleRecords > 1000, $"Expected over a thousand RLE records, saw {rleRecords}.");
        Assert.True(multiFrameRecords > 500, $"Expected many animated records, saw {multiFrameRecords}.");
    }

    [Fact]
    public void SolidArchives_GenerateTheColourSwatchRamp()
    {
        var root = RequireArena2();
        var path = Path.Combine(root, "TEXTURE.000");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("TEXTURE.000"));

        var texture = DaggerfallTextureFile.Parse(File.ReadAllBytes(path), "TEXTURE.000");

        // Record N is a 32x32 swatch of palette index N.
        for (var r = 0; r < texture.Records.Count; r++)
        {
            var frame = Assert.Single(texture.Records[r].Frames);
            Assert.Equal(DaggerfallTextureFile.SolidSize, frame.Width);
            Assert.All(frame.Indices, i => Assert.Equal((byte)r, i));
        }
    }
}
