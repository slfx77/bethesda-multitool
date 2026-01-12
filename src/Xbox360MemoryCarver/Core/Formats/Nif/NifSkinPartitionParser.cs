// NIF Skin Partition parser for extracting vertex maps
// Used to remap vertices from partition order to mesh order in skinned meshes

using System.Buffers.Binary;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Parses NiSkinPartition blocks to extract vertex mapping data.
///     In skinned meshes, BSPackedAdditionalGeometryData stores vertices in partition order,
///     but NiTriShapeData expects them in mesh order. The VertexMap provides the mapping.
/// </summary>
internal static class NifSkinPartitionParser
{
    /// <summary>
    ///     Extracts the combined VertexMap from all partitions in a NiSkinPartition block.
    ///     Returns null if no vertex map is present.
    /// </summary>
    public static ushort[]? ExtractVertexMap(byte[] data, int offset, int size, bool isBigEndian, bool verbose = false)
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

        if (verbose)
            Console.WriteLine($"      NiSkinPartition: {numPartitions} partitions, block size {size}");

        if (numPartitions == 0 || numPartitions > 1000) return null;

        // Collect all vertex maps from partitions
        var allVertexMaps = new List<ushort[]>();
        var totalVertices = 0;

        for (var p = 0; p < numPartitions && pos < end; p++)
        {
            // NumVertices (ushort)
            if (pos + 2 > end)
            {
                if (verbose) Console.WriteLine($"        Partition {p}: early break at NumVertices");
                break;
            }

            var numVertices = isBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;

            // NumTriangles (ushort)
            if (pos + 2 > end)
            {
                if (verbose) Console.WriteLine($"        Partition {p}: early break at NumTriangles");
                break;
            }

            var numTriangles = isBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;

            // NumBones (ushort)
            if (pos + 2 > end)
            {
                if (verbose) Console.WriteLine($"        Partition {p}: early break at NumBones");
                break;
            }

            var numBones = isBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;

            // NumStrips (ushort)
            if (pos + 2 > end)
            {
                if (verbose) Console.WriteLine($"        Partition {p}: early break at NumStrips");
                break;
            }

            var numStrips = isBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;

            // NumWeightsPerVertex (ushort)
            if (pos + 2 > end)
            {
                if (verbose) Console.WriteLine($"        Partition {p}: early break at NumWeightsPerVertex");
                break;
            }

            var numWeightsPerVertex = isBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;

            if (verbose)
                Console.WriteLine(
                    $"        Partition {p}: {numVertices} verts, {numTriangles} tris, {numBones} bones, {numStrips} strips, {numWeightsPerVertex} weights/vert");

            // Skip Bones array (numBones * ushort)
            pos += numBones * 2;
            if (pos > end)
            {
                if (verbose) Console.WriteLine($"        Partition {p}: early break after Bones");
                break;
            }

            // HasVertexMap (byte)
            if (pos + 1 > end)
            {
                if (verbose) Console.WriteLine($"        Partition {p}: early break at HasVertexMap");
                break;
            }

            var hasVertexMap = data[pos++];

            if (verbose)
                Console.WriteLine($"        Partition {p}: hasVertexMap={hasVertexMap}");

            // VertexMap (if HasVertexMap)
            ushort[]? vertexMap = null;
            if (hasVertexMap != 0)
            {
                if (pos + numVertices * 2 > end)
                {
                    if (verbose) Console.WriteLine($"        Partition {p}: early break reading VertexMap");
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

    /// <summary>
    ///     Extracts all triangles from a NiSkinPartition block by converting strips to triangles.
    ///     The triangles use mesh vertex indices (remapped via VertexMap).
    ///     Returns null if no faces are present.
    /// </summary>
    public static ushort[]? ExtractTriangles(byte[] data, int offset, int size, bool isBigEndian, bool verbose = false)
    {
        if (size < 4) return null;

        var pos = offset;
        var end = offset + size;

        // NumPartitions (uint)
        var numPartitions = isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
        pos += 4;

        if (numPartitions == 0 || numPartitions > 1000) return null;

        // Collect all triangles from partitions
        var allTriangles = new List<ushort>();

        for (var p = 0; p < numPartitions && pos < end; p++)
        {
            // NumVertices (ushort)
            if (pos + 2 > end) break;
            var numVertices = isBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;

            // NumTriangles (ushort)
            if (pos + 2 > end) break;
            var numTriangles = isBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;

            // NumBones (ushort)
            if (pos + 2 > end) break;
            var numBones = isBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;

            // NumStrips (ushort)
            if (pos + 2 > end) break;
            var numStrips = isBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;

            // NumWeightsPerVertex (ushort)
            if (pos + 2 > end) break;
            var numWeightsPerVertex = isBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;

            // Skip Bones array (numBones * ushort)
            pos += numBones * 2;
            if (pos > end) break;

            // HasVertexMap (byte)
            if (pos + 1 > end) break;
            var hasVertexMap = data[pos++];

            // Read VertexMap (for remapping partition indices to mesh indices)
            ushort[]? vertexMap = null;
            if (hasVertexMap != 0)
            {
                if (pos + numVertices * 2 > end) break;
                vertexMap = new ushort[numVertices];
                for (var i = 0; i < numVertices; i++)
                {
                    vertexMap[i] = isBigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
                        : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
                    pos += 2;
                }
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

            // Read Strips and convert to triangles
            if (hasFaces != 0 && numStrips != 0)
                for (var s = 0; s < numStrips; s++)
                {
                    if (pos + stripLengths[s] * 2 > end) break;

                    // Read strip indices
                    var strip = new ushort[stripLengths[s]];
                    for (var i = 0; i < stripLengths[s]; i++)
                    {
                        strip[i] = isBigEndian
                            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
                            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
                        pos += 2;
                    }

                    // Convert strip to triangles (using mesh vertex indices)
                    ConvertStripToTriangles(strip, vertexMap, allTriangles);
                }

            // Read direct Triangles (if HasFaces and NumStrips == 0)
            if (hasFaces != 0 && numStrips == 0)
                for (var t = 0; t < numTriangles && pos + 6 <= end; t++)
                {
                    var v0 = isBigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
                        : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
                    pos += 2;
                    var v1 = isBigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
                        : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
                    pos += 2;
                    var v2 = isBigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2))
                        : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
                    pos += 2;

                    // Remap to mesh indices if vertex map exists
                    if (vertexMap != null)
                    {
                        if (v0 < vertexMap.Length) v0 = vertexMap[v0];
                        if (v1 < vertexMap.Length) v1 = vertexMap[v1];
                        if (v2 < vertexMap.Length) v2 = vertexMap[v2];
                    }

                    allTriangles.Add(v0);
                    allTriangles.Add(v1);
                    allTriangles.Add(v2);
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
        }

        if (allTriangles.Count == 0)
            return null;

        return [.. allTriangles];
    }

    /// <summary>
    ///     Converts a triangle strip to individual triangles.
    ///     Handles degenerate triangles (consecutive duplicate vertices used for strip restart).
    /// </summary>
    private static void ConvertStripToTriangles(ushort[] strip, ushort[]? vertexMap, List<ushort> triangles)
    {
        if (strip.Length < 3) return;

        for (var i = 0; i < strip.Length - 2; i++)
        {
            var v0 = strip[i];
            var v1 = strip[i + 1];
            var v2 = strip[i + 2];

            // Skip degenerate triangles (any two vertices the same)
            if (v0 == v1 || v1 == v2 || v0 == v2)
                continue;

            // Remap to mesh indices if vertex map exists
            if (vertexMap != null)
            {
                if (v0 < vertexMap.Length) v0 = vertexMap[v0];
                if (v1 < vertexMap.Length) v1 = vertexMap[v1];
                if (v2 < vertexMap.Length) v2 = vertexMap[v2];
            }

            // Alternate winding order for each triangle in the strip
            if (i % 2 == 0)
            {
                triangles.Add(v0);
                triangles.Add(v1);
                triangles.Add(v2);
            }
            else
            {
                triangles.Add(v1);
                triangles.Add(v0);
                triangles.Add(v2);
            }
        }
    }
}
