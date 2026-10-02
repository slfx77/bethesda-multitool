using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Travels.OblivionPsp;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) over the six staged Oblivion PSP betas. The pack is a
///     fixed set of files, so the exact per-build census is legitimate to pin — and the anomaly it
///     records (the untagged June revision with relative offsets) is precisely the case a reader
///     written only against the four "normal" discs would get wrong. ⚠ The community repack that
///     appended an unsorted record and truncated an entry to nothing was dropped from the corpus
///     on 2026-09-08 (a fan modification, not a Bethesda build), so its row is gone from here.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class OblivionPspArchiveRetailTests
{
    /// <summary>
    ///     Every staged build: its date (the corpus directory is
    ///     <c>The Elder Scrolls Travels - Oblivion (date, PSP - Prototype)</c>), its entry count and
    ///     whether the pack carries the <c>A2.0</c> tag.
    /// </summary>
    private static readonly (string Directory, int Entries, bool Tagged)[] AllBuilds =
    [
        ("2006-6-9", 64, false),
        ("2006-11-21", 126, true),
        ("2007-1-11", 137, true),
        ("2007-1-31", 138, true),
        ("2007-2-1", 138, true),
        ("2007-4-27", 87, true)
    ];

    /// <summary>Build directory, entry count, and whether the pack carries the <c>A2.0</c> tag.</summary>
    public static TheoryData<string, int, bool> Builds()
    {
        var data = new TheoryData<string, int, bool>();
        foreach (var (directory, entries, tagged) in AllBuilds)
        {
            data.Add(directory, entries, tagged);
        }

        return data;
    }

    private static string PackPath(string build)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Travels.OblivionPspBuild(build);
        var pack = root is null ? null : Path.Combine(root, @"PSP_GAME\USRDIR\GR.ARC");
        Assert.SkipWhen(pack is null || !File.Exists(pack), RealAssetPaths.SkipMessage($"Oblivion PSP build '{build}'"));
        return pack;
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public void EveryBuildParsesAndTilesEveryByte(string build, int entryCount, bool tagged)
    {
        var path = PackPath(build);
        var archive = OblivionPspArchive.Parse(path);
        var length = new FileInfo(path).Length;

        Assert.Equal(entryCount, archive.Entries.Count);
        Assert.Equal(tagged, archive.IsTagged);
        Assert.Equal(length, archive.NameTableOffset + archive.NameTableSize);

        // Every payload lies inside the data area, in record order, with only alignment padding
        // between neighbours — the property that makes overlap impossible rather than merely absent.
        var previousEnd = archive.DataStart;
        foreach (var entry in archive.Entries)
        {
            Assert.True(entry.Offset >= archive.DataStart, $"{entry.Name} starts before the data area");
            Assert.True(entry.Offset + entry.Size <= archive.NameTableOffset, $"{entry.Name} runs into the name table");
            Assert.True(entry.Offset >= previousEnd, $"{entry.Name} overlaps its predecessor");
            Assert.True(entry.Offset - previousEnd < 32, $"{entry.Name} leaves a gap wider than the alignment");
            previousEnd = entry.Offset + entry.Size;
        }

        // Names are unique case-insensitively and none is empty.
        var names = archive.Entries.Select(e => e.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, n => Assert.False(string.IsNullOrEmpty(n)));
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public void EveryBuildOpensThroughTheArchiveProbeAndExtracts(string build, int entryCount, bool tagged)
    {
        var path = PackPath(build);

        using var reader = ArchiveReader.Open(path);
        Assert.Equal("PSP", reader.PlatformLabel);
        Assert.Equal(tagged ? "ARC (Oblivion PSP, A2.0)" : "ARC (Oblivion PSP)", reader.FormatName);
        Assert.Equal(entryCount, reader.TotalFiles);

        // Extracting every entry of a 216 MB pack is the slow way to prove the offsets; the first
        // and last records bracket the whole data area, which is what actually needs proving.
        var entries = reader.ListFiles();
        foreach (var entry in new[] { entries[0], entries[^1] })
        {
            var bytes = reader.Extract(entry);
            Assert.Equal(entry.Size, bytes.Length);
        }
    }

    [Fact]
    public void TheJune2006RevisionStoresRelativeOffsets()
    {
        // Its data area begins at the RAW record-table end (16 + 64 x 16 = 1,040), which is not
        // 32-aligned — the reason that build could not use absolute offsets. Reading its first
        // record absolutely would put a payload on top of the header.
        var archive = OblivionPspArchive.Parse(PackPath("2006-6-9"));

        Assert.False(archive.IsTagged);
        Assert.Equal(1040, archive.DataStart);
        Assert.NotEqual(archive.DataStart, OblivionPspArchive.Align(archive.DataStart));
        Assert.Equal(archive.DataStart, archive.Entries[0].Offset);

        // Only this build names entries with file extensions; the later packs use bare resource names.
        Assert.Contains(archive.Entries, e => e.Name.EndsWith(".txd", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OnlyTwoNamesSurviveEveryDatedBuild()
    {
        // June 2006 is a prison/crypt tech demo and April 2007 a separate demo build, so the pack's
        // content turns over almost completely. That is content history, not format instability —
        // worth pinning so a future reader does not "fix" a diff that is telling the truth.
        var dated = AllBuilds
            .Select(b => b.Directory)
            .Where(b => !b.StartsWith("Modified", StringComparison.Ordinal))
            .ToList();

        HashSet<string>? shared = null;
        foreach (var build in dated)
        {
            var names = OblivionPspArchive.Parse(PackPath(build))
                .Entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (shared is null)
            {
                shared = names;
            }
            else
            {
                shared.IntersectWith(names);
            }
        }

        Assert.Equal(
            ["CPlayerBehaviour.Oblivion", "GlobalStream"],
            shared!.OrderBy(n => n, StringComparer.Ordinal));
    }
}