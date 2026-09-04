using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BethesdaMultitool.Core.Formats.Audio;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks of DAGGER.SND and MIDI.BSA against the retail ARENA2 (<c>RUN_BUCKET_B=1</c>).
///     The counts were measured with an independent Python walk (2026-09-03) before the readers
///     were written.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class DaggerfallAudioRetailTests
{
    private static string RequireArena2()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Daggerfall();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Daggerfall (ARENA2)"));
        return root!;
    }

    [Fact]
    public void Sounds_AreFourHundredAndFiftyNineRecordsOfElevenKilohertzPcm()
    {
        var path = Path.Combine(RequireArena2(), DaggerfallSoundFile.FileName);
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("DAGGER.SND"));

        var sounds = DaggerfallSoundFile.Open(path);

        Assert.Equal(459, sounds.Count);
        var ids = Enumerable.Range(0, sounds.Count).Select(sounds.SoundId).ToList();
        Assert.Equal(458, ids.Distinct().Count());
        Assert.Equal(0u, ids.Min());
        Assert.Equal(11_462u, ids.Max());

        // One record is empty and one id (220) is used twice.
        Assert.Equal(1, Enumerable.Range(0, sounds.Count).Count(i => sounds.Samples(i).Length == 0));
        Assert.Equal(2, ids.Count(id => id == 220));

        var longest = Enumerable.Range(0, sounds.Count).MaxBy(i => sounds.Samples(i).Length);
        Assert.Equal(93_717, sounds.Samples(longest).Length);
        Assert.Equal(8.5, sounds.DurationSeconds(longest), 1);

        // Every non-empty record wraps into a WAV whose data chunk is the raw samples.
        foreach (var index in Enumerable.Range(0, sounds.Count).Where(i => sounds.Samples(i).Length > 0))
        {
            var wav = sounds.ToWav(index);
            Assert.Equal(44 + sounds.Samples(index).Length, wav.Length);
        }
    }

    [Fact]
    public void Music_IsOneHundredAndThirtyOneHmiSongs_WithMatchingTrackTables()
    {
        var path = Path.Combine(RequireArena2(), "MIDI.BSA");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("MIDI.BSA"));

        using var archive = ArchiveReader.Open(path);
        var entries = archive.ListFiles();
        Assert.Equal(131, entries.Count);
        Assert.All(entries, e => Assert.True(HmiFile.IsHmiFileName(e.Name)));

        var tracks = 0;
        var maxTracks = 0;
        foreach (var entry in entries)
        {
            var bytes = archive.ReadFile(entry.FullPath);
            Assert.NotNull(bytes);
            var song = HmiFile.Parse(bytes, entry.Name);
            Assert.StartsWith(HmiFile.SongTag, song.Tag, StringComparison.Ordinal);
            tracks += song.Tracks.Count;
            maxTracks = Math.Max(maxTracks, song.Tracks.Count);

            // Every declared offset landed on a marker (Parse enforces it) and the tracks tile.
            Assert.Equal(bytes.Length, song.TrackOffsets[^1] + song.Tracks[^1].Length);
        }

        Assert.Equal(41, maxTracks);
        Assert.True(tracks > 900, $"Expected over 900 tracks across the archive, saw {tracks}.");
    }

    [Fact]
    public async Task Analyzer_AddsSoundAndMusicRecords()
    {
        var arena2 = RequireArena2();
        var installRoot = Path.GetDirectoryName(arena2)!;

        var result = await ClassicGameAnalyzer.LoadAsync(installRoot, TestContext.Current.CancellationToken);

        var records = result.Records.GenericRecords;
        Assert.Equal(459, records.Count(r => r.RecordType == DaggerfallRecordSource.SoundRecordType));
        Assert.Equal(131, records.Count(r => r.RecordType == DaggerfallRecordSource.MusicRecordType));
        Assert.Equal(records.Count, records.Select(r => r.FormId).Distinct().Count());
    }
}
