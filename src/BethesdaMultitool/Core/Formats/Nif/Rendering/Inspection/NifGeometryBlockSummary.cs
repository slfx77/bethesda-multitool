namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;

/// <summary>Per-block geometry diagnostics: vertex count plus declared vs. extracted vs. degenerate triangle counts.</summary>
internal readonly record struct NifGeometryBlockSummary(
    int BlockIndex,
    string BlockType,
    int VertexCount,
    int TriangleCount,
    int DeclaredTriangleCount,
    int CandidateTriangleWindowCount,
    int DegenerateTriangleCount);
