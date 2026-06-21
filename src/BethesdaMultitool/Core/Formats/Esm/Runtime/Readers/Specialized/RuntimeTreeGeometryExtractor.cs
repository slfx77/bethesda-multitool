using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Scanning;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Extracts real engine-generated SpeedTree geometry from a memory dump. At runtime
///     <c>CSpeedTreeRT::Compute</c> + <c>BSTreeModel::CreateGeometry</c> build standard Gamebryo
///     <c>NiTriShapeData</c> arrays in <c>BSTreeModel</c>; this reads those arrays directly
///     (reusing <see cref="RuntimeGeometryScanner" />), and only walks the billboard shape pointer,
///     yielding the actual vertices/UVs/indices instead of the procedural approximation.
/// </summary>
internal sealed class RuntimeTreeGeometryExtractor(RuntimeMemoryContext context)
{
    // BSTreeModel data members (PDB + speedtree_decompiled.txt/Create{Branch,Leaf,Billboard}Geometry).
    private const int BranchDataArrayOffset = 0x14; // pspBranchData: NiTriShapeData* array
    private const int LeafDataArrayOffset = 0x18;   // pspLeafData: NiTriShapeData* array
    private const int BillboardShapeOffset = 0x1C;  // spBillboard: NiTriShape/NiTriStrips shape

    // Ni* runtime offsets (PDB / RuntimeSceneGraphWalker).
    private const int NiNodeChildrenPtrOffset = 196; // m_kChildren NiTArray m_pBase
    private const int NiNodeChildrenCountOffset = 202; // m_kChildren m_usSize (uint16)
    private const int NiGeometryModelDataOffset = 220; // NiGeometry.m_spModelData → NiTriShapeData
    private const int MaxChildren = 4096;
    private const int MaxDepth = 12;
    private const int MaxGeometrySlots = 4096;

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

        CollectGeometryDataArray(
            ReadPointer(bsTreeModelVa + BranchDataArrayOffset),
            TreeGeometryKind.Branch,
            submeshes,
            seen);
        CollectGeometryDataArray(
            ReadPointer(bsTreeModelVa + LeafDataArrayOffset),
            TreeGeometryKind.Leaf,
            submeshes,
            seen);
        CollectNode(ReadPointer(bsTreeModelVa + BillboardShapeOffset), TreeGeometryKind.Billboard, submeshes, seen, 0);

        return new ExtractedTree { BSTreeModelVa = bsTreeModelVa, Submeshes = submeshes };
    }

    private void CollectGeometryDataArray(uint arrayVa, TreeGeometryKind kind, List<ExtractedTreeSubmesh> output,
        HashSet<uint> seen)
    {
        if (arrayVa == 0 || !context.IsValidPointer(arrayVa) || arrayVa < 4)
        {
            return;
        }

        // The engine allocates one uint32 count followed by count pointers, then stores array+4
        // in BSTreeModel (+0x14 for branches, +0x18 for leaves).
        var count = ReadUInt32(arrayVa - 4);
        if (count == 0 || count > MaxGeometrySlots)
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            var dataVa = ReadPointer((uint)(arrayVa + i * 4));
            if (dataVa == 0 || !context.IsValidPointer(dataVa) || !seen.Add(dataVa))
            {
                continue;
            }

            var mesh = _geometry.ExtractSpeedTreeMeshAtVa(
                dataVa,
                allowImplicitLeafCardIndices: kind == TreeGeometryKind.Leaf);
            if (mesh != null)
            {
                output.Add(new ExtractedTreeSubmesh { Mesh = mesh, Kind = kind });
            }
        }
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

    // A runtime pointer is just a big-endian uint32 at the given VA.
    private uint ReadPointer(uint va) => ReadUInt32(va);

    private ushort ReadUInt16(uint va)
    {
        var bytes = context.ReadBytesAtVa(va, 2);
        return bytes is null || bytes.Length < 2 ? (ushort)0 : BinaryUtils.ReadUInt16BE(bytes, 0);
    }

    private uint ReadUInt32(uint va)
    {
        var bytes = context.ReadBytesAtVa(va, 4);
        return bytes is null || bytes.Length < 4 ? 0 : BinaryUtils.ReadUInt32BE(bytes, 0);
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

/// <summary>One submesh of an extracted tree (its mesh, geometry kind, and resolved texture).</summary>
public sealed record ExtractedTreeSubmesh
{
    public required ExtractedMesh Mesh { get; init; }

    public TreeGeometryKind Kind { get; init; }

    /// <summary>Resolved game texture path for this submesh, when known.</summary>
    public string? TexturePath { get; set; }
}

/// <summary>The kind of geometry a tree submesh represents (branch, leaf, or billboard).</summary>
public enum TreeGeometryKind
{
    Branch,
    Leaf,
    Billboard,
}
