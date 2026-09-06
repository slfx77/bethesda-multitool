using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Travels.OblivionPsp;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) over the seven staged Oblivion PSP betas. The pack is a
///     fixed set of files, so the exact per-build census is legitimate to pin — and the two
///     anomalies it records (the untagged June revision with relative offsets, and the community
///     repack that appended an unsorted record and truncated an entry to nothing) are precisely the
///     cases a reader written only against the four "normal" discs would get wrong.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class OblivionPspArchiveRetailTests
{
    /// <summary>
    ///     Every staged build: its directory, its entry count and whether the pack carries the
    ///     <c>A2.0</c> tag. The dated discs come first and the community repack last, so a test that
    ///     wants only Bethesda's own builds can take the prefix.
    /// </summary>
    private static readonly (string Directory, int Entries, bool Tagged)[] AllBuilds =
    [
        ("1june 9th 2006", 64, false),
        ("2November 21st 2006", 126, true),
        ("3January 11th 2007", 137, true),
        ("4January 31st 2007", 138, true),
        ("5Feburary 1st 2007", 138, true),
        ("6April 27th 2007", 87, true),
        ("Modified 5Feburary 1st 2007", 139, true)
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
        var root = RealAssetPaths.Travels.OblivionPspBuildsRoot();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Oblivion PSP betas"));

        var pack = Path.Combine(root!, build, @"PSP_GAME\USRDIR\GR.ARC");
        Assert.SkipWhen(!File.Exists(pack), RealAssetPaths.SkipMessage($"Oblivion PSP build '{build}'"));
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
        long previousEnd = archive.DataStart;
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
        var archive = OblivionPspArchive.Parse(PackPath("1june 9th 2006"));

        Assert.False(archive.IsTagged);
        Assert.Equal(1040, archive.DataStart);
        Assert.NotEqual(archive.DataStart, OblivionPspArchive.Align(archive.DataStart));
        Assert.Equal(archive.DataStart, archive.Entries[0].Offset);

        // Only this build names entries with file extensions; the later packs use bare resource names.
        Assert.Contains(archive.Entries, e => e.Name.EndsWith(".txd", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheModifiedDiscIsAnUnsortedRepackThatTruncatedAnEntryAndSignedItself()
    {
        var original = OblivionPspArchive.Parse(PackPath("5Feburary 1st 2007"));
        var modified = OblivionPspArchive.Parse(PackPath("Modified 5Feburary 1st 2007"));

        // One entry added, none removed.
        var originalNames = original.Entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var modifiedNames = modified.Entries.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(originalNames.IsSubsetOf(modifiedNames));
        Assert.Equal(originalNames.Count + 1, modifiedNames.Count);

        // Hub_5_Demo is truncated to nothing — the only zero-length record in any of the seven packs.
        Assert.Equal(1_418_856, original.Find("Hub_5_Demo")!.Size);
        Assert.Equal(0, modified.Find("Hub_5_Demo")!.Size);

        // The repacker appended its record rather than re-sorting, so this is the one pack whose
        // table breaks the uppercase-name ordering every Bethesda build holds to.
        Assert.True(IsUppercaseOrdered(original), "the original February disc should be name-ordered");
        Assert.False(IsUppercaseOrdered(modified), "the repack should break the ordering");

        // And it signed itself.
        using var reader = ArchiveReader.Open(PackPath("Modified 5Feburary 1st 2007"));
        var credits = System.Text.Encoding.ASCII.GetString(reader.ReadFile("Credits")!);
        Assert.Equal("Hack by NexTheReal", credits);
    }

    private static bool IsUppercaseOrdered(OblivionPspArchive archive)
    {
        var names = archive.Entries.Select(e => e.Name.ToUpperInvariant()).ToList();
        for (var i = 1; i < names.Count; i++)
        {
            if (string.CompareOrdinal(names[i - 1], names[i]) > 0)
            {
                return false;
            }
        }

        return true;
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
