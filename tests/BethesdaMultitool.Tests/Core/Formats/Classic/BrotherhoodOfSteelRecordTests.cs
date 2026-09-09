using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) that Fallout: Brotherhood of Steel's records synthesize
///     from the shipped disc image. The ISO is the install — it mounts through the shared probe
///     chain and is claimed by <c>ClassicSourceProbe.TryDetectDiscImage</c>, so nothing is extracted.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class BrotherhoodOfSteelRecordTests
{
    private static async Task<IReadOnlyList<GenericEsmRecord>>
        LoadAsync()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var iso = RealAssetPaths.Consoles.BrotherhoodOfSteelIso();
        Assert.SkipWhen(iso is null, RealAssetPaths.SkipMessage("Fallout: Brotherhood of Steel disc image"));

        var result = await ClassicGameAnalyzer.LoadAsync(iso, CancellationToken.None);
        Assert.Equal(BethesdaGame.FalloutBrotherhoodOfSteel, result.Records.Game);
        return result.Records.GenericRecords;
    }

    [Fact]
    public async Task TheDiscSynthesizesItsMasterCatalogueAndOneRecordPerLevel()
    {
        var records = await LoadAsync();

        // ⚑ The master table is the catalogue: 2,243 definitions in DATA\ALL.DDF, and 54 per-level
        // files whose records are 99% byte-identical copies of it. Emitting all 43,006 would be the
        // same definitions repeated 19 times over.
        Assert.Equal(2_243, records.Count(r => r.RecordType == BosRecordSource.DefinitionRecordType));
        Assert.Equal(54, records.Count(r => r.RecordType == BosRecordSource.LevelRecordType));
        Assert.Equal(3_933, records.Count(r => r.RecordType == BosRecordSource.TextRecordType));

        // ClassicNameHash keys the level records, and that can collide — the helper's own contract
        // says a source using it must prove uniqueness over the set it actually produced.
        var ids = records.Select(r => r.FormId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public async Task DefinitionsAreNamedByTheStringDatabasesAndReadAsProse()
    {
        var records = await LoadAsync();
        var definitions = records
            .Where(r => r.RecordType == BosRecordSource.DefinitionRecordType)
            .ToList();

        // ⚑ PROSE IS THE ORACLE. No structural check could establish that these hashes are the same
        // keys the string databases use; that "Cyrus", "Cain" and "Nadia" — the game's three player
        // characters — come back as actors, and "DuckAndCover" as an AI behaviour, could not happen
        // by accident.
        // ⚠ A name is NOT unique and does NOT determine a class: "Land Mine" is both a Weapon
        // (the deployable) and a Trap (the placed one), and several items share a name outright.
        // So this is a lookup, not a dictionary — an earlier revision of this test crashed on the
        // duplicate "Clothes", which is how the point got measured.
        var named = definitions
            .Where(r => r.FullName is not null)
            .ToLookup(r => r.FullName!, r => r.Fields["Class"], StringComparer.Ordinal);

        Assert.Contains("Actor", named["Cyrus"]);
        Assert.Contains("Actor", named["Nadia"]);
        Assert.Contains("AI behaviour", named["DuckAndCover"]);
        Assert.Contains("Weapon", named["Double Barrel Shotgun"]);
        Assert.Contains("Inventory item", named["Stimpak"]);
        Assert.Contains("Trap", named["Frag Grenade Trap"]);
        Assert.Contains("Weapon", named["Land Mine"]);
        Assert.Contains("Trap", named["Land Mine"]);

        // Re-measured 2026-09-08 after the naming upgrade: 1,586 of the 2,243 resolve, up from
        // 1,524. The gain is the hash split — a string is now taken as a record's own NAME when
        // BosNameHash.Compute(text) == the key (1,035 records) and as a DISPLAY string otherwise
        // (551 more), so an ambiguous display value no longer erases a name the hash proves.
        // The rest are absent from every database on this disc; the Xbox release, which ships
        // deftexte.sdb, leaves only 72 unnamed.
        Assert.Equal(1_586, definitions.Count(r => r.FullName is not null));
        Assert.Equal(1_035, definitions.Count(r => r.Fields.ContainsKey("Name")));
    }

    [Fact]
    public async Task EachLevelCensusSumsToItsOwnRecordCount()
    {
        var records = await LoadAsync();
        var levels = records
            .Where(r => r.RecordType == BosRecordSource.LevelRecordType)
            .ToList();

        // The per-class counts are a partition of the file's records, so they must add back up —
        // a class the taxonomy failed to name would show as a missing remainder here.
        foreach (var level in levels)
        {
            var total = (int)level.Fields["Records"]!;
            var census = level.Fields
                .Where(f => f.Key is not ("File" or "Records" or "Size" or "OverridesMaster"))
                .Sum(f => (int)f.Value!);
            Assert.Equal(total, census);
        }

        // ⚠ A level's own content is the 1% that differs from the master in BYTES — 398 records
        // across the disc, mostly actors (295) and traps (54).
        Assert.Equal(398, levels.Sum(l => (int)l.Fields["OverridesMaster"]!));
    }

    [Fact]
    public async Task StringsThatNameNoRecordAreTheGamesDialogue()
    {
        var records = await LoadAsync();
        var text = records
            .Where(r => r.RecordType == BosRecordSource.TextRecordType)
            .ToList();

        // ⚑ Prose again: these are sentences, and no structural rule could have told them apart
        // from the object names — the split is exactly "does a record carry this hash".
        var lines = text.Select(r => (string)r.Fields["Text"]!).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Locked", lines);
        Assert.Contains("Nuclear Blast", lines);
        Assert.Contains("Chapter 1 - Cyrus", lines);

        // Most of them are sentence-length, which is what makes "dialogue" the honest reading
        // rather than "more identifiers".
        Assert.Equal(2_075, text.Count(r => (int)r.Fields["Length"]! > 40));
        Assert.Equal(356, text.Max(r => (int)r.Fields["Length"]!));

        // ⚠ The index is the game's own 32-bit hash truncated to 24 bits. That is lossless HERE —
        // zero collisions across all 5,520 union hashes — and this is the check that says so, since
        // a corpus with more strings could break it.
        var ids = text.Select(r => r.FormId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}