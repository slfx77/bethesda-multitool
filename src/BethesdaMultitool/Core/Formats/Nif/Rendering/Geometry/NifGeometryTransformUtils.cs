using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;

/// <summary>
///     Applies object-space transforms to geometry streams.
/// </summary>
internal static class NifGeometryTransformUtils
{
    internal static float[] TransformPositions(float[] positions, Matrix4x4 transform)
    {
        if (transform == Matrix4x4.Identity)
        {
            return positions;
        }

        var result = new float[positions.Length];
        for (var i = 0; i < positions.Length; i += 3)
        {
            var v = Vector3.Transform(
                new Vector3(positions[i], positions[i + 1], positions[i + 2]),
                transform);
            result[i] = v.X;
            result[i + 1] = v.Y;
            result[i + 2] = v.Z;
        }

        return result;
    }

    internal static float[] TransformNormals(float[] normals, Matrix4x4 transform)
    {
        if (transform == Matrix4x4.Identity)
        {
            return normals;
        }

        var result = new float[normals.Length];
        for (var i = 0; i < normals.Length; i += 3)
        {
            var source = new Vector3(normals[i], normals[i + 1], normals[i + 2]);
            var normal = Vector3.TransformNormal(source, transform);
            var sourceLength = source.Length();
            var transformedLength = normal.Length();
            if (float.IsFinite(sourceLength) && float.IsFinite(transformedLength) &&
                transformedLength > 0.001f)
            {
                // NIF node transforms are rigid plus uniform scale. Bake only their direction into
                // N/T/B and retain the authored vector magnitude. The retail FO3/FNV basic-bump
                // vertex shaders perform raw dp3 against those streams, so normalizing a non-unit
                // tangent or bitangent here changes the recovered lighting equation. Unit-authored
                // normals retain the previous unit result.
                normal *= sourceLength / transformedLength;
            }

            result[i] = normal.X;
            result[i + 1] = normal.Y;
            result[i + 2] = normal.Z;
        }

        return result;
    }

    internal static float[] RecomputeSmoothNormals(float[] positions, ushort[] triangles)
    {
        var numVerts = positions.Length / 3;
        var normals = new float[positions.Length];

        for (var t = 0; t < triangles.Length; t += 3)
        {
            var i0 = triangles[t];
            var i1 = triangles[t + 1];
            var i2 = triangles[t + 2];
            if (i0 >= numVerts || i1 >= numVerts || i2 >= numVerts)
            {
                continue;
            }

            var v0 = ReadPosition(positions, i0);
            var v1 = ReadPosition(positions, i1);
            var v2 = ReadPosition(positions, i2);
            var faceNormal = Vector3.Cross(v1 - v0, v2 - v0);

            Accumulate(faceNormal, normals, i0);
            Accumulate(faceNormal, normals, i1);
            Accumulate(faceNormal, normals, i2);
        }

        for (var i = 0; i < normals.Length; i += 3)
        {
            var normal = new Vector3(normals[i], normals[i + 1], normals[i + 2]);
            var length = normal.Length();
            if (length > 0.001f)
            {
                normal /= length;
            }

            normals[i] = normal.X;
            normals[i + 1] = normal.Y;
            normals[i + 2] = normal.Z;
        }

        return normals;
    }

    private static void Accumulate(Vector3 faceNormal, float[] normals, int vertexIndex)
    {
        normals[vertexIndex * 3] += faceNormal.X;
        normals[vertexIndex * 3 + 1] += faceNormal.Y;
        normals[vertexIndex * 3 + 2] += faceNormal.Z;
    }

    private static Vector3 ReadPosition(float[] positions, int vertexIndex)
    {
        return new Vector3(
            positions[vertexIndex * 3],
            positions[vertexIndex * 3 + 1],
            positions[vertexIndex * 3 + 2]);
    }
}
