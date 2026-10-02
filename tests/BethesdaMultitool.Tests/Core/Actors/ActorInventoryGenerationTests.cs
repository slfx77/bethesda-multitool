using System.Text.Json;
using BethesdaMultitool.Core.Actors;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Actors;

/// <summary>Checks repeatable generation, ownership, explicit uncertainty and adversarial bounds with synthetic records.</summary>
public sealed class ActorInventoryGenerationTests
{
    private static readonly int[] ExpectedSeedSevenDraws = [2, 0, 1, 2, 0, 0];
    private static readonly uint[] ExpectedSeedSevenItems = [203, 201, 202, 203, 201, 201];
    private static readonly uint[] ExpectedUseAllItems = [201, 202, 203];
    private static readonly int[] ExpectedUseAllCounts = [2, 2, 6];

    /// <summary>Known SHA-256 draws computed independently with Python pin the portable seed/counter encoding.</summary>
    [Fact]
    public void RandomDrawsMatchIndependentEncoding()
    {
        var random = new ActorInventoryRandom(7);
        Assert.Equal(ExpectedSeedSevenDraws, Enumerable.Range(0, 6).Select(_ => random.Next(3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => random.Next(0));
        Assert.True(new ActorInventoryRandom(1).ChanceNone(50));
        Assert.False(new ActorInventoryRandom(0).ChanceNone(50));
    }

    /// <summary>Each-item generation produces independently chosen leaves in authored order for the same seed.</summary>
    [Fact]
    public void EachItemUsesRepeatableConcreteChoices()
    {
        var inspector = new ActorInspector(Records(flags: 3, count: 6));
        var first = inspector.Inspect(1, false, 5, seed: 7, cancellationToken: TestContext.Current.CancellationToken)!;
        var second = inspector.Inspect(1, false, 5, seed: 7, cancellationToken: TestContext.Current.CancellationToken)!;
        var generation = Assert.IsType<ActorInventoryGeneration>(first.Generation);
        Assert.True(generation.Complete);
        Assert.Equal((uint)7, generation.Seed);
        Assert.Equal(ExpectedSeedSevenItems, generation.Items.Select(item => item.ItemFormId));
        Assert.All(generation.Items, item => Assert.Equal(1, item.Count));
        Assert.Equal(generation.Items, second.Generation!.Items);
        Assert.NotEqual(generation.Items.Select(item => item.ItemFormId),
            inspector.Inspect(1, false, 5, seed: 0, cancellationToken: TestContext.Current.CancellationToken)!.Generation!.Items.Select(item => item.ItemFormId));
        Assert.Null(inspector.Inspect(1, false, 5, cancellationToken: TestContext.Current.CancellationToken)!.Generation);
    }

    /// <summary>Without the each-item flag one selected entry supplies the complete parent count.</summary>
    [Fact]
    public void OneRollMultipliesCountAndRetainsOwnership()
    {
        var records = Records(flags: 1, count: 6);
        records.Npcs[0].Inventory[0] = new InventoryItem(100, 6)
            { OwnerFormId = 9, GlobalOrRank = 4, ItemCondition = 0.25f };
        var detail = new ActorInspector(records).Inspect(1, false, 5, seed: 7, cancellationToken: TestContext.Current.CancellationToken)!;
        var item = Assert.Single(detail.Generation!.Items);
        Assert.Equal((uint)203, item.ItemFormId);
        Assert.Equal(6, item.Count);
        Assert.Equal((uint)9, item.OwnerFormId);
        Assert.Equal((uint)4, item.GlobalOrRank);
        Assert.Equal(0.25f, item.ItemCondition);
        Assert.Contains(detail.Inventory, row => row.Status == "Generated" && row.SourceActor == 1);
    }

    /// <summary>Use All overrides the level and other flag choices while preserving each entry's count.</summary>
    [Fact]
    public void UseAllIncludesHigherLevelsAndOverridesEachItem()
    {
        var records = Records(flags: 7, count: 2);
        records.LeveledLists[0].Entries[2] = new LeveledEntry(100, 203, 3);
        var generated = new ActorInspector(records).Inspect(1, false, 1, seed: 7, cancellationToken: TestContext.Current.CancellationToken)!.Generation!;
        Assert.True(generated.Complete);
        Assert.Equal(ExpectedUseAllItems, generated.Items.Select(item => item.ItemFormId));
        Assert.Equal(ExpectedUseAllCounts, generated.Items.Select(item => item.Count));
    }

    /// <summary>The same tier selection serves candidate inspection and seeded generation.</summary>
    [Fact]
    public void HighestTierMatchesCandidateEligibility()
    {
        var records = Records(flags: 0, count: 1);
        records.LeveledLists[0].Entries[0] = new LeveledEntry(1, 201, 1);
        records.LeveledLists[0].Entries[1] = new LeveledEntry(5, 202, 2);
        records.LeveledLists[0].Entries[2] = new LeveledEntry(8, 203, 1);
        var inspector = new ActorInspector(records);
        Assert.Equal((uint)202, Assert.Single(inspector.Inspect(1, false, 5, cancellationToken: TestContext.Current.CancellationToken)!.Inventory, row => row.Status == "Candidate").ItemFormId);
        var generated = Assert.Single(inspector.Inspect(1, false, 5, seed: 42, cancellationToken: TestContext.Current.CancellationToken)!.Generation!.Items);
        Assert.Equal(new InventoryItem(202, 2), generated);
    }

    /// <summary>Explicit chance endpoints produce a complete empty inventory or the authored selection.</summary>
    [Fact]
    public void ChanceNoneAndGlobalSnapshotAreExplicit()
    {
        var records = Records(flags: 0, count: 1);
        records.LeveledLists[0] = records.LeveledLists[0] with { ChanceNone = 100 };
        var none = new ActorInspector(records).Inspect(1, false, 5, seed: 7, cancellationToken: TestContext.Current.CancellationToken)!;
        Assert.True(none.Generation!.Complete);
        Assert.Empty(none.Generation.Items);
        Assert.Contains(none.Inventory, row => row.Status == "ChanceNone");
        records.LeveledLists[0] = records.LeveledLists[0] with { GlobalFormId = 9 };
        var missing = new ActorInspector(records).Inspect(1, false, 5, seed: 7, cancellationToken: TestContext.Current.CancellationToken)!;
        Assert.False(missing.Generation!.Complete);
        Assert.Equal("GlobalUnresolved", missing.InventoryNotice);
        records.Globals.Add(new GlobalRecord { FormId = 9, Value = 100 });
        records.Globals.Add(new GlobalRecord { FormId = 9, Value = 0 });
        var actual = new ActorInspector(records).Inspect(1, false, 5, seed: 7, cancellationToken: TestContext.Current.CancellationToken)!;
        Assert.True(actual.Generation!.Complete);
        Assert.Single(actual.Generation.Items);
    }

    /// <summary>Malformed global chances never fall back to an unrelated literal probability.</summary>
    [Theory]
    [InlineData(-1f)]
    [InlineData(101f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidGlobalChanceRemainsUnresolved(float value)
    {
        var records = Records(flags: 0, count: 1);
        records.LeveledLists[0] = records.LeveledLists[0] with { GlobalFormId = 9 };
        records.Globals.Add(new GlobalRecord { FormId = 9, Value = value });
        var result = new ActorInspector(records).Inspect(1, false, 5, seed: 7, cancellationToken: TestContext.Current.CancellationToken)!;
        Assert.False(result.Generation!.Complete);
        Assert.Empty(result.Generation.Items);
        Assert.Equal("InvalidChance", result.InventoryNotice);
    }

    /// <summary>A cycle and missing record remain visible while an independent concrete sibling survives.</summary>
    [Fact]
    public void FailedBranchesPreserveSuccessfulSiblings()
    {
        var records = Records(flags: 4, count: 1);
        records.LeveledLists[0].Entries[0] = new LeveledEntry(1, 100, 1);
        records.LeveledLists[0].Entries[1] = new LeveledEntry(1, 999, 1);
        var result = new ActorInspector(records).Inspect(1, false, 1, seed: 7, cancellationToken: TestContext.Current.CancellationToken)!;
        Assert.False(result.Generation!.Complete);
        Assert.Equal((uint)203, Assert.Single(result.Generation.Items).ItemFormId);
        Assert.Contains(result.Inventory, row => row.Status == "Cycle");
        Assert.Contains(result.Inventory, row => row.Status == "Unresolved" && row.ItemFormId == 999);
    }

    /// <summary>Only explicit template inheritance supplies inventory and reports its original actor.</summary>
    [Fact]
    public void InheritedInventoryRetainsSourceActor()
    {
        var records = Records(flags: 0, count: 1);
        records.Npcs.Add(records.Npcs[0] with { FormId = 2 });
        records.Npcs[0] = new NpcRecord { FormId = 1, Template = 2,
            Stats = new ActorBaseSubrecord(0, 0, 0, 1, 1, 1, 100, 0, 0, 0x100, 0, false) };
        var result = new ActorInspector(records).Inspect(1, false, 5, seed: 7, cancellationToken: TestContext.Current.CancellationToken)!;
        Assert.All(result.Inventory, row => Assert.Equal((uint)2, row.SourceActor));
        Assert.Single(result.Generation!.Items);
    }

    /// <summary>Template failures retain an incomplete generation even when no concrete branch is visited.</summary>
    /// <param name="template">A cyclic or missing template identity.</param>
    /// <param name="expectedNotice">The independently expected template failure reason.</param>
    [Theory]
    [InlineData(1u, "TemplateCycle")]
    [InlineData(99u, "TemplateUnresolved")]
    public void TemplateFailureKeepsEmptyGenerationIncomplete(uint template, string expectedNotice)
    {
        var records = new RecordCollection
        {
            Npcs = [new NpcRecord { FormId = 1, Template = template,
                Stats = new ActorBaseSubrecord(0, 0, 0, 1, 1, 1, 100, 0, 0, 0x100, 0, false) }]
        };
        var result = new ActorInspector(records).Inspect(1, false, 5, seed: 7,
            cancellationToken: TestContext.Current.CancellationToken)!;
        var generation = Assert.IsType<ActorInventoryGeneration>(result.Generation);
        Assert.False(generation.Complete);
        Assert.Empty(generation.Items);
        Assert.Equal(expectedNotice, result.InventoryNotice);
        Assert.Contains(expectedNotice, generation.Notices);
    }

    /// <summary>Very large item counts stop with explicit truncation instead of allocating one row per unit.</summary>
    [Fact]
    public void WorkAndOutputAreBounded()
    {
        var result = new ActorInspector(Records(flags: 3, count: int.MaxValue)).Inspect(1, false, 5, seed: 7, cancellationToken: TestContext.Current.CancellationToken)!;
        Assert.False(result.Generation!.Complete);
        Assert.Equal("Truncated", result.InventoryNotice);
        Assert.Equal(ActorInventoryGenerator.MaximumRows, result.Inventory.Count);
        Assert.InRange(result.Generation.Items.Count, 1, ActorInventoryGenerator.MaximumRows - 1);
    }

    /// <summary>Overflowed concrete counts are omitted without suppressing ordinary sibling items.</summary>
    [Fact]
    public void CountOverflowIsVisible()
    {
        var records = Records(flags: 4, count: int.MaxValue);
        records.LeveledLists[0].Entries[0] = new LeveledEntry(1, 201, 2);
        var result = new ActorInspector(records).Inspect(1, false, 5, seed: 7, cancellationToken: TestContext.Current.CancellationToken)!;
        Assert.False(result.Generation!.Complete);
        Assert.Contains(result.Inventory, row => row.Status == "CountOverflow");
        Assert.Equal(2, result.Generation.Items.Count);
    }

    /// <summary>Seeded requests require a nonzero explicit level and observe cancellation before scanning.</summary>
    [Fact]
    public void SeedRequiresLevelAndHonorsCancellation()
    {
        var inspector = new ActorInspector(Records(flags: 0, count: 1));
        Assert.Throws<ArgumentException>(() => inspector.Inspect(1, false, seed: 7, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => inspector.Inspect(1, false, 0, seed: 7, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<OperationCanceledException>(() => inspector.Inspect(1, false, 5, seed: 7,
            cancellationToken: new CancellationToken(true)));
    }

    /// <summary>The optional generation section is invariant and the old unseeded JSON contract remains unchanged.</summary>
    [Fact]
    public void JsonSeparatesPreviewItemsFromAuthoredCandidates()
    {
        var inspector = new ActorInspector(Records(flags: 0, count: 1));
        using var before = new MemoryStream();
        ActorInspectionJsonWriter.Write(before, inspector.Inspect(1, false, 5, cancellationToken: TestContext.Current.CancellationToken)!, "fixture.esm", 5);
        using var oldJson = JsonDocument.Parse(before.ToArray());
        Assert.Equal("authored-and-eligible-candidates", oldJson.RootElement.GetProperty("inventoryMode").GetString());
        Assert.False(oldJson.RootElement.TryGetProperty("generation", out _));
        using var after = new MemoryStream();
        ActorInspectionJsonWriter.Write(after, inspector.Inspect(1, false, 5, seed: uint.MaxValue, cancellationToken: TestContext.Current.CancellationToken)!, "fixture.esm", 5);
        using var json = JsonDocument.Parse(after.ToArray());
        Assert.Equal("seeded-preview", json.RootElement.GetProperty("inventoryMode").GetString());
        var generation = json.RootElement.GetProperty("generation");
        Assert.Equal(uint.MaxValue, generation.GetProperty("seed").GetUInt32());
        Assert.Equal(ActorInventoryGeneration.Algorithm, generation.GetProperty("algorithm").GetString());
        Assert.True(generation.GetProperty("complete").GetBoolean());
        Assert.Single(generation.GetProperty("items").EnumerateArray());
        Assert.Contains(generation.GetProperty("assumptions").EnumerateArray(), item => item.GetString() == "no-scripts-or-runtime-rng");
    }

    /// <summary>Builds one three-choice list and typed concrete items without consulting private corpora.</summary>
    private static RecordCollection Records(byte flags, int count) => new()
    {
        Npcs = [new NpcRecord { FormId = 1, Inventory = [new InventoryItem(100, count)] }],
        MiscItems = [new MiscItemRecord { FormId = 201 }, new MiscItemRecord { FormId = 202 }, new MiscItemRecord { FormId = 203 }],
        LeveledLists = [new LeveledListRecord { FormId = 100, Flags = flags,
            Entries = [new LeveledEntry(1, 201, 1), new LeveledEntry(1, 202, 1), new LeveledEntry(1, 203, 1)] }]
    };
}
