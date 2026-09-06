using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) that the two retail Travels JARs become the record
///     census measured on 2026-09-05. These are fixed fixture files whose tables are headerless or
///     count-driven with no magic number, so exact per-signature counts are the correctness
///     argument rather than an over-fit.
///     <para>
///         Every id here is a composite of a table id and a row ordinal rather than a hash, so a
///         duplicate FormID could only be a synthesizer bug; the uniqueness assertion is what
///         proves the source did not quietly renumber a row to dodge one.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class TravelsRecordSourceRetailTests
{
    private static string Require(string? path, string what)
    {
        Assert.SkipWhen(path is null, RealAssetPaths.SkipMessage(what));
        return path!;
    }

    private static async Task<List<GenericEsmRecord>> LoadAsync(string jar, BethesdaGame game)
    {
        var result = await ClassicGameAnalyzer.LoadAsync(jar);
        Assert.Equal(game, result.Records.Game);
        return result.Records.GenericRecords;
    }

    private static Dictionary<string, int> Census(IEnumerable<GenericEsmRecord> records)
    {
        return records
            .GroupBy(r => r.RecordType, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    }

    private static GenericEsmRecord Row(IEnumerable<GenericEsmRecord> records, string recordType, uint formId)
    {
        return Assert.Single(records.Where(r => r.RecordType == recordType && r.FormId == formId));
    }

    [Fact]
    public async Task TheStormholdJarSynthesizesItsMeasuredCensus()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        var records = await LoadAsync(
            Require(RealAssetPaths.Travels.StormholdJar(), "Stormhold JAR"), BethesdaGame.Stormhold);

        Assert.Equal(440, records.Count);
        Assert.Equal(
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [StormholdRecordSource.ClassRecordType] = 7,
                [StormholdRecordSource.RaceRecordType] = 6,
                [StormholdRecordSource.SkillRecordType] = 14,
                [StormholdRecordSource.ItemRecordType] = 109,
                [StormholdRecordSource.SpellRecordType] = 25,
                [StormholdRecordSource.MonsterRecordType] = 41,
                [StormholdRecordSource.SpriteRecordType] = 5,
                [StormholdRecordSource.DropRecordType] = 43,
                [StormholdRecordSource.DungeonRecordType] = 37,
                [StormholdRecordSource.StringRecordType] = 153
            },
            Census(records));

        // No two records may share a FormID — the whole point of a source-identity index.
        Assert.Equal(records.Count, records.Select(r => r.FormId).Distinct().Count());
        Assert.All(records, r => Assert.InRange(
            ClassicFormIdScheme.DomainOf(r.FormId),
            StormholdRecordSource.FirstDomain,
            StormholdRecordSource.LastDomain));

        // Stormhold ships dungnamesin.dat, so every dungeon is named; Dawnstar's are not.
        Assert.All(
            records.Where(r => r.RecordType == StormholdRecordSource.DungeonRecordType),
            r => Assert.NotNull(r.FullName));

        var camp = Row(records, StormholdRecordSource.DungeonRecordType, 0x4008_0000u);
        Assert.Equal("Dungeon Camp", camp.FullName);
        Assert.Equal(2, camp.Fields["North"]);
        Assert.Equal(11, camp.Fields["East"]);
        Assert.Equal(20, camp.Fields["South"]);
        Assert.Equal(29, camp.Fields["West"]);

        var item = Row(records, StormholdRecordSource.ItemRecordType, 0x4004_0000u);
        Assert.Equal("Miner Pick", item.FullName);
        Assert.Equal("Axe", item.Fields["Type"]);

        var monster = Row(records, StormholdRecordSource.MonsterRecordType, 0x4006_0000u);
        Assert.Equal("Weak Prisoner", monster.FullName);
        Assert.Equal(1, monster.Fields["Family"]);
        Assert.Equal(30, monster.Fields["HitPoints"]);
        Assert.Equal(40, monster.Fields["DropChance"]);
        Assert.Equal(1, monster.Fields["LootRolls"]);

        // The spell school is a skill index resolved through charin.dat: five spells per school in
        // school order, so spell 1 is an Alteration spell.
        var spell = Row(records, StormholdRecordSource.SpellRecordType, 0x4005_0000u);
        Assert.Equal("Frenzy", spell.FullName);
        Assert.Equal("Alteration", spell.Fields["School"]);

        var barbarian = Row(records, StormholdRecordSource.ClassRecordType, 0x4001_0000u);
        Assert.Equal("Barbarian", barbarian.FullName);
        Assert.Equal("Nord", barbarian.Fields["DefaultRace"]);

        // The 5 x 7 sprite grid, the last family using 5 of its 7 slots (33 non-empty of 35).
        var families = records
            .Where(r => r.RecordType == StormholdRecordSource.SpriteRecordType)
            .OrderBy(r => r.FormId)
            .Select(r => (int)r.Fields["PartsUsed"]!)
            .ToList();
        Assert.Equal(new[] { 7, 7, 7, 7, 5 }, families);

        // npcstrings.dat: 8 groups sized 20,20,20,20,5,22,5,41, in the SECOND reserved domain.
        var strings = records.Where(r => r.RecordType == StormholdRecordSource.StringRecordType).ToList();
        Assert.All(strings, r => Assert.Equal(
            (byte)(StormholdRecordSource.FirstDomain + 1), ClassicFormIdScheme.DomainOf(r.FormId)));
        Assert.Equal(
            new[] { 20, 20, 20, 20, 5, 22, 5, 41 },
            strings.GroupBy(r => (int)r.Fields["Group"]!).OrderBy(g => g.Key).Select(g => g.Count()));
    }

    [Fact]
    public async Task TheDawnstarJarSynthesizesItsMeasuredCensusThroughTheLump()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        var records = await LoadAsync(
            Require(RealAssetPaths.Travels.DawnstarJar(), "Dawnstar JAR"), BethesdaGame.Dawnstar);

        // Reaching this census at all proves the lump fallback works: the Dawnstar JAR serves
        // datfiles.lmp and NOT its members, so a source reading only the mounted paths would find
        // one table — the loose npcstrings.dat — and stop at 167 records.
        Assert.Equal(482, records.Count);
        Assert.Equal(
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [DawnstarRecordSource.ClassRecordType] = 7,
                [DawnstarRecordSource.RaceRecordType] = 6,
                [DawnstarRecordSource.SkillRecordType] = 14,
                [DawnstarRecordSource.ItemRecordType] = 101,
                [DawnstarRecordSource.SpellRecordType] = 25,
                [DawnstarRecordSource.MonsterRecordType] = 42,
                [DawnstarRecordSource.SpriteRecordType] = 5,
                [DawnstarRecordSource.DropRecordType] = 43,
                [DawnstarRecordSource.DungeonRecordType] = 37,
                [DawnstarRecordSource.HelpRecordType] = 35,
                [DawnstarRecordSource.StringRecordType] = 167
            },
            Census(records));

        Assert.Equal(records.Count, records.Select(r => r.FormId).Distinct().Count());
        Assert.All(records, r => Assert.InRange(
            ClassicFormIdScheme.DomainOf(r.FormId),
            DawnstarRecordSource.FirstDomain,
            DawnstarRecordSource.LastDomain));

        var item = Row(records, DawnstarRecordSource.ItemRecordType, 0x4404_0000u);
        Assert.Equal("Hatchet", item.FullName);
        Assert.Equal("Axe", item.Fields["Type"]);
        Assert.Contains(
            records.Where(r => r.RecordType == DawnstarRecordSource.ItemRecordType),
            r => (string?)r.Fields["Type"] == "Magic Item");

        var monster = Row(records, DawnstarRecordSource.MonsterRecordType, 0x4406_0000u);
        Assert.Equal("Sickly Bandit", monster.FullName);

        var help = Row(records, DawnstarRecordSource.HelpRecordType, 0x4409_0000u);
        Assert.Equal("Goal", help.Fields["Text"]);

        // Dawnstar ships no dungnamesin.dat, so every dungeon here is genuinely nameless.
        var dungeons = records.Where(r => r.RecordType == DawnstarRecordSource.DungeonRecordType).ToList();
        Assert.All(dungeons, r => Assert.Null(r.FullName));
        Assert.All(dungeons, r => Assert.DoesNotContain("NameLine1", r.Fields.Keys, StringComparer.Ordinal));

        // Sprite families: the .png sets, 26 non-empty parts of the 35 slots.
        var families = records
            .Where(r => r.RecordType == DawnstarRecordSource.SpriteRecordType)
            .OrderBy(r => r.FormId)
            .Select(r => (int)r.Fields["PartsUsed"]!)
            .ToList();
        Assert.Equal(new[] { 7, 7, 6, 3, 3 }, families);
        Assert.Equal(26, families.Sum());

        var strings = records.Where(r => r.RecordType == DawnstarRecordSource.StringRecordType).ToList();
        Assert.Equal(
            new[] { 3, 3, 3, 3, 14, 16, 16, 16, 16, 77 },
            strings.GroupBy(r => (int)r.Fields["Group"]!).OrderBy(g => g.Key).Select(g => g.Count()));
    }

    [Fact]
    public async Task TheTablesThatAreByteIdenticalBetweenTheGamesProduceIdenticalRecords()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        var stormhold = await LoadAsync(
            Require(RealAssetPaths.Travels.StormholdJar(), "Stormhold JAR"), BethesdaGame.Stormhold);
        var dawnstar = await LoadAsync(
            Require(RealAssetPaths.Travels.DawnstarJar(), "Dawnstar JAR"), BethesdaGame.Dawnstar);

        // spellsin.dat, droppeditemsin.dat and geomin.dat are byte-identical across the two games,
        // and charin.dat's name lists and class rows are too — so a difference reported here is a
        // pipeline bug, not content. The item names the loot cells resolve to are NOT shared (the
        // item tables differ), which is why only the numeric loot columns are compared.
        AssertSameFields(
            stormhold, StormholdRecordSource.SpellRecordType,
            dawnstar, DawnstarRecordSource.SpellRecordType,
            [
                "Id", "SchoolSkillIndex", "School", "Cost", "Duration", "Target", "BaseChance",
                "RankRequired", "Description"
            ]);

        AssertSameFields(
            stormhold, StormholdRecordSource.DropRecordType,
            dawnstar, DawnstarRecordSource.DropRecordType,
            ["Index", "MagicItem", "ScrollItem", "ScrollSpell", "ArmourItem", "WeaponItem"]);

        AssertSameFields(
            stormhold, StormholdRecordSource.DungeonRecordType,
            dawnstar, DawnstarRecordSource.DungeonRecordType,
            ["Id", "North", "East", "South", "West", "ExitDirection", "ReturnDirection"]);

        AssertSameFields(
            stormhold, StormholdRecordSource.ClassRecordType,
            dawnstar, DawnstarRecordSource.ClassRecordType,
            ["Index", "DefaultRaceIndex", "DefaultRace", "MagickaMultiplier", "Field11", "Field12"]);

        // The ONE measured content difference in charin.dat: two governing-attribute entries,
        // skills 1 and 10 (Stormhold 4 where Dawnstar has 2).
        var differing = Governing(stormhold, StormholdRecordSource.SkillRecordType)
            .Zip(Governing(dawnstar, DawnstarRecordSource.SkillRecordType), (s, d) => s == d)
            .Select((same, index) => (Same: same, Index: index))
            .Where(pair => !pair.Same)
            .Select(pair => pair.Index)
            .ToList();
        Assert.Equal(new[] { 1, 10 }, differing);
    }

    private static List<int> Governing(IEnumerable<GenericEsmRecord> records, string recordType)
    {
        return records
            .Where(r => r.RecordType == recordType)
            .OrderBy(r => r.FormId)
            .Select(r => (int)r.Fields["GoverningAttributeIndex"]!)
            .ToList();
    }

    private static void AssertSameFields(
        IEnumerable<GenericEsmRecord> left,
        string leftType,
        IEnumerable<GenericEsmRecord> right,
        string rightType,
        IReadOnlyList<string> keys)
    {
        var leftRows = left.Where(r => r.RecordType == leftType).OrderBy(r => r.FormId).ToList();
        var rightRows = right.Where(r => r.RecordType == rightType).OrderBy(r => r.FormId).ToList();
        Assert.Equal(leftRows.Count, rightRows.Count);
        Assert.NotEmpty(leftRows);

        for (var i = 0; i < leftRows.Count; i++)
        {
            // The low 24 bits must match too: the same row keeps the same composite index in both
            // games, which is what lets a cross-game diff line the rows up at all.
            Assert.Equal(
                ClassicFormIdScheme.IndexOf(leftRows[i].FormId),
                ClassicFormIdScheme.IndexOf(rightRows[i].FormId));

            foreach (var key in keys)
            {
                Assert.Equal(leftRows[i].Fields[key], rightRows[i].Fields[key]);
            }
        }
    }
}
