using System.Numerics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;

internal sealed class NpcExportScene
{
    private readonly Dictionary<string, int> _namedNodes =
        new(StringComparer.OrdinalIgnoreCase);

    public NpcExportScene()
    {
        Nodes.Add(new NpcExportNode
        {
            Name = "SceneRoot",
            ParentIndex = null,
            LocalTransform = Matrix4x4.Identity,
            WorldTransform = Matrix4x4.Identity,
            Kind = NpcExportNodeKind.Root
        });
    }

    public List<NpcExportNode> Nodes { get; } = [];

    public List<NpcExportMeshPart> MeshParts { get; } = [];

    public int RootNodeIndex => 0;

    public int AddNode(
        string name,
        int? parentIndex,
        Matrix4x4 localTransform,
        Matrix4x4 worldTransform,
        NpcExportNodeKind kind,
        string? lookupName = null)
    {
        var nodeIndex = Nodes.Count;
        Nodes.Add(new NpcExportNode
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
