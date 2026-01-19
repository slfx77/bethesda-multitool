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

        foreach (var partition in skinPartition.Partitions)
        {
            outPos = WritePartition(partition, packedData, output, outPos, ref packedVertexOffset);
        }

        Log.Debug(
            $"      Wrote expanded NiSkinPartition: {outPos - startPos} bytes (was {skinPartition.OriginalSize})");

        return outPos;
    }

    /// <summary>
    ///     Writes a single partition.
    /// </summary>
    private static int WritePartition(
        PartitionInfo partition,
        PackedGeometryData packedData,
        byte[] output,
        int outPos,
        ref int packedVertexOffset)
    {
        outPos = WritePartitionHeader(partition, output, outPos);
        outPos = WriteBonesArray(partition.Bones, output, outPos);
        outPos = WriteVertexMapSection(partition, output, outPos);
        outPos = WriteVertexWeightsSection(partition, packedData, output, outPos, packedVertexOffset);
        outPos = WriteStripLengthsArray(partition.StripLengths, output, outPos);
        outPos = WriteFacesSection(partition, output, outPos);
        outPos = WriteBoneIndicesSection(partition, packedData, output, outPos, packedVertexOffset);

        // Track offset for non-mapped partitions
        if (!partition.HasVertexMap)
        {
            packedVertexOffset += partition.NumVertices;
        }

        return outPos;
    }

    /// <summary>
    ///     Writes the partition header fields.
    /// </summary>
    private static int WritePartitionHeader(PartitionInfo partition, byte[] output, int outPos)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), partition.NumVertices);
        outPos += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), partition.NumTriangles);
        outPos += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), partition.NumBones);
        outPos += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), partition.NumStrips);
        outPos += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), partition.NumWeightsPerVertex);
        outPos += 2;
        return outPos;
    }

    /// <summary>
    ///     Writes the bones array.
    /// </summary>
    private static int WriteBonesArray(ushort[] bones, byte[] output, int outPos)
    {
        foreach (var bone in bones)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), bone);
            outPos += 2;
        }

        return outPos;
    }

    /// <summary>
    ///     Writes the vertex map section (flag + optional data).
    /// </summary>
    private static int WriteVertexMapSection(PartitionInfo partition, byte[] output, int outPos)
    {
        output[outPos++] = (byte)(partition.HasVertexMap ? 1 : 0);

        if (partition.HasVertexMap)
        {
            foreach (var idx in partition.VertexMap)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), idx);
                outPos += 2;
            }
        }

        return outPos;
    }

    /// <summary>
    ///     Writes the vertex weights section from packed geometry data.
    /// </summary>
    private static int WriteVertexWeightsSection(
        PartitionInfo partition,
        PackedGeometryData packedData,
        byte[] output,
        int outPos,
        int packedVertexOffset)
    {
        // HasVertexWeights = 1 (enabled for expansion)
        output[outPos++] = 1;

        // Write weights for each vertex
        for (var v = 0; v < partition.NumVertices; v++)
        {
            var globalVertexIdx = GetGlobalVertexIndex(partition, v, packedVertexOffset);
            outPos = WriteVertexWeights(partition, packedData, globalVertexIdx, output, outPos);
        }

        return outPos;
    }

    /// <summary>
    ///     Gets the global vertex index from partition local index.
    /// </summary>
    private static int GetGlobalVertexIndex(PartitionInfo partition, int localIdx, int packedVertexOffset)
    {
        return partition.HasVertexMap && localIdx < partition.VertexMap.Length
            ? partition.VertexMap[localIdx]
            : packedVertexOffset + localIdx;
    }

    /// <summary>
    ///     Writes bone weights for a single vertex.
    /// </summary>
    private static int WriteVertexWeights(
        PartitionInfo partition,
        PackedGeometryData packedData,
        int globalVertexIdx,
        byte[] output,
        int outPos)
    {
        for (var w = 0; w < partition.NumWeightsPerVertex; w++)
        {
            var weight = GetBoneWeight(packedData, globalVertexIdx, w);
            BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(outPos), weight);
            outPos += 4;
        }

        return outPos;
    }

    /// <summary>
    ///     Gets a bone weight from packed data.
    /// </summary>
    private static float GetBoneWeight(PackedGeometryData packedData, int globalVertexIdx, int weightIdx)
    {
        if (packedData.BoneWeights == null || globalVertexIdx >= packedData.NumVertices)
        {
            return 0f;
        }

        var idx = globalVertexIdx * 4 + weightIdx;
        return idx < packedData.BoneWeights.Length ? packedData.BoneWeights[idx] : 0f;
    }

    /// <summary>
    ///     Writes the strip lengths array.
    /// </summary>
    private static int WriteStripLengthsArray(ushort[] stripLengths, byte[] output, int outPos)
    {
        foreach (var len in stripLengths)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), len);
            outPos += 2;
        }

        return outPos;
    }

    /// <summary>
    ///     Writes the faces section (flag + strips or triangles).
    /// </summary>
    private static int WriteFacesSection(PartitionInfo partition, byte[] output, int outPos)
    {
        output[outPos++] = (byte)(partition.HasFaces ? 1 : 0);

        if (!partition.HasFaces)
        {
            return outPos;
        }

        if (partition is { NumStrips: > 0, Strips.Length: > 0 })
        {
            return WriteStrips(partition, output, outPos);
        }

        if (partition.Triangles != null)
        {
            return WriteTriangles(partition, output, outPos);
        }

        return outPos;
    }

    /// <summary>
    ///     Writes triangle strip indices.
    /// </summary>
    private static int WriteStrips(PartitionInfo partition, byte[] output, int outPos)
    {
        for (var s = 0; s < partition.NumStrips && s < partition.Strips.Length; s++)
        {
            foreach (var idx in partition.Strips[s])
            {
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), idx);
                outPos += 2;
            }
        }

        return outPos;
    }

    /// <summary>
    ///     Writes direct triangle indices.
    /// </summary>
    private static int WriteTriangles(PartitionInfo partition, byte[] output, int outPos)
    {
        for (var t = 0; t < partition.NumTriangles; t++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), partition.Triangles![t, 0]);
            outPos += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), partition.Triangles[t, 1]);
            outPos += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(outPos), partition.Triangles[t, 2]);
            outPos += 2;
        }

        return outPos;
    }

    /// <summary>
    ///     Writes the bone indices section from packed geometry data.
    /// </summary>
    private static int WriteBoneIndicesSection(
        PartitionInfo partition,
        PackedGeometryData packedData,
        byte[] output,
        int outPos,
        int packedVertexOffset)
    {
        // HasBoneIndices = 1 (enabled for expansion)
        output[outPos++] = 1;

        // Write bone indices for each vertex
        for (var v = 0; v < partition.NumVertices; v++)
        {
            var globalVertexIdx = GetGlobalVertexIndex(partition, v, packedVertexOffset);
            outPos = WriteVertexBoneIndices(partition, packedData, globalVertexIdx, output, outPos);
        }

        return outPos;
    }

    /// <summary>
    ///     Writes bone indices for a single vertex.
    /// </summary>
    private static int WriteVertexBoneIndices(
        PartitionInfo partition,
        PackedGeometryData packedData,
        int globalVertexIdx,
        byte[] output,
        int outPos)
    {
        for (var w = 0; w < partition.NumWeightsPerVertex; w++)
        {
            var boneIdx = GetMappedBoneIndex(packedData, globalVertexIdx, w, partition.Bones);
            output[outPos++] = boneIdx;
        }

        return outPos;
    }

    /// <summary>
    ///     Gets a bone index from packed data and maps it to partition-local index.
    /// </summary>
    private static byte GetMappedBoneIndex(
        PackedGeometryData packedData,
        int globalVertexIdx,
        int weightIdx,
        ushort[] partitionBones)
    {
        if (packedData.BoneIndices == null || globalVertexIdx >= packedData.NumVertices)
        {
            return 0;
        }

        var idx = globalVertexIdx * 4 + weightIdx;
        if (idx >= packedData.BoneIndices.Length)
        {
            return 0;
        }

        var globalBoneIdx = packedData.BoneIndices[idx];
        return MapToPartitionBoneIndex(globalBoneIdx, partitionBones);
    }

    /// <summary>
    ///     Maps a global bone index to a partition-local bone index.
    ///     The partition's Bones array contains the mapping from local to global.
    /// </summary>
    private static byte MapToPartitionBoneIndex(byte globalBoneIdx, ushort[] partitionBones)
    {
        for (var i = 0; i < partitionBones.Length; i++)
        {
            if (partitionBones[i] == globalBoneIdx)
            {
                return (byte)i;
            }
        }

        // Bone not found in partition - return 0 as fallback
        return 0;
    }
}
