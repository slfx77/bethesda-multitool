using System.Numerics;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     Bounded CPU policy for FNV's active retail pass 193 (<c>BSSM_ADT</c>) base route: one
///     directional light, no placed lights, projected sun shadow, or fog, and an ordinary opaque
///     standard/type-1 static material. <see cref="FnvClassicBasicShaderMode" /> is reused only as the strict decoded-material
///     classifier and vertex-color discriminator; this policy does not activate or count the dormant
///     SLS1009/SLS1013 pass family.
/// </summary>
internal static class FnvActiveAdtBasePolicy
{
    internal const int ActiveAdtPassId = 193;
    internal const uint RuntimeActiveAdtFlag = 1u << 10;
    internal const uint RuntimeActiveAdtVertexColorFlag = 1u << 11;

    internal static bool IsEligible(in FnvActiveAdtBaseEligibility eligibility) =>
        eligibility.Game == BethesdaGame.FalloutNewVegas &&
        eligibility.LightingEnabled &&
        eligibility.PlacedLightCount == 0 &&
        !eligibility.HasProjectedSunShadow &&
        !eligibility.FogEnabled &&
        !eligibility.HasAlphaBlend &&
        !eligibility.HasAlphaTest &&
#pragma warning disable S1244 // eligibility requires the exact authored default alpha of 1; any deviation routes to the alpha path
        eligibility.MaterialAlpha == 1f &&
#pragma warning restore S1244
        !eligibility.HasMaterialAlphaController &&
        IsClassifiedOrdinaryMaterial(eligibility.ClassifierMode);

    /// <summary>
    ///     Applies policy-owned flags while preserving every unrelated material bit. Bits 10/11 are
    ///     named for the active ADT route and its vertex-color variant, never for the audit-only
    ///     classifier's historical shader-package names.
    /// </summary>
    internal static uint ApplyRuntimeFlags(
        in FnvActiveAdtBaseEligibility eligibility,
        uint materialFlags)
    {
        materialFlags &= ~(RuntimeActiveAdtFlag | RuntimeActiveAdtVertexColorFlag);
        if (!IsEligible(eligibility))
        {
            return materialFlags;
        }

        materialFlags |= RuntimeActiveAdtFlag;
        if (eligibility.ClassifierMode == FnvClassicBasicShaderMode.Sls1013VertexColor)
        {
            materialFlags |= RuntimeActiveAdtVertexColorFlag;
        }

        return materialFlags;
    }

    /// <summary>
    ///     CPU oracle for shipped PC <c>SLS2000</c>. The normal-map RGB is decoded to signed space and
    ///     normalized without any bump-scale adjustment. The per-vertex light is transformed by the
    ///     authored T/B/N rows and normalized after that transform. Their dot remains signed; only the
    ///     component-wise <c>Ambient + Sun * dot</c> result is clamped at zero. The base sample is first
    ///     multiplied by vertex RGB for the classified vertex-color variant, then by that shade.
    /// </summary>
    internal static FnvActiveAdtBaseEvaluation EvaluateSls2000(
        FnvClassicBasicShaderMode classifierMode,
        Vector3 normalMapSample,
        Vector3 tangent,
        Vector3 bitangent,
        Vector3 vertexNormal,
        Vector3 lightData,
        Vector3 ambientRgb,
        Vector3 sunRgb,
        Vector3 baseRgb,
        Vector3 vertexRgb)
    {
        if (!IsClassifiedOrdinaryMaterial(classifierMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(classifierMode), classifierMode, "An ordinary classified FNV material is required.");
        }

        // SLS2000: add -0.5, double, then nrm. There is no authored/viewer bump-scale multiplier.
        var decodedNormal = (normalMapSample - new Vector3(0.5f)) * 2f;
        var normalizedDecodedNormal = Vector3.Normalize(decodedNormal);

        // The VS applies the raw authored T/B/N rows first, then normalizes the resulting light vector.
        var tangentSpaceLight = new Vector3(
            Vector3.Dot(tangent, lightData),
            Vector3.Dot(bitangent, lightData),
            Vector3.Dot(vertexNormal, lightData));
        var normalizedTangentSpaceLight = Vector3.Normalize(tangentSpaceLight);

        var rawSignedDot = Vector3.Dot(normalizedDecodedNormal, normalizedTangentSpaceLight);
        var shade = Vector3.Max(ambientRgb + sunRgb * rawSignedDot, Vector3.Zero);
        var effectiveBaseRgb = baseRgb;
        if (classifierMode == FnvClassicBasicShaderMode.Sls1013VertexColor)
        {
            effectiveBaseRgb = Vector3.Multiply(effectiveBaseRgb, vertexRgb);
        }

        var rgb = Vector3.Multiply(effectiveBaseRgb, shade);

        return new FnvActiveAdtBaseEvaluation(
            normalizedDecodedNormal,
            normalizedTangentSpaceLight,
            rawSignedDot,
            shade,
            rgb);
    }

    private static bool IsClassifiedOrdinaryMaterial(FnvClassicBasicShaderMode mode) =>
        mode is FnvClassicBasicShaderMode.Sls1009 or FnvClassicBasicShaderMode.Sls1013VertexColor;
}

internal readonly record struct FnvActiveAdtBaseEligibility(
    BethesdaGame Game,
    bool LightingEnabled,
    int PlacedLightCount,
    bool HasProjectedSunShadow,
    bool FogEnabled,
    bool HasAlphaBlend,
    bool HasAlphaTest,
    float MaterialAlpha,
    bool HasMaterialAlphaController,
    FnvClassicBasicShaderMode ClassifierMode);

internal readonly record struct FnvActiveAdtBaseEvaluation(
    Vector3 NormalizedDecodedNormal,
    Vector3 NormalizedTangentSpaceLight,
    float RawSignedDot,
    Vector3 Shade,
    Vector3 Rgb);
