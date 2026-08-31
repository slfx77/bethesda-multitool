using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     Derives the Starfield material-normal request at GPU upload time. Starfield NIFs put the
///     <c>.mat</c> reference in the diffuse lane and leave the ordinary normal-path lane empty, while
///     slot 1 of <c>materialsbeta.cdb</c> owns the actual normal. Keeping this policy after decoded-mesh
///     caching lets existing warm payloads gain the missing normal without invalidating that cache or
///     forcing a decoder-version cold start.
/// </summary>
internal static class StarfieldMaterialNormalPolicy
{
    internal readonly record struct Resolution(string? TexturePath, bool HasBump, bool IsDerived);

    /// <summary>
    ///     Returns the authored normal unchanged, or derives a role-qualified Starfield material
    ///     request when a usable tangent basis exists. The pixel shader repeats the per-fragment
    ///     tangent guard; this one-time scan avoids paying for a normal sample on wholly tangentless
    ///     geometry.
    /// </summary>
    internal static Resolution Resolve(
        string? diffuseTexturePath,
        string? normalMapTexturePath,
        bool hasBump,
        ReadOnlySpan<GpuMeshUploader.GpuVertex> vertices)
    {
        if (!string.IsNullOrEmpty(normalMapTexturePath))
        {
            return new Resolution(normalMapTexturePath, hasBump, false);
        }

        if (string.IsNullOrEmpty(diffuseTexturePath) ||
            !MaterialTexturePathResolver.IsStarfieldMaterialPath(diffuseTexturePath) ||
            !HasUsableTangentBasis(vertices))
        {
            return new Resolution(normalMapTexturePath, hasBump, false);
        }

        return new Resolution(
            MaterialTexturePathResolver.BuildStarfieldNormalMapRequest(diffuseTexturePath),
            true,
            true);
    }

    private static bool HasUsableTangentBasis(ReadOnlySpan<GpuMeshUploader.GpuVertex> vertices)
    {
        foreach (ref readonly var vertex in vertices)
        {
            var tangentLengthSquared = vertex.Tangent.LengthSquared();
            var bitangentLengthSquared = vertex.Bitangent.LengthSquared();
            if (float.IsFinite(tangentLengthSquared) &&
                float.IsFinite(bitangentLengthSquared) &&
                tangentLengthSquared > 1e-6f &&
                bitangentLengthSquared > 1e-6f)
            {
                return true;
            }
        }

        return false;
    }
}
