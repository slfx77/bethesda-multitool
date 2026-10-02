namespace BethesdaMultitool.Core.Formats.Esm.Xref;

internal sealed record StaticReferenceNode(uint FormId, uint Root, int Depth, FormIdEdge? Via);

/// <summary>One shortest static typed-reference path per reached ID, never an execution/reachability proof.</summary>
internal sealed record StaticReferenceTraversal(IReadOnlyList<uint> Roots, int MaxDepth, int MaxNodes,
    IReadOnlyList<StaticReferenceNode> Nodes, int DepthBoundaries, bool NodeLimitReached)
{
    internal static StaticReferenceTraversal Build(IEnumerable<FormIdEdge> edges, IReadOnlyList<uint> roots,
        int maxDepth, int maxNodes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxNodes, 1);
        var distinctRoots = roots.Distinct().ToList();
        if (distinctRoots.Count > maxNodes) { throw new ArgumentException("--max-nodes cannot be smaller than the number of distinct roots."); }
        if (distinctRoots.Any(value => value == 0 || value >> 24 == 0xFF))
        { throw new ArgumentException("Static roots cannot be null, sentinel or runtime-only IDs."); }
        var graph = edges.Where(edge => edge.Certainty == "typed" && !edge.SourceTypeConflict && !edge.SourceAmbiguous &&
                edge.TargetLoadOrderFormId != 0 && edge.TargetLoadOrderFormId >> 24 != 0xFF &&
                edge.TargetStatus is not ("type-conflict" or "ambiguous-winning-records"))
            .GroupBy(edge => edge.SourceLoadOrderFormId)
            .ToDictionary(group => group.Key, group => group.ToList());
        var visited = new Dictionary<uint, StaticReferenceNode>();
        var queue = new Queue<StaticReferenceNode>();
        foreach (var root in distinctRoots)
        {
            var node = new StaticReferenceNode(root, root, 0, null); visited.Add(root, node); queue.Enqueue(node);
        }
        var boundaries = 0; var limited = false;
        while (queue.TryDequeue(out var current))
        {
            if (!graph.TryGetValue(current.FormId, out var outgoing)) { continue; }
            if (current.Depth == maxDepth)
            {
                if (outgoing.Any(edge => !visited.ContainsKey(edge.TargetLoadOrderFormId))) { boundaries++; }
                continue;
            }
            foreach (var edge in outgoing)
            {
                if (visited.ContainsKey(edge.TargetLoadOrderFormId)) { continue; }
                if (visited.Count == maxNodes) { limited = true; continue; }
                var node = new StaticReferenceNode(edge.TargetLoadOrderFormId, current.Root, current.Depth + 1, edge);
                visited.Add(node.FormId, node); queue.Enqueue(node);
            }
        }
        return new(distinctRoots, maxDepth, maxNodes, visited.Values.ToList(), boundaries, limited);
    }
}
