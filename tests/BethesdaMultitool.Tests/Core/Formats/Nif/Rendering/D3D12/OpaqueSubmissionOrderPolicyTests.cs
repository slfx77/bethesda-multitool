using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

public sealed class OpaqueSubmissionOrderPolicyTests
{
    private readonly record struct Item(
        string Name,
        OpaqueSubmissionLane Lane,
        string Pso,
        double Depth = 0,
        int Ordinal = 0);
    private readonly record struct ReferenceItem(string Name, object Pso);

    private sealed class ValueEqualPso
    {
        public override bool Equals(object? obj) => obj is ValueEqualPso;

        public override int GetHashCode() => 0;
    }

    private sealed class ItemDepthComparer : IComparer<Item>
    {
        public int Compare(Item x, Item y)
        {
            var depth = x.Depth.CompareTo(y.Depth);
            return depth != 0 ? depth : x.Ordinal.CompareTo(y.Ordinal);
        }
    }

    [Fact]
    public void Ordinary_batches_group_by_first_seen_pso_while_special_lanes_stay_stable()
    {
        var items = new List<Item>
        {
            new("decal-a", OpaqueSubmissionLane.Decal, "decal"),
            new("ordinary-a", OpaqueSubmissionLane.Ordinary, "pso-b"),
            new("grass-a", OpaqueSubmissionLane.Grass, "grass"),
            new("ordinary-b", OpaqueSubmissionLane.Ordinary, "pso-a"),
            new("decal-b", OpaqueSubmissionLane.Decal, "decal"),
            new("ordinary-c", OpaqueSubmissionLane.Ordinary, "pso-b"),
            new("ordinary-d", OpaqueSubmissionLane.Ordinary, "pso-a"),
            new("grass-b", OpaqueSubmissionLane.Grass, "grass")
        };
        var scratch = new List<Item>();
        var groupScratch = new List<string>();

        Order(items, scratch, groupScratch);

        Assert.Equal(
            [
                "ordinary-a", "ordinary-c", "ordinary-b", "ordinary-d",
                "decal-a", "decal-b", "grass-a", "grass-b"
            ],
            items.Select(static item => item.Name));
        Assert.Empty(scratch);
        Assert.Empty(groupScratch);
    }

    [Fact]
    public void Ordering_is_idempotent_and_never_loses_or_duplicates_a_batch()
    {
        var items = new List<Item>
        {
            new("o1", OpaqueSubmissionLane.Ordinary, "p2"),
            new("d1", OpaqueSubmissionLane.Decal, "p9"),
            new("o2", OpaqueSubmissionLane.Ordinary, "p1"),
            new("g1", OpaqueSubmissionLane.Grass, "p8"),
            new("o3", OpaqueSubmissionLane.Ordinary, "p2")
        };
        var scratch = new List<Item>();
        var groupScratch = new List<string>();

        Order(items, scratch, groupScratch);
        var once = items.ToArray();
        Order(items, scratch, groupScratch);

        Assert.Equal(once, items);
        Assert.Equal(5, items.Select(static item => item.Name).Distinct().Count());
    }

    [Fact]
    public void Pso_groups_use_reference_identity_even_when_objects_are_value_equal()
    {
        var psoA = new ValueEqualPso();
        var psoB = new ValueEqualPso();
        Assert.Equal(psoA, psoB);
        Assert.NotSame(psoA, psoB);
        var items = new List<ReferenceItem>
        {
            new("a1", psoA),
            new("b1", psoB),
            new("a2", psoA)
        };
        var scratch = new List<ReferenceItem>();
        var groupScratch = new List<object>();

        OpaqueSubmissionOrderPolicy.Order(
            items,
            scratch,
            groupScratch,
            static _ => OpaqueSubmissionLane.Ordinary,
            static item => item.Pso,
            ReferenceEqualityComparer.Instance);

        Assert.Equal(["a1", "a2", "b1"], items.Select(static item => item.Name));
    }

    [Fact]
    public void Optional_front_to_back_comparer_moves_only_inside_each_ordinary_pso_group()
    {
        var items = new List<Item>
        {
            new("decal-a", OpaqueSubmissionLane.Decal, "decal", 1, 0),
            new("b-far", OpaqueSubmissionLane.Ordinary, "pso-b", 9, 1),
            new("grass-a", OpaqueSubmissionLane.Grass, "grass", 0, 2),
            new("a-far", OpaqueSubmissionLane.Ordinary, "pso-a", 8, 3),
            new("b-near", OpaqueSubmissionLane.Ordinary, "pso-b", 3, 4),
            new("decal-b", OpaqueSubmissionLane.Decal, "decal", -100, 5),
            new("a-near-first", OpaqueSubmissionLane.Ordinary, "pso-a", 2, 6),
            new("a-near-second", OpaqueSubmissionLane.Ordinary, "pso-a", 2, 7),
            new("grass-b", OpaqueSubmissionLane.Grass, "grass", -100, 8)
        };
        var scratch = new List<Item>();
        var groupScratch = new List<string>();

        OpaqueSubmissionOrderPolicy.Order(
            items,
            scratch,
            groupScratch,
            static item => item.Lane,
            static item => item.Pso,
            StringComparer.Ordinal,
            new ItemDepthComparer());

        Assert.Equal(
            [
                "b-near", "b-far",
                "a-near-first", "a-near-second", "a-far",
                "decal-a", "decal-b", "grass-a", "grass-b"
            ],
            items.Select(static item => item.Name));
        Assert.Empty(scratch);
        Assert.Empty(groupScratch);
    }

    [Fact]
    public void Registry_orders_once_at_publication_and_classifies_the_three_correctness_lanes()
    {
        var registry = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "OpaqueBatchRegistry12.cs");
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");

        SourceContract.AssertOrder(
            registry,
            "if (batch.Submesh.DepthWritingBlend || batch.UsesGrassDistanceEnvelope)",
            "return OpaqueSubmissionLane.Grass;",
            "return batch.Submesh.IsDecal");
        Assert.Contains("ReferenceEqualityComparer.Instance", registry, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            renderer,
            "state.Target.OpaqueBatches.OrderGrassBatchesLast();",
            "state.Target.OpaqueBatches.OrderForSubmission(in frontToBackView);",
            "SortBatchInstancesByCascade(");
    }

    private static void Order(List<Item> items, List<Item> scratch, List<string> groupScratch)
    {
        OpaqueSubmissionOrderPolicy.Order(
            items,
            scratch,
            groupScratch,
            static item => item.Lane,
            static item => item.Pso,
            StringComparer.Ordinal);
    }
}
