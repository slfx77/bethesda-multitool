using BethesdaMultitool.Core.Formats.Esm.Xref;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Xref;

public sealed class StaticReferenceTraversalTests
{
    [Fact]
    public void BreadthFirstPathsTerminateCyclesAndUseTheShortestObservedPath()
    {
        var graph = StaticReferenceTraversal.Build([
            Edge(0x8000, 0x9000), Edge(0x9000, 0xA000), Edge(0xA000, 0x8000), Edge(0x8000, 0xA000)
        ], [0x8000], 8, 100);
        Assert.Equal(3, graph.Nodes.Count);
        var end = Assert.Single(graph.Nodes, node => node.FormId == 0xA000);
        Assert.Equal(1, end.Depth); Assert.Equal(0x8000u, end.Root);
        Assert.Equal(0x8000u, end.Via!.SourceLoadOrderFormId);
        Assert.False(graph.NodeLimitReached); Assert.Equal(0, graph.DepthBoundaries);
    }

    [Fact]
    public void DepthAndNodeCapsAreReportedWithoutClaimingUnreachedIdsAreUnused()
    {
        var edges = new[] { Edge(0x8000, 0x9000), Edge(0x9000, 0xA000), Edge(0xA000, 0xB000) };
        var depth = StaticReferenceTraversal.Build(edges, [0x8000], 1, 100);
        Assert.Equal(2, depth.Nodes.Count); Assert.Equal(1, depth.DepthBoundaries);
        Assert.False(depth.NodeLimitReached);
        var count = StaticReferenceTraversal.Build(edges, [0x8000], 10, 2);
        Assert.Equal(2, count.Nodes.Count); Assert.True(count.NodeLimitReached);
        Assert.Equal(0, count.DepthBoundaries);
    }

    [Fact]
    public void GuessesRuntimeIdsAndTypeConflictsNeverEnterStaticPaths()
    {
        var graph = StaticReferenceTraversal.Build([
            Edge(0x8000, 0x9000) with { Certainty = "union-fallback" },
            Edge(0x8000, 0xA000) with { Certainty = "untyped" },
            Edge(0x8000, 0xB000) with { SourceTypeConflict = true },
            Edge(0x8000, 0xC000) with { TargetStatus = "type-conflict" },
            Edge(0x8000, 0xD000) with { SourceAmbiguous = true },
            Edge(0x8000, 0xE000) with { TargetStatus = "ambiguous-winning-records" },
            Edge(0x8000, 0), Edge(0x8000, uint.MaxValue), Edge(0x8000, 0xFF001234),
            Edge(0x8000, 7) with { TargetStatus = "engine-reserved" }
        ], [0x8000], 4, 100);
        Assert.Equal(new uint[] { 0x8000, 7 }, graph.Nodes.Select(node => node.FormId));
    }

    [Fact]
    public void MultipleExplicitRootsAreDeduplicatedAndRetainTheirOwnOrigin()
    {
        var graph = StaticReferenceTraversal.Build([Edge(0x8000, 0x9000), Edge(0xA000, 0xB000)],
            [0x8000, 0x8000, 0xA000], 1, 4);
        Assert.Equal(2, graph.Roots.Count);
        Assert.Equal(0xA000u, Assert.Single(graph.Nodes, node => node.FormId == 0xB000).Root);
        Assert.Throws<ArgumentException>(() => StaticReferenceTraversal.Build([], [0x8000, 0x9000], 3, 1));
        Assert.Throws<ArgumentException>(() => StaticReferenceTraversal.Build([], [0xFF001234], 3, 1));
    }

    private static FormIdEdge Edge(uint source, uint target) => new("FalloutNV.esm", "FalloutNV.esm",
        source, source, "TERM", null, target, target, "FalloutNV.esm", "resolved", "terminal-submenu",
        "typed", "TNAM", 0, 0, "Submenu", 0, 0, 6, 30, false, false);
}
