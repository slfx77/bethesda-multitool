using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;

/// <summary>
///     Bounded TES4 actor Hair LayerMap admission. The source census proves raw green/alpha1;
///     composition proves the current family; submission separately proves a resident texture.
/// </summary>
internal static class OblivionHairLayerPolicy
{
    internal const uint TextureFlag = 1u << 19;

    internal static void Apply(
        RenderableSubmesh submesh,
        NifTextureResolver textures,
        string? actorDiffusePath)
    {
        ArgumentNullException.ThrowIfNull(submesh);
        ArgumentNullException.ThrowIfNull(textures);
        submesh.OblivionHairLayerDiffusePath = null;
        submesh.OblivionHairLayerTexturePath = null;
        if (!HasCompatibleMaterial(submesh) || string.IsNullOrWhiteSpace(actorDiffusePath) ||
            !SamePath(actorDiffusePath, submesh.DiffuseTexturePath))
        {
            return;
        }

        var layer = FaceGenHeadShaderFamilyResolver.BuildSiblingPath(actorDiffusePath, "_hl");
        if (layer is null || textures.GetTexture(layer) is null)
        {
            return;
        }

        submesh.OblivionHairLayerDiffusePath = NifTexturePathUtility.Normalize(actorDiffusePath);
        submesh.OblivionHairLayerTexturePath = layer;
    }

    internal static string? ResolveTexturePath(RenderableSubmesh submesh, string? effectiveDiffusePath)
    {
        if (!HasCompatibleMaterial(submesh) ||
            !SamePath(submesh.OblivionHairLayerDiffusePath, effectiveDiffusePath) ||
            !SamePath(submesh.DiffuseTexturePath, effectiveDiffusePath))
        {
            return null;
        }

        var expected = FaceGenHeadShaderFamilyResolver.BuildSiblingPath(effectiveDiffusePath, "_hl");
        return SamePath(expected, submesh.OblivionHairLayerTexturePath)
            ? submesh.OblivionHairLayerTexturePath
            : null;
    }

    internal static bool IsResidentLayer(
        string? cacheKey, bool resident, bool cubemap, GpuTexturePayloadFormat format)
    {
        return !string.IsNullOrWhiteSpace(cacheKey) && resident && !cubemap &&
               format is GpuTexturePayloadFormat.Rgba8 or GpuTexturePayloadFormat.BC1 or
                   GpuTexturePayloadFormat.BC2 or GpuTexturePayloadFormat.BC3 or GpuTexturePayloadFormat.BC7;
    }

    private static bool HasCompatibleMaterial(RenderableSubmesh submesh)
    {
        if (!submesh.HasAuthoredOblivionHairLayerInputs || !submesh.UsesClassicHairMaterial ||
            !string.Equals(submesh.LegacyMaterialName, "Hair", StringComparison.OrdinalIgnoreCase) ||
            submesh.ShaderMetadata is not null || submesh.IsFaceGen || submesh.IsEyeEnvmap ||
            submesh.IsEmissive || submesh.IsLighting30 || submesh.IsDoubleSided ||
            !submesh.HasAlphaBlend || !submesh.HasAlphaTest || submesh.AlphaTestThreshold != 0 ||
            submesh.AlphaTestFunction != 4 || submesh.SrcBlendMode != 6 || submesh.DstBlendMode != 7 ||
            submesh.MaterialAlphaController is not null || submesh.AnimatedEmissiveColor is not null ||
            !submesh.MaterialAlpha.Equals(1f) || submesh.MaterialDiffuse != (1f, 1f, 1f) ||
            submesh.EffectTint != (1f, 1f, 1f) || submesh.EffectFalloff is not null ||
            submesh.UsesExternalEmittance || !submesh.SoftParticleFalloffDepth.Equals(0f) ||
            submesh.BgsmEmissionColor != default || submesh.IsTreeAnimation || submesh.IsLeafBillboard ||
            submesh.IsSpeedTreeBranch || submesh.IsBillboard || submesh.IsParticleCloud || submesh.IsDecal ||
            submesh.ClampTextureU || submesh.ClampTextureV || submesh.UvScrollVelocity != default ||
            submesh.StarfieldMaterialColor != default || submesh.StarfieldMaterialAlpha != default ||
            HasAdditionalTextures(submesh) || submesh.TintColor is not { } tint ||
            !ValidTint(tint.R) || !ValidTint(tint.G) || !ValidTint(tint.B))
        {
            return false;
        }

        var count = submesh.Positions.Length / 3;
        if (count < 3 || submesh.Positions.Length != count * 3 ||
            submesh.UVs is not { } uvs || uvs.Length != count * 2 ||
            submesh.VertexColors is not { } colors || colors.Length != count * 4 ||
            uvs.Any(value => !float.IsFinite(value)))
        {
            return false;
        }

        // This is a current composition recheck only. Exact raw-float equality was proved by
        // the source reader before quantization; byte255 alone cannot prove authored1.0.
        for (var vertex = 0; vertex < count; vertex++)
        {
            if (colors[vertex * 4 + 1] != 255 || colors[vertex * 4 + 3] != 255)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidTint(float value)
    {
        return float.IsFinite(value) && value is >= 0f and <= 1f;
    }

    private static bool SamePath(string? first, string? second)
    {
        return !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second) &&
               string.Equals(NifTexturePathUtility.Normalize(first), NifTexturePathUtility.Normalize(second),
                   StringComparison.Ordinal);
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
