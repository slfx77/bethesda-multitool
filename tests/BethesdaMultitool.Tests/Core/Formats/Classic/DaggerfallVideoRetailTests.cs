using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks of the <c>.VID</c> movies against the retail ARENA2 (<c>RUN_BUCKET_B=1</c>).
///     The block census was measured with an independent Python walk (2026-09-03) before the
///     decoder was written.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class DaggerfallVideoRetailTests
{
    /// <summary>The two canvas widths retail movies use.</summary>
    private static readonly int[] KnownWidths = [256, 320];

    private static string RequireArena2()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Daggerfall();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Daggerfall (ARENA2)"));
        return root;
    }

    [Fact]
    public void EveryMovie_WalksToTheByte_AndDecodesItsDeclaredFrameCount()
    {
        var root = RequireArena2();
        var paths = Directory.EnumerateFiles(root, "*.VID")
            .Where(p => DaggerfallVidFile.IsVidFileName(Path.GetFileName(p)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Equal(17, paths.Count);

        var totalFrames = 0;
        var audioSeconds = 0d;
        foreach (var path in paths)
        {
            var vid = DaggerfallVidFile.Parse(File.ReadAllBytes(path), Path.GetFileName(path));

            Assert.Equal(512, vid.Unknown1);
            Assert.Equal(14, vid.Unknown2);
            Assert.Equal(200, vid.Height);
            Assert.Contains(vid.Width, KnownWidths);
            Assert.True(vid.EndOfFileSeen, $"{vid.Name} did not end on an end-of-file block.");
            Assert.Equal(vid.DeclaredFrameCount, vid.FrameCount);

            // No retail movie changes palette after the header.
            Assert.Equal(1, vid.BlockCounts[DaggerfallVidBlockType.Palette]);

            totalFrames += vid.FrameCount;
            audioSeconds += vid.AudioSeconds;
        }

        Assert.Equal(4_661, totalFrames);
        Assert.InRange(audioSeconds, 400, 500);
    }

    [Fact]
    public void TheIntroMovie_DecodesEveryFrameOntoItsCanvas()
    {
        var path = Path.Combine(RequireArena2(), "ANIM0000.VID");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("ANIM0000.VID"));

        var vid = DaggerfallVidFile.Parse(File.ReadAllBytes(path), "ANIM0000.VID");

        Assert.Equal((320, 200), (vid.Width, vid.Height));
        Assert.Equal(633, vid.FrameCount);
        Assert.Equal(475_054, vid.Audio.Length);
        Assert.Equal(43.1, vid.AudioSeconds, 1);
        Assert.Equal(1, vid.BlockCounts[DaggerfallVidBlockType.VideoStartFrame]);
        Assert.Equal(632, vid.BlockCounts[DaggerfallVidBlockType.VideoIncrementalRowOffsetFrame]);

        var frames = vid.EnumerateFrames().ToList();
        Assert.Equal(633, frames.Count);
        Assert.All(frames, f => Assert.Equal(320 * 200, f.Bitmap.Indices.Length));

        // The first frame is a full paint, so it must not be a blank canvas.
        Assert.Equal(127, frames[0].Bitmap.Indices.Distinct().Count());

        // Every incremental block changes the canvas, and the movie holds 311 distinct images —
        // it repeats frames and its last frame returns to the opening one, so "last differs from
        // first" would be a false expectation here (measured independently on the file).
        Assert.Equal(632, Enumerable.Range(1, frames.Count - 1)
            .Count(i => !frames[i].Bitmap.Indices.SequenceEqual(frames[i - 1].Bitmap.Indices)));
        Assert.Equal(311, frames.Select(f => Convert.ToBase64String(f.Bitmap.Indices)).Distinct().Count());
        Assert.True(frames[^1].Bitmap.Indices.SequenceEqual(frames[0].Bitmap.Indices));
    }

    [Fact]
    public async Task Analyzer_AddsAMovieRecordPerFile()
    {
        var arena2 = RequireArena2();
        var installRoot = Path.GetDirectoryName(arena2)!;

        var result = await ClassicGameAnalyzer.LoadAsync(installRoot, TestContext.Current.CancellationToken);

        var videos = result.Records.GenericRecords
            .Where(r => r.RecordType == DaggerfallRecordSource.VideoRecordType)
            .ToList();

        Assert.Equal(17, videos.Count);
        Assert.Equal("ANIM0000", videos[0].EditorId);
        Assert.Equal(633, videos[0].Fields["Frames"]);
        Assert.Equal(320, videos[0].Fields["Width"]);
        Assert.True((bool)videos[0].Fields["EndsCleanly"]!);
        Assert.Equal("DAG2", videos[^1].EditorId);
        Assert.Equal(256, videos[^1].Fields["Width"]);
    }
}