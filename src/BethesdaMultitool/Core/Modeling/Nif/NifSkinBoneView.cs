namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     One NiSkinData BoneData entry (nif.xml BoneData): its Skin Transform (the bone's inverse bind as authored), the
///     stored Num Vertices, and the Vertex Weights when the skin data stores them (Has Vertex Weights not 0), in stored
///     order with duplicates kept.
/// </summary>
internal sealed class NifSkinBoneView
{
    /// <summary>Creates the view of one bone.</summary>
    /// <param name="transform">The bone's Skin Transform.</param>
    /// <param name="storedVertexCount">The stored Num Vertices.</param>
    /// <param name="weights">The stored (vertex index, weight) pairs, empty when the skin data stores no weights.</param>
    public NifSkinBoneView(NifSkinTransformView transform, int storedVertexCount,
        IReadOnlyList<(int Vertex, float Weight)> weights)
    {
        Transform = transform;
        StoredVertexCount = storedVertexCount;
        Weights = weights;
    }

    /// <summary>The bone's Skin Transform (skin space to bone space), exactly as stored.</summary>
    public NifSkinTransformView Transform { get; }

    /// <summary>The stored Num Vertices (present even when the weights themselves are not stored).</summary>
    public int StoredVertexCount { get; }

    /// <summary>The stored (vertex index, weight) pairs in file order, duplicates and non-unit sums included.</summary>
    public IReadOnlyList<(int Vertex, float Weight)> Weights { get; }
}
