using System.Text.Json.Nodes;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>What <see cref="NifModelLayerReader" /> produced for one read.</summary>
internal sealed class NifModelLayerResult
{
    /// <summary>Creates a result.</summary>
    public NifModelLayerResult(
        IReadOnlyList<SceneLayerSet> layerSets,
        IReadOnlyDictionary<int, SceneBillboard> billboardByNode,
        IReadOnlyDictionary<int, JsonObject> nodeFactsByBlock,
        IReadOnlyDictionary<int, NifModelBlockDisposition> dispositions,
        IReadOnlyDictionary<int, int> nodeByFedBlock,
        IReadOnlyList<SceneDiagnostic> diagnostics)
    {
        LayerSets = layerSets;
        BillboardByNode = billboardByNode;
        NodeFactsByBlock = nodeFactsByBlock;
        Dispositions = dispositions;
        NodeByFedBlock = nodeByFedBlock;
        Diagnostics = diagnostics;
    }

    /// <summary>The switch, LOD and hidden layer sets, in block order (each block's sets in ordinal order).</summary>
    public IReadOnlyList<SceneLayerSet> LayerSets { get; }

    /// <summary>The billboard of each NiBillboardNode occurrence (node index).</summary>
    public IReadOnlyDictionary<int, SceneBillboard> BillboardByNode { get; }

    /// <summary>
    ///     Per placed block, the layer and billboard facts merged into its <c>bmt.nif.block</c> row's <c>node</c> object
    ///     (keys <c>switch</c>, <c>lod</c>, <c>hiddenLayer</c>, <c>billboard</c>; the node payload keeps its own
    ///     <c>hidden</c> flag).
    /// </summary>
    public IReadOnlyDictionary<int, JsonObject> NodeFactsByBlock { get; }

    /// <summary>Coverage decisions for the LOD data blocks the reader visited.</summary>
    public IReadOnlyDictionary<int, NifModelBlockDisposition> Dispositions { get; }

    /// <summary>For each LOD data block, the first LOD node occurrence it serves (native-state targeting).</summary>
    public IReadOnlyDictionary<int, int> NodeByFedBlock { get; }

    /// <summary>Layer and billboard diagnostics, bounded per code.</summary>
    public IReadOnlyList<SceneDiagnostic> Diagnostics { get; }
}
