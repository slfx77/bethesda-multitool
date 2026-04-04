// NIF NiSkinData expander for adding vertex weights from packed geometry data.
// Xbox 360 NIFs have HasVertexWeights=0 because bone weights are stored in
// BSPackedAdditionalGeometryData. PC NIFs need them in NiSkinData for skeletal
// animation to work.

using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Nif.Geometry;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Skinning;

/// <summary>
///     Expands NiSkinData blocks to include per-bone vertex weights.
///     Xbox 360 NIFs have HasVertexWeights=0 because the data is stored in
///     BSPackedAdditionalGeometryData. PC NIFs need per-bone weights in
///     NiSkinData for animations to work.
/// </summary>
internal static class NifSkinDataExpander
{
    private static readonly Logger Log = Logger.Instance;

    /// <summary>
    ///     Parses a NiSkinData block and returns structured data.
    /// </summary>
    public static ParsedSkinData? Parse(byte[] data, int offset, int size, bool isBigEndian)
    {
        var pos = offset;
        var end = offset + size;

        // Overall SkinTransform: 13 floats (rotation 3x3 + translation xyz + scale)
        if (pos + 52 > end) return null;
        var overallTransform = ReadTransformFloats(data, pos, isBigEndian);
        pos += 52;

        // NumBones (uint32)
        if (pos + 4 > end) return null;
        var numBones = BinaryUtils.ReadUInt32(data, pos, isBigEndian);
        pos += 4;

        if (numBones == 0 || numBones > 500) return null;

        // HasVertexWeights (byte)
        if (pos + 1 > end) return null;
        var hasVertexWeights = data[pos] != 0;
        pos += 1;

        var bones = new ParsedBoneData[(int)numBones];

        for (var b = 0; b < numBones; b++)
        {
            // Per-bone SkinTransform (52 bytes)
            if (pos + 52 > end) return null;
            var boneTransform = ReadTransformFloats(data, pos, isBigEndian);
            pos += 52;

            // BoundingSphere: center(3 floats) + radius(1 float) = 16 bytes
            if (pos + 16 > end) return null;
            var boundingSphere = new float[4];
            for (var i = 0; i < 4; i++)
            {
                boundingSphere[i] = BinaryUtils.ReadFloat(data, pos + i * 4, isBigEndian);
            }

            pos += 16;

            // NumVertices (ushort)
            if (pos + 2 > end) return null;
            var numVerts = BinaryUtils.ReadUInt16(data, pos, isBigEndian);
            pos += 2;

            // Skip existing vertex weights if present
            if (hasVertexWeights && numVerts > 0)
            {
                var weightBytes = numVerts * 6;
                if (pos + weightBytes > end) return null;
                pos += weightBytes;
            }

            bones[b] = new ParsedBoneData
            {
                SkinTransform = boneTransform,
                BoundingSphere = boundingSphere
            };
        }

        Log.Debug($"      Parsed NiSkinData: {numBones} bones, HasVertexWeights={hasVertexWeights}");

        return new ParsedSkinData
        {
            OverallTransform = overallTransform,
            NumBones = (int)numBones,
            OriginalHasVertexWeights = hasVertexWeights,
            OriginalSize = size,
            Bones = bones
        };
    }

    /// <summary>
    ///     Populates per-bone vertex weights from packed geometry data.
    ///     Packed bone indices are PARTITION-LOCAL (indices into each partition's Bones[] array).
    ///     This method maps them to GLOBAL bone indices (into NiSkinData's bone list) via the
    ///     partition Bones[] palette.
    /// </summary>
    /// <param name="skinData">Parsed NiSkinData to populate with weights.</param>
    /// <param name="packedData">Packed geometry data with bone indices/weights.</param>
    /// <param name="vertexMap">Combined vertex map: packed vertex index → mesh-global vertex index.</param>
    /// <param name="partitions">Partition info list with Bones[] arrays for local→global mapping.</param>
    public static void PopulateWeightsFromPackedData(
        ParsedSkinData skinData,
        PackedGeometryData packedData,
        ushort[]? vertexMap,
        List<NifSkinPartitionExpander.PartitionInfo>? partitions)
    {
        if (packedData.BoneIndices == null || packedData.BoneWeights == null)
        {
            Log.Debug("      NiSkinData expansion skipped: no bone data in packed geometry");
            return;
        }

        Log.Debug(
            $"      NiSkinData expansion: packedVerts={packedData.NumVertices}, vertexMapLen={vertexMap?.Length ?? -1}, numBones={skinData.NumBones}, partitions={partitions?.Count ?? 0}");

        if (vertexMap != null && vertexMap.Length != packedData.NumVertices)
        {
            Log.Warn(
                $"      VERTEX MAP MISMATCH: vertexMap.Length={vertexMap.Length} vs packedData.NumVertices={packedData.NumVertices}.");
        }

        // Build partition boundary lookup: for each packed vertex, which partition does it belong to?
        // Packed data stores vertices in partition order (partition 0 first, then 1, etc.)
        var partitionForVertex = BuildPartitionLookup(packedData.NumVertices, partitions);

        var outOfRangeBones = 0;
        var zeroWeightSkips = 0;
        var totalAdded = 0;
        var unmappedLocalBones = 0;

        for (var v = 0; v < packedData.NumVertices; v++)
        {
            for (var w = 0; w < 4; w++)
            {
                var idx = v * 4 + w;
                if (idx >= packedData.BoneIndices.Length || idx >= packedData.BoneWeights.Length)
                {
                    break;
                }

                var localBoneIdx = packedData.BoneIndices[idx];
                var weight = packedData.BoneWeights[idx];

                if (weight <= 0.0001f)
                {
                    zeroWeightSkips++;
                    continue;
                }

                // Map partition-local bone index → global bone index
                var globalBoneIdx = MapToGlobalBoneIndex(localBoneIdx, v, partitionForVertex, partitions);
                if (globalBoneIdx < 0)
                {
                    unmappedLocalBones++;
                    continue;
                }

                if (globalBoneIdx >= skinData.NumBones)
                {
                    outOfRangeBones++;
                    continue;
                }

                // Remap packed (partition-order) vertex index to mesh-global vertex index.
                var meshVertex = vertexMap != null && v < vertexMap.Length
                    ? vertexMap[v]
                    : (ushort)v;

                skinData.Bones[globalBoneIdx].VertexWeights.Add((meshVertex, weight));
                totalAdded++;
            }
        }

        Log.Debug(
            $"      NiSkinData expansion result: {totalAdded} weights added, {outOfRangeBones} out-of-range bones, {zeroWeightSkips} zero-weight skips, {unmappedLocalBones} unmapped local bones");

        // Log weight sums for first few vertices
        for (var checkV = 0; checkV < Math.Min(5, (int)packedData.NumVertices); checkV++)
        {
            var weightSum = 0f;
            var boneList = new StringBuilder();
            for (var w = 0; w < 4; w++)
            {
                var idx = checkV * 4 + w;
                if (idx < packedData.BoneWeights.Length && packedData.BoneWeights[idx] > 0.0001f)
                {
                    weightSum += packedData.BoneWeights[idx];
                    var globalB = MapToGlobalBoneIndex(packedData.BoneIndices[idx], checkV, partitionForVertex,
                        partitions);
                    boneList.Append(
                        $" local{packedData.BoneIndices[idx]}→global{globalB}={packedData.BoneWeights[idx]:F3}");
                }
            }

            var mappedIdx = vertexMap != null && checkV < vertexMap.Length ? vertexMap[checkV] : (ushort)checkV;
            Log.Debug($"        Packed v{checkV}→mesh v{mappedIdx}: sum={weightSum:F4}{boneList}");
        }
    }

    /// <summary>
    ///     Builds a lookup array mapping packed vertex index → partition index.
    /// </summary>
    private static int[] BuildPartitionLookup(int numVertices,
        List<NifSkinPartitionExpander.PartitionInfo>? partitions)
    {
        var lookup = new int[numVertices];

        if (partitions == null || partitions.Count == 0)
        {
            // No partition info — all vertices map to partition -1 (use raw indices)
            Array.Fill(lookup, -1);
            return lookup;
        }

        var packedOffset = 0;
        for (var p = 0; p < partitions.Count; p++)
        {
            var count = partitions[p].NumVertices;
            for (var v = 0; v < count && packedOffset + v < numVertices; v++)
            {
                lookup[packedOffset + v] = p;
            }

            packedOffset += count;
        }

        // Any remaining vertices beyond partition coverage → partition -1
        for (var v = packedOffset; v < numVertices; v++)
        {
            lookup[v] = -1;
        }

        return lookup;
    }

    /// <summary>
    ///     Maps a partition-local bone index to a global bone index using the partition's Bones[] array.
    /// </summary>
    private static int MapToGlobalBoneIndex(byte localBoneIdx, int packedVertexIdx,
        int[] partitionForVertex, List<NifSkinPartitionExpander.PartitionInfo>? partitions)
    {
        if (partitions == null || partitions.Count == 0)
        {
            // No partition info — use raw index as global (fallback)
            return localBoneIdx;
        }

        var partitionIdx = partitionForVertex[packedVertexIdx];
        if (partitionIdx < 0 || partitionIdx >= partitions.Count)
        {
            return localBoneIdx; // Fallback
        }

        var bones = partitions[partitionIdx].Bones;
        if (localBoneIdx >= bones.Length)
        {
            return -1; // Local bone index out of range for this partition
        }

        return bones[localBoneIdx];
    }

    /// <summary>
    ///     Calculates the expanded size of a NiSkinData block with vertex weights added.
    /// </summary>
    public static int CalculateExpandedSize(ParsedSkinData skinData)
    {
        var size = 52 + 4 + 1; // OverallTransform + NumBones + HasVertexWeights

        for (var b = 0; b < skinData.NumBones; b++)
        {
            size += 52; // SkinTransform
            size += 16; // BoundingSphere
            size += 2; // NumVertices
            size += skinData.Bones[b].VertexWeights.Count * 6; // VertexWeights (ushort + float)
        }

        return size;
    }

    /// <summary>
    ///     Writes an expanded NiSkinData block with per-bone vertex weights.
    /// </summary>
    public static int WriteExpanded(ParsedSkinData skinData, byte[] output, int outPos)
    {
        var startPos = outPos;

        // Overall SkinTransform (13 floats = 52 bytes)
        outPos = WriteTransformFloats(skinData.OverallTransform, output, outPos);

        // NumBones (uint32 LE)
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos, 4), (uint)skinData.NumBones);
        outPos += 4;

        // HasVertexWeights = 1 (enabling weights)
        output[outPos++] = 1;

        // Write each bone
        for (var b = 0; b < skinData.NumBones; b++)
        {
            var bone = skinData.Bones[b];

            // SkinTransform (13 floats = 52 bytes)
            outPos = WriteTransformFloats(bone.SkinTransform, output, outPos);

            // BoundingSphere (4 floats = 16 bytes)
            for (var i = 0; i < 4; i++)
            {
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos, 4), bone.BoundingSphere[i]);
                outPos += 4;
            }

            // NumVertices (ushort LE)
            var numVerts = (ushort)bone.VertexWeights.Count;
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos, 2), numVerts);
            outPos += 2;

            // VertexWeights: (VertexIndex ushort + Weight float) × NumVertices
            foreach (var (vertIdx, weight) in bone.VertexWeights)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos, 2), vertIdx);
                outPos += 2;
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos, 4), weight);
                outPos += 4;
            }
        }

        Log.Debug(
            $"      Wrote expanded NiSkinData: {outPos - startPos} bytes (was {skinData.OriginalSize})");

        return outPos;
    }

    /// <summary>
    ///     Reads a NiTransform as 13 floats (rotation 3x3 + translation xyz + scale).
    /// </summary>
    private static float[] ReadTransformFloats(byte[] data, int pos, bool isBigEndian)
    {
        var floats = new float[13];
        for (var i = 0; i < 13; i++)
        {
            floats[i] = BinaryUtils.ReadFloat(data, pos + i * 4, isBigEndian);
        }

        return floats;
    }

    /// <summary>
    ///     Writes a NiTransform from 13 floats in little-endian.
    /// </summary>
    private static int WriteTransformFloats(float[] transform, byte[] output, int outPos)
    {
        for (var i = 0; i < 13; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos, 4), transform[i]);
            outPos += 4;
        }

        return outPos;
    }

    /// <summary>
    ///     Parsed NiSkinData block data.
    /// </summary>
    public sealed class ParsedSkinData
    {
        public float[] OverallTransform { get; set; } = new float[13];
        public int NumBones { get; set; }
        public bool OriginalHasVertexWeights { get; set; }
        public int OriginalSize { get; set; }
        public ParsedBoneData[] Bones { get; set; } = [];
    }

    /// <summary>
    ///     Per-bone data from NiSkinData.
    /// </summary>
    public sealed class ParsedBoneData
    {
        public float[] SkinTransform { get; set; } = new float[13];
        public float[] BoundingSphere { get; set; } = new float[4];
        public List<(ushort VertexIndex, float Weight)> VertexWeights { get; set; } = [];
    }
}
