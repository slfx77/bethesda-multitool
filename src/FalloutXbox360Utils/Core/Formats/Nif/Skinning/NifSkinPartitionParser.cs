// NIF Skin Partition parser for extracting vertex maps and triangles
// Used to remap vertices from partition order to mesh order in skinned meshes,
// and to extract/convert triangle strips from NiSkinPartition blocks.

using System.Buffers.Binary;

namespace FalloutXbox360Utils.Core.Formats.Nif.Skinning;

/// <summary>
///     Parses NiSkinPartition blocks to extract vertex mapping data and triangles.
///     In skinned meshes, BSPackedAdditionalGeometryData stores vertices in partition order,
///     but NiTriShapeData expects them in mesh order. The VertexMap provides the mapping.
/// </summary>
internal static class NifSkinPartitionParser
{
    private static readonly Logger Log = Logger.Instance;

    #region Vertex Map Extraction

    /// <summary>
    ///     Extracts the combined VertexMap from all partitions in a NiSkinPartition block.
    ///     Returns null if no vertex map is present.
    /// </summary>
    public static ushort[]? ExtractVertexMap(byte[] data, int offset, int size, bool isBigEndian)
    {
        if (size < 4)
        {
            return null;
        }

        var reader = new PartitionReader(data, offset, size, isBigEndian);
        var numPartitions = reader.ReadUInt32();

        Log.Debug($"      NiSkinPartition: {numPartitions} partitions, block size {size}");

        if (numPartitions == 0 || numPartitions > 1000)
        {
            return null;
        }

        var allVertexMaps = new List<ushort[]>();
        var totalVertices = 0;

        for (var p = 0; p < numPartitions && reader.Pos < reader.End; p++)
        {
            var header = TryReadPartitionHeader(ref reader, p);
            if (header == null)
            {
                break;
            }

            Log.Debug(
                $"        Partition {p}: {header.Value.NumVertices} verts, {header.Value.NumTriangles} tris, " +
                $"{header.Value.NumBones} bones, {header.Value.NumStrips} strips, {header.Value.NumWeightsPerVertex} weights/vert");

            // Skip Bones array
            reader.Skip(header.Value.NumBones * 2);
            if (reader.Pos > reader.End)
            {
                break;
            }

            // Read vertex map if present
            var vertexMap = TryReadVertexMap(ref reader, header.Value.NumVertices, p);
            if (vertexMap != null)
            {
                allVertexMaps.Add(vertexMap);
                totalVertices += header.Value.NumVertices;
            }

            // Skip remaining partition data
            if (!SkipRemainingPartitionData(ref reader, header.Value))
            {
                break;
            }
        }

        return CombineVertexMaps(allVertexMaps, totalVertices);
    }

    private static PartitionHeader? TryReadPartitionHeader(ref PartitionReader reader, int partitionIndex)
    {
        if (!reader.CanRead(10))
        {
            Log.Debug($"        Partition {partitionIndex}: early break at header");
            return null;
        }

        return new PartitionHeader(
            reader.ReadUInt16(),
            reader.ReadUInt16(),
            reader.ReadUInt16(),
            reader.ReadUInt16(),
            reader.ReadUInt16());
    }

    private static ushort[]? TryReadVertexMap(ref PartitionReader reader, ushort numVertices, int partitionIndex)
    {
        if (!reader.CanRead(1))
        {
            Log.Debug($"        Partition {partitionIndex}: early break at HasVertexMap");
            return null;
        }

        var hasVertexMap = reader.ReadByte();
        Log.Debug($"        Partition {partitionIndex}: hasVertexMap={hasVertexMap}");

        if (hasVertexMap == 0)
        {
            return null;
        }

        if (!reader.CanRead(numVertices * 2))
        {
            Log.Debug($"        Partition {partitionIndex}: early break reading VertexMap");
            return null;
        }

        var vertexMap = new ushort[numVertices];
        for (var i = 0; i < numVertices; i++)
        {
            vertexMap[i] = reader.ReadUInt16();
        }

        return vertexMap;
    }

    private static bool SkipRemainingPartitionData(ref PartitionReader reader, PartitionHeader header)
    {
        if (!TrySkipVertexWeightsSection(ref reader, header))
        {
            return false;
        }

        var stripLengths = ReadStripLengthsArray(ref reader, header.NumStrips);

        if (!TrySkipFacesSection(ref reader, header, stripLengths))
        {
            return false;
        }

        return TrySkipBoneIndicesSection(ref reader, header);
    }

    private static bool TrySkipVertexWeightsSection(ref PartitionReader reader, PartitionHeader header)
    {
        if (!reader.CanRead(1))
        {
            return false;
        }

        var hasVertexWeights = reader.ReadByte();
        if (hasVertexWeights == 0)
        {
            return true;
        }

        reader.Skip(header.NumVertices * header.NumWeightsPerVertex * 4);
        return reader.Pos <= reader.End;
    }

    private static ushort[] ReadStripLengthsArray(ref PartitionReader reader, ushort numStrips)
    {
        var stripLengths = new ushort[numStrips];
        for (var i = 0; i < numStrips && reader.CanRead(2); i++)
        {
            stripLengths[i] = reader.ReadUInt16();
        }

        return stripLengths;
    }

    private static bool TrySkipFacesSection(ref PartitionReader reader, PartitionHeader header, ushort[] stripLengths)
    {
        if (!reader.CanRead(1))
        {
            return false;
        }

        var hasFaces = reader.ReadByte();
        if (hasFaces == 0)
        {
            return true;
        }

        return header.NumStrips != 0
            ? TrySkipAllStrips(ref reader, stripLengths)
            : TrySkipTriangles(ref reader, header.NumTriangles);
    }

    private static bool TrySkipAllStrips(ref PartitionReader reader, ushort[] stripLengths)
    {
        foreach (var stripLength in stripLengths)
        {
            reader.Skip(stripLength * 2);
            if (reader.Pos > reader.End)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TrySkipTriangles(ref PartitionReader reader, ushort numTriangles)
    {
        reader.Skip(numTriangles * 6);
        return reader.Pos <= reader.End;
    }

    private static bool TrySkipBoneIndicesSection(ref PartitionReader reader, PartitionHeader header)
    {
        if (!reader.CanRead(1))
        {
            return false;
        }

        var hasBoneIndices = reader.ReadByte();
        if (hasBoneIndices == 0)
        {
            return true;
        }

        reader.Skip(header.NumVertices * header.NumWeightsPerVertex);
        return reader.Pos <= reader.End;
    }

    private static ushort[]? CombineVertexMaps(List<ushort[]> allVertexMaps, int totalVertices)
    {
        if (allVertexMaps.Count == 0)
        {
            return null;
        }

        var combined = new ushort[totalVertices];
        var idx = 0;
        foreach (var map in allVertexMaps)
        {
            map.CopyTo(combined, idx);
            idx += map.Length;
        }

        return combined;
    }

    #endregion

    #region Triangle Extraction

    /// <summary>
    ///     Extracts all triangles from a NiSkinPartition block by converting strips to triangles.
    ///     The triangles use mesh vertex indices (remapped via VertexMap).
    ///     Returns null if no faces are present.
    /// </summary>
    public static ushort[]? ExtractTriangles(byte[] data, int offset, int size, bool isBigEndian, bool verbose = false)
    {
        _ = verbose; // Reserved for future diagnostic use
        if (size < 4)
        {
            return null;
        }

        var reader = new TriangleReader
        {
            Data = data,
            Pos = offset,
            End = offset + size,
            IsBigEndian = isBigEndian
        };

        var numPartitions = reader.ReadUInt32();
        if (numPartitions == 0 || numPartitions > 1000)
        {
            return null;
        }

        var allTriangles = new List<ushort>();

        for (var p = 0; p < numPartitions && reader.Pos < reader.End; p++)
        {
            if (!TryExtractPartitionTriangles(ref reader, allTriangles))
            {
                break;
            }
        }

        return allTriangles.Count == 0 ? null : [.. allTriangles];
    }

    /// <summary>
    ///     Extracts triangles from a single partition.
    /// </summary>
    private static bool TryExtractPartitionTriangles(ref TriangleReader reader, List<ushort> allTriangles)
    {
        if (!TryReadTrianglePartitionHeader(ref reader, out var header))
        {
            return false;
        }

        // Skip Bones array
        reader.Skip(header.NumBones * 2);
        if (reader.Pos > reader.End)
        {
            return false;
        }

        // Read vertex map
        if (!TryReadTriangleVertexMap(ref reader, header.NumVertices, out var vertexMap))
        {
            return false;
        }

        // Skip vertex weights
        if (!TrySkipTriangleVertexWeights(ref reader, header.NumVertices, header.NumWeightsPerVertex))
        {
            return false;
        }

        // Read strip lengths
        if (!TryReadTriangleStripLengths(ref reader, header.NumStrips, out var stripLengths))
        {
            return false;
        }

        // Read faces
        if (!TryReadFaces(ref reader, header, vertexMap, stripLengths, allTriangles))
        {
            return false;
        }

        // Skip bone indices
        return TrySkipTriangleBoneIndices(ref reader, header.NumVertices, header.NumWeightsPerVertex);
    }

    /// <summary>
    ///     Reads the partition header (5 ushorts = 10 bytes).
    /// </summary>
    private static bool TryReadTrianglePartitionHeader(ref TriangleReader reader, out TrianglePartitionHeader header)
    {
        header = default;
        if (!reader.CanRead(10))
        {
            return false;
        }

        header = new TrianglePartitionHeader(
            reader.ReadUInt16(),
            reader.ReadUInt16(),
            reader.ReadUInt16(),
            reader.ReadUInt16(),
            reader.ReadUInt16());
        return true;
    }

    /// <summary>
    ///     Reads the vertex map if present.
    /// </summary>
    private static bool TryReadTriangleVertexMap(ref TriangleReader reader, ushort numVertices, out ushort[]? vertexMap)
    {
        vertexMap = null;
        if (!reader.CanRead(1))
        {
            return false;
        }

        var hasVertexMap = reader.ReadByte();
        if (hasVertexMap == 0)
        {
            return true;
        }

        if (!reader.CanRead(numVertices * 2))
        {
            return false;
        }

        vertexMap = new ushort[numVertices];
        for (var i = 0; i < numVertices; i++)
        {
            vertexMap[i] = reader.ReadUInt16();
        }

        return true;
    }

    /// <summary>
    ///     Skips vertex weights if present.
    /// </summary>
    private static bool TrySkipTriangleVertexWeights(ref TriangleReader reader, ushort numVertices, ushort numWeightsPerVertex)
    {
        if (!reader.CanRead(1))
        {
            return false;
        }

        var hasVertexWeights = reader.ReadByte();
        if (hasVertexWeights != 0)
        {
            reader.Skip(numVertices * numWeightsPerVertex * 4);
            if (reader.Pos > reader.End)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Reads strip lengths array.
    /// </summary>
    private static bool TryReadTriangleStripLengths(ref TriangleReader reader, ushort numStrips, out ushort[] stripLengths)
    {
        stripLengths = new ushort[numStrips];
        for (var i = 0; i < numStrips; i++)
        {
            if (!reader.CanRead(2))
            {
                return false;
            }

            stripLengths[i] = reader.ReadUInt16();
        }

        return true;
    }

    /// <summary>
    ///     Reads faces (strips or direct triangles) from the partition.
    /// </summary>
    private static bool TryReadFaces(
        ref TriangleReader reader,
        TrianglePartitionHeader header,
        ushort[]? vertexMap,
        ushort[] stripLengths,
        List<ushort> allTriangles)
    {
        if (!reader.CanRead(1))
        {
            return false;
        }

        var hasFaces = reader.ReadByte();
        if (hasFaces == 0)
        {
            return true;
        }

        if (header.NumStrips != 0)
        {
            return TryReadStrips(ref reader, stripLengths, vertexMap, allTriangles);
        }

        return TryReadDirectTriangles(ref reader, header.NumTriangles, vertexMap, allTriangles);
    }

    /// <summary>
    ///     Reads triangle strips and converts them to triangles.
    /// </summary>
    private static bool TryReadStrips(
        ref TriangleReader reader,
        ushort[] stripLengths,
        ushort[]? vertexMap,
        List<ushort> allTriangles)
    {
        foreach (var stripLength in stripLengths)
        {
            if (!reader.CanRead(stripLength * 2))
            {
                return false;
            }

            var strip = new ushort[stripLength];
            for (var i = 0; i < stripLength; i++)
            {
                strip[i] = reader.ReadUInt16();
            }

            ConvertStripToTriangles(strip, vertexMap, allTriangles);
        }

        return true;
    }

    /// <summary>
    ///     Reads direct triangles (non-strip format).
    /// </summary>
    private static bool TryReadDirectTriangles(
        ref TriangleReader reader,
        ushort numTriangles,
        ushort[]? vertexMap,
        List<ushort> allTriangles)
    {
        for (var t = 0; t < numTriangles; t++)
        {
            if (!reader.CanRead(6))
            {
                return false;
            }

            var v0 = reader.ReadUInt16();
            var v1 = reader.ReadUInt16();
            var v2 = reader.ReadUInt16();

            AddRemappedTriangle(v0, v1, v2, vertexMap, allTriangles);
        }

        return true;
    }

    /// <summary>
    ///     Adds a triangle with optional vertex remapping.
    /// </summary>
    private static void AddRemappedTriangle(
        ushort v0,
        ushort v1,
        ushort v2,
        ushort[]? vertexMap,
        List<ushort> triangles)
    {
        if (vertexMap != null)
        {
            if (v0 < vertexMap.Length)
            {
                v0 = vertexMap[v0];
            }

            if (v1 < vertexMap.Length)
            {
                v1 = vertexMap[v1];
            }

            if (v2 < vertexMap.Length)
            {
                v2 = vertexMap[v2];
            }
        }

        triangles.Add(v0);
        triangles.Add(v1);
        triangles.Add(v2);
    }

    /// <summary>
    ///     Skips bone indices if present.
    /// </summary>
    private static bool TrySkipTriangleBoneIndices(ref TriangleReader reader, ushort numVertices, ushort numWeightsPerVertex)
    {
        if (!reader.CanRead(1))
        {
            return false;
        }

        var hasBoneIndices = reader.ReadByte();
        if (hasBoneIndices != 0)
        {
            reader.Skip(numVertices * numWeightsPerVertex);
            if (reader.Pos > reader.End)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Converts a triangle strip to individual triangles.
    ///     Handles degenerate triangles (consecutive duplicate vertices used for strip restart).
    /// </summary>
    private static void ConvertStripToTriangles(ushort[] strip, ushort[]? vertexMap, List<ushort> triangles)
    {
        if (strip.Length < 3)
        {
            return;
        }

        for (var i = 0; i < strip.Length - 2; i++)
        {
            var v0 = strip[i];
            var v1 = strip[i + 1];
            var v2 = strip[i + 2];

            if (IsDegenerateTriangle(v0, v1, v2))
            {
                continue;
            }

            AddStripTriangle(i, v0, v1, v2, vertexMap, triangles);
        }
    }

    /// <summary>
    ///     Checks if a triangle is degenerate (any two vertices are the same).
    /// </summary>
    private static bool IsDegenerateTriangle(ushort v0, ushort v1, ushort v2)
    {
        return v0 == v1 || v1 == v2 || v0 == v2;
    }

    /// <summary>
    ///     Adds a triangle from a strip with correct winding order and optional remapping.
    /// </summary>
    private static void AddStripTriangle(
        int index,
        ushort v0,
        ushort v1,
        ushort v2,
        ushort[]? vertexMap,
        List<ushort> triangles)
    {
        // Remap to mesh indices if vertex map exists
        if (vertexMap != null)
        {
            if (v0 < vertexMap.Length)
            {
                v0 = vertexMap[v0];
            }

            if (v1 < vertexMap.Length)
            {
                v1 = vertexMap[v1];
            }

            if (v2 < vertexMap.Length)
            {
                v2 = vertexMap[v2];
            }
        }

        // Alternate winding order for each triangle in the strip
        if (index % 2 == 0)
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

    #endregion

    #region Reader Types

    /// <summary>Reader context for partition parsing.</summary>
    private ref struct PartitionReader(byte[] data, int offset, int size, bool isBigEndian)
    {
        private readonly byte[] _data = data;
        private readonly bool _isBigEndian = isBigEndian;
        public readonly int End = offset + size;
        public int Pos = offset;

        public readonly bool CanRead(int bytes)
        {
            return Pos + bytes <= End;
        }

        public ushort ReadUInt16()
        {
            var value = _isBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(Pos, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Pos, 2));
            Pos += 2;
            return value;
        }

        public uint ReadUInt32()
        {
            var value = _isBigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(Pos, 4))
                : BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Pos, 4));
            Pos += 4;
            return value;
        }

        public byte ReadByte()
        {
            return _data[Pos++];
        }

        public void Skip(int bytes)
        {
            Pos += bytes;
        }
    }

    /// <summary>Parsed partition header fields.</summary>
    private readonly record struct PartitionHeader(
        ushort NumVertices,
        ushort NumTriangles,
        ushort NumBones,
        ushort NumStrips,
        ushort NumWeightsPerVertex);

    /// <summary>
    ///     Reader context for triangle extraction with position tracking.
    /// </summary>
    private ref struct TriangleReader
    {
        public byte[] Data;
        public int End;
        public bool IsBigEndian;
        public int Pos;

        public readonly bool CanRead(int bytes)
        {
            return Pos + bytes <= End;
        }

        public ushort ReadUInt16()
        {
            var value = IsBigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan(Pos, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(Pos, 2));
            Pos += 2;
            return value;
        }

        public uint ReadUInt32()
        {
            var value = IsBigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(Pos, 4))
                : BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(Pos, 4));
            Pos += 4;
            return value;
        }

        public byte ReadByte()
        {
            return Data[Pos++];
        }

        public void Skip(int bytes)
        {
            Pos += bytes;
        }
    }

    /// <summary>
    ///     Partition header data for triangle extraction.
    /// </summary>
    private readonly record struct TrianglePartitionHeader(
        ushort NumVertices,
        ushort NumTriangles,
        ushort NumBones,
        ushort NumStrips,
        ushort NumWeightsPerVertex);

    #endregion
}
