namespace BethesdaMultitool.Core.Formats.Nif.Geometry;

internal readonly record struct NifTriStripSectionInfo(
    int DeclaredTriangleCount,
    int StripCount,
    ushort[] StripLengths,
    int CandidateTriangleWindowCount,
    int DegenerateTriangleCount,
    int ExtractedTriangleCount);
