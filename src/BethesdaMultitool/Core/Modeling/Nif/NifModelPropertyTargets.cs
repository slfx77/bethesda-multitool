namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Cut-1b slice 14: the document targets a property track can address, built from the cut-1a material and geometry
///     results: every material a property block feeds (<see cref="NifModelMaterialResult.MaterialsByFedBlock" />; a
///     block shared by several geometries feeds several materials and the engine drives all of them), the layer
///     ordinal a (block, slot) pair occupies in each material (<see cref="NifModelMaterialResult.LayerOrigins" />,
///     which is what <see cref="Slfx77.Multitool.Core.Models.ScenePropertyTarget.LayerIndex" /> means), and the
///     materials a placed node draws with (for NiUVController, which targets a geometry). Since cut-1b slice 10 it also
///     carries, when built from a geometry result, the morph target count of every placed node's mesh, so the reader
///     emits a morph weight channel only where Shared's mesh target domain matches (<see cref="AdmitsMorphChannels" />).
///     Immutable.
/// </summary>
internal sealed class NifModelPropertyTargets
{
    private static readonly IReadOnlyList<int> NoMaterials = Array.Empty<int>();

    private readonly IReadOnlyDictionary<int, IReadOnlyList<int>> _materialsByFedBlock;
    private readonly IReadOnlyList<IReadOnlyList<NifModelLayerOrigin>> _layerOrigins;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<int>> _materialsByNode;
    private readonly IReadOnlyDictionary<int, int>? _morphTargetsByNode;

    private NifModelPropertyTargets(
        int materialCount,
        IReadOnlyDictionary<int, IReadOnlyList<int>> materialsByFedBlock,
        IReadOnlyList<IReadOnlyList<NifModelLayerOrigin>> layerOrigins,
        IReadOnlyDictionary<int, IReadOnlyList<int>> materialsByNode,
        IReadOnlyDictionary<int, int>? morphTargetsByNode)
    {
        MaterialCount = materialCount;
        _materialsByFedBlock = materialsByFedBlock;
        _layerOrigins = layerOrigins;
        _materialsByNode = materialsByNode;
        _morphTargetsByNode = morphTargetsByNode;
    }

    /// <summary>
    ///     No targets: a component test without a material or geometry stage. It does not know the document's meshes, so
    ///     <see cref="AdmitsMorphChannels" /> admits every occurrence (the test assembles its own document).
    /// </summary>
    public static NifModelPropertyTargets None { get; } = new(0, new Dictionary<int, IReadOnlyList<int>>(), [],
        new Dictionary<int, IReadOnlyList<int>>(), null);

    /// <summary>
    ///     The targets of a <c>.kf</c> document (cut-1b slice 10, D12): the resolved skeleton's nodes only, no material
    ///     and no mesh, so no property track binds and <see cref="AdmitsMorphChannels" /> admits no occurrence.
    /// </summary>
    public static NifModelPropertyTargets SkeletonOnly { get; } = new(0, new Dictionary<int, IReadOnlyList<int>>(),
        [], new Dictionary<int, IReadOnlyList<int>>(), new Dictionary<int, int>());

    /// <summary>True when the targets were built from a geometry result, so they know which nodes draw which mesh.</summary>
    public bool KnowsMeshes => _morphTargetsByNode is not null;

    /// <summary>The number of document materials.</summary>
    public int MaterialCount { get; }

    /// <summary>Builds the targets from the cut-1a results of one read.</summary>
    /// <param name="materials">The material reader's result.</param>
    /// <param name="geometry">The geometry reader's result, for the node-to-material map; null leaves that map empty.</param>
    /// <returns>The targets.</returns>
    /// <exception cref="ArgumentException">The layer origins do not describe every material's layers.</exception>
    public static NifModelPropertyTargets FromResults(NifModelMaterialResult materials,
        NifModelGeometryResult? geometry)
    {
        ArgumentNullException.ThrowIfNull(materials);
        if (materials.LayerOrigins.Count != materials.Materials.Count)
        {
            throw new ArgumentException("The material result records layer origins for a different material count.",
                nameof(materials));
        }

        for (var material = 0; material < materials.Materials.Count; material++)
        {
            if (materials.LayerOrigins[material].Count != materials.Materials[material].Layers.Count)
            {
                throw new ArgumentException(
                    $"Material {material} records {materials.LayerOrigins[material].Count} layer origins for " +
                    $"{materials.Materials[material].Layers.Count} layers.", nameof(materials));
            }
        }

        var byNode = new Dictionary<int, IReadOnlyList<int>>();
        Dictionary<int, int>? morphTargetsByNode = null;
        if (geometry is not null)
        {
            morphTargetsByNode = new Dictionary<int, int>();
            foreach (var (node, mesh) in geometry.MeshByNode)
            {
                if (mesh < 0 || mesh >= geometry.Meshes.Count)
                {
                    continue;
                }

                morphTargetsByNode[node] = MorphTargetCount(geometry.Meshes[mesh]);

                var list = new List<int>();
                foreach (var primitive in geometry.Meshes[mesh].Primitives)
                {
                    if (primitive.MaterialIndex is { } index && !list.Contains(index))
                    {
                        list.Add(index);
                    }
                }

                byNode[node] = list.AsReadOnly();
            }
        }

        return new NifModelPropertyTargets(materials.Materials.Count, materials.MaterialsByFedBlock,
            materials.LayerOrigins, byNode, morphTargetsByNode);
    }

    /// <summary>
    ///     Whether a morph weight channel may address the given occurrences: every one draws a mesh whose primitives each
    ///     carry exactly <paramref name="targetCount" /> morph targets (Shared validates a whole-vector morph track against
    ///     the node's mesh target count and a per-target one against its target range). Targets built without a geometry
    ///     result (<see cref="None" />) admit every occurrence; <see cref="SkeletonOnly" /> admits none.
    /// </summary>
    /// <param name="occurrences">The target block's document nodes.</param>
    /// <param name="targetCount">The morph data's target count (its morphs minus the Base).</param>
    /// <returns>True when the channel may be emitted on every occurrence.</returns>
    public bool AdmitsMorphChannels(IReadOnlyList<int> occurrences, int targetCount)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        if (_morphTargetsByNode is null)
        {
            return true;
        }

        if (targetCount <= 0 || occurrences.Count == 0)
        {
            return false;
        }

        foreach (var node in occurrences)
        {
            if (!_morphTargetsByNode.TryGetValue(node, out var count) || count != targetCount)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The morph target count every primitive of a mesh carries, or -1 when they differ or there is none.</summary>
    private static int MorphTargetCount(Slfx77.Multitool.Core.Models.SceneMesh mesh)
    {
        var count = -1;
        foreach (var primitive in mesh.Primitives)
        {
            if (count >= 0 && primitive.MorphTargets.Count != count)
            {
                return -1;
            }

            count = primitive.MorphTargets.Count;
        }

        return count;
    }

    /// <summary>Every material a property or texture-set block feeds, in first-use order; empty when it feeds none.</summary>
    /// <param name="block">The property block.</param>
    /// <returns>The material indices.</returns>
    public IReadOnlyList<int> MaterialsFedBy(int block)
    {
        return _materialsByFedBlock.TryGetValue(block, out var materials) ? materials : NoMaterials;
    }

    /// <summary>The materials a placed node draws with (its mesh's primitive materials, distinct, in primitive order).</summary>
    /// <param name="node">The document node.</param>
    /// <returns>The material indices; empty for a node without a mesh or materials.</returns>
    public IReadOnlyList<int> MaterialsOfNode(int node)
    {
        return _materialsByNode.TryGetValue(node, out var materials) ? materials : NoMaterials;
    }

    /// <summary>The first layer of a material that came from a given slot of any block (NiUVController's Base map).</summary>
    /// <param name="material">The material index.</param>
    /// <param name="slot">The slot key (<see cref="NifModelLayerOrigin.Slot" />).</param>
    /// <param name="sourceBlock">The block that contributed the layer.</param>
    /// <param name="ordinal">The layer ordinal.</param>
    /// <returns>False when the material holds no layer from that slot.</returns>
    public bool TryFindLayer(int material, string slot, out int sourceBlock, out int ordinal)
    {
        ArgumentNullException.ThrowIfNull(slot);
        sourceBlock = -1;
        ordinal = -1;
        if ((uint)material >= (uint)_layerOrigins.Count)
        {
            return false;
        }

        var origins = _layerOrigins[material];
        for (var index = 0; index < origins.Count; index++)
        {
            if (string.Equals(origins[index].Slot, slot, StringComparison.Ordinal))
            {
                sourceBlock = origins[index].SourceBlock;
                ordinal = index;
                return true;
            }
        }

        return false;
    }

    /// <summary>The ordinal in a material's layers of the layer one (block, slot) pair contributed.</summary>
    /// <param name="material">The material index.</param>
    /// <param name="block">The property or texture-set block.</param>
    /// <param name="slot">The slot key (<see cref="NifModelLayerOrigin.Slot" />).</param>
    /// <param name="ordinal">The layer ordinal.</param>
    /// <returns>False when the material holds no such layer (the slot was empty, omitted, or belongs to another block).</returns>
    public bool TryGetLayerOrdinal(int material, int block, string slot, out int ordinal)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ordinal = -1;
        if ((uint)material >= (uint)_layerOrigins.Count)
        {
            return false;
        }

        var origins = _layerOrigins[material];
        for (var index = 0; index < origins.Count; index++)
        {
            if (origins[index].SourceBlock == block && string.Equals(origins[index].Slot, slot, StringComparison.Ordinal))
            {
                ordinal = index;
                return true;
            }
        }

        return false;
    }
}
