// NIF Skin Partition parser for extracting vertex maps
// Used to remap vertices from partition order to mesh order in skinned meshes

using System.Buffers.Binary;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Parses NiSkinPartition blocks to extract vertex mapping data.
///     In skinned meshes, BSPackedAdditionalGeometryData stores vertices in partition order,
///     but NiTriShapeData expects them in mesh order. The VertexMap provides the mapping.
/// </summary>
internal static partial class NifSkinPartitionParser
{
    private static readonly Logger Log = Logger.Instance;

    /// <summary>
    ///     Extracts the combined VertexMap from all partitions in a NiSkinPartition block.
    ///     Returns null if no vertex map is present.
    /// </summary>
    public static ushort[]? ExtractVertexMap(byte[] data, int offset, int size, bool isBigEndian)
    {
        if (size < 4) return null;

        var pos = offset;
        var end = offset + size;

        // NiSkinPartition structure for Fallout 3/NV (version 20.2.0.7, BS version 34):
        // The DataSize/VertexSize/VertexDesc/VertexData fields only exist in Skyrim SE+ (#BS_SSE#)
        // For FO3/NV, the structure is simply:
        // - NumPartitions (uint, 4 bytes)
        // - Partitions (SkinPartition array)

        // NumPartitions (uint)
        var numPartitions = isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
        pos += 4;

        Log.Debug($"      NiSkinPartition: {numPartitions} partitions, block size {size}");

        if (numPartitions == 0 || numPartitions > 1000) return null;

        // Collect all vertex maps from partitions
        var allVertexMaps = new List<ushort[]>();
        var totalVertices = 0;

        for (var p = 0; p < numPartitions && pos < end; p++)
        {
            // NumVertices (ushort)
            if (pos + 2 > end)
            {
                Log.Debug($"        Partition {p}: early break at NumVertices");
                break;
            }

            var numVertices = isBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;

            // NumTriangles (ushort)
            if (pos + 2 > end)
            {
                Log.Debug($"        Partition {p}: early break at NumTriangles");
                break;
            }

            var numTriangles = isBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;

            // NumBones (ushort)
            if (pos + 2 > end)
            {
                Log.Debug($"        Partition {p}: early break at NumBones");
                break;
            }

            var numBones = isBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;

            // NumStrips (ushort)
            if (pos + 2 > end)
            {
                Log.Debug($"        Partition {p}: early break at NumStrips");
                break;
            }

            var numStrips = isBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;

            // NumWeightsPerVertex (ushort)
            if (pos + 2 > end)
            {
                Log.Debug($"        Partition {p}: early break at NumWeightsPerVertex");
                break;
            }

            var numWeightsPerVertex = isBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;

            Log.Debug(
                $"        Partition {p}: {numVertices} verts, {numTriangles} tris, {numBones} bones, {numStrips} strips, {numWeightsPerVertex} weights/vert");

            // Skip Bones array (numBones * ushort)
            pos += numBones * 2;
            if (pos > end)
            {
                Log.Debug($"        Partition {p}: early break after Bones");
                break;
            }

            // HasVertexMap (byte)
            if (pos + 1 > end)
            {
                Log.Debug($"        Partition {p}: early break at HasVertexMap");
                break;
            }

            var hasVertexMap = data[pos++];

            Log.Debug($"        Partition {p}: hasVertexMap={hasVertexMap}");

            // VertexMap (if HasVertexMap)
            ushort[]? vertexMap = null;
            if (hasVertexMap != 0)
            {
                if (pos + numVertices * 2 > end)
                {
                    Log.Debug($"        Partition {p}: early break reading VertexMap");
                    break;
                }

                vertexMap = new ushort[numVertices];
                for (var i = 0; i < numVertices; i++)
                {
                    vertexMap[i] = isBigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
                        : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
                    pos += 2;
                }

                allVertexMaps.Add(vertexMap);
                totalVertices += numVertices;
            }

            // HasVertexWeights (byte)
            if (pos + 1 > end) break;
            var hasVertexWeights = data[pos++];

            // Skip VertexWeights (NumVertices x NumWeightsPerVertex floats)
            if (hasVertexWeights != 0)
            {
                pos += numVertices * numWeightsPerVertex * 4;
                if (pos > end) break;
            }

            // StripLengths array (numStrips * ushort)
            var stripLengths = new ushort[numStrips];
            for (var i = 0; i < numStrips && pos + 2 <= end; i++)
            {
                stripLengths[i] = isBigEndian
                    ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
                    : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
                pos += 2;
            }

            // HasFaces (byte)
            if (pos + 1 > end) break;
            var hasFaces = data[pos++];

            // Skip Strips (if HasFaces and NumStrips != 0)
            if (hasFaces != 0 && numStrips != 0)
                for (var s = 0; s < numStrips; s++)
                {
                    pos += stripLengths[s] * 2;
                    if (pos > end) break;
                }

            // Skip Triangles (if HasFaces and NumStrips == 0)
            if (hasFaces != 0 && numStrips == 0)
            {
                pos += numTriangles * 6; // 3 ushorts per triangle
                if (pos > end) break;
            }

            // HasBoneIndices (byte)
            if (pos + 1 > end) break;
            var hasBoneIndices = data[pos++];

            // Skip BoneIndices (NumVertices x NumWeightsPerVertex bytes)
            if (hasBoneIndices != 0)
            {
                pos += numVertices * numWeightsPerVertex;
                if (pos > end) break;
            }

            // Note: LODLevel/GlobalVB fields only exist in BS version 100+ (Skyrim SE+)
            // For FO3/NV (BS version 34), we don't skip anything here
        }

        // If no vertex maps found, return null (not a skinned mesh or no remapping needed)
        if (allVertexMaps.Count == 0)
            return null;

        // Combine all vertex maps into one
        // For skinned meshes, the partitions are processed in order and their
        // vertex maps concatenate to form the complete mapping from partition order to mesh order
        var combined = new ushort[totalVertices];
        var idx = 0;
        foreach (var map in allVertexMaps)
        {
            map.CopyTo(combined, idx);
            idx += map.Length;
        }

        return combined;
    }
}

