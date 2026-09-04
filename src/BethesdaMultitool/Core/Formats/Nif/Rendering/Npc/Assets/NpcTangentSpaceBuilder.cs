using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;

/// <summary>Rebuilds a finite orthonormal tangent frame from final NPC geometry.</summary>
internal static class NpcTangentSpaceBuilder
{
    private const float VectorLengthEpsilon = 0.000001f;

    internal static bool TryRebuild(RenderableSubmesh submesh)
    {
        ArgumentNullException.ThrowIfNull(submesh);

        var positions = submesh.Positions;
        var normals = submesh.Normals;
        var uvs = submesh.UVs;
        var vertexCount = submesh.VertexCount;
        if (vertexCount == 0 ||
            positions.Length != vertexCount * 3 ||
            normals == null ||
            normals.Length != positions.Length ||
            uvs == null ||
            uvs.Length != vertexCount * 2 ||
            submesh.Triangles.Length % 3 != 0 ||
            !HasFiniteGeometry(positions, normals, uvs, vertexCount) ||
            HasOutOfRangeIndex(submesh.Triangles, vertexCount))
        {
            Clear(submesh);
            return false;
        }

        // FaceGen EGM changes positions, and normal welding changes the normal basis. Reusing an
        // authored tangent stream after either operation would make it stale.
        Clear(submesh);
        var gltfTangents = NpcGlbTangentBuilder.BuildTangents(submesh);
        if (gltfTangents == null || gltfTangents.Length != vertexCount)
        {
            return false;
        }

        var tangents = new float[positions.Length];
        var bitangents = new float[positions.Length];
        for (var vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            var offset = vertexIndex * 3;
            var normal = new Vector3(normals[offset], normals[offset + 1], normals[offset + 2]);
            normal = Vector3.Normalize(normal);

            var gltfTangent = gltfTangents[vertexIndex];
            var tangent = new Vector3(gltfTangent.X, gltfTangent.Y, gltfTangent.Z);
            tangent -= normal * Vector3.Dot(normal, tangent);
            if (!IsUsable(tangent) ||
                !float.IsFinite(gltfTangent.W) ||
                MathF.Abs(gltfTangent.W) <= VectorLengthEpsilon)
            {
                Clear(submesh);
                return false;
            }

            tangent = Vector3.Normalize(tangent);
            var bitangent = Vector3.Cross(normal, tangent) * gltfTangent.W;
            if (!IsUsable(bitangent))
            {
                Clear(submesh);
                return false;
            }

            bitangent = Vector3.Normalize(bitangent);
            tangents[offset] = tangent.X;
            tangents[offset + 1] = tangent.Y;
            tangents[offset + 2] = tangent.Z;
            bitangents[offset] = bitangent.X;
            bitangents[offset + 1] = bitangent.Y;
            bitangents[offset + 2] = bitangent.Z;
        }

        submesh.Tangents = tangents;
        submesh.Bitangents = bitangents;
        return true;
    }

    private static bool HasFiniteGeometry(
        float[] positions,
        float[] normals,
        float[] uvs,
        int vertexCount)
    {
        if (!positions.All(float.IsFinite) || !uvs.All(float.IsFinite))
        {
            return false;
        }

        for (var vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            var offset = vertexIndex * 3;
            var normal = new Vector3(normals[offset], normals[offset + 1], normals[offset + 2]);
            if (!IsUsable(normal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasOutOfRangeIndex(ushort[] triangles, int vertexCount)
    {
        foreach (var index in triangles)
        {
            if (index >= vertexCount)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUsable(Vector3 vector)
    {
        var lengthSquared = vector.LengthSquared();
        return float.IsFinite(vector.X) &&
               float.IsFinite(vector.Y) &&
               float.IsFinite(vector.Z) &&
               float.IsFinite(lengthSquared) &&
               lengthSquared > VectorLengthEpsilon;
    }

    private static void Clear(RenderableSubmesh submesh)
    {
        submesh.Tangents = null;
        submesh.Bitangents = null;
    }
}
