// NIF Skin Partition expander - Write methods
// Writes expanded NiSkinPartition blocks with bone weights/indices

using System.Buffers.Binary;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     NiSkinPartition expander - write methods.
/// </summary>
internal static partial class NifSkinPartitionExpander
{
    /// <summary>
    ///     Writes an expanded NiSkinPartition block with bone weights and indices from packed geometry data.
    /// </summary>
    /// <param name="skinPartition">Parsed NiSkinPartition data</param>
    /// <param name="packedData">Packed geometry data containing bone indices/weights</param>
    /// <param name="output">Output buffer to write to</param>
    /// <param name="outPos">Position to start writing at</param>
    /// <returns>New position after writing</returns>
    public static int WriteExpanded(
        SkinPartitionData skinPartition,
        PackedGeometryData packedData,
        byte[] output,
        int outPos)
    {
        var startPos = outPos;

        // NumPartitions (uint, little-endian for PC)
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(outPos, 4), skinPartition.NumPartitions);
        outPos += 4;

        // Running vertex offset to track position in packed data
        var packedVertexOffset = 0;

        foreach (var p in skinPartition.Partitions)
        {
            // Basic partition header
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), p.NumVertices);
            outPos += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), p.NumTriangles);
            outPos += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), p.NumBones);
            outPos += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), p.NumStrips);
            outPos += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), p.NumWeightsPerVertex);
            outPos += 2;

            // Bones array
            foreach (var bone in p.Bones)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), bone);
                outPos += 2;
            }

            // HasVertexMap (keep same)
            output[outPos++] = (byte)(p.HasVertexMap ? 1 : 0);

            // VertexMap (if present)
            if (p.HasVertexMap)
                foreach (var idx in p.VertexMap)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), idx);
                    outPos += 2;
                }

            // HasVertexWeights = 1 (enabled for expansion)
            output[outPos++] = 1;

            // VertexWeights - populate from packed geometry data
            // The VertexMap tells us which global vertex index corresponds to each partition vertex
            for (var v = 0; v < p.NumVertices; v++)
            {
                // Get the global vertex index from VertexMap (or use sequential if no map)
                var globalVertexIdx = p.HasVertexMap && v < p.VertexMap.Length
                    ? p.VertexMap[v]
                    : packedVertexOffset + v;

                for (var w = 0; w < p.NumWeightsPerVertex; w++)
                {
                    var weight = 0f;
                    if (packedData.BoneWeights != null && globalVertexIdx < packedData.NumVertices)
                    {
                        var weightIdx = globalVertexIdx * 4 + w;
                        if (weightIdx < packedData.BoneWeights.Length) weight = packedData.BoneWeights[weightIdx];
                    }

                    BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), weight);
                    outPos += 4;
                }
            }

            // StripLengths
            foreach (var len in p.StripLengths)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), len);
                outPos += 2;
            }

            // HasFaces
            output[outPos++] = (byte)(p.HasFaces ? 1 : 0);

            // Strips or Triangles
            if (p.HasFaces)
            {
                if (p.NumStrips > 0 && p.Strips != null)
                    for (var s = 0; s < p.NumStrips && s < p.Strips.Length; s++)
                        foreach (var idx in p.Strips[s])
                        {
                            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), idx);
                            outPos += 2;
                        }
                else if (p.Triangles != null)
                    for (var t = 0; t < p.NumTriangles; t++)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), p.Triangles[t, 0]);
                        outPos += 2;
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), p.Triangles[t, 1]);
                        outPos += 2;
                        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), p.Triangles[t, 2]);
                        outPos += 2;
                    }
            }

            // HasBoneIndices = 1 (enabled for expansion)
            output[outPos++] = 1;

            // BoneIndices - populate from packed geometry data
            // Need to remap global bone indices to partition-local bone indices
            for (var v = 0; v < p.NumVertices; v++)
            {
                var globalVertexIdx = p.HasVertexMap && v < p.VertexMap.Length
                    ? p.VertexMap[v]
                    : packedVertexOffset + v;

                for (var w = 0; w < p.NumWeightsPerVertex; w++)
                {
                    byte boneIdx = 0;
                    if (packedData.BoneIndices != null && globalVertexIdx < packedData.NumVertices)
                    {
                        var packedIdx = globalVertexIdx * 4 + w;
                        if (packedIdx < packedData.BoneIndices.Length)
                        {
                            var globalBoneIdx = packedData.BoneIndices[packedIdx];
                            // Map global bone index to partition-local index
                            boneIdx = MapToPartitionBoneIndex(globalBoneIdx, p.Bones);
                        }
                    }

                    output[outPos++] = boneIdx;
                }
            }

            // Track offset for non-mapped partitions
            if (!p.HasVertexMap) packedVertexOffset += p.NumVertices;
        }

        Log.Debug(
            $"      Wrote expanded NiSkinPartition: {outPos - startPos} bytes (was {skinPartition.OriginalSize})");

        return outPos;
    }

    /// <summary>
    ///     Maps a global bone index to a partition-local bone index.
    ///     The partition's Bones array contains the mapping from local to global.
    /// </summary>
    private static byte MapToPartitionBoneIndex(byte globalBoneIdx, ushort[] partitionBones)
    {
        for (var i = 0; i < partitionBones.Length; i++)
            if (partitionBones[i] == globalBoneIdx)
                return (byte)i;

        // Bone not found in partition - return 0 as fallback
        return 0;
    }
}
