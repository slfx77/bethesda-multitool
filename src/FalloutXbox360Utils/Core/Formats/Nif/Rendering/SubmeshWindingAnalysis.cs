namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

internal readonly record struct SubmeshWindingAnalysis(
    int TotalTriangles,
    int FlippedCount,
    int ZeroNormalCount,
    IReadOnlyList<int> SampleFlippedIndices);