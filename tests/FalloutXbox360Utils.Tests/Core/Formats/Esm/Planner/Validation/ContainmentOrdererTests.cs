using System.Collections.Immutable;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Validation;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Validation;

public sealed class ContainmentOrdererTests
{
    [Fact]
    public void Order_Places_Parents_Before_Children()
    {
        var dial = Plan("DIAL", 0x1000, []);
        var info = Plan("INFO", 0x1001,
            [new RecordContainmentEdge { ParentFormId = 0x1000, Relationship = ContainmentRelationship.DialogueTopic }]);

        var ordered = ContainmentOrderer.Order([info, dial]);

        Assert.Equal(0x1000u, ordered[0].FormId);
        Assert.Equal(0x1001u, ordered[1].FormId);
    }

    [Fact]
    public void Order_Preserves_Insertion_Order_Within_Same_Type()
    {
        // Insertion order, NOT FormID order — legacy emits in catalog order and Tier 1 parity
        // depends on matching it byte-for-byte.
        var weap1 = Plan("WEAP", 0x100, []);
        var weap2 = Plan("WEAP", 0x102, []);
        var weap3 = Plan("WEAP", 0x101, []);

        var ordered = ContainmentOrderer.Order([weap2, weap1, weap3]);

        Assert.Equal(0x102u, ordered[0].FormId);
        Assert.Equal(0x100u, ordered[1].FormId);
        Assert.Equal(0x101u, ordered[2].FormId);
    }

    [Fact]
    public void Order_Detects_Cycle_And_Throws()
    {
        var a = Plan("WEAP", 0x100,
            [new RecordContainmentEdge { ParentFormId = 0x101, Relationship = ContainmentRelationship.DialogueTopic }]);
        var b = Plan("WEAP", 0x101,
            [new RecordContainmentEdge { ParentFormId = 0x100, Relationship = ContainmentRelationship.DialogueTopic }]);

        Assert.Throws<InvalidOperationException>(() => ContainmentOrderer.Order([a, b]));
    }

    private static RecordPlan Plan(string type, uint formId, ImmutableArray<RecordContainmentEdge> edges) =>
        new()
        {
            Type = type,
            Disposition = RecordDisposition.KeepMaster,
            FormId = formId,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = edges,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" },
        };
}
