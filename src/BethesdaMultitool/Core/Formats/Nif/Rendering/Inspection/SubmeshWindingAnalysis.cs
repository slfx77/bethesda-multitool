namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;

/// <summary>
///     Diagnostics on a submesh's triangle winding: how many faces are back-facing (flipped) or have zero-length
///     normals.
/// </summary>
internal readonly record struct SubmeshWindingAnalysis(
    int TotalTriangles,
    int FlippedCount,
    int ZeroNormalCount,
    IReadOnlyList<int> SampleFlippedIndices);
