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
}
