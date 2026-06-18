using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Scanning;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Extracts real engine-generated SpeedTree geometry from a memory dump. At runtime
///     <c>CSpeedTreeRT::Compute</c> + <c>BSTreeModel::CreateGeometry</c> build standard Gamebryo
///     <c>NiTriShape</c> objects under a <c>BSTreeModel</c>; this walks each model's node graph and
///     reads the runtime <c>NiTriShapeData</c> arrays (reusing <see cref="RuntimeGeometryScanner" />),
///     yielding the actual vertices/UVs/indices instead of the procedural approximation.
/// </summary>
internal sealed class RuntimeTreeGeometryExtractor(RuntimeMemoryContext context)
{
    // BSTreeModel data members (PDB types_full.txt).
    private const int SpModelOffset = 272;     // root NiNode of all generated geometry
    private const int PspBranchesOffset = 276; // bark geometry node(s)
    private const int PspLeavesOffset = 280;   // foliage geometry node(s)
    private const int SpBillboardOffset = 284; // billboard node

    // Ni* runtime offsets (PDB / RuntimeSceneGraphWalker).
    private const int NiNodeChildrenPtrOffset = 196; // m_kChildren NiTArray m_pBase
    private const int NiNodeChildrenCountOffset = 202; // m_kChildren m_usSize (uint16)
    private const int NiGeometryModelDataOffset = 220; // NiGeometry.m_spModelData → NiTriShapeData
    private const int MaxChildren = 4096;
    private const int MaxDepth = 12;

    private readonly RuntimeGeometryScanner _geometry = new(context);

    /// <summary>
    ///     Extract every <c>BSTreeModel</c> reachable at the given instance VAs. Each result groups the
    ///     model's branch / leaf / billboard meshes (classified by which node subtree they came from).
    /// </summary>
    public List<ExtractedTree> Extract(IReadOnlyList<uint> bsTreeModelInstanceVas)
    {
        var trees = new List<ExtractedTree>();
        foreach (var va in bsTreeModelInstanceVas)
        {
            var tree = ExtractTree(va);
            if (tree.Submeshes.Count > 0)
            {
                trees.Add(tree);
            }
        }

        return trees;
    }

    private ExtractedTree ExtractTree(uint bsTreeModelVa)
    {
        var submeshes = new List<ExtractedTreeSubmesh>();
        var seen = new HashSet<uint>();

        CollectNode(ReadPointer(bsTreeModelVa + PspBranchesOffset), TreeGeometryKind.Branch, submeshes, seen, 0);
        CollectNode(ReadPointer(bsTreeModelVa + PspLeavesOffset), TreeGeometryKind.Leaf, submeshes, seen, 0);
        CollectNode(ReadPointer(bsTreeModelVa + SpBillboardOffset), TreeGeometryKind.Billboard, submeshes, seen, 0);

        // Fallback: if the typed branch/leaf node pointers weren't usable, walk the whole model root.
        if (submeshes.Count == 0)
        {
            CollectNode(ReadPointer(bsTreeModelVa + SpModelOffset), TreeGeometryKind.Branch, submeshes, seen, 0);
        }

        return new ExtractedTree { BSTreeModelVa = bsTreeModelVa, Submeshes = submeshes };
    }

    /// <summary>
    ///     Recursively walk a node: if it carries geometry (NiGeometry.m_spModelData parses as a mesh)
    ///     emit it; otherwise treat it as a NiNode and descend into its children.
    /// </summary>
    private void CollectNode(uint nodeVa, TreeGeometryKind kind, List<ExtractedTreeSubmesh> output,
        HashSet<uint> seen, int depth)
    {
        if (nodeVa == 0 || depth > MaxDepth || !context.IsValidPointer(nodeVa) || !seen.Add(nodeVa))
        {
            return;
        }

        // NiGeometry leaf? m_spModelData → a NiTriShapeData we can parse.
        var modelDataVa = ReadPointer(nodeVa + NiGeometryModelDataOffset);
        if (modelDataVa != 0 && context.IsValidPointer(modelDataVa))
        {
            var mesh = _geometry.ExtractMeshAtVa(modelDataVa);
            if (mesh != null)
            {
                output.Add(new ExtractedTreeSubmesh { Mesh = mesh, Kind = kind });
                return;
            }
        }

        // Otherwise, descend as a NiNode.
        var childArrayVa = ReadPointer(nodeVa + NiNodeChildrenPtrOffset);
        var childCount = ReadUInt16(nodeVa + NiNodeChildrenCountOffset);
        if (childArrayVa == 0 || childCount == 0 || childCount > MaxChildren || !context.IsValidPointer(childArrayVa))
        {
            return;
        }

        for (var i = 0; i < childCount; i++)
        {
            var childVa = ReadPointer((uint)(childArrayVa + i * 4));
            CollectNode(childVa, kind, output, seen, depth + 1);
        }
    }

    private uint ReadPointer(uint va)
    {
        var bytes = context.ReadBytesAtVa(va, 4);
        return bytes is null || bytes.Length < 4 ? 0 : BinaryUtils.ReadUInt32BE(bytes, 0);
    }

    private ushort ReadUInt16(uint va)
    {
        var bytes = context.ReadBytesAtVa(va, 2);
        return bytes is null || bytes.Length < 2 ? (ushort)0 : BinaryUtils.ReadUInt16BE(bytes, 0);
    }
}

/// <summary>A tree's worth of extracted geometry, grouped from one runtime <c>BSTreeModel</c>.</summary>
public sealed record ExtractedTree
{
    public uint BSTreeModelVa { get; init; }

    public List<ExtractedTreeSubmesh> Submeshes { get; init; } = [];

    public int TotalVertices => Submeshes.Sum(s => s.Mesh.VertexCount);
    public int TotalTriangles => Submeshes.Sum(s => s.Mesh.TriangleCount);
}

public sealed record ExtractedTreeSubmesh
{
    public required ExtractedMesh Mesh { get; init; }

    public TreeGeometryKind Kind { get; init; }

    /// <summary>Resolved game texture path for this submesh, when known.</summary>
    public string? TexturePath { get; set; }
}

public enum TreeGeometryKind
{
    Branch,
    Leaf,
    Billboard,
}
