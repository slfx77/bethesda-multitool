using System.Numerics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

internal static class MeshWindingDiagnostic
{
    internal static SubmeshWindingAnalysis Analyze(RenderableSubmesh submesh)
    {
        ArgumentNullException.ThrowIfNull(submesh);

        if (submesh.Normals == null || submesh.TriangleCount == 0 || submesh.VertexCount == 0)
        {
            return new SubmeshWindingAnalysis(0, 0, 0, []);
        }

        var positions = submesh.Positions;
        var normals = submesh.Normals;
        var triangles = submesh.Triangles;
        var flippedCount = 0;
        var zeroNormalCount = 0;
        var sampleFlipped = new List<int>();

        for (var vertex = 0; vertex < normals.Length; vertex += 3)
        {
            var len2 = normals[vertex] * normals[vertex] +
                       normals[vertex + 1] * normals[vertex + 1] +
                       normals[vertex + 2] * normals[vertex + 2];
            if (len2 < 0.0001f)
            {
                zeroNormalCount++;
            }
        }

        var triangleCount = triangles.Length / 3;
        for (var triangle = 0; triangle < triangles.Length; triangle += 3)
        {
            var i0 = triangles[triangle];
            var i1 = triangles[triangle + 1];
            var i2 = triangles[triangle + 2];

            var faceNormal = Vector3.Cross(
                ReadVec3(positions, i1) - ReadVec3(positions, i0),
                ReadVec3(positions, i2) - ReadVec3(positions, i0));

            if (faceNormal.LengthSquared() < 1e-12f)
            {
                continue;
            }

            var avgNormal = ReadVec3(normals, i0) + ReadVec3(normals, i1) + ReadVec3(normals, i2);
            if (avgNormal.LengthSquared() < 1e-12f)
            {
                continue;
            }

            if (Vector3.Dot(Vector3.Normalize(faceNormal), Vector3.Normalize(avgNormal)) < 0f)
            {
                flippedCount++;
                if (sampleFlipped.Count < 20)
                {
                    sampleFlipped.Add(triangle / 3);
                }
            }
        }

        return new SubmeshWindingAnalysis(triangleCount, flippedCount, zeroNormalCount, sampleFlipped);
    }

    internal static int FixWindingOrder(RenderableSubmesh submesh)
    {
        ArgumentNullException.ThrowIfNull(submesh);

        if (submesh.Normals == null)
        {
            return 0;
        }

        var positions = submesh.Positions;
        var normals = submesh.Normals;
        var triangles = submesh.Triangles;
        var fixedCount = 0;

        for (var triangle = 0; triangle < triangles.Length; triangle += 3)
        {
            var i0 = triangles[triangle];
            var i1 = triangles[triangle + 1];
            var i2 = triangles[triangle + 2];

            var faceNormal = Vector3.Cross(
                ReadVec3(positions, i1) - ReadVec3(positions, i0),
                ReadVec3(positions, i2) - ReadVec3(positions, i0));

            if (faceNormal.LengthSquared() < 1e-12f)
            {
                continue;
            }

            var avgNormal = ReadVec3(normals, i0) + ReadVec3(normals, i1) + ReadVec3(normals, i2);
            if (avgNormal.LengthSquared() < 1e-12f)
            {
                continue;
            }

            if (Vector3.Dot(faceNormal, avgNormal) < 0f)
            {
                (triangles[triangle + 1], triangles[triangle + 2]) = (triangles[triangle + 2], triangles[triangle + 1]);
                fixedCount++;
            }
        }

        return fixedCount;
    }

    private static Vector3 ReadVec3(float[] array, int vertexIndex)
    {
        var offset = vertexIndex * 3;
        return new Vector3(array[offset], array[offset + 1], array[offset + 2]);
    }
}

internal readonly record struct SubmeshWindingAnalysis(
    int TotalTriangles,
    int FlippedCount,
    int ZeroNormalCount,
    IReadOnlyList<int> SampleFlippedIndices);