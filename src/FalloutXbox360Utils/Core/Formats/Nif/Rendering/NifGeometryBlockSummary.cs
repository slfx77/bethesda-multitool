namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

internal readonly record struct NifGeometryBlockSummary(
    int BlockIndex,
    string BlockType,
    int VertexCount,
    int TriangleCount,
    int DeclaredTriangleCount,
    int CandidateTriangleWindowCount,
    int DegenerateTriangleCount);