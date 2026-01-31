namespace FalloutXbox360Utils.Core.Formats.Nif.Geometry;

/// <summary>
///     Geometry data extracted from BSPackedAdditionalGeometryData.
/// </summary>
internal sealed class PackedGeometryData
{
    public ushort NumVertices { get; set; }
    public float[]? Positions { get; set; }
    public float[]? Normals { get; set; }
    public float[]? Tangents { get; set; }
    public float[]? Bitangents { get; set; }
    public float[]? UVs { get; set; }

    /// <summary>Vertex colors as RGBA bytes (4 bytes per vertex).</summary>
    public byte[]? VertexColors { get; set; }

    /// <summary>
    ///     Bone indices for skinned meshes (4 bytes per vertex).
    ///     Each vertex references up to 4 bones by index.
    ///     These are partition-local indices, not global skeleton indices.
    /// </summary>
    public byte[]? BoneIndices { get; set; }

    /// <summary>
    ///     Bone weights for skinned meshes (4 floats per vertex).
    ///     Each weight corresponds to the bone at the same index in BoneIndices.
    ///     Weights should sum to 1.0 for each vertex.
    /// </summary>
    public float[]? BoneWeights { get; set; }

    public ushort BsDataFlags { get; set; }
}
