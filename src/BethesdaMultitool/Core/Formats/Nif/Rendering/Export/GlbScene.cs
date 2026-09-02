using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     An in-progress glTF/GLB scene graph: the node hierarchy and mesh parts being assembled for export,
///     plus a name index for resolving bone/node references.
/// </summary>
internal sealed class GlbScene
{
    private readonly Dictionary<string, int> _namedNodes =
        new(StringComparer.OrdinalIgnoreCase);

    public GlbScene()
    {
        Nodes.Add(new GlbNode
        {
            Name = "SceneRoot",
            ParentIndex = null,
            LocalTransform = Matrix4x4.Identity,
            WorldTransform = Matrix4x4.Identity,
            Kind = GlbNodeKind.Root
        });
    }

    public List<GlbNode> Nodes { get; } = [];

    public List<GlbMeshPart> MeshParts { get; } = [];

    public static int RootNodeIndex => 0;

    /// <summary>
    ///     Appends a node to the scene graph and returns its index; registers <paramref name="lookupName" /> for later
    ///     resolution.
    /// </summary>
    public int AddNode(
        string name,
        int? parentIndex,
        Matrix4x4 localTransform,
        Matrix4x4 worldTransform,
        GlbNodeKind kind,
        string? lookupName = null,
        int? sourceBlockIndex = null)
    {
        var nodeIndex = Nodes.Count;
        Nodes.Add(new GlbNode
        {
            Name = name,
            ParentIndex = parentIndex,
            LocalTransform = localTransform,
            WorldTransform = worldTransform,
            Kind = kind,
            LookupName = lookupName,
            SourceBlockIndex = sourceBlockIndex
        });

        if (!string.IsNullOrWhiteSpace(lookupName) && !_namedNodes.ContainsKey(lookupName))
        {
            _namedNodes.Add(lookupName, nodeIndex);
        }

        return nodeIndex;
    }

    /// <summary>Looks up a previously registered node by its lookup name.</summary>
    public bool TryGetNodeIndex(string nodeName, out int nodeIndex)
    {
        return _namedNodes.TryGetValue(nodeName, out nodeIndex);
    }
}
