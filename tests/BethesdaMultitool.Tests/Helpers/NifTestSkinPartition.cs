namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     One SkinPartition for <see cref="NifTestBlockLayouts.SkinPartition" /> (nif.xml:6681-6781) at 20.2.0.7 and BS 34
///     or below. A null array writes its Has flag as 0 and omits the array. Triangles and strips are in partition vertex
///     indices; strips win when both are set. Nothing here consults NifSchema.
/// </summary>
internal sealed class NifTestSkinPartition
{
    /// <summary>The partition's Num Vertices.</summary>
    public required ushort VertexCount { get; init; }

    /// <summary>The partition's Bones (joint-palette indices).</summary>
    public ushort[] Bones { get; init; } = [0];

    /// <summary>Num Weights Per Vertex.</summary>
    public ushort WeightsPerVertex { get; init; }

    /// <summary>The vertex map (partition vertex to shape vertex), or null for Has Vertex Map = 0.</summary>
    public ushort[]? VertexMap { get; init; }

    /// <summary>The weights, <see cref="WeightsPerVertex" /> per partition vertex, or null for Has Vertex Weights = 0.</summary>
    public float[][]? Weights { get; init; }

    /// <summary>The triangles, three partition vertex indices each (used when <see cref="Strips" /> is null).</summary>
    public ushort[]? Triangles { get; init; }

    /// <summary>The strips, each a run of partition vertex indices, or null for a triangle list.</summary>
    public ushort[][]? Strips { get; init; }

    /// <summary>The bone indices (into <see cref="Bones" />) per partition vertex, or null for Has Bone Indices = 0.</summary>
    public byte[][]? BoneIndices { get; init; }
}
