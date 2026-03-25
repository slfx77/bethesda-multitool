using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;

internal static class NpcGlbMaterialTuning
{
    private const float DefaultRoughness = 0.92f;
    private const float EyeRoughness = 0.18f;
    private const float HairMinimumRoughness = 0.95f;
    private const float HairMaxSpecular = 0.08f;
    private const float MinimumRoughness = 0.12f;
    private const float GlossyMaterialRoughness = 0.78f;
    private const float EnvironmentGlossyMaterialRoughness = 0.48f;
    private const float DefaultGlossiness = 10f;
    private const float MaximumMappedGlossiness = 80f;
    private const float DefaultSpecularFactor = 0.3f;
    private const float EnvironmentSpecularFactor = 0.55f;
    private const uint EnvironmentMappingFlag = 1u << 7;

    internal static NpcGlbMaterialProfile Derive(
        RenderableSubmesh submesh,
        DecodedTexture? normalTexture,
        bool hasGlossAlpha = true)
    {
        ArgumentNullException.ThrowIfNull(submesh);

        var glossStrength = EstimateGlossStrength(normalTexture, hasGlossAlpha);
        var hasEnvironmentMapping = HasEnvironmentMapping(submesh);
        var roughness = ConvertGlossinessToRoughness(submesh.MaterialGlossiness, hasEnvironmentMapping);

        if (hasEnvironmentMapping)
        {
            roughness = MathF.Max(MinimumRoughness, roughness - 0.08f);
        }

        if (hasEnvironmentMapping && submesh.EnvMapScale > 0f)
        {
            roughness = MathF.Max(
                MinimumRoughness,
                roughness - Math.Clamp(submesh.EnvMapScale, 0f, 1.5f) * 0.1f);
        }

        if (submesh.IsEyeEnvmap)
        {
            roughness = MathF.Min(roughness, EyeRoughness);
        }

        var isHair = IsHairSubmesh(submesh);
        if (isHair)
        {
            roughness = MathF.Max(roughness, HairMinimumRoughness);
        }

        float specularFactor;
        if (submesh.IsEyeEnvmap)
        {
            specularFactor = 1f;
        }
        else if (hasEnvironmentMapping)
        {
            specularFactor = EnvironmentSpecularFactor;
        }
        else
        {
            specularFactor = DefaultSpecularFactor;
        }
        if (hasEnvironmentMapping && submesh.EnvMapScale > 0f)
        {
            specularFactor = MathF.Min(1f, specularFactor + Math.Clamp(submesh.EnvMapScale, 0f, 1.5f) * 0.15f);
        }

        if (glossStrength > 0f)
        {
            specularFactor *= Math.Clamp(0.4f + glossStrength * 0.6f, 0.25f, 1f);
        }

        if (isHair)
        {
            specularFactor = MathF.Min(specularFactor, HairMaxSpecular);
        }

        return new NpcGlbMaterialProfile(
            0f,
            Math.Clamp(roughness, MinimumRoughness, 1f),
            glossStrength,
            Math.Clamp(specularFactor, 0f, 1f));
    }

    internal static float EstimateGlossStrength(DecodedTexture? normalTexture)
    {
        return EstimateGlossStrength(normalTexture, true);
    }

    internal static float EstimateGlossStrength(
        DecodedTexture? normalTexture,
        bool hasGlossAlpha)
    {
        if (!hasGlossAlpha || normalTexture?.Pixels == null || normalTexture.Pixels.Length < 4)
        {
            return 0f;
        }

        long alphaTotal = 0;
        var sampleCount = 0;
        for (var index = 3; index < normalTexture.Pixels.Length; index += 4)
        {
            alphaTotal += normalTexture.Pixels[index];
            sampleCount++;
        }

        return sampleCount > 0
            ? alphaTotal / (float)(sampleCount * 255)
            : 0f;
    }

    internal static bool HasEnvironmentHints(RenderableSubmesh submesh)
    {
        ArgumentNullException.ThrowIfNull(submesh);

        return submesh.IsEyeEnvmap || HasEnvironmentMapping(submesh);
    }

    internal static bool HasEnvironmentMapping(RenderableSubmesh submesh)
    {
        ArgumentNullException.ThrowIfNull(submesh);

        return submesh.ShaderMetadata?.ShaderFlags is uint shaderFlags &&
               (shaderFlags & EnvironmentMappingFlag) != 0;
    }

    internal static float ConvertGlossinessToRoughness(float glossiness, bool hasEnvironmentMapping = false)
    {
        if (glossiness <= DefaultGlossiness)
        {
            return DefaultRoughness;
        }

        var glossyMaterialRoughness = hasEnvironmentMapping
            ? EnvironmentGlossyMaterialRoughness
            : GlossyMaterialRoughness;
        var normalized = Math.Clamp(
            (glossiness - DefaultGlossiness) / (MaximumMappedGlossiness - DefaultGlossiness),
            0f,
            1f);
        return DefaultRoughness - normalized * (DefaultRoughness - glossyMaterialRoughness);
    }

    private static bool IsHairSubmesh(RenderableSubmesh submesh)
    {
        if (submesh.TintColor.HasValue)
        {
            return true;
        }

        return ContainsHairHint(submesh.ShapeName) || ContainsHairHint(submesh.DiffuseTexturePath);

        static bool ContainsHairHint(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.Contains("hair", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("brow", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("lash", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("beard", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("mustache", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("goatee", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal readonly record struct NpcGlbMaterialProfile(
        float MetallicFactor,
        float RoughnessFactor,
        float GlossStrength,
        float SpecularFactor);
}
