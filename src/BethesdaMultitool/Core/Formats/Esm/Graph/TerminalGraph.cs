using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;

namespace BethesdaMultitool.Core.Formats.Esm.Graph;

internal enum TerminalTargetStatus
{
    Resolved, Missing, MasterNotLoaded, NotPresentInCapture, Deleted, TypeConflict, AmbiguousRecords, WrongType, NotParsed
}

internal sealed record TerminalGraphNode(TerminalRecord Terminal, int Depth,
    IReadOnlyList<TerminalMenuItemDescriber.ItemDescription> Items);

internal sealed record TerminalGraphEdge(uint From, int MenuIndex, uint To,
    TerminalTargetStatus TargetStatus, string Traversal);

/// <summary>A bounded structural TNAM graph. Conditions and scripts are evidence, never evaluated.</summary>
internal sealed record TerminalGraphResult(uint Root, int MaxDepth, int MaxNodes, bool PartialCapture,
    ConditionDisplayContext ConditionContext, IReadOnlyList<TerminalGraphNode> Nodes,
    IReadOnlyList<TerminalGraphEdge> Edges)
{
    internal const string Scope = "Structural TNAM links only. Conditions and scripts are not evaluated; " +
        "this graph does not establish in-game reachability. Script-driven navigation is not followed.";
    internal bool Truncated => Edges.Any(edge => edge.Traversal is "DepthLimit" or "NodeLimit");
}

internal static class TerminalGraph
{
    /// <summary>
    /// Breadth-first order gives each inspected terminal its shortest structural depth. Every menu item
    /// and TNAM edge of an inspected node is retained, including back edges and unresolved boundaries.
    /// maxNodes bounds inspected terminal records, not the number of boundary references.
    /// </summary>
    internal static TerminalGraphResult Build(RecordCollection records, FormIdResolver resolver, uint root,
        int maxDepth = 32, int maxNodes = 1000, bool partialCapture = false,
        Func<uint, TerminalTargetStatus?>? targetStatus = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(resolver);
        if (maxDepth is < 0 or > 1024) throw new ArgumentOutOfRangeException(nameof(maxDepth), "Depth must be 0-1024.");
        if (maxNodes is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(maxNodes), "Node count must be 1-10000.");
        var terminals = records.Terminals.GroupBy(t => t.FormId).ToDictionary(g => g.Key, g => g.Last());
        var conditions = ConditionDisplayContext.From(records, resolver);
        var rootStatus = Resolve(root);
        if (rootStatus != TerminalTargetStatus.Resolved)
            throw new InvalidOperationException($"Root terminal 0x{root:X8} cannot be inspected: {rootStatus}.");
        var nodes = new List<TerminalGraphNode>();
        var edges = new List<TerminalGraphEdge>();
        var discovered = new HashSet<uint> { root };
        var queue = new Queue<(uint Id, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.TryDequeue(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var terminal = terminals[current.Id];
            var items = TerminalMenuItemDescriber.Describe(terminal, conditions, partialCapture);
            nodes.Add(new(terminal, current.Depth, items));
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.SubTerminalFormId is not { } child) continue;
                var status = Resolve(child);
                string traversal;
                if (status != TerminalTargetStatus.Resolved) traversal = "Unresolved";
                else if (discovered.Contains(child)) traversal = "AlreadyDiscovered";
                else if (current.Depth >= maxDepth) traversal = "DepthLimit";
                else if (discovered.Count >= maxNodes) traversal = "NodeLimit";
                else
                {
                    discovered.Add(child);
                    queue.Enqueue((child, current.Depth + 1));
                    traversal = "Discovered";
                }
                edges.Add(new(current.Id, item.Index, child, status, traversal));
            }
        }
        return new(root, maxDepth, maxNodes, partialCapture, conditions, nodes, edges);

        TerminalTargetStatus Resolve(uint id)
        {
            // Raw identity is authoritative for deleted/type-conflicting/wrong-type records, even if
            // a typed parser kept an earlier live instance. A positive raw match still needs a parser.
            var status = targetStatus?.Invoke(id);
            if (status is { } known && known != TerminalTargetStatus.Resolved) return known;
            if (terminals.ContainsKey(id)) return TerminalTargetStatus.Resolved;
            if (status == TerminalTargetStatus.Resolved) return TerminalTargetStatus.NotParsed;
            return partialCapture ? TerminalTargetStatus.NotPresentInCapture : TerminalTargetStatus.Missing;
        }
    }
}
