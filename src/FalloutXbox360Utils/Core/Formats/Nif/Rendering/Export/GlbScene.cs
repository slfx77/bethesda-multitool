using System.Numerics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;

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

    public int AddNode(
        string name,
        int? parentIndex,
        Matrix4x4 localTransform,
        Matrix4x4 worldTransform,
        GlbNodeKind kind,
        string? lookupName = null)
    {
        var nodeIndex = Nodes.Count;
        Nodes.Add(new GlbNode
        {
            Name = name,
            ParentIndex = parentIndex,
            LocalTransform = localTransform,
            WorldTransform = worldTransform,
            Kind = kind,
            LookupName = lookupName
        });

        if (!string.IsNullOrWhiteSpace(lookupName) && !_namedNodes.ContainsKey(lookupName))
        {
            _namedNodes.Add(lookupName, nodeIndex);
        }

        return nodeIndex;
    }

    public bool TryGetNodeIndex(string nodeName, out int nodeIndex)
    {
        return _namedNodes.TryGetValue(nodeName, out nodeIndex);
    }
}
