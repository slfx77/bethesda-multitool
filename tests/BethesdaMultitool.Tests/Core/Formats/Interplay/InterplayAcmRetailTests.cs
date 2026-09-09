using System.Text;
using System.Text.RegularExpressions;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Interplay;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Interplay;

/// <summary>
///     Opt-in (<c>RUN_BUCKET_B=1</c>) decode of EVERY Interplay ACM in Fallout 1's
///     <c>MASTER.DAT</c> and Fallout 2's <c>master.dat</c> through the libacm port.
///     <para>
///         The installs are modded, but a DAT is vanilla and self-contained, so its internal ACM
///         count is pinned: 2,511 and 2,473, counted 2026-09-07 by an independent Python walk of
///         the DAT1/DAT2 directories (not through this repo's reader). The loose music files under
///         the installs are asserted per file, never by count.
///     </para>
///     <para>
///         The sample-count arithmetic: the header's u32 total counts VALUES over all channels; a
///         block carries <c>rows * (1 &lt;&lt; level)</c> values and the last block only
///         <c>total mod blockLength</c> of them, so a decode that emits exactly <c>total</c>
///         values has walked <c>ceil(total / blockLength)</c> blocks and stopped inside the last
///         one.
///     </para>
///     <para>
///         The channel assertions rest on an oracle OUTSIDE the file: every speech line ships a
///         same-stem <c>.TXT</c> transcript, and words per second is conversational (2-3.5) only when
///         the line is read as mono — the header's stereo doubles it. A test that pinned the
///         header's own count would pass on a decoder that plays speech at double speed.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class InterplayAcmRetailTests
{
    private static readonly Regex WordPattern = new("[A-Za-z0-9']+", RegexOptions.Compiled);

    private static string RequireArchive(string? root, string game, string archiveName)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage(game));
        var path = Path.Combine(root, archiveName);
        Assert.SkipWhen(!File.Exists(path), $"{archiveName} not found under {root}");
        return path;
    }

    /// <summary>Decodes every <c>.ACM</c> entry, asserting each emits exactly its header total.</summary>
    private static DecodeSummary DecodeEveryAcm(string archivePath)
    {
        using var archive = ArchiveReader.Open(archivePath);
        var all = archive.ListFiles();
        var entries = all.Where(e => e.Extension == ".acm").ToList();
        var transcripts = all
            .Where(e => e.Extension == ".txt")
            .ToDictionary(
                e => (e.FolderPath.ToUpperInvariant(), Path.GetFileNameWithoutExtension(e.Name).ToUpperInvariant()),
                e => e);

        var declaredStereo = 0;
        var oddStereo = 0;
        var inferredStereo = 0;
        var speech = 0;
        var effects = 0;
        var rates = new HashSet<int>();
        var packings = new HashSet<int>();
        long values = 0;
        var transcribed = new List<(int, double, double)>();
        foreach (var entry in entries)
        {
            var bytes = archive.ReadFile(entry.FullPath);
            Assert.NotNull(bytes);
            Assert.True(InterplayAcmFile.IsAcm(bytes), $"{entry.FullPath} does not probe as ACM");

            var acm = InterplayAcmFile.Parse(bytes, entry.Name);
            var pcm = acm.Decode();

            Assert.Equal(acm.TotalValues, pcm.Samples.Length);
            Assert.Equal(acm.TotalValues, (acm.BlockCount - 1) * acm.BlockLength + acm.LastBlockValues);
            Assert.Equal(acm.DeclaredChannels, pcm.Channels);

            rates.Add(acm.SampleRate);
            packings.Add((acm.Rows << 4) | acm.Level);
            values += acm.TotalValues;
            var folder = entry.FolderPath.ToUpperInvariant();
            if (folder.Contains("SPEECH", StringComparison.Ordinal))
            {
                speech++;
            }
            else if (folder.Contains("SFX", StringComparison.Ordinal))
            {
                effects++;
            }

            if (acm.DeclaredChannels == 2)
            {
                declaredStereo++;
                if (acm.TotalValues % 2 != 0)
                {
                    oddStereo++;
                    Assert.Equal(1, pcm.Probe.InferredChannels);
                }
            }

            if (pcm.Probe.InferredChannels == 2)
            {
                inferredStereo++;
            }

            if (transcripts.TryGetValue((folder, Path.GetFileNameWithoutExtension(entry.Name).ToUpperInvariant()),
                    out var txt))
            {
                var text = Encoding.Latin1.GetString(archive.ReadFile(txt.FullPath)!);
                var words = WordPattern.Count(text);
                if (words >= 3)
                {
                    var inferred = pcm.AsInferred();
                    transcribed.Add((words, inferred.DurationSeconds, acm.DurationSeconds));
                }
            }
        }

        return new DecodeSummary(entries.Count, declaredStereo, oddStereo, inferredStereo, speech, effects, rates,
            packings, values, transcribed);
    }

    private static double MedianWordsPerSecond(IEnumerable<(int Words, double Seconds)> lines)
    {
        var rates = lines.Select(l => l.Words / l.Seconds).OrderBy(r => r).ToList();
        return rates[rates.Count / 2];
    }

    [Fact]
    public void Fallout1MasterDat_EveryAcmDecodesToItsHeaderTotal()
    {
        var path = RequireArchive(RealAssetPaths.Classics.Fallout1(), "Fallout", "MASTER.DAT");

        var summary = DecodeEveryAcm(path);

        Assert.Equal(2511, summary.Files);

        // Every retail ACM is 22,050 Hz; packing is 0x0107 (16 x 128) or 0x0108 (16 x 256).
        Assert.Equal([22050], summary.Rates);
        Assert.Equal(new HashSet<int> { 0x0107, 0x0108 }, summary.Packings);

        // 1,647 files declare 2 channels (every speech line among them) and 1,301 of those carry an
        // ODD value total, which no interleaved stereo stream can — the "header channels often
        // wrong" case libacm warns about. The DAT holds speech and effects only (the music is
        // loose), and the values say every one of them is mono.
        Assert.Equal(1646, summary.SpeechFiles);
        Assert.Equal(864, summary.EffectFiles);
        Assert.Equal(1647, summary.DeclaredStereo);
        Assert.Equal(1301, summary.OddTotalDeclaredStereo);
        Assert.Equal(0, summary.InferredStereo);

        // The transcript oracle: 1,538 speech lines carry a .TXT of 3+ words. Read as mono they
        // run at 2.65 words/s (median) — conversational; read at the header's stereo, 5.3.
        Assert.Equal(1538, summary.Transcribed.Count);
        var inferredRate = MedianWordsPerSecond(summary.Transcribed.Select(t => (t.Words, t.InferredSeconds)));
        var declaredRate = MedianWordsPerSecond(summary.Transcribed.Select(t => (t.Words, t.DeclaredSeconds)));
        Assert.InRange(inferredRate, 2.0, 3.5);
        Assert.InRange(declaredRate, 4.5, 7.0);
    }

    [Fact]
    public void Fallout2MasterDat_EveryAcmDecodesToItsHeaderTotal()
    {
        var path = RequireArchive(RealAssetPaths.Classics.Fallout2(), "Fallout 2", "master.dat");

        var summary = DecodeEveryAcm(path);

        Assert.Equal(2473, summary.Files);
        Assert.Equal([22050], summary.Rates);
        Assert.Equal(new HashSet<int> { 0x0107, 0x0108 }, summary.Packings);

        // 1,551 declare stereo: all 1,111 speech lines plus 439 of the 1,361 effects (and the
        // stray DATA\KILL13B.ACM); 752 have odd totals. Every one is mono by the values.
        Assert.Equal(1111, summary.SpeechFiles);
        Assert.Equal(1361, summary.EffectFiles);
        Assert.Equal(1551, summary.DeclaredStereo);
        Assert.Equal(752, summary.OddTotalDeclaredStereo);
        Assert.Equal(0, summary.InferredStereo);

        Assert.Equal(970, summary.Transcribed.Count);
        var inferredRate = MedianWordsPerSecond(summary.Transcribed.Select(t => (t.Words, t.InferredSeconds)));
        var declaredRate = MedianWordsPerSecond(summary.Transcribed.Select(t => (t.Words, t.DeclaredSeconds)));
        Assert.InRange(inferredRate, 2.0, 3.5);
        Assert.InRange(declaredRate, 4.5, 7.0);
    }

    [Theory]
    [InlineData("Fallout", @"DATA\SOUND\MUSIC")]
    [InlineData("Fallout 2", @"sound\music")]
    public void LooseMusic_DecodesToItsHeaderTotalAndIsStereo(string game, string musicDir)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = game == "Fallout" ? RealAssetPaths.Classics.Fallout1() : RealAssetPaths.Classics.Fallout2();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage(game));
        var dir = Path.Combine(root, musicDir);
        Assert.SkipWhen(!Directory.Exists(dir), $"{musicDir} not found under {root}");

        // Structure only: the install is modded, so the file list is not pinned.
        var files = Directory.EnumerateFiles(dir, "*.acm", SearchOption.TopDirectoryOnly).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            var acm = InterplayAcmFile.Parse(bytes, Path.GetFileName(file));
            var pcm = acm.Decode();

            Assert.Equal(acm.TotalValues, pcm.Samples.Length);
            Assert.Equal(22050, acm.SampleRate);
            Assert.Equal(2, acm.DeclaredChannels);

            // The soundtrack is stereo and its cues run 3-4 minutes at that reading. The two WIND
            // ambience loops show no two-channel structure at all and read as mono; MAYBE.ACM (the
            // Ink Spots' 1940 recording) is stereo only in the dual-mono sense and is caught by the
            // pairing rule, not the step rule.
            var stem = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
            if (stem.StartsWith("WIND", StringComparison.Ordinal))
            {
                Assert.Equal(1, pcm.Probe.InferredChannels);
                continue;
            }

            Assert.True(pcm.Probe.InferredChannels == 2, $"{stem}: {pcm.Probe.Reason}");
            if (stem == "MAYBE")
            {
                Assert.Contains("paired", pcm.Probe.Reason, StringComparison.Ordinal);
                Assert.InRange(pcm.Probe.WithinToBetweenStep, 0.0, 0.1);
            }
            else
            {
                Assert.InRange(pcm.Probe.Lag1ToLag2Step, 2.0, 20.0);
            }
        }
    }

    private sealed record DecodeSummary(
        int Files,
        int DeclaredStereo,
        int OddTotalDeclaredStereo,
        int InferredStereo,
        int SpeechFiles,
        int EffectFiles,
        HashSet<int> Rates,
        HashSet<int> Packings,
        long Values,
        List<(int Words, double InferredSeconds, double DeclaredSeconds)> Transcribed);
}
