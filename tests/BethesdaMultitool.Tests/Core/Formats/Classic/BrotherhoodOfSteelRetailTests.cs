using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) of the Fallout: Brotherhood of Steel disc image. The
///     image mounts through the shared <c>DiscImageBackend</c>, so these read the shipped files
///     directly out of the ISO with no extraction step.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class BrotherhoodOfSteelRetailTests
{
    private static string RequireIso()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var iso = RealAssetPaths.Consoles.BrotherhoodOfSteelIso();
        Assert.SkipWhen(iso is null, RealAssetPaths.SkipMessage("Fallout: Brotherhood of Steel disc image"));
        return iso!;
    }

    [Fact]
    public void EveryStringDatabaseResolvesExactlyTheCountItsHeaderDeclares()
    {
        // ⚑ The count IS the proof of the 8-byte slot stride: on the shipped BAR.SDB it yields the
        // 85 strings the header declares, where a 16-byte stride yields only 40. The reader refuses
        // any file whose resolved count disagrees, so parsing every database is the check.
        var iso = RequireIso();
        using var disc = ArchiveReader.Open(iso);

        var databases = disc.ListFiles()
            .Where(e => e.Name.EndsWith(".SDB", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // ⚠ 56, not the 18 an earlier census claimed: that number came from `archive list`, which
        // prints only the first 100 of the disc's 352 files and says "... and 252 more". Confirmed
        // independently by extracting every *.SDB — 56 files.
        Assert.Equal(56, databases.Count);

        var failures = new List<string>();
        var strings = 0;
        var unresolved = 0;
        foreach (var entry in databases)
        {
            var bytes = disc.ReadFile(entry.FullPath);
            Assert.NotNull(bytes);

            if (!BosStringDatabase.TryParse(bytes, entry.Name, out var database, out var error))
            {
                failures.Add(error);
                continue;
            }

            strings += database.Entries.Count;
            unresolved += database.UnresolvedSlots;

            // The text is UTF-16LE; read as ASCII every string would carry NULs between letters.
            Assert.All(database.Entries, e => Assert.DoesNotContain('\0', e.Value));
        }

        Assert.Empty(failures);
        Assert.True(strings > 0, "no strings were read from any database");
    }

    [Fact]
    public void EveryDataFileDirectoryTilesFromItsFixedHeader()
    {
        // ⚑ PARSING IS THE PROOF: 772 + 12 * count must equal the first record's offset, offsets
        // must ascend, and the third field must be zero. Measured 2026-09-06 over all 55 files and
        // 43,006 records.
        var iso = RequireIso();
        using var disc = ArchiveReader.Open(iso);

        var files = disc.ListFiles()
            .Where(e => e.Name.EndsWith(".DDF", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Equal(55, files.Count);

        var failures = new List<string>();
        var records = 0;
        var withinFileCollisions = 0;
        var distinctAcrossFiles = new HashSet<uint>();

        foreach (var entry in files)
        {
            var bytes = disc.ReadFile(entry.FullPath);
            Assert.NotNull(bytes);

            if (!BosDataFile.TryParse(bytes, entry.Name, out var file, out var error))
            {
                failures.Add(error);
                continue;
            }

            records += file.Records.Count;
            var seen = new HashSet<uint>();
            foreach (var record in file.Records)
            {
                Assert.True(record.Size > 0, $"{entry.Name} record {record.Index} is empty");
                if (!seen.Add(record.Hash))
                {
                    withinFileCollisions++;
                }

                distinctAcrossFiles.Add(record.Hash);
            }
        }

        Assert.Empty(failures);
        Assert.Equal(43_006, records);

        // ⚑ Hashes are keys WITHIN a file — exactly one collision across all 55, so a hash addresses
        // a record unambiguously in the file that holds it.
        Assert.Equal(1, withinFileCollisions);

        // ⚠ But they are NOT file-unique identities: only 2,242 distinct values back all 43,006
        // records. Keying a cross-file index on the hash alone would alias ~95% of the corpus.
        Assert.Equal(2_242, distinctAcrossFiles.Count);

        // ⚑ The reason: ALL.DDF is the MASTER table (2,243 records, 2,242 distinct after its single
        // collision) and every per-level file is a SUBSET of it — 54/54, with zero hashes occurring
        // anywhere outside it. So a level ships the records it needs, drawn from one global table.
        var master = ReadHashes(disc, files.Single(e => e.Name.Equals("ALL.DDF", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(2_242, master.Count);
        foreach (var entry in files.Where(e => !e.Name.Equals("ALL.DDF", StringComparison.OrdinalIgnoreCase)))
        {
            Assert.ProperSubset(master, ReadHashes(disc, entry));
        }
    }

    /// <summary>The distinct record hashes in one .DDF.</summary>
    private static HashSet<uint> ReadHashes(ArchiveReader disc, ArchiveReader.ArchiveEntry entry)
    {
        var file = BosDataFile.Parse(disc.ReadFile(entry.FullPath)!, entry.Name);
        return [.. file.Records.Select(r => r.Hash)];
    }

    [Fact]
    public void TheBarLevelDatabaseHasItsMeasuredShape()
    {
        var iso = RequireIso();
        using var disc = ArchiveReader.Open(iso);

        var entry = disc.ListFiles().FirstOrDefault(e =>
            e.FullPath.Replace('\\', '/').Equals("DATA/C1/BAR/BAR.SDB", StringComparison.OrdinalIgnoreCase));
        Assert.SkipWhen(entry is null, RealAssetPaths.SkipMessage("BAR.SDB"));

        var database = BosStringDatabase.Parse(disc.ReadFile(entry!.FullPath)!, "BAR.SDB");

        // Measured 2026-09-06 on the shipped file: 85 strings, table at 3,436. Nothing is left
        // unresolved once the walk stops at the terminator — the slot that used to show up as a
        // stray WAS the terminator, sitting exactly 128 slots in (the hashtablesize the disc's own
        // debug dump names).
        Assert.Equal(85, database.Entries.Count);
        Assert.Equal(0, database.UnresolvedSlots);
        Assert.Equal(3436, database.HashTableOffset);

        // Prose, which is the oracle that the UTF-16 decode and the offsets are both right.
        Assert.Contains(database.Entries, e => e.Value == "Sleeping Man");
        Assert.Contains(database.Entries, e => e.Value == "Freezer Chest");
    }
}
