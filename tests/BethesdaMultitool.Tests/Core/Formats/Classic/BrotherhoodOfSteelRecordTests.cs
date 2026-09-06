using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BethesdaMultitool.Core.Formats.Classic;
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
    private static async Task<IReadOnlyList<BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc.GenericEsmRecord>>
        LoadAsync()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var iso = RealAssetPaths.Consoles.BrotherhoodOfSteelIso();
        Assert.SkipWhen(iso is null, RealAssetPaths.SkipMessage("Fallout: Brotherhood of Steel disc image"));

        var result = await ClassicGameAnalyzer.LoadAsync(iso!, CancellationToken.None);
        Assert.Equal(BethesdaMultitool.Core.Games.BethesdaGame.FalloutBrotherhoodOfSteel, result.Records.Game);
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

        // Measured 2026-09-06: 1,524 of the 2,243 resolve. The rest are either absent from every
        // database or ambiguous across them — 63 hashes carry different text in different files, and
        // those are deliberately left unnamed rather than resolved by coin-flip.
        Assert.Equal(1_524, definitions.Count(r => r.FullName is not null));
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
}
