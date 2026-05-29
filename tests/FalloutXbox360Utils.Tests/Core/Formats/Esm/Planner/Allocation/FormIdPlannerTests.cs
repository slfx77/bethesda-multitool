using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Allocation;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Disposition;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Allocation;

public sealed class FormIdPlannerTests
{
    [Fact]
    public void AllocateAll_Assigns_PluginRange_FormId_Per_New_Entry()
    {
        var allocator = new FormIdAllocator(0x800);
        var planner = new FormIdPlanner(allocator);

        // DeterministicAllocationOrder sorts by record-type (ordinal) then FormID, so ARMO
        // entries are allocated before WEAP. The plugin range starts at 0x01000800.
        var decisions = new List<(CatalogEntry, DispositionDecision)>
        {
            MakeNewEntry("WEAP", 0xAA000001),
            MakeNewEntry("WEAP", 0xAA000002),
            MakeNewEntry("ARMO", 0xBB000003),
        };

        var map = planner.AllocateAll(decisions);

        Assert.Equal(3, map.Count);
        Assert.Equal(0x01000800u, map[0xBB000003]);
        Assert.Equal(0x01000801u, map[0xAA000001]);
        Assert.Equal(0x01000802u, map[0xAA000002]);
    }

    [Fact]
    public void AllocateAll_Ignores_KeepMaster_And_Override_Entries()
    {
        var allocator = new FormIdAllocator(0x800);
        var planner = new FormIdPlanner(allocator);

        var decisions = new List<(CatalogEntry, DispositionDecision)>
        {
            MakeKeepMaster("WEAP", 0x000A0001),
            MakeOverride("WEAP", 0x000A0002),
            MakeNewEntry("ARMO", 0xBB000003),
        };

        var map = planner.AllocateAll(decisions);

        Assert.Single(map);
        Assert.True(map.ContainsKey(0xBB000003));
        // NextObjectId is the LOCAL id (24-bit, not the full FormId with plugin index).
        // One allocation moved it from 0x800 to 0x801.
        Assert.Equal(0x801u, planner.NextObjectId);
    }

    [Fact]
    public void AllocateAll_Is_Deterministic_Across_Input_Order_Permutations()
    {
        var inputs = new List<CatalogEntry>
        {
            MakeNewCatalogEntry("WEAP", 0xAA000005),
            MakeNewCatalogEntry("ARMO", 0xBB000003),
            MakeNewCatalogEntry("WEAP", 0xAA000002),
        };

        var first = AllocateForInputs(inputs);
        var second = AllocateForInputs(inputs.OrderByDescending(e => e.DmpFormId).ToList());

        Assert.Equal(first[0xAA000002], second[0xAA000002]);
        Assert.Equal(first[0xAA000005], second[0xAA000005]);
        Assert.Equal(first[0xBB000003], second[0xBB000003]);
    }

    private static IReadOnlyDictionary<uint, uint> AllocateForInputs(IReadOnlyList<CatalogEntry> inputs)
    {
        var allocator = new FormIdAllocator(0x800);
        var planner = new FormIdPlanner(allocator);
        var decisions = inputs.Select(e => (e, NewDecision())).ToList();
        return planner.AllocateAll(decisions);
    }

    private static (CatalogEntry, DispositionDecision) MakeNewEntry(string type, uint sourceFormId)
    {
        return (MakeNewCatalogEntry(type, sourceFormId), NewDecision());
    }

    private static CatalogEntry MakeNewCatalogEntry(string type, uint sourceFormId) =>
        new()
        {
            Type = type,
            Source = SourceKind.DmpNew,
            DmpFormId = sourceFormId,
        };

    private static (CatalogEntry, DispositionDecision) MakeKeepMaster(string type, uint formId) =>
        (new CatalogEntry
        {
            Type = type,
            Source = SourceKind.MasterOnly,
            MasterFormId = formId,
        },
        new DispositionDecision
        {
            Disposition = RecordDisposition.KeepMaster,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" },
        });

    private static (CatalogEntry, DispositionDecision) MakeOverride(string type, uint formId) =>
        (new CatalogEntry
        {
            Type = type,
            Source = SourceKind.DmpOverride,
            MasterFormId = formId,
            DmpFormId = formId,
        },
        new DispositionDecision
        {
            Disposition = RecordDisposition.Override,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" },
        });

    private static DispositionDecision NewDecision() => new()
    {
        Disposition = RecordDisposition.New,
        Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" },
    };
}
