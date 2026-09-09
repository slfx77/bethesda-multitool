using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;

/// <summary>
///     TES4 ordinary base-only materials select specular from the loaded normal's format.
///     The source proof and current composition state must both permit this narrow policy.
/// </summary>
internal static class OblivionOrdinarySpecularPolicy
{
    internal static bool IsEligible(
        RenderableSubmesh submesh,
        string? diffusePath,
        string? normalPath)
    {
        if (!submesh.HasAuthoredOblivionOrdinaryInputs ||
            !IsOrdinaryMaterialName(submesh.LegacyMaterialName) ||
            string.IsNullOrWhiteSpace(submesh.AuthoredOblivionOrdinaryDiffusePath) ||
            string.IsNullOrWhiteSpace(diffusePath) ||
            submesh.ShaderMetadata is not null || submesh.IsFaceGen ||
            submesh.UsesClassicHairMaterial || submesh.IsEyeEnvmap || submesh.IsEmissive ||
            submesh.HasAlphaBlend || submesh.HasAlphaTest || !submesh.MaterialAlpha.Equals(1f) ||
            submesh.MaterialAlphaController is not null || submesh.AnimatedEmissiveColor is not null ||
            HasAdditionalTextures(submesh))
        {
            return false;
        }

        var authoredDiffuse = NifTexturePathUtility.Normalize(submesh.AuthoredOblivionOrdinaryDiffusePath);
        if (!string.Equals(authoredDiffuse, NifTexturePathUtility.Normalize(diffusePath), StringComparison.Ordinal))
        {
            return false;
        }

        // An override can supply another material's normal. Preserve the old behavior for that
        // unproven variant; absent normals remain the ordinary no-specular case.
        var authoredNormal = FaceGenHeadShaderFamilyResolver.BuildSiblingPath(authoredDiffuse, "_n");
        return string.IsNullOrWhiteSpace(normalPath) ||
               string.Equals(authoredNormal, NifTexturePathUtility.Normalize(normalPath), StringComparison.Ordinal);
    }

    internal static Vector4 Resolve(
        Vector4 candidate,
        bool usesOrdinaryPolicy,
        bool hasBump,
        bool isPathBacked,
        bool isResident,
        GpuTexturePayloadFormat normalFormat)
    {
        // PC 007D8160 admits formats 1/5/6 (or the separately excluded override bit0x80).
        // The traced supported-format table preserves DXT1 as discriminator4. Inspect the actual
        // resident payload: converted/generated RGBA is not DXT1 merely because its source was.
        // Pending/missing normals have no positive format evidence. Keep RGB for emissive users.
        if (usesOrdinaryPolicy && candidate.W > 0f &&
            (!hasBump || !isPathBacked || !isResident || normalFormat == GpuTexturePayloadFormat.BC1))
        {
            candidate.W = 0f;
        }

        return candidate;
    }

    internal static bool IsOrdinaryMaterialName(string? name)
    {
        // Exact special families in retail 007DA220, plus the 0047AC20 skin binder.
        // Prefix exclusions conservatively cover their parameterized variants. Check both the
        // source material and its current identity: unsupported NPC skin can still be IsFaceGen=false.
        return !string.IsNullOrWhiteSpace(name) &&
               !name.Equals("skin", StringComparison.OrdinalIgnoreCase) &&
               !name.Equals("right eye", StringComparison.OrdinalIgnoreCase) &&
               !name.Equals("left eye", StringComparison.OrdinalIgnoreCase) &&
               !name.StartsWith("envmap", StringComparison.OrdinalIgnoreCase) &&
               !name.StartsWith("refract", StringComparison.OrdinalIgnoreCase) &&
               !name.StartsWith("dynalpha", StringComparison.OrdinalIgnoreCase) &&
               !name.StartsWith("HideSecret", StringComparison.OrdinalIgnoreCase) &&
               !name.StartsWith("hair", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAdditionalTextures(RenderableSubmesh submesh)
    {
        return submesh.SpecularMapTexturePath is not null || submesh.GradientMapTexturePath is not null ||
               submesh.EnvironmentMapTexturePath is not null || submesh.ClassicEnvironmentMapTexturePath is not null ||
               submesh.ClassicEnvironmentMaskTexturePath is not null ||
               submesh.ClassicParallaxHeightMapTexturePath is not null ||
               submesh.Lighting30GlowMapTexturePath is not null || submesh.BgsmGlowMapTexturePath is not null;
    }
}
