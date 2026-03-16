using System.Numerics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;

internal static class NpcGlbTangentBuilder
{
    internal static Vector4[]? BuildTangents(RenderableSubmesh submesh)
    {
        ArgumentNullException.ThrowIfNull(submesh);

        if (submesh.VertexCount == 0 || submesh.Normals == null)
        {
            return null;
        }

        if (submesh.Tangents != null && submesh.Tangents.Length == submesh.Positions.Length)
        {
            return BuildFromProvidedTangents(submesh);
        }

        if (submesh.UVs == null || submesh.UVs.Length != submesh.VertexCount * 2)
        {
            return null;
        }

        var tan1 = new Vector3[submesh.VertexCount];
        var tan2 = new Vector3[submesh.VertexCount];

        for (var triangleIndex = 0; triangleIndex + 2 < submesh.Triangles.Length; triangleIndex += 3)
        {
            var i0 = submesh.Triangles[triangleIndex];
            var i1 = submesh.Triangles[triangleIndex + 1];
            var i2 = submesh.Triangles[triangleIndex + 2];

            var p0 = ReadPosition(submesh, i0);
            var p1 = ReadPosition(submesh, i1);
            var p2 = ReadPosition(submesh, i2);
            var uv0 = ReadUv(submesh, i0);
            var uv1 = ReadUv(submesh, i1);
            var uv2 = ReadUv(submesh, i2);

            var edge1 = p1 - p0;
            var edge2 = p2 - p0;
            var deltaUv1 = uv1 - uv0;
            var deltaUv2 = uv2 - uv0;
            var det = deltaUv1.X * deltaUv2.Y - deltaUv2.X * deltaUv1.Y;
            if (MathF.Abs(det) < 0.000001f)
            {
                continue;
            }

            var invDet = 1f / det;
            var sdir = (edge1 * deltaUv2.Y - edge2 * deltaUv1.Y) * invDet;
            var tdir = (edge2 * deltaUv1.X - edge1 * deltaUv2.X) * invDet;

            tan1[i0] += sdir;
            tan1[i1] += sdir;
            tan1[i2] += sdir;
            tan2[i0] += tdir;
            tan2[i1] += tdir;
            tan2[i2] += tdir;
        }

        var tangents = new Vector4[submesh.VertexCount];
        for (var vertexIndex = 0; vertexIndex < submesh.VertexCount; vertexIndex++)
        {
            var normal = ReadNormal(submesh, vertexIndex);
            var tangent = tan1[vertexIndex];
            if (tangent.LengthSquared() < 0.000001f)
            {
                tangents[vertexIndex] = BuildFallbackTangent(normal);
                continue;
            }

            tangent = Vector3.Normalize(tangent - normal * Vector3.Dot(normal, tangent));
            var handedness = Vector3.Dot(Vector3.Cross(normal, tangent), tan2[vertexIndex]) < 0f
                ? -1f
                : 1f;
            tangents[vertexIndex] = new Vector4(tangent, handedness);
        }

        return tangents;
    }

    private static Vector4[] BuildFromProvidedTangents(RenderableSubmesh submesh)
    {
        var tangents = new Vector4[submesh.VertexCount];
        for (var vertexIndex = 0; vertexIndex < submesh.VertexCount; vertexIndex++)
        {
            var tangentOffset = vertexIndex * 3;
            var tangent = new Vector3(
                submesh.Tangents![tangentOffset],
                submesh.Tangents[tangentOffset + 1],
                submesh.Tangents[tangentOffset + 2]);
            if (tangent.LengthSquared() < 0.000001f)
            {
                tangents[vertexIndex] = BuildFallbackTangent(ReadNormal(submesh, vertexIndex));
                continue;
            }

            tangent = Vector3.Normalize(tangent);
            var handedness = 1f;
            if (submesh.Bitangents != null && submesh.Bitangents.Length == submesh.Positions.Length)
            {
                var bitangent = new Vector3(
                    submesh.Bitangents[tangentOffset],
                    submesh.Bitangents[tangentOffset + 1],
                    submesh.Bitangents[tangentOffset + 2]);
                if (bitangent.LengthSquared() > 0.000001f)
                {
                    var normal = ReadNormal(submesh, vertexIndex);
                    handedness = Vector3.Dot(Vector3.Cross(normal, tangent), Vector3.Normalize(bitangent)) < 0f
                        ? -1f
                        : 1f;
                }
            }

            tangents[vertexIndex] = new Vector4(tangent, handedness);
        }

        return tangents;
    }

    private static Vector4 BuildFallbackTangent(Vector3 normal)
    {
        var axis = MathF.Abs(normal.Y) < 0.999f
            ? Vector3.UnitY
            : Vector3.UnitX;
        var tangent = Vector3.Normalize(Vector3.Cross(axis, normal));
        return new Vector4(tangent, 1f);
    }

    private static Vector3 ReadPosition(RenderableSubmesh submesh, int vertexIndex)
    {
        var offset = vertexIndex * 3;
        return new Vector3(
            submesh.Positions[offset],
            submesh.Positions[offset + 1],
            submesh.Positions[offset + 2]);
    }

    private static Vector3 ReadNormal(RenderableSubmesh submesh, int vertexIndex)
    {
        var offset = vertexIndex * 3;
        var normal = new Vector3(
            submesh.Normals![offset],
            submesh.Normals[offset + 1],
            submesh.Normals[offset + 2]);
        return normal.LengthSquared() > 0.000001f
            ? Vector3.Normalize(normal)
            : Vector3.UnitZ;
    }

    private static Vector2 ReadUv(RenderableSubmesh submesh, int vertexIndex)
    {
        var offset = vertexIndex * 2;
        return new Vector2(
            submesh.UVs![offset],
            submesh.UVs[offset + 1]);
    }
}
