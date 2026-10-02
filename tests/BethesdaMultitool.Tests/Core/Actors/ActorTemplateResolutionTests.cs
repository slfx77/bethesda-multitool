using System.Text.Json;
using BethesdaMultitool.Core.Actors;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Actors;

/// <summary>Discriminating template cases: local leftovers, independent group chains and unavailable inputs.</summary>
public sealed class ActorTemplateResolutionTests
{
    [Fact]
    public void Creature_InventoryUsesFlagBeforeLeftoverLocalItems_AndRetainsSource()
    {
        var detail = Inspect(Rats(0x03FD), 1);
        var row = Assert.Single(detail.Inventory);
        Assert.Equal(900u, row.ItemFormId);
        Assert.Equal(3u, row.SourceActor);
        Assert.Equal("Inherited", row.Status);
        Assert.Null(detail.InventoryNotice);
        Assert.Equal([1u, 2u, 3u], detail.TemplateChain.Select(h => h.FormId).ToArray());
        Assert.Equal("675", Assert.Single(detail.EffectiveStatistics, s => s.Key == "Health").Value);
        Assert.Contains(detail.Statistics, s => s.Key == "Luck" && s.Value == "1");
    }

    [Theory]
    [InlineData(0x03FD, "675", "Authored", 1u)]
    [InlineData(0x03FF, "75", "Inherited", 2u)]
    public void Creature_HealthFollowsItsOwnGroup_StoppingBeforeInventoryTemplate(
        int flags, string health, string provenance, uint source)
    {
        var detail = Inspect(Rats((ushort)flags), 1);
        var value = Assert.Single(detail.EffectiveStatistics, s => s.Key == "Health");
        Assert.Equal(health, value.Value);
        Assert.Equal(provenance, value.Provenance);
        Assert.Equal(source, value.SourceActor);
        Assert.Contains(detail.Statistics, s => s.Key == "Health" && s.Value == "675");
        Assert.All(detail.Calculated, s => Assert.Equal("NotComputed", s.Status));
    }

    [Fact]
    public void UnusedTemplate_WithNoFlags_HasNoInventoryWarning()
    {
        var detail = Inspect(new RecordCollection { Creatures =
            [new CreatureRecord { FormId = 1, Template = 999, Stats = Stats(0), Health = 0 }] }, 1);
        Assert.Null(detail.InventoryNotice);
        Assert.All(detail.TemplateGroups, group => Assert.Equal("Authored", group.Status));
        Assert.Equal("0", Assert.Single(detail.EffectiveStatistics, s => s.Key == "Health").Value);
    }

    [Fact]
    public void Npc_UseInventory_ReplacesLocalContainer_ForInspectionAndSeededGeneration()
    {
        var records = new RecordCollection { Npcs =
        [
            new NpcRecord { FormId = 1, Template = 2, Stats = Stats(0x100), Inventory = [new(500, 1)] },
            new NpcRecord { FormId = 2, Inventory = [new(600, 2)] }
        ], MiscItems = [new() { FormId = 500 }, new() { FormId = 600 }] };
        var inspector = new ActorInspector(records);
        var ordinary = inspector.Inspect(1, false)!;
        Assert.Equal(600u, Assert.Single(ordinary.Inventory).ItemFormId);
        Assert.Equal(2u, ordinary.Inventory[0].SourceActor);
        var seeded = inspector.Inspect(1, false, 10, 42, TestContext.Current.CancellationToken)!;
        Assert.Equal(600u, Assert.Single(seeded.Generation!.Items).ItemFormId);
        Assert.DoesNotContain(seeded.Inventory, row => row.ItemFormId == 500);
    }

    [Fact]
    public void LeveledTemplate_ProvidesEligibleCandidatesButNeverAnEffectiveHealth()
    {
        var records = new RecordCollection
        {
            Creatures = [new CreatureRecord { FormId = 1, Template = 10, Stats = Stats(2), Health = 999 }],
            LeveledLists = [new LeveledListRecord { FormId = 10, ListType = "LVLC",
                Entries = [new(1, 2, 1), new(10, 3, 1), new(20, 4, 1)] }]
        };
        var inspector = new ActorInspector(records);
        var all = inspector.Inspect(1, true)!;
        Assert.Equal(3, StatsGroup(all).Candidates.Count);
        var filtered = inspector.Inspect(1, true, 12)!;
        Assert.Equal("LeveledTemplate", StatsGroup(filtered).Status);
        Assert.Equal(3u, Assert.Single(StatsGroup(filtered).Candidates).FormId);
        var health = Assert.Single(filtered.EffectiveStatistics, s => s.Key == "Health");
        Assert.Null(health.Value);
        Assert.Equal("Unresolved", health.Provenance);
    }

    [Theory]
    [InlineData(0x00001000u, false, false, "MasterNotLoaded")]
    [InlineData(0x00001000u, true, false, "TemplateMissing")]
    [InlineData(0x01FFFFFFu, false, false, "TemplateMissing")]
    [InlineData(0x00001000u, false, true, "NotInCapture")]
    public void AbsentTemplates_AreClassifiedBySourceCoverage(uint template, bool loaded, bool partial, string expected)
    {
        var records = new RecordCollection { Creatures =
            [new CreatureRecord { FormId = 0x01005000, Template = template, Stats = Stats(2) }] };
        var identity = new ActorSourceIdentity { PrimaryFileName = "Dlc.esm", IsPartialCapture = partial,
            Masters = [new("FalloutNV.esm", loaded)] };
        var result = new ActorInspector(records, identity).Inspect(0x01005000, true)!;
        Assert.Equal(expected, StatsGroup(result).Status);
        if (expected == "MasterNotLoaded") Assert.Equal("FalloutNV.esm", StatsGroup(result).SourcePlugin);
        if (partial) Assert.Contains("partial capture", StatsGroup(result).Detail!);
    }

    [Fact]
    public void Cycles_WrongActorFamily_AndNonActorTemplatesAreDistinct()
    {
        var records = new RecordCollection
        {
            Creatures =
            [
                new CreatureRecord { FormId = 1, Template = 2, Stats = Stats(2) },
                new CreatureRecord { FormId = 2, Template = 1, Stats = Stats(2) },
                new CreatureRecord { FormId = 3, Template = 4, Stats = Stats(2) },
                new CreatureRecord { FormId = 5, Template = 6, Stats = Stats(2) }
            ],
            Npcs = [new NpcRecord { FormId = 4 }],
            Weapons = [new() { FormId = 6 }]
        };
        Assert.Equal("TemplateCycle", StatsGroup(Inspect(records, 1)).Status);
        Assert.Equal("TemplateWrongType", StatsGroup(Inspect(records, 3)).Status);
        Assert.Equal("TemplateWrongType", StatsGroup(Inspect(records, 5)).Status);
    }

    [Fact]
    public void MissingAcbsAndHealth_AreNeverFilledFromGuesses()
    {
        var records = new RecordCollection { Creatures =
        [new CreatureRecord { FormId = 1, Template = 2 }, new CreatureRecord { FormId = 2, Health = 675 }] };
        var detail = Inspect(records, 1);
        Assert.Equal("TemplateFlagsMissing", StatsGroup(detail).Status);
        Assert.Null(Assert.Single(detail.EffectiveStatistics, s => s.Key == "Health").Value);
        Assert.DoesNotContain(detail.Statistics, s => s.Key == "Health");
    }

    [Theory]
    [InlineData(1500, "1.5")]
    [InlineData(0, "0")]
    [InlineData(-1500, "-1.5")]
    public void PlayerLevelMultiplier_PreservesSignedStoredEncodingWithoutCalculatingSpawnLevel(short encoded, string multiplier)
    {
        var detail = Inspect(new RecordCollection { Creatures =
            [new CreatureRecord { FormId = 1, Stats = Stats(0) with { Flags = 0x80, Level = encoded } }] }, 1);
        Assert.Contains(detail.Statistics, s => s.Key == "LevelMultiplier" && s.Value == multiplier);
        Assert.Contains(detail.Statistics, s => s.Key == "LevelEncoded" && s.Value == encoded.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.DoesNotContain(detail.Statistics, s => s.Key == "Level");
        Assert.Contains(detail.Calculated, s => s.Key == "EffectiveLevel" && s.Status == "NotComputed");
    }

    [Fact]
    public void Json_PreservesLegacyFieldsAndAddsExplicitStaticProvenance()
    {
        var detail = Inspect(Rats(0x03FD), 1);
        using var stream = new MemoryStream();
        ActorInspectionJsonWriter.Write(stream, detail, "test.esm", null);
        using var doc = JsonDocument.Parse(stream.ToArray());
        var root = doc.RootElement;
        Assert.Equal(3, root.GetProperty("templateChain").GetArrayLength());
        Assert.False(root.TryGetProperty("inventoryNotice", out _));
        var health = Assert.Single(root.GetProperty("effectiveStatistics").EnumerateArray(),
            e => e.GetProperty("key").GetString() == "Health");
        Assert.Equal("675", health.GetProperty("value").GetString());
        Assert.Equal("Authored", health.GetProperty("provenance").GetString());
        Assert.Equal(1u, health.GetProperty("sourceActor").GetUInt32());
        Assert.True(root.GetProperty("statistics").GetArrayLength() > 0);
    }

    [Fact]
    public void KnownOtherGames_DoNotApplyFalloutTemplateFlagsOrLevelInterpretation()
    {
        var records = Rats(0x03FF) with { Game = BethesdaGame.Skyrim };
        records.Creatures[0] = records.Creatures[0] with
        {
            Stats = Stats(0x03FF) with { Flags = 0x80, Level = 1500 }, Inventory = [new(500, 1)]
        };
        var detail = Inspect(records, 1);
        Assert.Equal("UnsupportedGame", detail.TemplateSemantics);
        Assert.All(detail.TemplateGroups, g => Assert.Equal("UnsupportedGame", g.Status));
        Assert.All(detail.EffectiveStatistics, s => Assert.Null(s.Value));
        Assert.Equal(500u, Assert.Single(detail.Inventory).ItemFormId);
        Assert.Contains(detail.Statistics, s => s.Key == "Level" && s.Value == "1500");
        Assert.DoesNotContain(detail.Statistics, s => s.Key == "LevelMultiplier");
    }

    [Theory]
    [InlineData("TemplateDeleted")]
    [InlineData("TemplateTypeConflict")]
    [InlineData("TemplateNotParsed")]
    public void ExistingButUnusableTemplate_IsNeverMisreportedAsAbsent(string status)
    {
        var records = new RecordCollection
        {
            Creatures = [new CreatureRecord { FormId = 1, Stats = Stats(2), Template = 2 }],
            // A deleted or conflicting winner can still have a name in a merged dictionary.
            FormIdToEditorId = new() { [2] = "KnownButUnusable" }
        };
        var identity = new ActorSourceIdentity { ResolveMissingReason = _ => status };
        var actor = new ActorInspector(records, identity).Inspect(1, true)!;
        Assert.Equal(status, StatsGroup(actor).Status);
        Assert.Null(Assert.Single(actor.EffectiveStatistics, s => s.Key == "Health").Value);
    }

    private static ActorInspection Inspect(RecordCollection records, uint id) => new ActorInspector(records)
        .Inspect(id, true, cancellationToken: TestContext.Current.CancellationToken)!;
    private static ActorTemplateGroupResolution StatsGroup(ActorInspection actor) =>
        Assert.Single(actor.TemplateGroups, g => g.Group == ActorTemplateGroup.UseStats);
    private static ActorBaseSubrecord Stats(ushort flags) => new(0, 0, 0, 1, 1, 1, 100, 0, 0, flags, 0, false);
    private static RecordCollection Rats(ushort flags) => new() { Creatures =
    [
        new CreatureRecord { FormId = 1, Template = 2, Stats = Stats(flags), Health = 675,
            AttackDamage = 15, Attributes = [2, 4, 3, 1, 2, 4, 1] },
        new CreatureRecord { FormId = 2, Template = 3, Stats = Stats(0x100), Health = 75,
            Inventory = [new InventoryItem(800, 99)] },
        new CreatureRecord { FormId = 3, Stats = Stats(0), Health = 24, Inventory = [new InventoryItem(900, 1)] }
    ] };
}
