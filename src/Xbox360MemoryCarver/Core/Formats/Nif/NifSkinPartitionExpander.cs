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

        var pos = offset;
        var end = offset + size;

        // NumPartitions (uint)
        var numPartitions = isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
        pos += 4;

        if (numPartitions == 0 || numPartitions > 1000) return null;

        var result = new SkinPartitionData
        {
            NumPartitions = numPartitions,
            OriginalSize = size
        };

        Log.Debug($"      Parsing NiSkinPartition: {numPartitions} partitions");

        for (var p = 0; p < numPartitions && pos < end; p++)
        {
            var partition = new PartitionInfo();

            // NumVertices (ushort)
            if (pos + 2 > end) break;
            partition.NumVertices = ReadUInt16(data, pos, isBigEndian);
            pos += 2;

            // NumTriangles (ushort)
            if (pos + 2 > end) break;
            partition.NumTriangles = ReadUInt16(data, pos, isBigEndian);
            pos += 2;

            // NumBones (ushort)
            if (pos + 2 > end) break;
            partition.NumBones = ReadUInt16(data, pos, isBigEndian);
            pos += 2;

            // NumStrips (ushort)
            if (pos + 2 > end) break;
            partition.NumStrips = ReadUInt16(data, pos, isBigEndian);
            pos += 2;

            // NumWeightsPerVertex (ushort)
            if (pos + 2 > end) break;
            partition.NumWeightsPerVertex = ReadUInt16(data, pos, isBigEndian);
            pos += 2;

            Log.Debug(
                $"        Partition {p}: {partition.NumVertices} verts, {partition.NumTriangles} tris, " +
                $"{partition.NumBones} bones, {partition.NumStrips} strips, {partition.NumWeightsPerVertex} weights/vert");

            // Bones array (numBones * ushort)
            partition.Bones = new ushort[partition.NumBones];
            for (var i = 0; i < partition.NumBones && pos + 2 <= end; i++)
            {
                partition.Bones[i] = ReadUInt16(data, pos, isBigEndian);
                pos += 2;
            }

            // HasVertexMap (byte)
            if (pos + 1 > end) break;
            partition.HasVertexMap = data[pos++] != 0;

            // VertexMap (if HasVertexMap)
            if (partition.HasVertexMap)
            {
                partition.VertexMap = new ushort[partition.NumVertices];
                for (var i = 0; i < partition.NumVertices && pos + 2 <= end; i++)
                {
                    partition.VertexMap[i] = ReadUInt16(data, pos, isBigEndian);
                    pos += 2;
                }
            }

            // HasVertexWeights (byte)
            if (pos + 1 > end) break;
            partition.HasVertexWeights = data[pos++] != 0;

            // VertexWeights (if HasVertexWeights)
            if (partition.HasVertexWeights)
            {
                partition.VertexWeights = new float[partition.NumVertices, partition.NumWeightsPerVertex];
                for (var v = 0; v < partition.NumVertices && pos < end; v++)
                    for (var w = 0; w < partition.NumWeightsPerVertex && pos + 4 <= end; w++)
                    {
                        partition.VertexWeights[v, w] = ReadFloat(data, pos, isBigEndian);
                        pos += 4;
                    }
            }

            // StripLengths array (numStrips * ushort)
            partition.StripLengths = new ushort[partition.NumStrips];
            for (var i = 0; i < partition.NumStrips && pos + 2 <= end; i++)
            {
                partition.StripLengths[i] = ReadUInt16(data, pos, isBigEndian);
                pos += 2;
            }

            // HasFaces (byte)
            if (pos + 1 > end) break;
            partition.HasFaces = data[pos++] != 0;

            // Strips or Triangles (if HasFaces)
            if (partition.HasFaces)
            {
                if (partition.NumStrips > 0)
                {
                    // Strip data
                    partition.Strips = new ushort[partition.NumStrips][];
                    for (var s = 0; s < partition.NumStrips && pos < end; s++)
                    {
                        partition.Strips[s] = new ushort[partition.StripLengths[s]];
                        for (var i = 0; i < partition.StripLengths[s] && pos + 2 <= end; i++)
                        {
                            partition.Strips[s][i] = ReadUInt16(data, pos, isBigEndian);
                            pos += 2;
                        }
                    }
                }
                else
                {
                    // Triangle data
                    partition.Triangles = new ushort[partition.NumTriangles, 3];
                    for (var t = 0; t < partition.NumTriangles && pos + 6 <= end; t++)
                    {
                        partition.Triangles[t, 0] = ReadUInt16(data, pos, isBigEndian);
                        partition.Triangles[t, 1] = ReadUInt16(data, pos + 2, isBigEndian);
                        partition.Triangles[t, 2] = ReadUInt16(data, pos + 4, isBigEndian);
                        pos += 6;
                    }
                }
            }

            // HasBoneIndices (byte)
            if (pos + 1 > end) break;
            partition.HasBoneIndices = data[pos++] != 0;

            // BoneIndices (if HasBoneIndices)
            if (partition.HasBoneIndices)
            {
                partition.BoneIndices = new byte[partition.NumVertices, partition.NumWeightsPerVertex];
                for (var v = 0; v < partition.NumVertices && pos < end; v++)
                    for (var w = 0; w < partition.NumWeightsPerVertex && pos + 1 <= end; w++)
                        partition.BoneIndices[v, w] = data[pos++];
            }

            result.Partitions.Add(partition);
        }

        return result;
    }

    /// <summary>
    ///     Calculates the expanded size of a NiSkinPartition block when bone weights/indices are added.
    /// </summary>
    public static int CalculateExpandedSize(SkinPartitionData skinPartition)
    {
        var size = 4; // NumPartitions

        foreach (var p in skinPartition.Partitions)
        {
            size += 10; // NumVertices, NumTriangles, NumBones, NumStrips, NumWeightsPerVertex
            size += p.NumBones * 2; // Bones array
            size += 1; // HasVertexMap
            if (p.HasVertexMap) size += p.NumVertices * 2; // VertexMap

            size += 1; // HasVertexWeights
            // Vertex weights are always included in expansion
            size += p.NumVertices * p.NumWeightsPerVertex * 4; // VertexWeights (floats)

            size += p.NumStrips * 2; // StripLengths
            size += 1; // HasFaces
            if (p.HasFaces)
            {
                if (p.NumStrips > 0)
                    foreach (var len in p.StripLengths)
                        size += len * 2;
                else
                    size += p.NumTriangles * 6; // Triangles
            }

            size += 1; // HasBoneIndices
            // Bone indices are always included in expansion
            size += p.NumVertices * p.NumWeightsPerVertex; // BoneIndices (bytes)
        }

        return size;
    }

    private static ushort ReadUInt16(byte[] data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    }

    private static float ReadFloat(byte[] data, int offset, bool bigEndian)
    {
        if (bigEndian)
        {
            var span = data.AsSpan(offset, 4);
            Span<byte> swapped = stackalloc byte[4];
            swapped[0] = span[3];
            swapped[1] = span[2];
            swapped[2] = span[1];
            swapped[3] = span[0];
            return BinaryPrimitives.ReadSingleLittleEndian(swapped);
        }

        return BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset, 4));
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

