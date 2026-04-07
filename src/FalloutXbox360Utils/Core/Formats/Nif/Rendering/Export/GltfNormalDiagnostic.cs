using System.Numerics;
using FalloutXbox360Utils.CLI;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Diagnostic tool that checks normal/winding consistency on an export scene.
///     For each submesh, computes the geometric face normal (cross product of triangle edges)
///     and compares against the stored vertex normals. Reports mismatches that would cause
///     faces to appear dark (normals pointing inward) in glTF viewers.
/// </summary>
internal static class GltfNormalDiagnostic
{
    private static readonly Logger Log = Logger.Instance;

    internal static void Run(NpcExportScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        var totalFlipped = 0;
        var totalTriangles = 0;

        foreach (var meshPart in scene.MeshParts)
        {
            var submesh = meshPart.Submesh;
            if (submesh.TriangleCount == 0 || submesh.VertexCount == 0 || submesh.Normals == null)
            {
                continue;
            }

            ReportSubmeshState(meshPart.Name ?? "(unnamed)", submesh);
            var result = MeshWindingDiagnostic.Analyze(submesh);
            totalFlipped += result.FlippedCount;
            totalTriangles += result.TotalTriangles;

            if (result.FlippedCount > 0 || result.ZeroNormalCount > 0)
            {
                Log.Info("[NormalDiag] {0}: {1}/{2} triangles flipped ({3:F1}%), {4} zero-length normals",
                    meshPart.Name ?? "(unnamed)",
                    result.FlippedCount,
                    result.TotalTriangles,
                    result.TotalTriangles > 0 ? 100.0 * result.FlippedCount / result.TotalTriangles : 0,
                    result.ZeroNormalCount);

                if (result.SampleFlippedIndices.Count > 0)
                {
                    Log.Info("  Sample flipped tri indices: [{0}]",
                        string.Join(", ", result.SampleFlippedIndices.Take(10)));
                }

                if (result.FlippedCount == result.TotalTriangles)
                {
                    Log.Info("  -> ALL triangles flipped — likely negative-determinant transform");
                }
                else if (result.FlippedCount > result.TotalTriangles / 2)
                {
                    Log.Info("  -> MAJORITY flipped — possible transform or winding issue");
                }
                else
                {
                    Log.Info("  -> PARTIAL flip — inconsistent winding in source NIF");
                }
            }
            else
            {
                Log.Info("[NormalDiag] {0}: OK ({1} triangles, all consistent)",
                    meshPart.Name ?? "(unnamed)",
                    result.TotalTriangles);
            }
        }

        Log.Info("[NormalDiag] Summary: {0}/{1} total triangles flipped across {2} mesh parts",
            totalFlipped, totalTriangles, scene.MeshParts.Count);
    }

    /// <summary>
    ///     Runs the same analysis but on positions/normals that have already been converted
    ///     to glTF coordinate space (Y-up). Call this to check whether the coordinate
    ///     conversion itself introduces flips.
    /// </summary>
    internal static void RunWithCoordinateConversion(NpcExportScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);

        Log.Info("[NormalDiag] === Post-coordinate-conversion analysis ===");

        foreach (var meshPart in scene.MeshParts)
        {
            var submesh = meshPart.Submesh;
            if (submesh.TriangleCount == 0 || submesh.VertexCount == 0 || submesh.Normals == null)
            {
                continue;
            }

            var result = AnalyzeSubmeshConverted(submesh);

            if (result.FlippedCount > 0)
            {
                Log.Info("[NormalDiag-Converted] {0}: {1}/{2} triangles flipped ({3:F1}%)",
                    meshPart.Name ?? "(unnamed)",
                    result.FlippedCount,
                    result.TotalTriangles,
                    100.0 * result.FlippedCount / result.TotalTriangles);
            }
            else
            {
                Log.Info("[NormalDiag-Converted] {0}: OK ({1} triangles)",
                    meshPart.Name ?? "(unnamed)",
                    result.TotalTriangles);
            }
        }
    }

    private static void ReportSubmeshState(string name, RenderableSubmesh submesh)
    {
        var hasVcol = submesh.VertexColors != null;
        var useVcol = submesh.UseVertexColors;
        var isEmissive = submesh.IsEmissive;
        var isDoubleSided = submesh.IsDoubleSided;
        var hasAlphaBlend = submesh.HasAlphaBlend;
        var hasAlphaTest = submesh.HasAlphaTest;
        var hasTint = submesh.TintColor.HasValue;
        var hasNormalMap = !string.IsNullOrEmpty(submesh.NormalMapTexturePath);
        var matAlpha = submesh.MaterialAlpha;

        Log.Info(
            "[NormalDiag] {0}: vCol={1} useVCol={2} emissive={3} doubleSide={4} aBlend={5} aTest={6} tint={7} normalMap={8} matAlpha={9:F2}",
            name, hasVcol, useVcol, isEmissive, isDoubleSided, hasAlphaBlend, hasAlphaTest, hasTint, hasNormalMap, matAlpha);

        if (hasVcol && submesh.VertexColors != null)
        {
            var pixels = submesh.VertexColors;
            int minR = 255, minG = 255, minB = 255, minA = 255;
            int maxR = 0, maxG = 0, maxB = 0, maxA = 0;
            long sumR = 0, sumG = 0, sumB = 0, sumA = 0;
            var count = pixels.Length / 4;

            for (var i = 0; i < pixels.Length; i += 4)
            {
                if (pixels[i] < minR) minR = pixels[i];
                if (pixels[i] > maxR) maxR = pixels[i];
                if (pixels[i + 1] < minG) minG = pixels[i + 1];
                if (pixels[i + 1] > maxG) maxG = pixels[i + 1];
                if (pixels[i + 2] < minB) minB = pixels[i + 2];
                if (pixels[i + 2] > maxB) maxB = pixels[i + 2];
                if (pixels[i + 3] < minA) minA = pixels[i + 3];
                if (pixels[i + 3] > maxA) maxA = pixels[i + 3];
                sumR += pixels[i];
                sumG += pixels[i + 1];
                sumB += pixels[i + 2];
                sumA += pixels[i + 3];
            }

            Log.Info(
                "  vColor stats: R[{0}-{1} avg{2}] G[{3}-{4} avg{5}] B[{6}-{7} avg{8}] A[{9}-{10} avg{11}]",
                minR, maxR, sumR / count,
                minG, maxG, sumG / count,
                minB, maxB, sumB / count,
                minA, maxA, sumA / count);
        }
    }

    private static SubmeshAnalysis AnalyzeSubmeshConverted(RenderableSubmesh submesh)
    {
        var positions = submesh.Positions;
        var normals = submesh.Normals!;
        var triangles = submesh.Triangles;

        var flippedCount = 0;
        var sampleFlipped = new List<int>();
        var triCount = triangles.Length / 3;

        for (var t = 0; t < triangles.Length; t += 3)
        {
            var i0 = triangles[t];
            var i1 = triangles[t + 1];
            var i2 = triangles[t + 2];

            // Convert positions and normals to Y-up (glTF space)
            var p0 = GltfCoordinateAdapter.ConvertPosition(ReadVec3(positions, i0));
            var p1 = GltfCoordinateAdapter.ConvertPosition(ReadVec3(positions, i1));
            var p2 = GltfCoordinateAdapter.ConvertPosition(ReadVec3(positions, i2));

            var edge1 = p1 - p0;
            var edge2 = p2 - p0;
            var faceNormal = Vector3.Cross(edge1, edge2);

            if (faceNormal.LengthSquared() < 1e-12f)
            {
                continue;
            }

            faceNormal = Vector3.Normalize(faceNormal);

            var vn0 = GltfCoordinateAdapter.ConvertDirection(ReadVec3(normals, i0));
            var vn1 = GltfCoordinateAdapter.ConvertDirection(ReadVec3(normals, i1));
            var vn2 = GltfCoordinateAdapter.ConvertDirection(ReadVec3(normals, i2));
            var avgNormal = vn0 + vn1 + vn2;

            if (avgNormal.LengthSquared() < 1e-12f)
            {
                continue;
            }

            avgNormal = Vector3.Normalize(avgNormal);
            var dot = Vector3.Dot(faceNormal, avgNormal);

            if (dot < 0f)
            {
                flippedCount++;
                if (sampleFlipped.Count < 20)
                {
                    sampleFlipped.Add(t / 3);
                }
            }
        }

        return new SubmeshAnalysis(triCount, flippedCount, 0, sampleFlipped);
    }

    /// <summary>
    ///     Fixes triangle winding order so that the geometric face normal (from cross product)
    ///     agrees with the stored vertex normals. Swaps indices 1 and 2 for any triangle where
    ///     the cross product disagrees with the average vertex normal.
    /// </summary>
    internal static int FixWindingOrder(RenderableSubmesh submesh)
    {
        return MeshWindingDiagnostic.FixWindingOrder(submesh);
    }

    private static Vector3 ReadVec3(float[] array, int vertexIndex)
    {
        var offset = vertexIndex * 3;
        return new Vector3(array[offset], array[offset + 1], array[offset + 2]);
    }

    private readonly record struct SubmeshAnalysis(
        int TotalTriangles,
        int FlippedCount,
        int ZeroNormalCount,
        List<int> SampleFlippedIndices);
}
