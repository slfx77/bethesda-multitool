namespace BethesdaMultitool.Core.Formats.Nif.GeometryAnalysis;

/// <summary>
///     Strip-topology metadata read from a NiTriStripsData block: declared vs. extracted triangle counts,
///     strip lengths, and how many candidate windows were degenerate.
/// </summary>
internal readonly record struct NifTriStripSectionInfo(
    int DeclaredTriangleCount,
    int StripCount,
    ushort[] StripLengths,
    int CandidateTriangleWindowCount,
    int DegenerateTriangleCount,
    int ExtractedTriangleCount);
