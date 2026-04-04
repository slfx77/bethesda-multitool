namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

internal sealed class TriNifGeometryInspection
{
    private NifGeometryBlockSummary[] _geometryBlocks = [];

    public IReadOnlyList<NifGeometryBlockSummary> GeometryBlocks => _geometryBlocks;
    public int GeometryBlockCount => _geometryBlocks.Length;
    public int ExactMatchingGeometryBlockCount { get; private init; }
    public int DeclaredTriangleMatchingGeometryBlockCount { get; private init; }
    public int VertexMatchingGeometryBlockCount { get; private init; }
    public bool HasExactGeometryMatch => ExactMatchingGeometryBlockCount > 0;
    public bool HasDeclaredTriangleCountMatch => DeclaredTriangleMatchingGeometryBlockCount > 0;
    public bool HasVertexCountMatch => VertexMatchingGeometryBlockCount > 0;
    public int TotalVertexCount { get; private init; }
    public int TotalTriangleCount { get; private init; }
    public int TotalDeclaredTriangleCount { get; private init; }

    internal static TriNifGeometryInspection Create(
        NifGeometryBlockSummary[] geometryBlocks,
        int exactMatchingGeometryBlockCount,
        int declaredTriangleMatchingGeometryBlockCount,
        int vertexMatchingGeometryBlockCount)
    {
        return new TriNifGeometryInspection
        {
            _geometryBlocks = geometryBlocks,
            ExactMatchingGeometryBlockCount = exactMatchingGeometryBlockCount,
            DeclaredTriangleMatchingGeometryBlockCount = declaredTriangleMatchingGeometryBlockCount,
            VertexMatchingGeometryBlockCount = vertexMatchingGeometryBlockCount,
            TotalVertexCount = geometryBlocks.Where(static block => block.VertexCount >= 0)
                .Sum(static block => block.VertexCount),
            TotalTriangleCount = geometryBlocks.Where(static block => block.TriangleCount >= 0)
                .Sum(static block => block.TriangleCount),
            TotalDeclaredTriangleCount = geometryBlocks.Where(static block => block.DeclaredTriangleCount >= 0)
                .Sum(static block => block.DeclaredTriangleCount)
        };
    }
}
