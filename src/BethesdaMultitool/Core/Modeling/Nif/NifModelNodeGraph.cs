using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The node hierarchy <see cref="NifModelNodeReader" /> builds from the footer roots: one <see cref="SceneNode" />
///     per occurrence in pre-order, the root occurrences, which node indices each block produced and the per-block facts.
/// </summary>
internal sealed class NifModelNodeGraph
{
    /// <summary>Creates the graph.</summary>
    public NifModelNodeGraph(
        IReadOnlyList<SceneNode> nodes,
        IReadOnlyList<int> rootNodeIndices,
        IReadOnlyList<IReadOnlyList<int>> occurrencesByBlock,
        IReadOnlyDictionary<int, NifModelNodeFacts> factsByBlock,
        IReadOnlyList<SceneDiagnostic> diagnostics)
    {
        Nodes = nodes;
        RootNodeIndices = rootNodeIndices;
        OccurrencesByBlock = occurrencesByBlock;
        FactsByBlock = factsByBlock;
        Diagnostics = diagnostics;
    }

    /// <summary>The document nodes in pre-order (a parent always precedes its children).</summary>
    public IReadOnlyList<SceneNode> Nodes { get; }

    /// <summary>The node index of each footer root's occurrence, in footer order.</summary>
    public IReadOnlyList<int> RootNodeIndices { get; }

    /// <summary>For every block, the node indices of its occurrences (empty when it produced no node).</summary>
    public IReadOnlyList<IReadOnlyList<int>> OccurrencesByBlock { get; }

    /// <summary>The facts of every block that produced at least one node, keyed by block index.</summary>
    public IReadOnlyDictionary<int, NifModelNodeFacts> FactsByBlock { get; }

    /// <summary>Diagnostics about the hierarchy (non-identity roots).</summary>
    public IReadOnlyList<SceneDiagnostic> Diagnostics { get; }
}
