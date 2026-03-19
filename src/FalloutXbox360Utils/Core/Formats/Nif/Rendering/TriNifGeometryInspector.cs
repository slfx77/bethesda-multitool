using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Geometry;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

internal readonly record struct NifGeometryBlockSummary(
    int BlockIndex,
    string BlockType,
    int VertexCount,
    int TriangleCount,
    int DeclaredTriangleCount,
    int CandidateTriangleWindowCount,
    int DegenerateTriangleCount);

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
            TotalVertexCount = geometryBlocks.Where(static block => block.VertexCount >= 0).Sum(static block => block.VertexCount),
            TotalTriangleCount = geometryBlocks.Where(static block => block.TriangleCount >= 0).Sum(static block => block.TriangleCount),
            TotalDeclaredTriangleCount = geometryBlocks.Where(static block => block.DeclaredTriangleCount >= 0)
                .Sum(static block => block.DeclaredTriangleCount)
        };
    }
}

internal static class TriNifGeometryInspector
{
    public static TriNifGeometryInspection? Inspect(byte[] nifData, TriParser? tri = null)
    {
        var nif = NifParser.Parse(nifData);
        if (nif == null)
        {
            return null;
        }

        var geometryBlocks = new List<NifGeometryBlockSummary>();
        for (var blockIndex = 0; blockIndex < nif.Blocks.Count; blockIndex++)
        {
            var block = nif.Blocks[blockIndex];
            if (block.TypeName is not ("NiTriShapeData" or "NiTriStripsData"))
            {
                continue;
            }

            var vertexCount = NifBlockParsers.ReadVertexCount(nifData, block, nif.IsBigEndian);
            var triStripInfo = block.TypeName == "NiTriStripsData"
                ? NifTriStripExtractor.ReadStripSectionInfo(nifData, block, nif.IsBigEndian)
                : null;
            var submesh = block.TypeName == "NiTriShapeData"
                ? NifBlockParsers.ExtractTriShapeData(
                    nifData,
                    block,
                    nif.IsBigEndian,
                    nif.BsVersion,
                    Matrix4x4.Identity)
                : NifBlockParsers.ExtractTriStripsData(
                    nifData,
                    block,
                    nif.IsBigEndian,
                    nif.BsVersion,
                    Matrix4x4.Identity);
            var triangleCount = submesh?.TriangleCount ?? -1;
            var declaredTriangleCount = triStripInfo?.DeclaredTriangleCount ?? triangleCount;
            var candidateTriangleWindowCount = triStripInfo?.CandidateTriangleWindowCount ?? triangleCount;
            var degenerateTriangleCount = triStripInfo?.DegenerateTriangleCount ?? 0;

            geometryBlocks.Add(new NifGeometryBlockSummary(
                blockIndex,
                block.TypeName,
                vertexCount,
                triangleCount,
                declaredTriangleCount,
                candidateTriangleWindowCount,
                degenerateTriangleCount));
        }

        var exactMatchingGeometryBlockCount = tri == null
            ? 0
            : geometryBlocks.Count(block =>
                block.VertexCount == tri.VertexCount &&
                block.TriangleCount == tri.TriangleCount);
        var declaredTriangleMatchingGeometryBlockCount = tri == null
            ? 0
            : geometryBlocks.Count(block =>
                block.VertexCount == tri.VertexCount &&
                block.DeclaredTriangleCount == tri.TriangleCount);
        var vertexMatchingGeometryBlockCount = tri == null
            ? 0
            : geometryBlocks.Count(block => block.VertexCount == tri.VertexCount);

        return TriNifGeometryInspection.Create(
            [.. geometryBlocks],
            exactMatchingGeometryBlockCount,
            declaredTriangleMatchingGeometryBlockCount,
            vertexMatchingGeometryBlockCount);
    }
}
