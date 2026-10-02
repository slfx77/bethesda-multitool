using System.Globalization;
using System.Numerics;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Starfield;

/// <summary>
///     The nodes, meshes and LOD layer sets of a Starfield <c>.mesh</c> document (cut-2 plan section 3.3, decision D1).
///     Node 0 carries mesh 0, the main index list. Each LOD list k (counted from 1) gets node k; a non-empty list's node
///     carries its own mesh, whose one primitive shares every buffer of the main primitive, and an empty list's node
///     carries none. With LOD lists the nodes form one exclusive layer-set group <see cref="ExclusiveGroup" />:
///     <c>lod0</c> (node 0) on by default, <c>lod1</c> onward off. Without LOD lists there are no layer sets. Every node is
///     a scene root with the identity transform.
/// </summary>
/// <remarks>
///     A mesh's primitives are drawn together (<see cref="SceneMesh.Primitives" />), so LOD primitives inside mesh 0 would
///     draw every level at once; layer sets are the contract's only alternative-selection vocabulary, and cut 1a maps
///     NiLODNode the same way (finest level on). <c>mesh info</c> counts each LOD mesh's shared vertices again (the
///     plan's optional Shared ask SA-M2).
/// </remarks>
internal static class StarfieldMeshModelLayers
{
    /// <summary>The source kind of the LOD layer sets.</summary>
    public const string SourceKind = "bmt.starfield.mesh.lod";

    /// <summary>The exclusive group every LOD layer set belongs to.</summary>
    public const string ExclusiveGroup = "starfield.mesh.lod";

    /// <summary>The identity transform every node carries.</summary>
    public static SceneTrs Identity { get; } = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    /// <summary>The layer-set id of LOD <paramref name="level" /> (0 is the main index list).</summary>
    public static string LayerSetId(int level)
    {
        return string.Create(CultureInfo.InvariantCulture, $"lod{level}");
    }

    /// <summary>The node and mesh name of LOD <paramref name="level" /> (1 and above).</summary>
    public static string LodName(string name, int level)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{name}.lod{level}");
    }

    /// <summary>Builds the nodes, meshes, layer sets and scene roots.</summary>
    /// <param name="geometry">The main and LOD primitives.</param>
    /// <param name="name">The document name (node 0 and mesh 0 carry it).</param>
    /// <param name="cancellationToken">Observed per LOD.</param>
    public static StarfieldMeshModelLayerResult Build(StarfieldMeshModelGeometryResult geometry, string name,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(name);
        var meshes = new List<SceneMesh> { new(name, [geometry.Primary]) };
        var nodes = new List<SceneNode> { new(name, Identity, meshIndex: 0) { Role = SceneNodeRole.Transform } };
        var layerSets = new List<SceneLayerSet>();
        if (geometry.Lods.Count > 0)
        {
            layerSets.Add(new SceneLayerSet(LayerSetId(0), "LOD 0 (main index list)", [0], true, SourceKind,
                ExclusiveGroup, cancellationToken: cancellationToken));
        }

        for (var lod = 0; lod < geometry.Lods.Count; lod++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var level = lod + 1;
            var lodName = LodName(name, level);
            int? meshIndex = null;
            if (geometry.Lods[lod] is { } primitive)
            {
                meshIndex = meshes.Count;
                meshes.Add(new SceneMesh(lodName, [primitive]));
            }

            var node = nodes.Count;
            nodes.Add(new SceneNode(lodName, Identity, meshIndex: meshIndex) { Role = SceneNodeRole.Transform });
            layerSets.Add(new SceneLayerSet(LayerSetId(level),
                string.Create(CultureInfo.InvariantCulture, $"LOD {level}"), [node], false, SourceKind, ExclusiveGroup,
                cancellationToken: cancellationToken));
        }

        var roots = Enumerable.Range(0, nodes.Count).ToArray();
        return new StarfieldMeshModelLayerResult(nodes, meshes, layerSets, roots);
    }
}
