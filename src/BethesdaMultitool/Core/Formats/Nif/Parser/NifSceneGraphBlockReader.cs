using System.Text;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Parser;

/// <summary>
///     Reads scene-graph and shape linkage fields from common NIF block types.
/// </summary>
internal static class NifSceneGraphBlockReader
{
    /// <summary>BSGeometry always writes four LOD slots (nif.xml <c>length="4"</c>); LOD 0 is finest.</summary>
    private const int BsGeometryLodCount = 4;

    /// <summary>
    ///     Sanity cap on a mesh-path SizedString. Retail paths are two 20-char hex components plus a
    ///     separator (41 chars); anything near this is a misparse, not a long name.
    /// </summary>
    private const int MaxMeshPathLength = 512;

    internal static int ParseShapeSkinInstanceRef(
        byte[] data,
        BlockInfo block,
        uint bsVersion,
        uint binaryVersion,
        bool be,
        bool hasInlineStrings = false)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        if (!SkipNiGeometryHeader(
                data,
                ref pos,
                end,
                bsVersion,
                binaryVersion,
                be,
                hasInlineStrings))
        {
            return -1;
        }

        if (pos + 4 > end)
        {
            return -1;
        }

        pos += 4;

        if (pos + 4 > end)
        {
            return -1;
        }

        return BinaryUtils.ReadInt32(data, pos, be);
    }

    internal static int[]? ParseDismemberPartitions(byte[] data, BlockInfo block, bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        if (pos + 12 > end)
        {
            return null;
        }

        pos += 12;

        if (pos + 4 > end)
        {
            return null;
        }

        var numBones = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        pos += (int)Math.Min(numBones, 500) * 4;

        if (pos + 4 > end)
        {
            return null;
        }

        var numPartitions = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (numPartitions == 0 || numPartitions > 100 || pos + numPartitions * 4 > end)
        {
            return null;
        }

        var bodyParts = new int[(int)numPartitions];
        for (var i = 0; i < numPartitions; i++)
        {
            pos += 2;
            bodyParts[i] = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;
        }

        return bodyParts;
    }

    internal static bool IsDismemberGoreShape(int[]? bodyParts)
    {
        if (bodyParts == null || bodyParts.Length == 0)
        {
            return false;
        }

        foreach (var bodyPart in bodyParts)
        {
            if (bodyPart >= 100 && bodyPart <= 299)
            {
                return true;
            }
        }

        return false;
    }

    internal static int ParseGeometryAdditionalDataRef(
        byte[] data,
        BlockInfo block,
        uint bsVersion,
        uint binaryVersion,
        bool be)
    {
        // Morrowind NiGeometryData has no Group ID and is read by the dedicated Morrowind extractor;
        // its Additional Data ref (added at 20.0.0.4) does not exist, so report none.
        if (NifVersions.IsLegacyNetImmerse(binaryVersion))
        {
            return -1;
        }

        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        pos += 4;

        if (pos + 2 > end)
        {
            return -1;
        }

        var numVertices = BinaryUtils.ReadUInt16(data, pos, be);
        pos += 2;

        if (bsVersion >= 34)
        {
            pos += 2;
        }

        if (pos + 1 > end)
        {
            return -1;
        }

        if (data[pos++] != 0)
        {
            pos += numVertices * 12;
        }

        ushort bsVectorFlags = 0;
        if (bsVersion >= 34)
        {
            if (pos + 2 > end)
            {
                return -1;
            }

            bsVectorFlags = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;
        }

        if (pos + 1 > end)
        {
            return -1;
        }

        if (data[pos++] != 0)
        {
            pos += numVertices * 12;
            if (bsVersion >= 34)
            {
                pos += 16;
                if ((bsVectorFlags & 0x1000) != 0)
                {
                    pos += numVertices * 24;
                }
            }
        }
        else if (bsVersion >= 34)
        {
            pos += 16;
        }

        if (pos + 1 > end)
        {
            return -1;
        }

        if (data[pos++] != 0)
        {
            pos += numVertices * 16;
        }

        if (bsVersion >= 34)
        {
            var numUvSets = (bsVectorFlags & 0x0001) != 0 ? 1 : 0;
            pos += numVertices * numUvSets * 8;
        }
        else
        {
            if (pos + 4 > end)
            {
                return -1;
            }

            var numUvSets = BinaryUtils.ReadUInt16(data, pos, be) & 0x3F;
            pos += 4;
            pos += numVertices * numUvSets * 8;
        }

        pos += 2;
        if (pos + 4 > end)
        {
            return -1;
        }

        return BinaryUtils.ReadInt32(data, pos, be);
    }

    internal static int ReadVertexCount(byte[] data, BlockInfo block, bool be, uint binaryVersion)
    {
        // NiGeometryData.Group ID (int) is present only since NIF 10.1.0.114. Num Vertices is the first
        // field when it's absent — Morrowind (4.0.0.2) and Oblivion's 10.1.0.106 architecture — and sits
        // 4 bytes in otherwise (10.2.0.0 / 20.0.0.x / FNV).
        var pos = block.DataOffset + (NifVersions.HasGeometryGroupId(binaryVersion) ? 4 : 0);
        return pos + 2 > block.DataOffset + block.Size
            ? -1
            : BinaryUtils.ReadUInt16(data, pos, be);
    }

    internal static List<int>? ParseNodeChildren(
        byte[] data,
        BlockInfo block,
        uint bsVersion,
        uint binaryVersion,
        bool be,
        bool hasInlineStrings = false)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        if (!SkipNiGeometryHeader(
                data,
                ref pos,
                end,
                bsVersion,
                binaryVersion,
                be,
                hasInlineStrings))
        {
            return null;
        }

        if (pos + 4 > end)
        {
            return null;
        }

        var numChildren = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (numChildren > 500 || pos + numChildren * 4 > end)
        {
            return null;
        }

        var children = new List<int>((int)numChildren);
        for (var i = 0; i < numChildren; i++)
        {
            var childRef = BinaryUtils.ReadInt32(data, pos, be);
            pos += 4;
            if (childRef >= 0)
            {
                children.Add(childRef);
            }
        }

        return children;
    }

    /// <summary>
    ///     Reads a NiNode's dynamic-effects list — the refs that follow the Children array
    ///     (nif.xml: present through Skyrim SE, removed for FO4+ via <c>#NI_BS_LT_FO4#</c>).
    ///     Returns null for a malformed block, FO4+ streams, or an empty list. Bethesda's TES3
    ///     exporter lists NiTextureEffects only among CHILDREN with an empty Effects array
    ///     (byte-verified on <c>meshes\a\a_glass_boots_gnd.nif</c>), so consumers must check both.
    /// </summary>
    internal static List<int>? ParseNodeEffects(
        byte[] data,
        BlockInfo block,
        uint bsVersion,
        uint binaryVersion,
        bool be,
        bool hasInlineStrings = false)
    {
        if (bsVersion >= 130)
        {
            return null; // FO4+ NiNode carries no effects array
        }

        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        if (!SkipNiGeometryHeader(
                data,
                ref pos,
                end,
                bsVersion,
                binaryVersion,
                be,
                hasInlineStrings) ||
            pos + 4 > end)
        {
            return null;
        }

        var numChildren = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (numChildren > 500 || pos + numChildren * 4 > end)
        {
            return null;
        }

        pos += (int)numChildren * 4;
        if (pos + 4 > end)
        {
            return null;
        }

        var numEffects = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (numEffects == 0 || numEffects > 500 || pos + numEffects * 4 > end)
        {
            return null;
        }

        var effects = new List<int>((int)numEffects);
        for (var i = 0; i < numEffects; i++)
        {
            var effectRef = BinaryUtils.ReadInt32(data, pos, be);
            pos += 4;
            if (effectRef >= 0)
            {
                effects.Add(effectRef);
            }
        }

        return effects.Count > 0 ? effects : null;
    }

    /// <summary>
    ///     Reads a <c>NiSwitchNode</c>'s active-child ordinal. Returns <see langword="null" /> for a
    ///     malformed block and <c>-1</c> when the authored index selects no child.
    /// </summary>
    internal static int? ParseSwitchNodeActiveChildOrdinal(
        byte[] data,
        BlockInfo block,
        uint bsVersion,
        uint binaryVersion,
        bool be,
        bool hasInlineStrings = false)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        if (!SkipNiGeometryHeader(
                data,
                ref pos,
                end,
                bsVersion,
                binaryVersion,
                be,
                hasInlineStrings) ||
            pos + 4 > end)
        {
            return null;
        }

        var numChildren = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (numChildren > 500 || pos + numChildren * 4 > end)
        {
            return null;
        }

        pos += (int)numChildren * 4;

        // NiNode carries a dynamic-effects array before NiSwitchNode's own fields through Skyrim
        // SE; FO4 (BS stream 130+) removes it (#NI_BS_LT_FO4#).
        if (bsVersion < 130)
        {
            if (pos + 4 > end)
            {
                return null;
            }

            var numEffects = BinaryUtils.ReadUInt32(data, pos, be);
            pos += 4;
            if (numEffects > 500 || pos + numEffects * 4 > end)
            {
                return null;
            }

            pos += (int)numEffects * 4;
        }

        // Switch Node Flags (ushort) + Index (uint).
        if (pos + 6 > end)
        {
            return null;
        }

        pos += 2;
        var active = BinaryUtils.ReadUInt32(data, pos, be);
        return active < numChildren ? (int)active : -1;
    }

    internal static int ParseShapeDataRef(byte[] data, BlockInfo block, uint bsVersion, uint binaryVersion,
        bool be, bool hasInlineStrings = false)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        if (!SkipNiGeometryHeader(
                data,
                ref pos,
                end,
                bsVersion,
                binaryVersion,
                be,
                hasInlineStrings) ||
            pos + 4 > end)
        {
            return -1;
        }

        return BinaryUtils.ReadInt32(data, pos, be);
    }

    internal static List<int>? ParseShapePropertyRefs(
        byte[] data,
        BlockInfo block,
        uint bsVersion,
        uint binaryVersion,
        bool be,
        bool hasInlineStrings = false)
    {
        // Skyrim and later (BS stream > 34) replaced the NiAVObject "Properties" array with two
        // dedicated NiGeometry refs: Shader Property + Alpha Property, located after the
        // Data ref, Skin Instance ref, and the variable-length Material Data block.
        if (bsVersion > 34)
        {
            return ParseNiGeometryBsPropertyRefs(data, block, bsVersion, binaryVersion, be);
        }

        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        if (!NifBinaryCursor.SkipNiObjectNET(data, ref pos, end, be, hasInlineStrings, binaryVersion))
        {
            return null;
        }

        pos += bsVersion > 26 ? 4 : 2; // Flags
        pos += 12 + 36 + 4; // Translation + Rotation + Scale

        if (NifVersions.HasAvObjectVelocity(binaryVersion))
        {
            pos += 12; // Velocity (Vector3, until 4.2.2.0) precedes the Properties array
        }

        if (pos + 4 > end)
        {
            return null;
        }

        var numProperties = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (numProperties == 0 || numProperties > 100 || pos + numProperties * 4 > end)
        {
            return null;
        }

        var refs = new List<int>((int)numProperties);
        for (var i = 0; i < numProperties; i++)
        {
            var propRef = BinaryUtils.ReadInt32(data, pos, be);
            pos += 4;
            if (propRef >= 0)
            {
                refs.Add(propRef);
            }
        }

        return refs;
    }

    /// <summary>
    ///     Read the Shader Property + Alpha Property refs from a Skyrim+ NiGeometry shape (e.g. the
    ///     NiTriShape used by Skyrim LE), which carry the BSLightingShaderProperty / NiAlphaProperty
    ///     in place of the old Properties array. Layout after the NiAVObject base: Data ref(4) +
    ///     Skin Instance ref(4) + MaterialData(variable) + Shader Property(4) + Alpha Property(4).
    /// </summary>
    private static List<int>? ParseNiGeometryBsPropertyRefs(
        byte[] data, BlockInfo block, uint bsVersion, uint binaryVersion, bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        if (!SkipNiGeometryHeader(data, ref pos, end, bsVersion, binaryVersion, be))
        {
            return null;
        }

        pos += 4; // Data ref
        pos += 4; // Skin Instance ref

        if (!SkipMaterialData(data, ref pos, end, be) || pos + 8 > end)
        {
            return null;
        }

        var shaderRef = BinaryUtils.ReadInt32(data, pos, be);
        var alphaRef = BinaryUtils.ReadInt32(data, pos + 4, be);

        var refs = new List<int>(2);
        if (shaderRef >= 0 && shaderRef < int.MaxValue)
        {
            refs.Add(shaderRef);
        }

        if (alphaRef >= 0 && alphaRef < int.MaxValue)
        {
            refs.Add(alphaRef);
        }

        return refs.Count > 0 ? refs : null;
    }

    /// <summary>
    ///     Skip a NiGeometry MaterialData block (NIF 20.2.0.7 form): Num Materials(4) +
    ///     Material Names(NiFixedString index ×N) + Material Extra Data(int ×N) + Active Material(4)
    ///     + Material Needs Update(bool). Returns false if the block is too small/inconsistent.
    /// </summary>
    private static bool SkipMaterialData(byte[] data, ref int pos, int end, bool be)
    {
        if (pos + 4 > end)
        {
            return false;
        }

        var numMaterials = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        if (numMaterials > 1000)
        {
            return false;
        }

        pos += (int)numMaterials * 4; // Material Name (NiFixedString string indices)
        pos += (int)numMaterials * 4; // Material Extra Data
        pos += 4; // Active Material
        pos += 1; // Material Needs Update
        return pos <= end;
    }

    /// <summary>
    ///     Parses a Starfield <c>BSGeometry</c> block (nif.xml, <c>versions="#STF#"</c>). Layout after
    ///     the NiAVObject base is Bounding Sphere (NiBound, 16) + Bounding Box (BSBoundingBox, 24) +
    ///     Skin/Shader/Alpha refs (3 × int32) + <c>Meshes</c>, a fixed array of four
    ///     <c>BSMeshArray</c> (LOD 0 first).
    ///     <para>
    ///         Each BSMeshArray is a <c>Has Mesh</c> byte, then when set a BSMesh of
    ///         <c>{ u32 Indices Size, u32 Num Verts, u32 Flags }</c> followed by EITHER a SizedString
    ///         mesh path or inline BSMeshData — selected by the BSGeometry's own <c>Flags &amp; 512</c>
    ///         (nif.xml threads it through as the struct argument). Inline data is not modelled; those
    ///         blocks return a null path and are skipped rather than misread.
    ///     </para>
    ///     Returns null when the block is truncated or the NiObjectNET base cannot be walked.
    /// </summary>
    internal static BsGeometryInfo? ParseBsGeometry(
        byte[] data, BlockInfo block, uint binaryVersion, bool be, bool hasInlineStrings)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        if (!NifBinaryCursor.SkipNiObjectNET(data, ref pos, end, be, hasInlineStrings, binaryVersion))
        {
            return null;
        }

        // The NiAVObject base is read rather than skipped here: BSGeometry's Flags field is what
        // decides whether the meshes below carry a path or inline data, so SkipNiGeometryHeader
        // (which discards it) cannot be reused.
        if (pos + 4 > end)
        {
            return null;
        }

        var flags = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4; // Flags
        pos += 12 + 36 + 4; // Translation (Vector3) + Rotation (Matrix33) + Scale (float)
        pos += 4; // Collision Object ref
        pos += 16; // Bounding Sphere (NiBound)
        pos += 24; // Bounding Box (BSBoundingBox)

        if (pos + 12 > end)
        {
            return null;
        }

        var skinRef = BinaryUtils.ReadInt32(data, pos, be);
        var shaderRef = BinaryUtils.ReadInt32(data, pos + 4, be);
        var alphaRef = BinaryUtils.ReadInt32(data, pos + 8, be);
        pos += 12;

        var carriesInlineMeshData = (flags & 512) != 0;
        string? meshPath = null;
        for (var lod = 0; lod < BsGeometryLodCount && meshPath is null && !carriesInlineMeshData; lod++)
        {
            if (pos + 1 > end)
            {
                break;
            }

            var hasMesh = data[pos];
            pos += 1;
            if (hasMesh != 1)
            {
                continue;
            }

            pos += 12; // Indices Size + Num Verts + Flags (all recoverable from the blob itself)
            if (pos + 4 > end)
            {
                break;
            }

            var length = BinaryUtils.ReadUInt32(data, pos, be);
            pos += 4;
            if (length == 0 || length > MaxMeshPathLength || pos + length > end)
            {
                break;
            }

            meshPath = Encoding.ASCII.GetString(data, pos, (int)length);
            pos += (int)length;
        }

        return new BsGeometryInfo(skinRef, shaderRef, alphaRef, meshPath);
    }

    /// <summary>
    ///     Parse a BSTriShape block's refs, vertex descriptor, counts, and buffer offsets. Returns
    ///     null if the block is too small or self-inconsistent. Only valid for bsVersion ≥ 100
    ///     (BSTriShape was introduced with Skyrim Special Edition).
    /// </summary>
    internal static BsTriShapeInfo? ParseBsTriShape(
        byte[] data, BlockInfo block, uint bsVersion, uint binaryVersion, bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // SkipNiGeometryHeader lands at the first NiAVObject-derived field. For bsVersion > 34 it
        // skips NiObjectNET + flags + transform + collision ref (no Properties array), which is
        // exactly the BSTriShape NiAVObject base — pos now points at the Bounding Sphere.
        if (!SkipNiGeometryHeader(data, ref pos, end, bsVersion, binaryVersion, be))
        {
            return null;
        }

        pos += 16; // Bounding Sphere (NiBound: Center vec3 + Radius float).

        // Fallout 76 (bsVersion ≥ 155) inserts a BSBoundingBox (Center vec3 + Dimensions vec3) right
        // after the Bounding Sphere — nif.xml BSTriShape, vercond #BS_GTE_F76#. Skipping it keeps the
        // skin/shader/alpha refs, vertex descriptor and counts aligned (else FO76 reads garbage counts
        // and the shape is dropped as inconsistent → "no renderable geometry").
        if (bsVersion >= 155)
        {
            pos += 24;
        }

        // Skin / Shader Property / Alpha Property refs (3 × int32).
        if (pos + 12 + 8 > end)
        {
            return null;
        }

        var skinRef = BinaryUtils.ReadInt32(data, pos, be);
        var shaderRef = BinaryUtils.ReadInt32(data, pos + 4, be);
        var alphaRef = BinaryUtils.ReadInt32(data, pos + 8, be);
        pos += 12;

        var vertexDesc = BinaryUtils.ReadUInt64(data, pos, be);
        pos += 8;

        // Num Triangles widened to uint32 in Fallout 4 (bsVersion ≥ 130); ushort in Skyrim SE.
        int numTriangles;
        if (bsVersion >= 130)
        {
            if (pos + 4 > end)
            {
                return null;
            }

            numTriangles = (int)BinaryUtils.ReadUInt32(data, pos, be);
            pos += 4;
        }
        else
        {
            if (pos + 2 > end)
            {
                return null;
            }

            numTriangles = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;
        }

        if (pos + 6 > end)
        {
            return null;
        }

        var numVertices = BinaryUtils.ReadUInt16(data, pos, be);
        pos += 2;
        pos += 4; // Data Size (recomputable from vertexDesc/counts; not needed)

        var vertexSize = (int)(vertexDesc & 0xF) * 4;
        var vertexBufferOffset = pos;
        var triangleBufferOffset = vertexBufferOffset + numVertices * vertexSize;

        if (vertexSize == 0 ||
            triangleBufferOffset + numTriangles * 6 > end)
        {
            return null;
        }

        return new BsTriShapeInfo(
            skinRef,
            shaderRef,
            alphaRef,
            vertexDesc,
            numVertices,
            numTriangles,
            vertexBufferOffset,
            triangleBufferOffset);
    }

    private static bool SkipNiGeometryHeader(
        byte[] data,
        ref int pos,
        int end,
        uint bsVersion,
        uint binaryVersion,
        bool be,
        bool hasInlineStrings = false)
    {
        if (!NifBinaryCursor.SkipNiObjectNET(data, ref pos, end, be, hasInlineStrings, binaryVersion))
        {
            return false;
        }

        pos += bsVersion > 26 ? 4 : 2; // Flags (uint Bethesda > 26, else ushort)
        pos += 12 + 36 + 4; // Translation (Vector3) + Rotation (Matrix33) + Scale (float)

        if (NifVersions.HasAvObjectVelocity(binaryVersion))
        {
            pos += 12; // Velocity (Vector3, until 4.2.2.0)
        }

        if (bsVersion <= 34)
        {
            if (pos + 4 > end)
            {
                return false;
            }

            var numProperties = BinaryUtils.ReadUInt32(data, pos, be);
            pos += 4;
            pos += (int)Math.Min(numProperties, 100) * 4;
        }

        if (pos + 4 > end)
        {
            return false;
        }

        if (!NifVersions.HasCollisionObjectRef(binaryVersion))
        {
            // Has Bounding Volume (bool32) replaces the Collision Object ref (added at 10.0.1.0). The
            // BoundingVolume union that follows when set is rare on shape/node bases and not modeled
            // here; bail so the block is skipped rather than desynced (each block's offset/size come
            // from the authoritative measure pass, so a skipped block never corrupts the others).
            var hasBoundingVolume = BinaryUtils.ReadUInt32(data, pos, be);
            pos += 4;
            return hasBoundingVolume == 0 && pos <= end;
        }

        pos += 4; // Collision Object ref
        return pos <= end;
    }

    /// <summary>
    ///     A Starfield <c>BSGeometry</c> block: the property refs plus the path of the external
    ///     <c>.mesh</c> blob that holds its actual vertex/index data. Starfield keeps NO geometry in
    ///     the NIF — the scene graph names a blob and the renderer loads it separately.
    /// </summary>
    /// <param name="MeshPath">
    ///     The highest-detail LOD's blob path, WITHOUT the <c>geometries\</c> prefix or <c>.mesh</c>
    ///     suffix (both are implied — see <c>StarfieldMeshFile</c>). Null when the block declares
    ///     inline mesh data instead, or names no blob at all.
    /// </param>
    internal readonly record struct BsGeometryInfo(
        int SkinRef,
        int ShaderRef,
        int AlphaRef,
        string? MeshPath);

    /// <summary>
    ///     Header + buffer layout of a self-contained BSTriShape block (Skyrim SE / Fallout 4 /
    ///     Fallout 76). Unlike NiTriShape, the vertex and index buffers live in the shape block
    ///     itself rather than a separate NiTriShapeData block.
    /// </summary>
    internal readonly record struct BsTriShapeInfo(
        int SkinRef,
        int ShaderRef,
        int AlphaRef,
        ulong VertexDesc,
        int NumVertices,
        int NumTriangles,
        int VertexBufferOffset,
        int TriangleBufferOffset);
}
