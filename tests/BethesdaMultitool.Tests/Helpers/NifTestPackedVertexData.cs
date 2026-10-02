namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     The per-vertex values a hand-laid BSPackedAdditionalGeometryData carries, as the floats a PC file would store;
///     <see cref="NifTestPackedLayouts" /> quantizes them the way the console exporter did (halves rounded to nearest
///     even, colors as bytes). Arrays are flat and element-major; every array present holds one element per vertex.
///     Nothing here consults NifSchema or the production layout table.
/// </summary>
internal sealed class NifTestPackedVertexData
{
    /// <summary>Positions, three per vertex.</summary>
    public required float[] Positions { get; init; }

    /// <summary>Normals, three per vertex.</summary>
    public required float[] Normals { get; init; }

    /// <summary>UV set 0, two per vertex.</summary>
    public required float[] Uvs { get; init; }

    /// <summary>
    ///     Bitangents (the PC Bitangents array), three per vertex, written to the lower-offset frame stream; in retail
    ///     files the one that runs along +dP/du, which the reader types as the glTF tangent.
    /// </summary>
    public required float[] Bitangents { get; init; }

    /// <summary>
    ///     Tangents (the PC Tangents array), three per vertex, written to the higher-offset frame stream; in retail files
    ///     the one that runs along +dP/dv.
    /// </summary>
    public required float[] Tangents { get; init; }

    /// <summary>Vertex colors as R, G, B, A bytes, four per vertex; required by the color layouts (L1, L4, L6).</summary>
    public byte[]? Colors { get; init; }

    /// <summary>Bone weights for partition slots 0..3, four per vertex, written raw; required by L3 and L4.</summary>
    public float[]? Weights { get; init; }

    /// <summary>Bone indices for partition slots 0..3, four per vertex in slot order; required by L3 and L4.</summary>
    public byte[]? BoneIndices { get; init; }

    /// <summary>The fourth half written after every position, normal, bitangent and tangent triple (1.0 by default).</summary>
    public ushort FourthHalfBits { get; init; } = 0x3C00;

    /// <summary>The vertex count.</summary>
    public int VertexCount => Positions.Length / 3;
}
