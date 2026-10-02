using BethesdaMultitool.Core.Actors;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Actors;

/// <summary>Verifies inventory provenance, level eligibility, and bounds without requiring game assets.</summary>
public sealed class ActorInspectorTests
{
    /// <summary>Missing statistics stay absent while explicit zero values remain visible.</summary>
    [Fact]
    public void Statistics_PreservesMissingAndExplicitZero()
    {
        var inspector = new ActorInspector(new RecordCollection
        {
            Npcs = [new NpcRecord { FormId = 1, BaseHealth = 0, SpecialStats = [1, 2, 3, 4, 5, 6, 7] }]
        });
        var detail = inspector.Inspect(1, false, cancellationToken: TestContext.Current.CancellationToken)!;
        Assert.Contains(detail.Statistics, value => value.Key == "BaseHealth" && value.Value == "0");
        Assert.Contains(detail.Statistics, value => value.Key == "Luck" && value.Value == "7");
        Assert.DoesNotContain(detail.Statistics, value => value.Key == "Level");
        Assert.Null(inspector.Inspect(2, false, cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>Leveled declarations remain unresolved when the user supplied no preview level.</summary>
    [Fact]
    public void Inventory_RequiresExplicitLevel()
    {
        var detail = Inspector(0).Inspect(1, false, cancellationToken: TestContext.Current.CancellationToken)!;
        Assert.Single(detail.Inventory);
        Assert.Equal("LevelRequired", detail.InventoryNotice);
        Assert.Equal((byte)25, detail.Inventory[0].ChanceNone);
        Assert.Equal((uint)400, detail.Inventory[0].GlobalFormId);
    }

    /// <summary>The highest eligible tier is used unless all-level eligibility is explicitly enabled.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    public void Inventory_RespectsLevelTierFlag(byte flags, int candidateCount)
    {
        var detail = Inspector(flags).Inspect(1, false, 5, cancellationToken: TestContext.Current.CancellationToken)!;
        var candidates = detail.Inventory.Where(item => item.Status == "Candidate").ToArray();
        Assert.Equal(candidateCount, candidates.Length);
        Assert.Contains(candidates, item => item.ItemFormId == 202 && item.Count == 6);
        Assert.DoesNotContain(candidates, item => item.ItemFormId == 203);
        Assert.All(candidates, item => Assert.Equal((uint)1, item.SourceActor));
    }

    /// <summary>Inherited inventory reports the originating actor and retains ownership data.</summary>
    [Fact]
    public void Inventory_PreservesTemplateAndOwnership()
    {
        var child = new NpcRecord { FormId = 1, Template = 2, Stats = Stats(0x100) };
        var source = new NpcRecord { FormId = 2, Inventory = [new InventoryItem(200, 3) { OwnerFormId = 9, ItemCondition = 0.5f }] };
        var inspector = new ActorInspector(new RecordCollection { Npcs = [child, source] });
        var row = Assert.Single(inspector.Inspect(1, false, cancellationToken: TestContext.Current.CancellationToken)!.Inventory);
        Assert.Equal("Inherited", row.Status);
        Assert.Equal((uint)2, row.SourceActor);
        Assert.Equal((uint)9, row.OwnerFormId);
        Assert.Equal(0.5f, row.ItemCondition);
    }

    /// <summary>Inventory inheritance cannot loop indefinitely or silently select a leveled actor template.</summary>
    [Fact]
    public void Inventory_ReportsTemplateCycleAndMissingTemplate()
    {
        var inspector = new ActorInspector(new RecordCollection { Npcs =
        [new NpcRecord { FormId = 1, Template = 1, Stats = Stats(0x100) },
         new NpcRecord { FormId = 2, Template = 99, Stats = Stats(0x100) }] });
        Assert.Equal("TemplateCycle", inspector.Inspect(1, false, cancellationToken: TestContext.Current.CancellationToken)!.InventoryNotice);
        Assert.Equal("TemplateUnresolved", inspector.Inspect(2, false, cancellationToken: TestContext.Current.CancellationToken)!.InventoryNotice);
    }

    /// <summary>Nested cycles terminate but do not suppress independent sibling candidates.</summary>
    [Fact]
    public void Inventory_BoundsCyclesWithoutLosingSibling()
    {
        var inspector = new ActorInspector(new RecordCollection
        {
            Npcs = [new NpcRecord { FormId = 1, Inventory = [new InventoryItem(100, 1)] }],
            LeveledLists = [new LeveledListRecord { FormId = 100, Flags = 1,
                Entries = [new LeveledEntry(1, 100, 1), new LeveledEntry(1, 200, 1)] }]
        });
        var rows = inspector.Inspect(1, false, 1, cancellationToken: TestContext.Current.CancellationToken)!.Inventory;
        Assert.Equal(3, rows.Count);
        Assert.Contains(rows, row => row.Status == "Cycle");
        Assert.Contains(rows, row => row.ItemFormId == 200 && row.Status == "Candidate");
    }

    /// <summary>Large adversarial lists have a bounded materialized preview.</summary>
    [Fact]
    public void Inventory_BoundsRows()
    {
        var inspector = new ActorInspector(new RecordCollection
        {
            Npcs = [new NpcRecord { FormId = 1, Inventory = [new InventoryItem(100, 1)] }],
            LeveledLists = [new LeveledListRecord { FormId = 100, Flags = 1,
                Entries = Enumerable.Range(1000, 3000).Select(id => new LeveledEntry(1, (uint)id, 1)).ToList() }]
        });
        var detail = inspector.Inspect(1, false, 1, cancellationToken: TestContext.Current.CancellationToken)!;
        Assert.Equal(2048, detail.Inventory.Count);
        Assert.Equal("Truncated", detail.InventoryNotice);
    }

    /// <summary>Creates a three-tier inventory with explicit chance and global metadata.</summary>
    private static ActorInspector Inspector(byte flags) => new(new RecordCollection
    {
        Npcs = [new NpcRecord { FormId = 1, Inventory = [new InventoryItem(100, 2)] }],
        LeveledLists = [new LeveledListRecord { FormId = 100, Flags = flags, ChanceNone = 25, GlobalFormId = 400,
            Entries = [new LeveledEntry(1, 201, 1), new LeveledEntry(5, 202, 3), new LeveledEntry(8, 203, 1)] }]
    });

    /// <summary>Creates ACBS metadata with the selected template-inheritance flags.</summary>
    private static ActorBaseSubrecord Stats(ushort flags) => new(0, 0, 0, 1, 1, 1, 100, 0, 0, flags, 0, false);
}
