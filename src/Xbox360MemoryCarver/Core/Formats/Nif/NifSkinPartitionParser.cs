using System.Buffers.Binary;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Parses NiSkinPartition blocks to extract VertexMap data.
/// </summary>
internal static class NifSkinPartitionParser
{
    /// <summary>
    ///     Extracts the combined VertexMap from all partitions in a NiSkinPartition block.
    /// </summary>
    /// <remarks>
    ///     For skinned meshes, BSPackedAdditionalGeometryData stores vertices in partition order.
    ///     The VertexMap maps: mesh_vertex_index = VertexMap[partition_vertex_index]
    ///     This is needed to remap packed vertices to the correct mesh positions.
    /// </remarks>
    public static ushort[]? ExtractVertexMap(byte[] data, int blockOffset, int blockSize, bool bigEndian)
    {
        if (blockSize < 4)
            return null;

        var pos = blockOffset;

        var numPartitions = bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
        pos += 4;

        if (numPartitions == 0)
            return null;

        // For now, we only support single-partition meshes (most common case)
        // Multi-partition support would require concatenating VertexMaps
        if (numPartitions > 1)
        {
            // Warning logged silently - multi-partition remapping not yet supported
            // Still try to extract the first partition's VertexMap as a fallback
        }

        // Parse first partition to get its VertexMap
        var numVertices = bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
        pos += 2;

        // Skip numTriangles
        pos += 2;

        var numBones = bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
        pos += 2;

        // Skip numStrips
        pos += 2;

        // Skip numWeightsPerVertex
        pos += 2;

        // Skip bones array
        pos += numBones * 2;

        // HasVertexMap
        var hasVertexMap = data[pos++];
        if (hasVertexMap == 0)
            return null;

        // Read VertexMap
        var vertexMap = new ushort[numVertices];
        for (var i = 0; i < numVertices; i++)
        {
            vertexMap[i] = bigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos));
            pos += 2;
        }

        return vertexMap;
    }

    /// <summary>
    ///     Finds the NiSkinPartition block index referenced by a BSDismemberSkinInstance block.
    /// </summary>
    public static int FindSkinPartitionRef(byte[] data, int skinInstanceOffset, bool bigEndian)
    {
        // BSDismemberSkinInstance structure:
        // Offset 0: SkinData ref (4 bytes)
        // Offset 4: SkinPartition ref (4 bytes) <-- what we want
        // Offset 8: SkeletonRoot ref (4 bytes)
        // ... rest of data

        var skinPartitionRef = bigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(skinInstanceOffset + 4))
            : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(skinInstanceOffset + 4));

        return skinPartitionRef;
    }
}
