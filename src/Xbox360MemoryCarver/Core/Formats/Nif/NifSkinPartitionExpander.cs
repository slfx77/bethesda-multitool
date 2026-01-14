// NIF Skin Partition expander for adding bone weights/indices from packed geometry data
// Xbox 360 NIFs store bone weights/indices in BSPackedAdditionalGeometryData,
// but PC NIFs need them in NiSkinPartition for skeletal animation to work.

using System.Buffers.Binary;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Expands NiSkinPartition blocks to include bone weights and indices.
///     Xbox 360 NIFs have HasVertexWeights=0 and HasBoneIndices=0 because the data
///     is stored in BSPackedAdditionalGeometryData. PC NIFs need this data in
///     NiSkinPartition for animations to work.
/// </summary>
internal static partial class NifSkinPartitionExpander
{
    private static readonly Logger Log = Logger.Instance;

    /// <summary>
    ///     Parses a NiSkinPartition block and returns structured data.
    /// </summary>
    public static SkinPartitionData? Parse(byte[] data, int offset, int size, bool isBigEndian)
    {
        if (size < 4) return null;

        var reader = new ExpanderReader
        {
            Data = data,
            Pos = offset,
            End = offset + size,
            IsBigEndian = isBigEndian
        };

        var numPartitions = reader.ReadUInt32();
        if (numPartitions == 0 || numPartitions > 1000) return null;

        var result = new SkinPartitionData
        {
            NumPartitions = numPartitions,
            OriginalSize = size
        };

        Log.Debug($"      Parsing NiSkinPartition: {numPartitions} partitions");

        for (var p = 0; p < numPartitions && reader.Pos < reader.End; p++)
        {
            var partition = TryParsePartition(ref reader, p);
            if (partition == null) break;

            result.Partitions.Add(partition);
        }

        return result;
    }

    /// <summary>
    ///     Parses a single partition from the reader.
    /// </summary>
    private static PartitionInfo? TryParsePartition(ref ExpanderReader reader, int index)
    {
        if (!TryReadPartitionHeader(ref reader, out var partition)) return null;

        Log.Debug(
            $"        Partition {index}: {partition.NumVertices} verts, {partition.NumTriangles} tris, " +
            $"{partition.NumBones} bones, {partition.NumStrips} strips, {partition.NumWeightsPerVertex} weights/vert");

        if (!TryReadBonesArray(ref reader, partition)) return null;

        if (!TryReadVertexMapSection(ref reader, partition)) return null;

        if (!TryReadVertexWeightsSection(ref reader, partition)) return null;

        if (!TryReadStripLengthsArray(ref reader, partition)) return null;

        if (!TryReadFacesSection(ref reader, partition)) return null;

        if (!TryReadBoneIndicesSection(ref reader, partition)) return null;

        return partition;
    }

    /// <summary>
    ///     Reads the partition header (5 ushorts).
    /// </summary>
    private static bool TryReadPartitionHeader(ref ExpanderReader reader, out PartitionInfo partition)
    {
        partition = new PartitionInfo();
        if (!reader.CanRead(10)) return false;

        partition.NumVertices = reader.ReadUInt16();
        partition.NumTriangles = reader.ReadUInt16();
        partition.NumBones = reader.ReadUInt16();
        partition.NumStrips = reader.ReadUInt16();
        partition.NumWeightsPerVertex = reader.ReadUInt16();
        return true;
    }

    /// <summary>
    ///     Reads the bones array.
    /// </summary>
    private static bool TryReadBonesArray(ref ExpanderReader reader, PartitionInfo partition)
    {
        partition.Bones = new ushort[partition.NumBones];
        for (var i = 0; i < partition.NumBones; i++)
        {
            if (!reader.CanRead(2)) return false;

            partition.Bones[i] = reader.ReadUInt16();
        }

        return true;
    }

    /// <summary>
    ///     Reads the vertex map section (flag + optional data).
    /// </summary>
    private static bool TryReadVertexMapSection(ref ExpanderReader reader, PartitionInfo partition)
    {
        if (!reader.CanRead(1)) return false;

        partition.HasVertexMap = reader.ReadByte() != 0;
        if (!partition.HasVertexMap) return true;

        partition.VertexMap = new ushort[partition.NumVertices];
        for (var i = 0; i < partition.NumVertices; i++)
        {
            if (!reader.CanRead(2)) return false;

            partition.VertexMap[i] = reader.ReadUInt16();
        }

        return true;
    }

    /// <summary>
    ///     Reads the vertex weights section (flag + optional data).
    /// </summary>
    private static bool TryReadVertexWeightsSection(ref ExpanderReader reader, PartitionInfo partition)
    {
        if (!reader.CanRead(1)) return false;

        partition.HasVertexWeights = reader.ReadByte() != 0;
        if (!partition.HasVertexWeights) return true;

        partition.VertexWeights = new float[partition.NumVertices, partition.NumWeightsPerVertex];
        for (var v = 0; v < partition.NumVertices; v++)
        {
            for (var w = 0; w < partition.NumWeightsPerVertex; w++)
            {
                if (!reader.CanRead(4)) return false;

                partition.VertexWeights[v, w] = reader.ReadFloat();
            }
        }

        return true;
    }

    /// <summary>
    ///     Reads the strip lengths array.
    /// </summary>
    private static bool TryReadStripLengthsArray(ref ExpanderReader reader, PartitionInfo partition)
    {
        partition.StripLengths = new ushort[partition.NumStrips];
        for (var i = 0; i < partition.NumStrips; i++)
        {
            if (!reader.CanRead(2)) return false;

            partition.StripLengths[i] = reader.ReadUInt16();
        }

        return true;
    }

    /// <summary>
    ///     Reads the faces section (strips or triangles).
    /// </summary>
    private static bool TryReadFacesSection(ref ExpanderReader reader, PartitionInfo partition)
    {
        if (!reader.CanRead(1)) return false;

        partition.HasFaces = reader.ReadByte() != 0;
        if (!partition.HasFaces) return true;

        if (partition.NumStrips > 0) return TryReadStrips(ref reader, partition);

        return TryReadTriangles(ref reader, partition);
    }

    /// <summary>
    ///     Reads triangle strips data.
    /// </summary>
    private static bool TryReadStrips(ref ExpanderReader reader, PartitionInfo partition)
    {
        partition.Strips = new ushort[partition.NumStrips][];
        for (var s = 0; s < partition.NumStrips; s++)
        {
            partition.Strips[s] = new ushort[partition.StripLengths[s]];
            for (var i = 0; i < partition.StripLengths[s]; i++)
            {
                if (!reader.CanRead(2)) return false;

                partition.Strips[s][i] = reader.ReadUInt16();
            }
        }

        return true;
    }

    /// <summary>
    ///     Reads triangle data.
    /// </summary>
    private static bool TryReadTriangles(ref ExpanderReader reader, PartitionInfo partition)
    {
        partition.Triangles = new ushort[partition.NumTriangles, 3];
        for (var t = 0; t < partition.NumTriangles; t++)
        {
            if (!reader.CanRead(6)) return false;

            partition.Triangles[t, 0] = reader.ReadUInt16();
            partition.Triangles[t, 1] = reader.ReadUInt16();
            partition.Triangles[t, 2] = reader.ReadUInt16();
        }

        return true;
    }

    /// <summary>
    ///     Reads the bone indices section (flag + optional data).
    /// </summary>
    private static bool TryReadBoneIndicesSection(ref ExpanderReader reader, PartitionInfo partition)
    {
        if (!reader.CanRead(1)) return false;

        partition.HasBoneIndices = reader.ReadByte() != 0;
        if (!partition.HasBoneIndices) return true;

        partition.BoneIndices = new byte[partition.NumVertices, partition.NumWeightsPerVertex];
        for (var v = 0; v < partition.NumVertices; v++)
        {
            for (var w = 0; w < partition.NumWeightsPerVertex; w++)
            {
                if (!reader.CanRead(1)) return false;

                partition.BoneIndices[v, w] = reader.ReadByte();
            }
        }

        return true;
    }

    /// <summary>
    ///     Calculates the expanded size of a NiSkinPartition block when bone weights/indices are added.
    /// </summary>
    public static int CalculateExpandedSize(SkinPartitionData skinPartition)
    {
        var size = 4; // NumPartitions

        foreach (var p in skinPartition.Partitions) size += CalculatePartitionSize(p);

        return size;
    }

    /// <summary>
    ///     Calculates the size of a single partition.
    /// </summary>
    private static int CalculatePartitionSize(PartitionInfo p)
    {
        var size = 10; // NumVertices, NumTriangles, NumBones, NumStrips, NumWeightsPerVertex
        size += p.NumBones * 2; // Bones array
        size += 1; // HasVertexMap
        if (p.HasVertexMap) size += p.NumVertices * 2; // VertexMap

        size += 1; // HasVertexWeights
        // Vertex weights are always included in expansion
        size += p.NumVertices * p.NumWeightsPerVertex * 4; // VertexWeights (floats)

        size += p.NumStrips * 2; // StripLengths
        size += 1; // HasFaces
        if (p.HasFaces) size += CalculateFacesSize(p);

        size += 1; // HasBoneIndices
        // Bone indices are always included in expansion
        size += p.NumVertices * p.NumWeightsPerVertex; // BoneIndices (bytes)

        return size;
    }

    /// <summary>
    ///     Calculates the size of faces data (strips or triangles).
    /// </summary>
    private static int CalculateFacesSize(PartitionInfo p)
    {
        if (p.NumStrips > 0)
        {
            var size = 0;
            foreach (var len in p.StripLengths) size += len * 2;

            return size;
        }

        return p.NumTriangles * 6; // Triangles
    }

    /// <summary>
    ///     Reader context for partition parsing with position tracking.
    /// </summary>
    private ref struct ExpanderReader
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

        public float ReadFloat()
        {
            if (IsBigEndian)
            {
                var span = Data.AsSpan(Pos, 4);
                Span<byte> swapped = stackalloc byte[4];
                swapped[0] = span[3];
                swapped[1] = span[2];
                swapped[2] = span[1];
                swapped[3] = span[0];
                Pos += 4;
                return BinaryPrimitives.ReadSingleLittleEndian(swapped);
            }

            var result = BinaryPrimitives.ReadSingleLittleEndian(Data.AsSpan(Pos, 4));
            Pos += 4;
            return result;
        }

        public byte ReadByte()
        {
            return Data[Pos++];
        }
    }

    /// <summary>
    ///     Information about a single partition within NiSkinPartition.
    /// </summary>
    public sealed class PartitionInfo
    {
        public ushort NumVertices { get; set; }
        public ushort NumTriangles { get; set; }
        public ushort NumBones { get; set; }
        public ushort NumStrips { get; set; }
        public ushort NumWeightsPerVertex { get; set; }
        public ushort[] Bones { get; set; } = [];
        public bool HasVertexMap { get; set; }
        public ushort[] VertexMap { get; set; } = [];
        public bool HasVertexWeights { get; set; }
        public float[,]? VertexWeights { get; set; } // [numVerts, numWeightsPerVertex]
        public bool HasFaces { get; set; }
        public ushort[] StripLengths { get; set; } = [];
        public ushort[][] Strips { get; set; } = []; // For strip data
        public ushort[,]? Triangles { get; set; } // For triangle data [numTris, 3]
        public bool HasBoneIndices { get; set; }
        public byte[,]? BoneIndices { get; set; } // [numVerts, numWeightsPerVertex]
    }

    /// <summary>
    ///     Parsed NiSkinPartition block data.
    /// </summary>
    public sealed class SkinPartitionData
    {
        public uint NumPartitions { get; set; }
        public List<PartitionInfo> Partitions { get; set; } = [];
        public int OriginalSize { get; set; }
    }
}
