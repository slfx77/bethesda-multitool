using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;

/// <summary>
///     Selects the simple FO3/FNV PP-lighting parallax permutation. Bit 28 is the separate
///     parallax-occlusion path and is deliberately excluded until its multi-sample shader contract
///     is implemented.
/// </summary>
internal static class NifClassicParallaxPolicy
{
    internal const uint ParallaxFlag = 1u << 11;
    internal const uint ParallaxOcclusionFlag = 1u << 28;
    internal const float HeightScale = 0.04f;
    internal const float HeightBias = -0.02f;

    private const float BasisLengthSquaredEpsilon = 1e-8f;

    internal static NifClassicParallaxMaterial? Resolve(NifShaderTextureMetadata? metadata)
    {
        if (metadata is not
            {
                PropertyType: "BSShaderPPLightingProperty",
                ShaderFlags: { } flags
            } ||
            (flags & ParallaxFlag) == 0 ||
            (flags & ParallaxOcclusionFlag) != 0 ||
            metadata.HeightMapPath is not { } heightMapPath ||
            string.IsNullOrWhiteSpace(heightMapPath))
        {
            return null;
        }

        return new NifClassicParallaxMaterial(heightMapPath);
    }

    /// <summary>
    ///     Rejects structurally missing or wholly degenerate UV/TBN payloads before the height map
    ///     enters the decoded/GPU cache. Individual interpolated pixels retain a matching shader-side
    ///     length guard because a valid mesh can still contain isolated degenerate vertices.
    /// </summary>
    internal static bool HasUsableGeometry(RenderableSubmesh submesh)
    {
        var positionCount = submesh.Positions.Length;
        if (positionCount == 0 || positionCount % 3 != 0 ||
            submesh.Normals is not { } normals || normals.Length < positionCount ||
            submesh.Tangents is not { } tangents || tangents.Length < positionCount ||
            submesh.Bitangents is not { } bitangents || bitangents.Length < positionCount ||
            submesh.UVs is not { } uvs || uvs.Length < positionCount / 3 * 2)
        {
            return false;
        }

        for (var vertex = 0; vertex < positionCount / 3; vertex++)
        {
            var vectorOffset = vertex * 3;
            var uvOffset = vertex * 2;
            var normal = ReadVector3(normals, vectorOffset);
            var tangent = ReadVector3(tangents, vectorOffset);
            var bitangent = ReadVector3(bitangents, vectorOffset);
            if (float.IsFinite(uvs[uvOffset]) && float.IsFinite(uvs[uvOffset + 1]) &&
                IsUsable(normal) && IsUsable(tangent) && IsUsable(bitangent))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     CPU reference for PC-final <c>SM3004.pso</c>. The shipping simple shader hardcodes
    ///     <c>height * 0.04 - 0.02</c>; the serialized parallax-occlusion scale is not an input.
    /// </summary>
    internal static Vector2 ComputeMaterialUv(
        Vector2 baseUv,
        float height,
        Vector3 tangent,
        Vector3 bitangent,
        Vector3 normal,
        Vector3 eyeMinusWorldPosition)
    {
        if (!IsFinite(baseUv) || !float.IsFinite(height) ||
            !TryNormalize(tangent, out tangent) ||
            !TryNormalize(bitangent, out bitangent) ||
            !TryNormalize(normal, out normal) ||
            !IsFinite(eyeMinusWorldPosition))
        {
            return baseUv;
        }

        var tangentView = new Vector3(
            Vector3.Dot(tangent, eyeMinusWorldPosition),
            Vector3.Dot(bitangent, eyeMinusWorldPosition),
            Vector3.Dot(normal, eyeMinusWorldPosition));
        if (!TryNormalize(tangentView, out tangentView))
        {
            return baseUv;
        }

        var offset = height * HeightScale + HeightBias;
        return baseUv + new Vector2(tangentView.X, tangentView.Y) * offset;
    }

    private static Vector3 ReadVector3(float[] values, int offset)
    {
        return new Vector3(values[offset], values[offset + 1], values[offset + 2]);
    }

    private static bool IsUsable(Vector3 value)
    {
        return IsFinite(value) && value.LengthSquared() > BasisLengthSquaredEpsilon;
    }

    private static bool TryNormalize(Vector3 value, out Vector3 normalized)
    {
        var lengthSquared = value.LengthSquared();
        if (!IsFinite(value) || !float.IsFinite(lengthSquared) || lengthSquared <= BasisLengthSquaredEpsilon)
        {
            normalized = default;
            return false;
        }

        normalized = value * (1f / MathF.Sqrt(lengthSquared));
        return true;
    }

    private static bool IsFinite(Vector2 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y);
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }
}

internal readonly record struct NifClassicParallaxMaterial(string HeightMapTexturePath);
