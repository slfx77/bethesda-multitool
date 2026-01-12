namespace NifAnalyzer.Models;

/// <summary>
///     Contains parsed NiSkinPartition data.
/// </summary>
internal class SkinPartitionInfo
{
    public uint NumPartitions { get; set; }
    public SkinPartitionData[] Partitions { get; set; } = [];
}

/// <summary>
///     Data for a single skin partition.
/// </summary>
internal class SkinPartitionData
{
    public ushort NumVertices { get; set; }
    public ushort NumTriangles { get; set; }
    public ushort NumBones { get; set; }
    public ushort NumStrips { get; set; }
    public ushort NumWeightsPerVertex { get; set; }
    public ushort[] Bones { get; set; } = [];
    public byte HasVertexMap { get; set; }
    public ushort[] VertexMap { get; set; } = [];
    public byte HasVertexWeights { get; set; }
    public float[][] VertexWeights { get; set; } = []; // NumVertices x NumWeightsPerVertex
    public ushort[] StripLengths { get; set; } = [];
    public byte HasFaces { get; set; }
    public ushort[][] Strips { get; set; } = [];
    public Triangle[] Triangles { get; set; } = [];
    public byte HasBoneIndices { get; set; }
    public byte[][] BoneIndices { get; set; } = []; // NumVertices x NumWeightsPerVertex
}

/// <summary>
///     Triangle face with three vertex indices.
/// </summary>
internal record struct Triangle(ushort V1, ushort V2, ushort V3);