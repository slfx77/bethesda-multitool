using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using FalloutXbox360Utils.Core.Formats.Nif.Geometry;
using FalloutXbox360Utils.Core.Formats.Nif.Skinning;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Skinning-related extraction logic: skin instance/data parsing, per-vertex bone influences,
///     linear blend skinning for positions and normals, and packed geometry extraction.
/// </summary>
internal static class NifSkinningExtractor
{
    /// <summary>Parsed NiSkinInstance: links a skinned shape to its skeleton and NiSkinData.</summary>
    internal sealed class SkinInstanceData
    {
        public int DataRef;              // → NiSkinData block index
        public int SkinPartitionRef;     // → NiSkinPartition block index
        public int SkeletonRootRef;      // → NiNode block index
        public int[] BoneRefs = [];      // → NiNode block indices per bone
    }

    /// <summary>Parsed NiSkinData: overall skin transform + per-bone inverse bind pose.</summary>
    internal sealed class SkinData
    {
        public Matrix4x4 OverallTransform;
        public BoneSkinInfo[] Bones = [];
        public bool HasVertexWeights;
    }

    /// <summary>Per-bone skinning data from NiSkinData.BoneList.</summary>
    internal sealed class BoneSkinInfo
    {
        public Matrix4x4 InverseBindPose;
        public (ushort VertexIndex, float Weight)[] VertexWeights = [];
    }

    /// <summary>
    ///     Build skinning data (per-bone matrices + per-vertex influences) for each skinned shape.
    /// </summary>
    internal static Dictionary<int, ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)>
        BuildShapeSkinningData(byte[] data, NifInfo nif,
            Dictionary<int, int> shapeSkinInstanceMap,
            Dictionary<int, int> shapeDataMap,
            Dictionary<int, Matrix4x4> worldTransforms,
            Dictionary<int, PackedGeometryData> packedGeometryMap,
            Dictionary<string, Matrix4x4>? externalBoneTransforms = null,
            Dictionary<string, Matrix4x4>? externalPoseDeltas = null)
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

            // Skinning formula: skinMatrix = IBP * boneWorld
            // NifSkope uses inverse(shapeWorld) * bone->localTrans(skelRoot) * IBP, which produces
            // vertices in shape-local space (then rendering applies shapeWorld). Since we composite
            // all shapes into absolute world space, we skip invShapeWorld/invSkelRootWorld — they
            // are Identity for all Bethesda equipment NIFs and would incorrectly remove the head
            // NIF's Z=112 positioning. The IBP * boneWorld product at bind pose gives the shape's
            // world transform, correctly positioning vertices in absolute space.
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

                Matrix4x4 boneWorldTransform;

                if (externalPoseDeltas != null)
                {
                    // Pose-delta path: use the NIF's OWN scene graph transform for every bone,
                    // then multiply by the pose delta. This ensures IBP * localWorld cancel to
                    // Identity (since IBP was derived from this NIF's bind pose), leaving just
                    // the delta as the effective skin matrix. All bones are reached through the
                    // local scene graph — no external bone replacement that could miss orphans.
                    if (!worldTransforms.TryGetValue(boneNodeIdx, out boneWorldTransform))
                    {
                        if (boneNodeIdx >= 0 && boneNodeIdx < nif.Blocks.Count)
                        {
                            boneWorldTransform = NifBlockParsers.ParseNiAVObjectTransform(data, nif.Blocks[boneNodeIdx],
                                nif.BsVersion, be);
                        }
                        else
                        {
                            boneWorldTransform = Matrix4x4.Identity;
                        }
                    }

                    // Apply pose delta if available for this bone
                    if (boneNodeIdx >= 0 && boneNodeIdx < nif.Blocks.Count)
                    {
                        var boneName = NifBlockParsers.ReadBlockName(data, nif.Blocks[boneNodeIdx], nif);
                        if (boneName != null && externalPoseDeltas.TryGetValue(boneName, out var delta))
                        {
                            boneWorldTransform = boneWorldTransform * delta;
                        }
                        // No pose delta for this bone — keep NIF's own world transform
                    }
                }
                else if (externalBoneTransforms != null && boneNodeIdx >= 0 && boneNodeIdx < nif.Blocks.Count)
                {
                    // External bone transform path: authoritative skeleton transforms override local.
                    // Used for head/hair/eyes where the NIF's truncated hierarchy is incorrect.
                    var boneName = NifBlockParsers.ReadBlockName(data, nif.Blocks[boneNodeIdx], nif);
                    if (boneName != null && externalBoneTransforms.TryGetValue(boneName, out var skelTransform))
                    {
                        boneWorldTransform = skelTransform;
                    }
                    else
                    {
                        // Bone not in external skeleton — fall back to NIF's own scene graph
                        if (!worldTransforms.TryGetValue(boneNodeIdx, out boneWorldTransform))
                        {
                            if (boneNodeIdx >= 0 && boneNodeIdx < nif.Blocks.Count)
                            {
                                boneWorldTransform = NifBlockParsers.ParseNiAVObjectTransform(data, nif.Blocks[boneNodeIdx],
                                    nif.BsVersion, be);
                            }
                            else
                            {
                                boneWorldTransform = Matrix4x4.Identity;
                            }
                        }
                    }
                }
                else
                {
                    // No external data — use local scene graph transform
                    if (!worldTransforms.TryGetValue(boneNodeIdx, out boneWorldTransform))
                    {
                        if (boneNodeIdx >= 0 && boneNodeIdx < nif.Blocks.Count)
                        {
                            boneWorldTransform = NifBlockParsers.ParseNiAVObjectTransform(data, nif.Blocks[boneNodeIdx],
                                nif.BsVersion, be);
                        }
                        else
                        {
                            boneWorldTransform = Matrix4x4.Identity;
                        }
                    }
                }

                boneSkinMatrices[b] = skinData.Bones[b].InverseBindPose * boneWorldTransform;
            }

            if (!allBonesResolved)
            {
                continue;
            }

            // Get vertex count from geometry data block
            if (!shapeDataMap.TryGetValue(shapeIndex, out var dataIdx)) continue;
            var numVerts = NifBlockParsers.ReadVertexCount(data, nif.Blocks[dataIdx], be);
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
    ///     Parse a NiTransform (rotation 3x3 + translation + scale) from raw bytes.
    ///     Total size: 52 bytes (36 rotation + 12 translation + 4 scale).
    /// </summary>
    internal static Matrix4x4 ParseNiTransform(byte[] data, int pos, bool be)
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
    internal static SkinInstanceData? ParseNiSkinInstance(byte[] data, BlockInfo block, bool be)
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
    internal static SkinData? ParseNiSkinData(byte[] data, BlockInfo block, bool be)
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
    internal static (int BoneIdx, float Weight)[][] BuildPerVertexInfluences(SkinData skinData, int numVertices)
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
    internal static (int BoneIdx, float Weight)[][]? BuildPerVertexInfluencesFromPartitions(
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
    internal static (int BoneIdx, float Weight)[][] BuildPerVertexInfluencesFromPackedData(
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
    internal static Dictionary<int, PackedGeometryData> ExtractPackedGeometry(
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

            var additionalRef = NifBlockParsers.ParseGeometryAdditionalDataRef(data, nif.Blocks[dataBlockIdx],
                nif.BsVersion, nif.IsBigEndian);
            if (additionalRef >= 0 && packedByBlock.TryGetValue(additionalRef, out var packed))
            {
                result[dataBlockIdx] = packed;
            }
        }

        return result;
    }


    /// <summary>
    ///     Apply linear blend skinning to vertex positions.
    /// </summary>
    internal static float[] ApplySkinningPositions(float[] positions,
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
    internal static float[] ApplySkinningNormals(float[] normals,
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
}
