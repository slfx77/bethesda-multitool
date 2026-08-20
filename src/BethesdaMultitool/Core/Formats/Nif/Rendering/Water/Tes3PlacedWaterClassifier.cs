using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     Fail-closed recognition for Morrowind's placed Vivec water material. TES3 has no
///     <c>WaterShaderProperty</c> marker: the retail surface is ordinary
///     <c>NiTexturingProperty</c> geometry, so no single legacy property type identifies it.
/// </summary>
internal static class Tes3PlacedWaterClassifier
{
    private const string VivecWaterTexture = @"textures\tx_v_water_01.tga";
    private const float ScalarTolerance = 0.0001f;
    private const float HorizontalZTolerance = 0.05f;
    private const float MinimumUpwardDot = 0.999f;
    private const float MinimumTriangleAreaSquared = 1e-12f;
    private const float MinimumUvSpeedSquared = 1e-8f;

    /// <summary>
    ///     Returns true only for the combined retail material, animation, and geometry signature.
    ///     Model, shape, and material names are deliberately absent from the decision.
    /// </summary>
    public static bool IsWaterSurface(uint binaryVersion, RenderableSubmesh submesh)
    {
        ArgumentNullException.ThrowIfNull(submesh);

        return binaryVersion == NifVersions.NetImmerse4002
               && HasExactTextureIdentity(submesh.DiffuseTexturePath)
               && submesh.HasAlphaBlend
               && !submesh.HasAlphaTest
               && submesh.SrcBlendMode == 6
               && submesh.DstBlendMode == 7
               && NearlyEquals(submesh.MaterialAlpha, 0.5f)
               && HasWhiteDiffuse(submesh.MaterialDiffuse)
               && HasActiveFiniteUvScroll(submesh.UvScrollVelocity)
               && HasHorizontalGeometryAndNormals(
                   submesh.Positions,
                   submesh.Triangles,
                   submesh.Normals);
    }

    private static bool HasExactTextureIdentity(string? texturePath)
    {
        if (string.IsNullOrWhiteSpace(texturePath))
        {
            return false;
        }

        return string.Equals(
            NifTexturePathUtility.Normalize(texturePath),
            VivecWaterTexture,
            StringComparison.Ordinal);
    }

    private static bool HasWhiteDiffuse((float R, float G, float B)? diffuse)
    {
        return diffuse is { } color
               && NearlyEquals(color.R, 1f)
               && NearlyEquals(color.G, 1f)
               && NearlyEquals(color.B, 1f);
    }

    private static bool HasActiveFiniteUvScroll(Vector2 velocity)
    {
        if (!float.IsFinite(velocity.X) || !float.IsFinite(velocity.Y))
        {
            return false;
        }

        var speedSquared = velocity.LengthSquared();
        return float.IsFinite(speedSquared) && speedSquared > MinimumUvSpeedSquared;
    }

    private static bool HasHorizontalGeometryAndNormals(
        float[] positions,
        ushort[] triangles,
        float[]? normals)
    {
        if (positions.Length < 9 || positions.Length % 3 != 0 ||
            triangles.Length < 3 || triangles.Length % 3 != 0 ||
            normals is null || normals.Length != positions.Length)
        {
            return false;
        }

        for (var i = 0; i < positions.Length; i += 3)
        {
            var position = new Vector3(positions[i], positions[i + 1], positions[i + 2]);
            var normal = new Vector3(normals[i], normals[i + 1], normals[i + 2]);
            if (!IsFinite(position) || !IsFinite(normal))
            {
                return false;
            }

            var normalLengthSquared = normal.LengthSquared();
            if (!float.IsFinite(normalLengthSquared) || normalLengthSquared <= MinimumTriangleAreaSquared)
            {
                return false;
            }

            if (normal.Z / MathF.Sqrt(normalLengthSquared) < MinimumUpwardDot)
            {
                return false;
            }
        }

        var vertexCount = positions.Length / 3;
        for (var i = 0; i < triangles.Length; i += 3)
        {
            var i0 = triangles[i];
            var i1 = triangles[i + 1];
            var i2 = triangles[i + 2];
            if (i0 >= vertexCount || i1 >= vertexCount || i2 >= vertexCount ||
                i0 == i1 || i0 == i2 || i1 == i2)
            {
                return false;
            }

            var p0 = PositionAt(positions, i0);
            var p1 = PositionAt(positions, i1);
            var p2 = PositionAt(positions, i2);
            var minZ = MathF.Min(p0.Z, MathF.Min(p1.Z, p2.Z));
            var maxZ = MathF.Max(p0.Z, MathF.Max(p1.Z, p2.Z));
            if (maxZ - minZ > HorizontalZTolerance)
            {
                return false;
            }

            var faceNormal = Vector3.Cross(p1 - p0, p2 - p0);
            var faceLengthSquared = faceNormal.LengthSquared();
            if (!IsFinite(faceNormal) || !float.IsFinite(faceLengthSquared) ||
                faceLengthSquared <= MinimumTriangleAreaSquared ||
                MathF.Abs(faceNormal.Z) / MathF.Sqrt(faceLengthSquared) < MinimumUpwardDot)
            {
                return false;
            }
        }

        return true;
    }

    private static Vector3 PositionAt(float[] positions, int index)
    {
        var offset = index * 3;
        return new Vector3(positions[offset], positions[offset + 1], positions[offset + 2]);
    }

    private static bool NearlyEquals(float value, float expected)
    {
        return float.IsFinite(value) && MathF.Abs(value - expected) <= ScalarTolerance;
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }
}
