using System.Collections.Concurrent;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Walks the Gamebryo scene graph to find NiTriShape parents of extracted meshes,
///     then follows the parent chain to discover model grouping and node names.
///     Uses reverse pointer scanning: finds NiTriShape structs whose m_spModelData (+220)
///     points to a known NiTriShapeData virtual address.
///
///     NiTriShape (240 bytes, extends NiGeometry → NiAVObject → NiObjectNET → NiObject):
///       +4   m_uiRefCount (uint32 BE) — reference count
///       +8   m_kName (NiFixedString = char*) — node name
///       +24  m_pkParent (NiNode*) — parent in scene graph
///       +64  m_kLocal (NiTransform, 64 bytes) — local transform
///       +128 m_kWorld (NiTransform, 64 bytes) — world transform
///       +220 m_spModelData (NiGeometryData*) — pointer to our found NiTriShapeData
///
///     NiNode (208 bytes, extends NiAVObject):
///       +8   m_kName (NiFixedString = char*)
///       +24  m_pkParent (NiNode*)
///       +192 m_kChildren (NiTArray: +196 m_pBase, +202 m_usSize)
/// </summary>
internal sealed class RuntimeSceneGraphWalker(RuntimeMemoryContext context)
{
    private readonly RuntimeMemoryContext _context = context;
    private readonly RuntimeObjectScanner _scanner = new(context);

    #region NiTriShape/NiAVObject Offsets (PDB-verified)

    private const int RefCountOffset = 4;
    private const int NamePtrOffset = 8;         // NiObjectNET.m_kName: NiFixedString (char*)
    private const int ParentPtrOffset = 24;      // NiAVObject.m_pkParent: NiNode*
    private const int WorldTransformOffset = 128; // NiAVObject.m_kWorld: NiTransform (64 bytes)
    private const int ModelDataPtrOffset = 220;  // NiGeometry.m_spModelData: NiGeometryData*
    private const int NiTriShapeSize = 240;

    #endregion

    #region Validation Thresholds

    private const int MaxRefCount = 10_000;
    private const int MaxParentChainDepth = 32;

    #endregion

    /// <summary>
    ///     For each extracted mesh, try to find the NiTriShape that owns it and walk
    ///     the parent chain to build a name path. Returns a dictionary mapping
    ///     mesh SourceOffset → SceneGraphInfo.
    /// </summary>
    public Dictionary<long, SceneGraphInfo> WalkSceneGraph(IReadOnlyList<ExtractedMesh> meshes)
    {
        var log = Logger.Instance;
        var minidump = _context.MinidumpInfo;

        // Step 1: Convert mesh file offsets to VAs and build lookup set
        var vaToMesh = new Dictionary<uint, ExtractedMesh>();
        foreach (var mesh in meshes)
        {
            var va = minidump.FileOffsetToVirtualAddress(mesh.SourceOffset);
            if (va is > 0 and <= uint.MaxValue)
            {
                vaToMesh.TryAdd((uint)va.Value, mesh);
            }
        }

        if (vaToMesh.Count == 0)
        {
            log.Debug("SceneGraphWalker: no mesh VAs resolved, skipping");
            return [];
        }

        log.Info("SceneGraphWalker: searching for NiTriShape parents of {0} meshes", vaToMesh.Count);

        // Step 2: Scan for NiTriShape structs whose m_spModelData matches a known mesh VA
        var results = new ConcurrentDictionary<long, SceneGraphInfo>();

        _scanner.ScanAligned(
            candidateTest: (chunk, offset) => FastFilter(chunk, offset, vaToMesh),
            processCandidate: (chunk, offset, fileOffset) =>
            {
                var modelDataPtr = BinaryUtils.ReadUInt32BE(chunk, offset + ModelDataPtrOffset);
                if (!vaToMesh.TryGetValue(modelDataPtr, out var mesh))
                {
                    return;
                }

                var info = BuildSceneGraphInfo(chunk, offset, fileOffset);
                if (info != null)
                {
                    results.TryAdd(mesh.SourceOffset, info);
                }
            },
            minStructSize: NiTriShapeSize);

        log.Info("SceneGraphWalker: resolved {0}/{1} meshes to scene graph nodes",
            results.Count, vaToMesh.Count);

        return new Dictionary<long, SceneGraphInfo>(results);
    }

    /// <summary>
    ///     Fast filter: check if this 16-byte aligned offset could be a NiTriShape
    ///     whose m_spModelData points to one of our known meshes.
    /// </summary>
    private static bool FastFilter(byte[] chunk, int offset, Dictionary<uint, ExtractedMesh> vaToMesh)
    {
        if (offset + NiTriShapeSize > chunk.Length)
        {
            return false;
        }

        // Primary check: m_spModelData at +220 must match a known mesh VA
        var modelDataPtr = BinaryUtils.ReadUInt32BE(chunk, offset + ModelDataPtrOffset);
        if (!vaToMesh.ContainsKey(modelDataPtr))
        {
            return false;
        }

        // Secondary: refcount must be valid
        var refCount = BinaryUtils.ReadUInt32BE(chunk, offset + RefCountOffset);
        if (refCount == 0 || refCount > MaxRefCount)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Build scene graph info by reading the NiTriShape node name and walking
    ///     the parent chain up to the root.
    /// </summary>
    private SceneGraphInfo? BuildSceneGraphInfo(byte[] chunk, int offset, long fileOffset)
    {
        // Read this node's name
        var namePtr = BinaryUtils.ReadUInt32BE(chunk, offset + NamePtrOffset);
        var nodeName = ReadNiFixedString(namePtr);

        // Read world transform translation (for position info)
        var worldX = BinaryUtils.ReadFloatBE(chunk, offset + WorldTransformOffset + 48);
        var worldY = BinaryUtils.ReadFloatBE(chunk, offset + WorldTransformOffset + 52);
        var worldZ = BinaryUtils.ReadFloatBE(chunk, offset + WorldTransformOffset + 56);

        // Walk parent chain to collect names
        var parentNames = new List<string>();
        var parentPtr = BinaryUtils.ReadUInt32BE(chunk, offset + ParentPtrOffset);
        var rootVa = 0u;
        var depth = 0;

        while (parentPtr != 0 && _context.IsValidPointer(parentPtr) && depth < MaxParentChainDepth)
        {
            var parentFileOffset = _context.VaToFileOffset(parentPtr);
            if (parentFileOffset == null)
            {
                break;
            }

            // Read the parent NiNode (need at least 208 bytes, but 32 is enough for name + parent)
            var parentBuf = _context.ReadBytes(parentFileOffset.Value, 32);
            if (parentBuf == null)
            {
                break;
            }

            // Read parent's name
            var parentNamePtr = BinaryUtils.ReadUInt32BE(parentBuf, NamePtrOffset);
            var parentName = ReadNiFixedString(parentNamePtr);
            if (parentName != null)
            {
                parentNames.Add(parentName);
            }

            rootVa = parentPtr;

            // Move to grandparent
            parentPtr = BinaryUtils.ReadUInt32BE(parentBuf, ParentPtrOffset);
            depth++;
        }

        return new SceneGraphInfo
        {
            NodeName = nodeName,
            ParentNames = parentNames.ToArray(),
            RootNodeVa = rootVa,
            WorldX = RuntimeMemoryContext.IsNormalFloat(worldX) ? worldX : 0,
            WorldY = RuntimeMemoryContext.IsNormalFloat(worldY) ? worldY : 0,
            WorldZ = RuntimeMemoryContext.IsNormalFloat(worldZ) ? worldZ : 0,
            NiTriShapeFileOffset = fileOffset
        };
    }

    /// <summary>
    ///     Read a NiFixedString (char* → null-terminated ASCII string).
    /// </summary>
    private string? ReadNiFixedString(uint ptr)
    {
        if (ptr == 0 || !_context.IsValidPointer(ptr))
        {
            return null;
        }

        var fileOffset = _context.VaToFileOffset(ptr);
        if (fileOffset == null)
        {
            return null;
        }

        var buf = _context.ReadBytes(fileOffset.Value, 128);
        if (buf == null)
        {
            return null;
        }

        // Find null terminator, validate ASCII
        for (var i = 0; i < buf.Length; i++)
        {
            if (buf[i] == 0)
            {
                return i == 0 ? null : Encoding.ASCII.GetString(buf, 0, i);
            }

            if (buf[i] < 32 || buf[i] > 126)
            {
                return null; // Not valid ASCII
            }
        }

        return null; // No null terminator found
    }
}

/// <summary>
///     Scene graph information for an extracted mesh, discovered by walking
///     the NiTriShape → NiNode parent chain.
/// </summary>
public record SceneGraphInfo
{
    /// <summary>Name of the NiTriShape node (e.g., "Tri Mesh 0", "PipBoy").</summary>
    public string? NodeName { get; init; }

    /// <summary>Names from parent chain, leaf-to-root order (e.g., ["Building01", "Scene Root"]).</summary>
    public required string[] ParentNames { get; init; }

    /// <summary>Virtual address of the root NiNode (for grouping meshes into models).</summary>
    public uint RootNodeVa { get; init; }

    /// <summary>World-space X position from NiAVObject.m_kWorld transform.</summary>
    public float WorldX { get; init; }

    /// <summary>World-space Y position from NiAVObject.m_kWorld transform.</summary>
    public float WorldY { get; init; }

    /// <summary>World-space Z position from NiAVObject.m_kWorld transform.</summary>
    public float WorldZ { get; init; }

    /// <summary>File offset of the NiTriShape struct.</summary>
    public long NiTriShapeFileOffset { get; init; }

    /// <summary>
    ///     The best display name: first parent with a .NIF path, first non-generic parent,
    ///     the node name, or the root name as fallback.
    /// </summary>
    public string? ModelName
    {
        get
        {
            // Prefer a parent that contains a .NIF path (indicates the model file)
            foreach (var parent in ParentNames)
            {
                if (parent.Contains(".NIF", StringComparison.OrdinalIgnoreCase))
                {
                    return parent;
                }
            }

            // Skip generic engine nodes, find first meaningful name
            // ParentNames is leaf-to-root, so earlier entries are more specific
            foreach (var parent in ParentNames)
            {
                if (!IsGenericNodeName(parent))
                {
                    return parent;
                }
            }

            return NodeName;
        }
    }

    private static bool IsGenericNodeName(string name) =>
        name is "WorldRoot Node" or "MenuRoot Node" or "shadow scene node"
            or "ObjectLODRoot" or "StaticNode" or "Scene Root";

    /// <summary>
    ///     Full path from root to leaf (e.g., "Scene Root/Building01/Tri Mesh 0").
    /// </summary>
    public string FullPath
    {
        get
        {
            var parts = new List<string>();
            for (var i = ParentNames.Length - 1; i >= 0; i--)
            {
                parts.Add(ParentNames[i]);
            }

            if (NodeName != null)
            {
                parts.Add(NodeName);
            }

            return parts.Count > 0 ? string.Join("/", parts) : "(unnamed)";
        }
    }
}
