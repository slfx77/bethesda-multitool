using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using FalloutXbox360Utils.Core.Formats.Nif.Geometry;
using FalloutXbox360Utils.Core.Formats.Nif.Skinning;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Extracts all renderable geometry from a NIF file with scene graph transforms applied.
///     Handles NiTriShapeData (explicit triangles) and NiTriStripsData (triangle strips).
///     Walks the NiNode scene graph to accumulate translation/rotation/scale transforms.
/// </summary>
internal static class NifGeometryExtractor
{
    private static readonly HashSet<string> NodeTypes =
        ["NiNode", "BSFadeNode", "BSMultiBoundNode", "BSOrderedNode", "BSLeafAnimNode"];

    private static readonly HashSet<string> ShapeTypes = ["NiTriShape", "NiTriStrips", "BSLODTriShape"];

    /// <summary>Parsed NiSkinInstance: links a skinned shape to its skeleton and NiSkinData.</summary>
    private sealed class SkinInstanceData
    {
        public int DataRef;              // → NiSkinData block index
        public int SkinPartitionRef;     // → NiSkinPartition block index
        public int SkeletonRootRef;      // → NiNode block index
        public int[] BoneRefs = [];      // → NiNode block indices per bone
    }

    /// <summary>Parsed NiSkinData: overall skin transform + per-bone inverse bind pose.</summary>
    private sealed class SkinData
    {
        public Matrix4x4 OverallTransform;
        public BoneSkinInfo[] Bones = [];
        public bool HasVertexWeights;
    }

    /// <summary>Per-bone skinning data from NiSkinData.BoneList.</summary>
    private sealed class BoneSkinInfo
    {
        public Matrix4x4 InverseBindPose;
        public (ushort VertexIndex, float Weight)[] VertexWeights = [];
    }

    /// <summary>
    ///     Extract all renderable geometry from a parsed NIF file.
    /// </summary>
    /// <param name="data">Raw NIF file bytes.</param>
    /// <param name="nif">Parsed NIF header info.</param>
    /// <param name="textureResolver">Optional texture resolver for extracting diffuse texture paths.</param>
    public static NifRenderableModel? Extract(byte[] data, NifInfo nif,
        NifTextureResolver? textureResolver = null)
    {
        if (nif.Blocks.Count == 0)
        {
            return null;
        }

        // Build the scene graph: for each node, find its children and its transform
        var nodeTransforms = new Dictionary<int, Matrix4x4>();
        var nodeChildren = new Dictionary<int, List<int>>();
        var shapeDataMap = new Dictionary<int, int>(); // shape block → data block
        Dictionary<int, List<int>>? shapePropertyMap = textureResolver != null
            ? new Dictionary<int, List<int>>()
            : null;

        var shapeSkinInstanceMap = new Dictionary<int, int>();
        ClassifyBlocks(data, nif, nodeChildren, shapeDataMap, shapePropertyMap, shapeSkinInstanceMap);

        // Compute world transforms by walking the scene graph from root.
        // Static NIF transforms represent the rest pose — animation overrides are not applied
        // since NiControllerSequence keyframes define runtime motion, not the initial pose.
        ComputeWorldTransforms(data, nif, nodeChildren, nodeTransforms);

        // Extract packed geometry data for skinned shapes (Xbox 360 stores bone weights here)
        var packedGeometryMap = ExtractPackedGeometry(data, nif, shapeDataMap);

        // Build skinning data for each skinned shape
        var shapeSkinning = BuildShapeSkinningData(data, nif, shapeSkinInstanceMap, shapeDataMap,
            nodeTransforms, packedGeometryMap);

        // Extract geometry from each shape block
        var model = new NifRenderableModel();

        foreach (var (shapeIndex, dataIndex) in shapeDataMap)
        {
            // Resolve texture paths from shader properties
            string? diffusePath = null;
            string? normalMapPath = null;
            var isEmissive = false;
            if (textureResolver != null && shapePropertyMap != null &&
                shapePropertyMap.TryGetValue(shapeIndex, out var propRefs))
            {
                diffusePath = NifTextureResolver.ResolveDiffusePath(data, nif, propRefs);
                normalMapPath = NifTextureResolver.ResolveNormalMapPath(data, nif, propRefs);
                isEmissive = propRefs.Exists(r =>
                    r >= 0 && r < nif.Blocks.Count &&
                    nif.Blocks[r].TypeName == "BSShaderNoLightingProperty");
            }

            // Look up skinning data for this shape (null if not skinned)
            ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)? skinning =
                shapeSkinning.TryGetValue(shapeIndex, out var sd) ? sd : null;

            var submesh = ExtractSubmesh(data, nif, shapeIndex, dataIndex, nodeTransforms,
                diffusePath, normalMapPath, isEmissive, skinning);
            if (submesh != null)
            {
                model.Submeshes.Add(submesh);
                model.ExpandBounds(submesh.Positions);
            }
        }

        return model.HasGeometry ? model : null;
    }

    /// <summary>
    ///     Classify all blocks: identify nodes (with children), shapes (with data refs),
    ///     and build the scene graph structure.
    /// </summary>
    private static void ClassifyBlocks(byte[] data, NifInfo nif,
        Dictionary<int, List<int>> nodeChildren, Dictionary<int, int> shapeDataMap,
        Dictionary<int, List<int>>? shapePropertyMap = null,
        Dictionary<int, int>? shapeSkinInstanceMap = null)
    {
        var be = nif.IsBigEndian;

        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            var block = nif.Blocks[i];

            if (NodeTypes.Contains(block.TypeName))
            {
                var children = ParseNodeChildren(data, block, nif.BsVersion, be);
                if (children != null)
                {
                    nodeChildren[i] = children;
                }
            }
            else if (ShapeTypes.Contains(block.TypeName))
            {
                // Skip gore/dismembered shape variants by name (e.g., "UpperBodyGore", "Decapitated")
                var shapeName = ReadBlockName(data, block, nif);
                if (IsGoreShape(shapeName))
                {
                    continue;
                }

                // Skip gore shapes identified via BSDismemberSkinInstance partition data.
                // Body part IDs 100-299 are gore caps (section caps + torso caps).
                var skinRef = ParseShapeSkinInstanceRef(data, block, nif.BsVersion, be);
                if (skinRef >= 0 && skinRef < nif.Blocks.Count &&
                    nif.Blocks[skinRef].TypeName == "BSDismemberSkinInstance")
                {
                    var bodyParts = ParseDismemberPartitions(data, nif.Blocks[skinRef], be);
                    if (IsDismemberGoreShape(bodyParts))
                    {
                        continue;
                    }
                }

                // Collect skin instance ref for skeleton deformation
                if (shapeSkinInstanceMap != null && skinRef >= 0 && skinRef < nif.Blocks.Count)
                {
                    var skinBlockType = nif.Blocks[skinRef].TypeName;
                    if (skinBlockType is "NiSkinInstance" or "BSDismemberSkinInstance")
                    {
                        shapeSkinInstanceMap[i] = skinRef;
                    }
                }

                var dataRef = ParseShapeDataRef(data, block, nif.BsVersion, be);
                if (dataRef >= 0 && dataRef < nif.Blocks.Count)
                {
                    shapeDataMap[i] = dataRef;
                }

                if (shapePropertyMap != null)
                {
                    var propRefs = ParseShapePropertyRefs(data, block, nif.BsVersion, be);
                    if (propRefs != null && propRefs.Count > 0)
                    {
                        shapePropertyMap[i] = propRefs;
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Walk the scene graph depth-first from root nodes, accumulating transforms.
    ///     Animation overrides (if any) replace the local transform of targeted nodes.
    /// </summary>
    private static void ComputeWorldTransforms(byte[] data, NifInfo nif,
        Dictionary<int, List<int>> nodeChildren, Dictionary<int, Matrix4x4> worldTransforms)
    {
        // Find root nodes: nodes that are not children of any other node
        var allChildren = new HashSet<int>();
        foreach (var children in nodeChildren.Values)
        {
            foreach (var child in children)
            {
                allChildren.Add(child);
            }
        }

        // Walk from each root
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            if (!nodeChildren.ContainsKey(i) && !allChildren.Contains(i))
            {
                // Not a node and not a child — skip
                continue;
            }

            if (!allChildren.Contains(i))
            {
                // This is a root node
                WalkNode(data, nif, i, Matrix4x4.Identity, nodeChildren, worldTransforms);
            }
        }

        // Also handle shapes that are direct root children (not under any node)
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            if (ShapeTypes.Contains(nif.Blocks[i].TypeName) && !worldTransforms.ContainsKey(i) && !allChildren.Contains(i))
            {
                // Root-level shape — parse its own transform
                var localTransform = ParseNiAVObjectTransform(data, nif.Blocks[i], nif.BsVersion, nif.IsBigEndian);
                worldTransforms[i] = localTransform;
            }
        }
    }

    private static void WalkNode(byte[] data, NifInfo nif, int blockIndex, Matrix4x4 parentTransform,
        Dictionary<int, List<int>> nodeChildren, Dictionary<int, Matrix4x4> worldTransforms)
    {
        var block = nif.Blocks[blockIndex];
        var localTransform = ParseNiAVObjectTransform(data, block, nif.BsVersion, nif.IsBigEndian);

        var worldTransform = localTransform * parentTransform;
        worldTransforms[blockIndex] = worldTransform;

        if (!nodeChildren.TryGetValue(blockIndex, out var children))
        {
            return;
        }

        foreach (var childIdx in children)
        {
            if (childIdx < 0 || childIdx >= nif.Blocks.Count)
            {
                continue;
            }

            var childType = nif.Blocks[childIdx].TypeName;
            if (NodeTypes.Contains(childType))
            {
                WalkNode(data, nif, childIdx, worldTransform, nodeChildren, worldTransforms);
            }
            else if (ShapeTypes.Contains(childType))
            {
                // Shape inherits parent's world transform + its own local transform
                var shapeLocal = ParseNiAVObjectTransform(data, nif.Blocks[childIdx], nif.BsVersion, nif.IsBigEndian);
                worldTransforms[childIdx] = shapeLocal * worldTransform;
            }
        }
    }

    /// <summary>
    ///     Build skinning data (per-bone matrices + per-vertex influences) for each skinned shape.
    /// </summary>
    private static Dictionary<int, ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)>
        BuildShapeSkinningData(byte[] data, NifInfo nif,
            Dictionary<int, int> shapeSkinInstanceMap,
            Dictionary<int, int> shapeDataMap,
            Dictionary<int, Matrix4x4> worldTransforms,
            Dictionary<int, PackedGeometryData> packedGeometryMap)
    {
        var result = new Dictionary<int, ((int, float)[][], Matrix4x4[])>();
        var be = nif.IsBigEndian;

        foreach (var (shapeIndex, skinInstanceBlockIndex) in shapeSkinInstanceMap)
        {
            var skinBlock = nif.Blocks[skinInstanceBlockIndex];
            var skinInstance = ParseNiSkinInstance(data, skinBlock, be);
            if (skinInstance == null || skinInstance.DataRef < 0 ||
                skinInstance.DataRef >= nif.Blocks.Count)
            {
                continue;
            }

            var skinDataBlock = nif.Blocks[skinInstance.DataRef];
            if (skinDataBlock.TypeName != "NiSkinData")
            {
                continue;
            }

            var skinData = ParseNiSkinData(data, skinDataBlock, be);
            if (skinData == null || skinData.Bones.Length == 0)
            {
                continue;
            }

            // Build per-bone combined matrices: SkinTransform * BoneWorld
            var boneSkinMatrices = new Matrix4x4[skinData.Bones.Length];
            var allBonesResolved = true;
            for (var b = 0; b < skinData.Bones.Length; b++)
            {
                if (b >= skinInstance.BoneRefs.Length)
                {
                    allBonesResolved = false;
                    break;
                }

                var boneNodeIdx = skinInstance.BoneRefs[b];
                if (!worldTransforms.TryGetValue(boneNodeIdx, out var boneWorldTransform))
                {
                    // Bone not in world transforms — try parsing its own transform
                    if (boneNodeIdx >= 0 && boneNodeIdx < nif.Blocks.Count)
                    {
                        boneWorldTransform = ParseNiAVObjectTransform(data, nif.Blocks[boneNodeIdx],
                            nif.BsVersion, be);
                    }
                    else
                    {
                        boneWorldTransform = Matrix4x4.Identity;
                    }
                }

                // v * BoneSkinTransform * BoneWorld (row-vector convention, left-to-right)
                // SkinTransform takes mesh→bone, BoneWorld takes bone→world
                // OverallSkinTransform is NOT chained — it's an alternative mesh→skelRoot path
                boneSkinMatrices[b] = skinData.Bones[b].InverseBindPose * boneWorldTransform;
            }

            if (!allBonesResolved)
            {
                continue;
            }

            // Get vertex count from geometry data block
            if (!shapeDataMap.TryGetValue(shapeIndex, out var dataIdx)) continue;
            var numVerts = ReadVertexCount(data, nif.Blocks[dataIdx], be);
            if (numVerts <= 0) continue;

            // Build per-vertex influences:
            // 1. NiSkinData vertex weights (PC path, HasVertexWeights=true)
            // 2. BSPackedAdditionalGeometryData (Xbox 360 packed path)
            // 3. NiSkinPartition direct weights (Xbox 360 NIFs without packed data)
            (int BoneIdx, float Weight)[][]? perVertexInfluences;
            if (skinData.HasVertexWeights)
            {
                perVertexInfluences = BuildPerVertexInfluences(skinData, numVerts);
            }
            else
            {
                packedGeometryMap.TryGetValue(dataIdx, out var packedData);
                if (packedData?.BoneWeights != null && packedData.BoneIndices != null)
                {
                    // Xbox 360 packed path: BSPackedAdditionalGeometryData
                    perVertexInfluences = BuildPerVertexInfluencesFromPackedData(packedData, numVerts);
                }
                else
                {
                    // Fallback: NiSkinPartition may have direct weights/indices
                    perVertexInfluences = BuildPerVertexInfluencesFromPartitions(data, nif, skinInstance, numVerts);
                    if (perVertexInfluences == null)
                    {
                        continue;
                    }
                }
            }

            result[shapeIndex] = (perVertexInfluences, boneSkinMatrices);
        }

        return result;
    }

    /// <summary>
    ///     Parse NiAVObject transform (translation + rotation + scale) from a block.
    ///     Layout: NiObjectNET header, then NiAVObject fields (flags, translation, rotation, scale).
    /// </summary>
    private static Matrix4x4 ParseNiAVObjectTransform(byte[] data, BlockInfo block, uint bsVersion, bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // NiObjectNET: Name (4) + NumExtraData (4) + ExtraData refs + Controller (4)
        if (pos + 4 > end) return Matrix4x4.Identity;
        pos += 4; // Name index

        if (pos + 4 > end) return Matrix4x4.Identity;
        var numExtraData = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        pos += (int)Math.Min(numExtraData, 100) * 4; // Extra data refs

        if (pos + 4 > end) return Matrix4x4.Identity;
        pos += 4; // Controller ref

        // NiAVObject: Flags
        if (bsVersion > 26)
        {
            if (pos + 4 > end) return Matrix4x4.Identity;
            pos += 4; // uint flags
        }
        else
        {
            if (pos + 2 > end) return Matrix4x4.Identity;
            pos += 2; // ushort flags
        }

        // Translation (3 floats = 12 bytes)
        if (pos + 12 > end) return Matrix4x4.Identity;
        var tx = BinaryUtils.ReadFloat(data, pos, be);
        var ty = BinaryUtils.ReadFloat(data, pos + 4, be);
        var tz = BinaryUtils.ReadFloat(data, pos + 8, be);
        pos += 12;

        // Rotation (3x3 matrix = 36 bytes)
        if (pos + 36 > end) return Matrix4x4.Identity;
        var m = new float[9];
        for (var i = 0; i < 9; i++)
        {
            m[i] = BinaryUtils.ReadFloat(data, pos + i * 4, be);
        }

        pos += 36;

        // Scale (1 float = 4 bytes)
        if (pos + 4 > end) return Matrix4x4.Identity;
        var scale = BinaryUtils.ReadFloat(data, pos, be);

        // Build 4x4 matrix: transposed rotation * scale + translation.
        // NIF stores column-vector rotation (M*v convention) in row-major order.
        // System.Numerics uses row-vector (v*M), so we transpose the 3x3 block.
        return new Matrix4x4(
            m[0] * scale, m[3] * scale, m[6] * scale, 0,
            m[1] * scale, m[4] * scale, m[7] * scale, 0,
            m[2] * scale, m[5] * scale, m[8] * scale, 0,
            tx, ty, tz, 1);
    }

    /// <summary>
    ///     Read the name string from a block's NiObjectNET header (first int32 = string index).
    /// </summary>
    private static string? ReadBlockName(byte[] data, BlockInfo block, NifInfo nif)
    {
        if (block.Size < 4) return null;
        var nameIdx = BinaryUtils.ReadInt32(data, block.DataOffset, nif.IsBigEndian);
        if (nameIdx < 0 || nameIdx >= nif.Strings.Count) return null;
        return nif.Strings[nameIdx];
    }

    /// <summary>
    ///     Returns true if the shape name indicates a gore/dismembered variant that should be filtered out.
    /// </summary>
    private static bool IsGoreShape(string? name)
    {
        if (name == null) return false;
        return name.Contains("gore", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("dismember", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("decap", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Parse the skin instance block reference from a NiTriShape/NiTriStrips block.
    ///     This is the field immediately after the data ref in the NiGeometry layout.
    /// </summary>
    private static int ParseShapeSkinInstanceRef(byte[] data, BlockInfo block, uint bsVersion, bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // Skip NiObjectNET
        if (pos + 4 > end) return -1;
        pos += 4; // Name
        if (pos + 4 > end) return -1;
        var numExtraData = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        pos += (int)Math.Min(numExtraData, 100) * 4;
        if (pos + 4 > end) return -1;
        pos += 4; // Controller

        // Skip NiAVObject
        pos += bsVersion > 26 ? 4 : 2; // Flags
        pos += 12 + 36 + 4; // Translation + Rotation + Scale

        // Properties array (BS <= 34)
        if (bsVersion <= 34)
        {
            if (pos + 4 > end) return -1;
            var numProperties = BinaryUtils.ReadUInt32(data, pos, be);
            pos += 4;
            pos += (int)Math.Min(numProperties, 100) * 4;
        }

        // Collision object ref
        if (pos + 4 > end) return -1;
        pos += 4;

        // NiGeometry: Data ref (skip)
        if (pos + 4 > end) return -1;
        pos += 4;

        // Skin Instance ref
        if (pos + 4 > end) return -1;
        return BinaryUtils.ReadInt32(data, pos, be);
    }

    /// <summary>
    ///     Parse body part IDs from a BSDismemberSkinInstance block.
    ///     Layout: NiSkinInstance fields (Data + SkinPartition + SkeletonRoot + NumBones + Bones[]),
    ///     then NumPartitions + BodyPartList[] where each entry is PartFlag(2) + BodyPart(2).
    /// </summary>
    private static int[]? ParseDismemberPartitions(byte[] data, BlockInfo block, bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // NiSkinInstance: Data(4) + SkinPartition(4) + SkeletonRoot(4) = 12 bytes
        if (pos + 12 > end) return null;
        pos += 12;

        // NumBones(4) + Bone refs
        if (pos + 4 > end) return null;
        var numBones = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        pos += (int)Math.Min(numBones, 500) * 4;

        // BSDismemberSkinInstance: NumPartitions
        if (pos + 4 > end) return null;
        var numPartitions = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;

        if (numPartitions == 0 || numPartitions > 100 || pos + numPartitions * 4 > end)
            return null;

        var bodyParts = new int[(int)numPartitions];
        for (var i = 0; i < numPartitions; i++)
        {
            // BodyPartList: PartFlag(uint16) + BodyPart(uint16) = 4 bytes
            pos += 2; // Skip PartFlag
            bodyParts[i] = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;
        }

        return bodyParts;
    }

    /// <summary>
    ///     Returns true if all body part IDs are dismember gore caps.
    ///     FO3/FNV dismember body parts: 0-13 = intact, 1000+ = torso sections (intact),
    ///     100-113 = section caps (gore stump on limb side),
    ///     200-213 = torso caps (gore stump on torso side).
    /// </summary>
    private static bool IsDismemberGoreShape(int[]? bodyParts)
    {
        if (bodyParts == null || bodyParts.Length == 0)
            return false;

        foreach (var bp in bodyParts)
        {
            if (bp < 100 || bp > 299)
                return false; // Has at least one non-gore-cap partition
        }

        return true; // All partitions are gore caps
    }

    // ── Skeleton deformation (linear blend skinning) ─────────────────────────

    /// <summary>
    ///     Parse NiTransform: 3x3 rotation (36 bytes) + translation (12 bytes) + scale (4 bytes) = 52 bytes.
    ///     NiTransform has Rotation FIRST, unlike NiAVObject which has Translation first.
    /// </summary>
    private static Matrix4x4 ParseNiTransform(byte[] data, int pos, bool be)
    {
        // Rotation (3x3 matrix = 36 bytes) — FIRST in NiTransform
        var m = new float[9];
        for (var i = 0; i < 9; i++)
        {
            m[i] = BinaryUtils.ReadFloat(data, pos + i * 4, be);
        }

        pos += 36;

        // Translation (3 floats = 12 bytes)
        var tx = BinaryUtils.ReadFloat(data, pos, be);
        var ty = BinaryUtils.ReadFloat(data, pos + 4, be);
        var tz = BinaryUtils.ReadFloat(data, pos + 8, be);
        pos += 12;

        // Scale (1 float = 4 bytes)
        var scale = BinaryUtils.ReadFloat(data, pos, be);

        // Same transpose convention as ParseNiAVObjectTransform:
        // NIF stores column-vector rotation (M*v) in row-major order.
        // System.Numerics uses row-vector (v*M), so transpose the 3x3 block.
        return new Matrix4x4(
            m[0] * scale, m[3] * scale, m[6] * scale, 0,
            m[1] * scale, m[4] * scale, m[7] * scale, 0,
            m[2] * scale, m[5] * scale, m[8] * scale, 0,
            tx, ty, tz, 1);
    }

    /// <summary>
    ///     Parse NiSkinInstance or BSDismemberSkinInstance to extract skeleton linkage.
    ///     Layout: Data(4) + SkinPartition(4) + SkeletonRoot(4) + NumBones(4) + Bones[NumBones].
    /// </summary>
    private static SkinInstanceData? ParseNiSkinInstance(byte[] data, BlockInfo block, bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // Data ref → NiSkinData
        if (pos + 4 > end) return null;
        var dataRef = BinaryUtils.ReadInt32(data, pos, be);
        pos += 4;

        // SkinPartition ref → NiSkinPartition
        if (pos + 4 > end) return null;
        var skinPartitionRef = BinaryUtils.ReadInt32(data, pos, be);
        pos += 4;

        // SkeletonRoot ref → NiNode
        if (pos + 4 > end) return null;
        var skelRootRef = BinaryUtils.ReadInt32(data, pos, be);
        pos += 4;

        // NumBones
        if (pos + 4 > end) return null;
        var numBones = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;

        if (numBones > 500 || pos + numBones * 4 > end) return null;

        var boneRefs = new int[(int)numBones];
        for (var i = 0; i < numBones; i++)
        {
            boneRefs[i] = BinaryUtils.ReadInt32(data, pos, be);
            pos += 4;
        }

        return new SkinInstanceData
        {
            DataRef = dataRef,
            SkinPartitionRef = skinPartitionRef,
            SkeletonRootRef = skelRootRef,
            BoneRefs = boneRefs
        };
    }

    /// <summary>
    ///     Parse NiSkinData block: overall skin transform + per-bone inverse bind pose + vertex weights.
    ///     Layout (FO3/FNV v20.2.0.7): SkinTransform(52) + NumBones(4) + HasVertexWeights(1)
    ///     + BoneList[NumBones]: SkinTransform(52) + NiBound(16) + NumVerts(2) + VertexWeights[].
    /// </summary>
    private static SkinData? ParseNiSkinData(byte[] data, BlockInfo block, bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // Overall SkinTransform (52 bytes)
        if (pos + 52 > end) return null;
        var overallTransform = ParseNiTransform(data, pos, be);
        pos += 52;

        // NumBones
        if (pos + 4 > end) return null;
        var numBones = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;

        if (numBones > 500) return null;

        // HasVertexWeights (since 4.2.1.0 — always present for FO3/FNV)
        if (pos + 1 > end) return null;
        var hasVertexWeights = data[pos] != 0;
        pos += 1;

        // Parse BoneList
        var bones = new BoneSkinInfo[(int)numBones];
        for (var b = 0; b < numBones; b++)
        {
            // SkinTransform (52 bytes) — inverse bind pose
            if (pos + 52 > end) return null;
            var boneTransform = ParseNiTransform(data, pos, be);
            pos += 52;

            // BoundingSphere: center(12) + radius(4) = 16 bytes
            if (pos + 16 > end) return null;
            pos += 16;

            // NumVertices
            if (pos + 2 > end) return null;
            var numVerts = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;

            // VertexWeights (only if HasVertexWeights)
            (ushort VertexIndex, float Weight)[] vertexWeights;
            if (hasVertexWeights && numVerts > 0)
            {
                if (pos + numVerts * 6 > end) return null;
                vertexWeights = new (ushort, float)[numVerts];
                for (var v = 0; v < numVerts; v++)
                {
                    var vertIdx = BinaryUtils.ReadUInt16(data, pos, be);
                    var weight = BinaryUtils.ReadFloat(data, pos + 2, be);
                    vertexWeights[v] = (vertIdx, weight);
                    pos += 6;
                }
            }
            else
            {
                vertexWeights = [];
            }

            bones[b] = new BoneSkinInfo
            {
                InverseBindPose = boneTransform,
                VertexWeights = vertexWeights
            };
        }

        return new SkinData
        {
            OverallTransform = overallTransform,
            Bones = bones,
            HasVertexWeights = hasVertexWeights
        };
    }

    /// <summary>
    ///     Build per-vertex bone influence table from per-bone vertex weight lists.
    ///     Returns array indexed by vertex index, each entry containing (boneIndex, weight) pairs.
    /// </summary>
    private static (int BoneIdx, float Weight)[][] BuildPerVertexInfluences(SkinData skinData, int numVertices)
    {
        var influences = new List<(int BoneIdx, float Weight)>[numVertices];
        for (var v = 0; v < numVertices; v++)
        {
            influences[v] = new List<(int, float)>(4);
        }

        for (var boneIdx = 0; boneIdx < skinData.Bones.Length; boneIdx++)
        {
            foreach (var (vertIdx, weight) in skinData.Bones[boneIdx].VertexWeights)
            {
                if (vertIdx < numVertices && weight > 0.0001f)
                {
                    influences[vertIdx].Add((boneIdx, weight));
                }
            }
        }

        var result = new (int BoneIdx, float Weight)[numVertices][];
        for (var v = 0; v < numVertices; v++)
        {
            var list = influences[v];
            if (list.Count == 0)
            {
                result[v] = [];
                continue;
            }

            // Take top 4 by weight, normalize
            list.Sort((a, b) => b.Weight.CompareTo(a.Weight));
            var count = Math.Min(list.Count, 4);
            var arr = new (int BoneIdx, float Weight)[count];
            var totalWeight = 0f;
            for (var i = 0; i < count; i++)
            {
                arr[i] = list[i];
                totalWeight += arr[i].Weight;
            }

            if (totalWeight > 0.001f && Math.Abs(totalWeight - 1f) > 0.01f)
            {
                for (var i = 0; i < count; i++)
                {
                    arr[i].Weight /= totalWeight;
                }
            }

            result[v] = arr;
        }

        return result;
    }

    /// <summary>
    ///     Build per-vertex bone influences from NiSkinPartition partition data (Xbox 360 path).
    ///     Each partition has a Bones[] array (indices into NiSkinInstance.BoneRefs) and a VertexMap[]
    ///     (maps partition-local vertices to shape-global indices). Assigns each vertex to its
    ///     partition's first bone with weight 1.0.
    /// </summary>
    private static (int BoneIdx, float Weight)[][]? BuildPerVertexInfluencesFromPartitions(
        byte[] data, NifInfo nif, SkinInstanceData skinInstance, int numVertices)
    {
        // Parse NiSkinPartition
        if (skinInstance.SkinPartitionRef < 0 || skinInstance.SkinPartitionRef >= nif.Blocks.Count)
        {
            return null;
        }

        var partBlock = nif.Blocks[skinInstance.SkinPartitionRef];
        if (partBlock.TypeName != "NiSkinPartition")
        {
            return null;
        }

        var partData = NifSkinPartitionExpander.Parse(data, partBlock.DataOffset, partBlock.Size, nif.IsBigEndian);
        if (partData == null || partData.Partitions.Count == 0)
        {
            return null;
        }

        var result = new (int BoneIdx, float Weight)[numVertices][];
        for (var v = 0; v < numVertices; v++)
        {
            result[v] = [];
        }

        foreach (var partition in partData.Partitions)
        {
            if (partition.Bones.Length == 0 || !partition.HasVertexMap || partition.VertexMap.Length == 0)
            {
                continue;
            }

            // Use per-vertex weights and bone indices when available
            if (partition.HasVertexWeights && partition.HasBoneIndices &&
                partition.VertexWeights != null && partition.BoneIndices != null)
            {
                for (var pv = 0; pv < partition.NumVertices; pv++)
                {
                    if (pv >= partition.VertexMap.Length) break;
                    var globalVertIdx = partition.VertexMap[pv];
                    if (globalVertIdx >= numVertices) continue;

                    var influences = new List<(int, float)>(partition.NumWeightsPerVertex);
                    var weightSum = 0f;
                    for (var w = 0; w < partition.NumWeightsPerVertex; w++)
                    {
                        var weight = partition.VertexWeights[pv, w];
                        if (weight <= 0.0001f) continue;

                        // BoneIndices are partition-local → map via partition.Bones[] to global
                        var partLocalBone = partition.BoneIndices[pv, w];
                        if (partLocalBone >= partition.Bones.Length) continue;
                        var globalBone = partition.Bones[partLocalBone];

                        influences.Add(((int)globalBone, weight));
                        weightSum += weight;
                    }

                    // Normalize weights to sum to 1.0 (NIF partitions may store non-normalized)
                    if (weightSum > 0 && Math.Abs(weightSum - 1.0f) > 0.001f)
                    {
                        for (var i = 0; i < influences.Count; i++)
                        {
                            influences[i] = (influences[i].Item1, influences[i].Item2 / weightSum);
                        }
                    }

                    if (influences.Count > 0)
                    {
                        result[globalVertIdx] = influences.ToArray();
                    }
                }
            }
            else
            {
                // Fallback: assign each vertex to the primary bone
                var primaryBone = partition.Bones[0];
                if (primaryBone >= skinInstance.BoneRefs.Length) continue;

                foreach (var globalVertIdx in partition.VertexMap)
                {
                    if (globalVertIdx < numVertices)
                    {
                        result[globalVertIdx] = [(primaryBone, 1.0f)];
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    ///     Build per-vertex bone influences from BSPackedAdditionalGeometryData (Xbox 360 path).
    ///     PackedGeometryData.BoneIndices are global indices into NiSkinInstance.BoneRefs[].
    /// </summary>
    private static (int BoneIdx, float Weight)[][] BuildPerVertexInfluencesFromPackedData(
        PackedGeometryData packedData, int numVertices)
    {
        var result = new (int BoneIdx, float Weight)[numVertices][];

        for (var v = 0; v < numVertices; v++)
        {
            var influences = new List<(int, float)>(4);
            for (var w = 0; w < 4; w++)
            {
                var idx = v * 4 + w;
                if (idx >= packedData.BoneWeights!.Length || idx >= packedData.BoneIndices!.Length)
                {
                    break;
                }

                var weight = packedData.BoneWeights[idx];
                if (weight > 0.0001f)
                {
                    influences.Add((packedData.BoneIndices[idx], weight));
                }
            }

            result[v] = influences.Count > 0 ? influences.ToArray() : [];
        }

        return result;
    }

    /// <summary>
    ///     Extract packed geometry data for shapes that reference BSPackedAdditionalGeometryData.
    ///     Returns a map from geometry data block index to PackedGeometryData.
    /// </summary>
    private static Dictionary<int, PackedGeometryData> ExtractPackedGeometry(
        byte[] data, NifInfo nif, Dictionary<int, int> shapeDataMap)
    {
        var result = new Dictionary<int, PackedGeometryData>();

        // Find and extract all BSPackedAdditionalGeometryData blocks
        var packedByBlock = new Dictionary<int, PackedGeometryData>();
        foreach (var block in nif.Blocks)
        {
            if (block.TypeName == "BSPackedAdditionalGeometryData")
            {
                var packed = NifPackedDataExtractor.Extract(data, block.DataOffset, block.Size, nif.IsBigEndian);
                if (packed != null)
                {
                    packedByBlock[block.Index] = packed;
                }
            }
        }

        if (packedByBlock.Count == 0)
        {
            return result;
        }

        // For each geometry data block, parse its Additional Data ref to link to packed data
        foreach (var (_, dataBlockIdx) in shapeDataMap)
        {
            if (result.ContainsKey(dataBlockIdx))
            {
                continue;
            }

            var additionalRef = ParseGeometryAdditionalDataRef(data, nif.Blocks[dataBlockIdx],
                nif.BsVersion, nif.IsBigEndian);
            if (additionalRef >= 0 && packedByBlock.TryGetValue(additionalRef, out var packed))
            {
                result[dataBlockIdx] = packed;
            }
        }

        return result;
    }

    /// <summary>
    ///     Parse the Additional Data ref from a NiTriShapeData or NiTriStripsData block.
    ///     Walks through the variable-length geometry header to find the ref field.
    /// </summary>
    private static int ParseGeometryAdditionalDataRef(byte[] data, BlockInfo block, uint bsVersion, bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // GroupId (4)
        pos += 4;

        // NumVertices (2)
        if (pos + 2 > end) return -1;
        var numVertices = BinaryUtils.ReadUInt16(data, pos, be);
        pos += 2;

        // KeepFlags + CompressFlags (BS >= 34)
        if (bsVersion >= 34) pos += 2;

        // HasVertices (1) + vertex data
        if (pos + 1 > end) return -1;
        if (data[pos++] != 0)
        {
            pos += numVertices * 12; // Vector3 per vertex
        }

        // BSVectorFlags (BS >= 34)
        ushort bsVectorFlags = 0;
        if (bsVersion >= 34)
        {
            if (pos + 2 > end) return -1;
            bsVectorFlags = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;
        }

        // HasNormals (1) + normal/tangent data
        if (pos + 1 > end) return -1;
        if (data[pos++] != 0)
        {
            pos += numVertices * 12; // Normals
            if (bsVersion >= 34)
            {
                pos += 16; // Tangent space center + radius
                if ((bsVectorFlags & 0x1000) != 0)
                {
                    pos += numVertices * 24; // Tangents + Bitangents
                }
            }
        }
        else if (bsVersion >= 34)
        {
            pos += 16; // Center + radius even without normals
        }

        // HasVertexColors (1) + vertex color data
        if (pos + 1 > end) return -1;
        if (data[pos++] != 0)
        {
            pos += numVertices * 16; // Color4 per vertex
        }

        // UV sets
        if (bsVersion >= 34)
        {
            var numUvSets = (bsVectorFlags & 0x0001) != 0 ? 1 : 0;
            pos += numVertices * numUvSets * 8;
        }
        else
        {
            if (pos + 4 > end) return -1;
            var numUvSets = BinaryUtils.ReadUInt16(data, pos, be) & 0x3F;
            pos += 4; // numUvSets + tSpaceFlag
            pos += numVertices * numUvSets * 8;
        }

        // ConsistencyFlags (2)
        pos += 2;

        // Additional Data ref (4)
        if (pos + 4 > end) return -1;
        return BinaryUtils.ReadInt32(data, pos, be);
    }

    /// <summary>
    ///     Apply linear blend skinning to vertex positions.
    /// </summary>
    private static float[] ApplySkinningPositions(float[] positions,
        (int BoneIdx, float Weight)[][] perVertexInfluences, Matrix4x4[] boneSkinMatrices)
    {
        var numVerts = positions.Length / 3;
        var result = new float[positions.Length];

        for (var v = 0; v < numVerts; v++)
        {
            var inf = perVertexInfluences[v];
            if (inf.Length == 0)
            {
                result[v * 3] = positions[v * 3];
                result[v * 3 + 1] = positions[v * 3 + 1];
                result[v * 3 + 2] = positions[v * 3 + 2];
                continue;
            }

            var src = new Vector3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]);
            var dst = Vector3.Zero;

            foreach (var (boneIdx, weight) in inf)
            {
                if (boneIdx < boneSkinMatrices.Length)
                {
                    dst += weight * Vector3.Transform(src, boneSkinMatrices[boneIdx]);
                }
            }

            result[v * 3] = dst.X;
            result[v * 3 + 1] = dst.Y;
            result[v * 3 + 2] = dst.Z;
        }

        return result;
    }

    /// <summary>
    ///     Apply linear blend skinning to normals/tangents/bitangents (rotation only, no translation).
    /// </summary>
    private static float[] ApplySkinningNormals(float[] normals,
        (int BoneIdx, float Weight)[][] perVertexInfluences, Matrix4x4[] boneSkinMatrices)
    {
        var numVerts = normals.Length / 3;
        var result = new float[normals.Length];

        for (var v = 0; v < numVerts; v++)
        {
            var inf = perVertexInfluences[v];
            if (inf.Length == 0)
            {
                result[v * 3] = normals[v * 3];
                result[v * 3 + 1] = normals[v * 3 + 1];
                result[v * 3 + 2] = normals[v * 3 + 2];
                continue;
            }

            var src = new Vector3(normals[v * 3], normals[v * 3 + 1], normals[v * 3 + 2]);
            var dst = Vector3.Zero;

            foreach (var (boneIdx, weight) in inf)
            {
                if (boneIdx < boneSkinMatrices.Length)
                {
                    dst += weight * Vector3.TransformNormal(src, boneSkinMatrices[boneIdx]);
                }
            }

            var len = dst.Length();
            if (len > 0.001f) dst /= len;

            result[v * 3] = dst.X;
            result[v * 3 + 1] = dst.Y;
            result[v * 3 + 2] = dst.Z;
        }

        return result;
    }

    /// <summary>
    ///     Read vertex count from a geometry data block header (GroupId(4) + NumVertices(2)).
    /// </summary>
    private static int ReadVertexCount(byte[] data, BlockInfo block, bool be)
    {
        var pos = block.DataOffset + 4; // Skip GroupId
        if (pos + 2 > block.DataOffset + block.Size) return -1;
        return BinaryUtils.ReadUInt16(data, pos, be);
    }

    /// <summary>
    ///     Parse the children array from a NiNode block.
    /// </summary>
    private static List<int>? ParseNodeChildren(byte[] data, BlockInfo block, uint bsVersion, bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // Skip NiObjectNET
        if (pos + 4 > end) return null;
        pos += 4; // Name
        if (pos + 4 > end) return null;
        var numExtraData = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        pos += (int)Math.Min(numExtraData, 100) * 4;
        if (pos + 4 > end) return null;
        pos += 4; // Controller

        // Skip NiAVObject: flags + translation(12) + rotation(36) + scale(4) = 52 or 54 bytes
        pos += bsVersion > 26 ? 4 : 2; // Flags
        pos += 12 + 36 + 4; // Translation + Rotation + Scale

        // Properties array (BS <= 34)
        if (bsVersion <= 34)
        {
            if (pos + 4 > end) return null;
            var numProperties = BinaryUtils.ReadUInt32(data, pos, be);
            pos += 4;
            pos += (int)Math.Min(numProperties, 100) * 4;
        }

        // Collision object ref
        if (pos + 4 > end) return null;
        pos += 4;

        // NiNode: Children array
        if (pos + 4 > end) return null;
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
    ///     Parse the data block reference from a NiTriShape/NiTriStrips block.
    ///     Layout: NiAVObject header, then NiGeometry fields (data ref, skin instance, etc.)
    /// </summary>
    private static int ParseShapeDataRef(byte[] data, BlockInfo block, uint bsVersion, bool be)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // Skip NiObjectNET
        if (pos + 4 > end) return -1;
        pos += 4; // Name
        if (pos + 4 > end) return -1;
        var numExtraData = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        pos += (int)Math.Min(numExtraData, 100) * 4;
        if (pos + 4 > end) return -1;
        pos += 4; // Controller

        // Skip NiAVObject
        pos += bsVersion > 26 ? 4 : 2; // Flags
        pos += 12 + 36 + 4; // Translation + Rotation + Scale

        // Properties array (BS <= 34)
        if (bsVersion <= 34)
        {
            if (pos + 4 > end) return -1;
            var numProperties = BinaryUtils.ReadUInt32(data, pos, be);
            pos += 4;
            pos += (int)Math.Min(numProperties, 100) * 4;
        }

        // Collision object ref
        if (pos + 4 > end) return -1;
        pos += 4;

        // NiGeometry: Data ref (this is what we want)
        if (pos + 4 > end) return -1;
        return BinaryUtils.ReadInt32(data, pos, be);
    }

    /// <summary>
    ///     Parse the property block references from a NiTriShape/NiTriStrips block.
    ///     Same NiObjectNET/NiAVObject header skip as ParseShapeDataRef, but returns property refs.
    /// </summary>
    private static List<int>? ParseShapePropertyRefs(byte[] data, BlockInfo block, uint bsVersion, bool be)
    {
        if (bsVersion > 34)
        {
            return null; // No properties array in newer versions
        }

        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // Skip NiObjectNET
        if (pos + 4 > end) return null;
        pos += 4; // Name
        if (pos + 4 > end) return null;
        var numExtraData = BinaryUtils.ReadUInt32(data, pos, be);
        pos += 4;
        pos += (int)Math.Min(numExtraData, 100) * 4;
        if (pos + 4 > end) return null;
        pos += 4; // Controller

        // Skip NiAVObject flags
        pos += bsVersion > 26 ? 4 : 2;
        // Skip Translation + Rotation + Scale
        pos += 12 + 36 + 4;

        // Properties array
        if (pos + 4 > end) return null;
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
    ///     Extract a submesh from a geometry data block, applying the shape's world transform.
    /// </summary>
    private static RenderableSubmesh? ExtractSubmesh(byte[] data, NifInfo nif,
        int shapeIndex, int dataIndex, Dictionary<int, Matrix4x4> worldTransforms,
        string? diffuseTexturePath = null, string? normalMapTexturePath = null,
        bool isEmissive = false,
        ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)? skinning = null)
    {
        var dataBlock = nif.Blocks[dataIndex];
        var be = nif.IsBigEndian;

        // Get world transform for this shape
        worldTransforms.TryGetValue(shapeIndex, out var transform);
        if (transform == default)
        {
            transform = Matrix4x4.Identity;
        }

        var typeName = dataBlock.TypeName;

        RenderableSubmesh? submesh = null;
        if (typeName is "NiTriShapeData")
        {
            submesh = ExtractTriShapeData(data, dataBlock, be, nif.BsVersion, transform, skinning);
        }
        else if (typeName is "NiTriStripsData")
        {
            submesh = ExtractTriStripsData(data, dataBlock, be, nif.BsVersion, transform, skinning);
        }

        // Attach texture paths and emissive flag
        if (submesh != null &&
            (isEmissive || (submesh.UVs != null &&
             (diffuseTexturePath != null || normalMapTexturePath != null))))
        {
            return new RenderableSubmesh
            {
                Positions = submesh.Positions,
                Triangles = submesh.Triangles,
                Normals = submesh.Normals,
                UVs = submesh.UVs,
                VertexColors = submesh.VertexColors,
                Tangents = submesh.Tangents,
                Bitangents = submesh.Bitangents,
                DiffuseTexturePath = diffuseTexturePath,
                NormalMapTexturePath = normalMapTexturePath,
                IsEmissive = isEmissive
            };
        }

        return submesh;
    }

    private static RenderableSubmesh? ExtractTriShapeData(byte[] data, BlockInfo block, bool be,
        uint bsVersion, Matrix4x4 transform,
        ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)? skinning = null)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // GroupId
        pos += 4;

        // NumVertices
        if (pos + 2 > end) return null;
        var numVerts = BinaryUtils.ReadUInt16(data, pos, be);
        pos += 2;
        if (numVerts == 0) return null;

        // KeepFlags, CompressFlags (BS >= 34)
        if (bsVersion >= 34) pos += 2;

        // HasVertices
        if (pos + 1 > end) return null;
        var hasVertices = data[pos++];

        float[]? positions = null;
        if (hasVertices != 0)
        {
            positions = ReadVertexPositions(data, pos, numVerts, be);
            pos += numVerts * 12;
        }

        // BS Vector Flags (BS >= 34)
        ushort bsVectorFlags = 0;
        if (bsVersion >= 34)
        {
            if (pos + 2 > end) return null;
            bsVectorFlags = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;
        }

        // HasNormals
        if (pos + 1 > end) return null;
        var hasNormals = data[pos++];

        float[]? normals = null;
        float[]? tangents = null;
        float[]? bitangents = null;
        if (hasNormals != 0)
        {
            normals = ReadVertexPositions(data, pos, numVerts, be); // Same format: 3 floats
            pos += numVerts * 12;

            // Tangent space center + radius (BS >= 34)
            if (bsVersion >= 34) pos += 16;

            // Tangents + Bitangents (if flag set)
            if (bsVersion >= 34 && (bsVectorFlags & 0x1000) != 0)
            {
                if (pos + numVerts * 24 <= end)
                {
                    tangents = ReadVertexPositions(data, pos, numVerts, be);
                    bitangents = ReadVertexPositions(data, pos + numVerts * 12, numVerts, be);
                }

                pos += numVerts * 24;
            }
        }
        else if (bsVersion >= 34)
        {
            // Center + radius even without normals
            pos += 16;
        }

        // HasVertexColors
        if (pos + 1 > end) return null;
        var hasVertexColors = data[pos++];
        byte[]? vertexColors = null;
        if (hasVertexColors != 0)
        {
            if (pos + numVerts * 16 <= end)
            {
                vertexColors = ReadVertexColors(data, pos, numVerts, be);
            }

            pos += numVerts * 16;
        }

        // UV sets — read first UV set for texture mapping
        float[]? uvs = null;
        if (bsVersion >= 34)
        {
            var numUvSets = (bsVectorFlags & 0x0001) != 0 ? 1 : 0;
            if (numUvSets > 0 && pos + numVerts * 8 <= end)
            {
                uvs = ReadUVs(data, pos, numVerts, be);
            }

            pos += numVerts * numUvSets * 8;
        }
        else
        {
            if (pos + 4 > end) return null;
            var numUvSets = BinaryUtils.ReadUInt16(data, pos, be) & 0x3F;
            pos += 4; // numUvSets + tSpaceFlag
            if (numUvSets > 0 && pos + numVerts * 8 <= end)
            {
                uvs = ReadUVs(data, pos, numVerts, be);
            }

            pos += numVerts * numUvSets * 8;
        }

        // ConsistencyFlags + AdditionalData ref
        pos += 2 + 4;

        // NumTriangles
        if (pos + 2 > end) return null;
        var numTriangles = BinaryUtils.ReadUInt16(data, pos, be);
        pos += 2;

        // NumTrianglePoints
        if (pos + 4 > end) return null;
        pos += 4;

        // HasTriangles
        if (pos + 1 > end) return null;
        var hasTriangles = data[pos++];

        if (hasTriangles == 0 || numTriangles == 0 || positions == null)
        {
            return null;
        }

        // Read triangle indices
        if (pos + numTriangles * 6 > end) return null;
        var triangles = new ushort[numTriangles * 3];
        for (var i = 0; i < numTriangles * 3; i++)
        {
            triangles[i] = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;
        }

        // Apply skinning (if available) or simple node transform
        float[] finalPositions;
        float[]? finalNormals;
        float[]? finalTangents;
        float[]? finalBitangents;

        if (skinning.HasValue)
        {
            var (perVertexInfluences, boneSkinMatrices) = skinning.Value;
            finalPositions = ApplySkinningPositions(positions, perVertexInfluences, boneSkinMatrices);
            finalNormals = normals != null
                ? ApplySkinningNormals(normals, perVertexInfluences, boneSkinMatrices) : null;
            finalTangents = tangents != null
                ? ApplySkinningNormals(tangents, perVertexInfluences, boneSkinMatrices) : null;
            finalBitangents = bitangents != null
                ? ApplySkinningNormals(bitangents, perVertexInfluences, boneSkinMatrices) : null;
        }
        else
        {
            finalPositions = TransformPositions(positions, transform);
            finalNormals = normals != null ? TransformNormals(normals, transform) : null;
            finalTangents = tangents != null ? TransformNormals(tangents, transform) : null;
            finalBitangents = bitangents != null ? TransformNormals(bitangents, transform) : null;
        }

        return new RenderableSubmesh
        {
            Positions = finalPositions,
            Triangles = triangles,
            Normals = finalNormals,
            UVs = uvs,
            VertexColors = vertexColors,
            Tangents = finalTangents,
            Bitangents = finalBitangents
        };
    }

    private static RenderableSubmesh? ExtractTriStripsData(byte[] data, BlockInfo block, bool be,
        uint bsVersion, Matrix4x4 transform,
        ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)? skinning = null)
    {
        // Use the existing strip extractor for triangle indices
        var triangles = NifTriStripExtractor.ExtractTrianglesFromTriStripsData(data, block, be);
        if (triangles == null || triangles.Length == 0)
        {
            return null;
        }

        // Parse vertex positions (same header as NiTriShapeData)
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        pos += 4; // GroupId

        if (pos + 2 > end) return null;
        var numVerts = BinaryUtils.ReadUInt16(data, pos, be);
        pos += 2;
        if (numVerts == 0) return null;

        if (bsVersion >= 34) pos += 2; // KeepFlags, CompressFlags

        if (pos + 1 > end) return null;
        var hasVertices = data[pos++];
        if (hasVertices == 0) return null;

        var positions = ReadVertexPositions(data, pos, numVerts, be);
        pos += numVerts * 12;

        // BS Vector Flags
        ushort bsVectorFlags = 0;
        if (bsVersion >= 34)
        {
            if (pos + 2 > end) return null;
            bsVectorFlags = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;
        }

        // HasNormals
        if (pos + 1 > end) return null;
        var hasNormals = data[pos++];
        float[]? normals = null;
        float[]? tangents = null;
        float[]? bitangents = null;
        if (hasNormals != 0)
        {
            normals = ReadVertexPositions(data, pos, numVerts, be);
            pos += numVerts * 12;

            if (bsVersion >= 34) pos += 16; // Center + radius

            if (bsVersion >= 34 && (bsVectorFlags & 0x1000) != 0)
            {
                if (pos + numVerts * 24 <= end)
                {
                    tangents = ReadVertexPositions(data, pos, numVerts, be);
                    bitangents = ReadVertexPositions(data, pos + numVerts * 12, numVerts, be);
                }

                pos += numVerts * 24;
            }
        }
        else if (bsVersion >= 34)
        {
            pos += 16; // Center + radius even without normals
        }

        // HasVertexColors
        byte[]? vertexColors = null;
        if (pos + 1 <= end)
        {
            var hasVertexColors = data[pos++];
            if (hasVertexColors != 0)
            {
                if (pos + numVerts * 16 <= end)
                {
                    vertexColors = ReadVertexColors(data, pos, numVerts, be);
                }

                pos += numVerts * 16;
            }
        }

        // UV sets — read first UV set for texture mapping
        float[]? uvs = null;
        if (bsVersion >= 34)
        {
            var numUvSets = (bsVectorFlags & 0x0001) != 0 ? 1 : 0;
            if (numUvSets > 0 && pos + numVerts * 8 <= end)
            {
                uvs = ReadUVs(data, pos, numVerts, be);
            }
        }
        else if (pos + 4 <= end)
        {
            var numUvSets = BinaryUtils.ReadUInt16(data, pos, be) & 0x3F;
            pos += 4;
            if (numUvSets > 0 && pos + numVerts * 8 <= end)
            {
                uvs = ReadUVs(data, pos, numVerts, be);
            }
        }

        // Apply skinning (if available) or simple node transform
        float[] finalPositions;
        float[]? finalNormals;
        float[]? finalTangents;
        float[]? finalBitangents;

        if (skinning.HasValue)
        {
            var (perVertexInfluences, boneSkinMatrices) = skinning.Value;
            finalPositions = ApplySkinningPositions(positions, perVertexInfluences, boneSkinMatrices);
            finalNormals = normals != null
                ? ApplySkinningNormals(normals, perVertexInfluences, boneSkinMatrices) : null;
            finalTangents = tangents != null
                ? ApplySkinningNormals(tangents, perVertexInfluences, boneSkinMatrices) : null;
            finalBitangents = bitangents != null
                ? ApplySkinningNormals(bitangents, perVertexInfluences, boneSkinMatrices) : null;
        }
        else
        {
            finalPositions = TransformPositions(positions, transform);
            finalNormals = normals != null ? TransformNormals(normals, transform) : null;
            finalTangents = tangents != null ? TransformNormals(tangents, transform) : null;
            finalBitangents = bitangents != null ? TransformNormals(bitangents, transform) : null;
        }

        return new RenderableSubmesh
        {
            Positions = finalPositions,
            Triangles = triangles,
            Normals = finalNormals,
            UVs = uvs,
            VertexColors = vertexColors,
            Tangents = finalTangents,
            Bitangents = finalBitangents
        };
    }

    private static float[] ReadVertexPositions(byte[] data, int offset, int numVerts, bool be)
    {
        var positions = new float[numVerts * 3];
        for (var v = 0; v < numVerts; v++)
        {
            positions[v * 3 + 0] = BinaryUtils.ReadFloat(data, offset + v * 12, be);
            positions[v * 3 + 1] = BinaryUtils.ReadFloat(data, offset + v * 12 + 4, be);
            positions[v * 3 + 2] = BinaryUtils.ReadFloat(data, offset + v * 12 + 8, be);
        }

        return positions;
    }

    private static float[] ReadUVs(byte[] data, int offset, int numVerts, bool be)
    {
        var uvs = new float[numVerts * 2];
        for (var v = 0; v < numVerts; v++)
        {
            uvs[v * 2 + 0] = BinaryUtils.ReadFloat(data, offset + v * 8, be);
            uvs[v * 2 + 1] = BinaryUtils.ReadFloat(data, offset + v * 8 + 4, be);
        }

        return uvs;
    }

    /// <summary>
    ///     Read vertex colors: 4 floats (RGBA, 0.0–1.0) per vertex → convert to byte[].
    /// </summary>
    private static byte[] ReadVertexColors(byte[] data, int offset, int numVerts, bool be)
    {
        var colors = new byte[numVerts * 4];
        for (var v = 0; v < numVerts; v++)
        {
            var baseOffset = offset + v * 16;
            colors[v * 4 + 0] = (byte)(Math.Clamp(BinaryUtils.ReadFloat(data, baseOffset, be), 0f, 1f) * 255);
            colors[v * 4 + 1] = (byte)(Math.Clamp(BinaryUtils.ReadFloat(data, baseOffset + 4, be), 0f, 1f) * 255);
            colors[v * 4 + 2] = (byte)(Math.Clamp(BinaryUtils.ReadFloat(data, baseOffset + 8, be), 0f, 1f) * 255);
            colors[v * 4 + 3] = (byte)(Math.Clamp(BinaryUtils.ReadFloat(data, baseOffset + 12, be), 0f, 1f) * 255);
        }

        return colors;
    }

    /// <summary>
    ///     Transform vertex positions by a 4x4 matrix.
    /// </summary>
    private static float[] TransformPositions(float[] positions, Matrix4x4 transform)
    {
        if (transform == Matrix4x4.Identity)
        {
            return positions;
        }

        var result = new float[positions.Length];
        for (var i = 0; i < positions.Length; i += 3)
        {
            var v = Vector3.Transform(new Vector3(positions[i], positions[i + 1], positions[i + 2]), transform);
            result[i] = v.X;
            result[i + 1] = v.Y;
            result[i + 2] = v.Z;
        }

        return result;
    }

    /// <summary>
    ///     Transform normals by the rotation portion of a 4x4 matrix (no translation).
    /// </summary>
    private static float[] TransformNormals(float[] normals, Matrix4x4 transform)
    {
        if (transform == Matrix4x4.Identity)
        {
            return normals;
        }

        var result = new float[normals.Length];
        for (var i = 0; i < normals.Length; i += 3)
        {
            var n = Vector3.TransformNormal(new Vector3(normals[i], normals[i + 1], normals[i + 2]), transform);
            var len = n.Length();
            if (len > 0.001f)
            {
                n /= len;
            }

            result[i] = n.X;
            result[i + 1] = n.Y;
            result[i + 2] = n.Z;
        }

        return result;
    }

}
