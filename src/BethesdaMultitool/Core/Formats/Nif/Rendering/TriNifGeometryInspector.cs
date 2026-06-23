using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.GeometryAnalysis;
using BethesdaMultitool.Core.Formats.Nif.Parser;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     Cross-checks a head NIF's geometry against an optional FaceGen TRI file, surfacing vertex-count and
///     layout mismatches for reverse-engineering diagnostics.
/// </summary>
internal static class TriNifGeometryInspector
{
    /// <summary>Inspects the NIF (and optional TRI) and returns a geometry report, or <c>null</c> when the NIF can't be parsed.</summary>
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

            var vertexCount = NifBlockParsers.ReadVertexCount(nifData, block, nif.IsBigEndian, nif.IsMorrowind);
            var triStripInfo = block.TypeName == "NiTriStripsData"
                ? NifTriStripExtractor.ReadStripSectionInfo(nifData, block, nif.IsBigEndian, nif.BinaryVersion)
                : null;
            var submesh = block.TypeName == "NiTriShapeData"
                ? NifBlockParsers.ExtractTriShapeData(
                    nifData,
                    block,
                    nif.IsBigEndian,
                    nif.BsVersion,
                    nif.BinaryVersion,
                    Matrix4x4.Identity)
                : NifBlockParsers.ExtractTriStripsData(
                    nifData,
                    block,
                    nif.IsBigEndian,
                    nif.BsVersion,
                    nif.BinaryVersion,
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
