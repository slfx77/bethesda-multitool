using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

internal enum ModernStandardOpaqueShaderVariant
{
    None = 0,
    SingleSidedOpaque = 1,
    SingleSidedGreaterCutout = 2,
    DoubleSidedGreaterCutout = 3,
    StarfieldSingleSidedOpaque = 4,
    StarfieldDoubleSidedOpaque = 5,
    StarfieldSingleSidedGreaterCutout = 6,
    StarfieldDoubleSidedGreaterCutout = 7
}

internal readonly record struct ModernStandardShaderActivation(
    bool FalloutModernStandardRequested,
    bool StarfieldDiffuseLitRequested);

/// <summary>
///     Resolves the shared shader override without erasing its deliberately different per-game
///     defaults. Fallout 4/76 keep the established exact-<c>1</c> opt-in. Starfield's bounded
///     diffuse-lit family is the fidelity path for audited <c>.mat</c> geometry, so an unset value
///     defaults on only after a Starfield world is identified. Exact <c>1</c> forces it on; every
///     other set value (including <c>0</c>) fails closed.
/// </summary>
internal static class ModernStandardShaderActivationPolicy
{
    internal static ModernStandardShaderActivation Resolve(
        BethesdaGame game,
        string? overrideValue)
    {
        var explicitlyEnabled = string.Equals(overrideValue, "1", StringComparison.Ordinal);
        return new ModernStandardShaderActivation(
            FalloutModernStandardRequested:
                (game is BethesdaGame.Fallout4 or BethesdaGame.Fallout76) && explicitlyEnabled,
            StarfieldDiffuseLitRequested:
                game == BethesdaGame.Starfield && (overrideValue is null || explicitlyEnabled));
    }
}

/// <summary>
///     Immutable authored material and placement facts used to admit the narrow modern
///     standard-opaque shader path. Texture residency is deliberately absent: a declared material
///     remains the same candidate while its asynchronously uploaded resources move from fallback
///     to resident.
/// </summary>
internal readonly record struct ModernStandardOpaqueShaderFacts(
    BethesdaGame Game,
    bool HeatmapEnabled,
    bool IsScatteredGrass,
    bool AlphaBlend,
    bool IsDecal,
    bool IsEmissive,
    bool IsLighting30,
    bool HasLighting30GlowMap,
    bool HasEffectFalloff,
    bool IsEffectTintNeutral,
    bool HasSoftParticle,
    bool IsBillboard,
    bool IsLeafBillboard,
    bool IsTallGrass,
    bool IsParticle,
    bool HasRuntimeSpeedTreeLod,
    bool HasClassicBasicShader,
    bool HasClassicEnvironmentMap,
    bool HasClassicParallax,
    bool HasGradientMap,
    string? StarfieldMaterialPath,
    bool HasDerivedStarfieldNormal,
    uint TextureFeatureMask,
    bool HasBump,
    bool HasSpecularMap,
    float SpecularExponent,
    bool ModernEnvironmentMapDeclared,
    float ModernEnvironmentMapScale,
    bool WrapTextureU,
    bool WrapTextureV,
    bool AlphaTestEnabled,
    byte AlphaTestFunction,
    bool DoubleSided);

/// <summary>
///     Fail-closed admission policy for the modern standard opaque/cutout shader family.
/// </summary>
internal static class ModernStandardOpaqueShaderPolicy
{
    private const byte GreaterAlphaTestFunction = 4;
    // Bit 0 is the declared modern specular map. Every other authored shader-visible bit belongs
    // to a route removed from the specialization; requiring the exact mask also makes future bits
    // fail closed until they are deliberately audited.
    private const uint ModernSpecularMapFeatureMask = 1u;
    private const uint StarfieldOpacityFeatureMask = 1u << 15;

    internal static ModernStandardOpaqueShaderVariant Resolve(
        in ModernStandardOpaqueShaderFacts facts)
    {
        if (facts.HeatmapEnabled ||
            facts.IsScatteredGrass ||
            facts.AlphaBlend ||
            facts.IsDecal ||
            facts.IsEmissive ||
            facts.IsLighting30 ||
            facts.HasLighting30GlowMap ||
            facts.HasEffectFalloff ||
            !facts.IsEffectTintNeutral ||
            facts.HasSoftParticle ||
            facts.IsBillboard ||
            facts.IsLeafBillboard ||
            facts.IsTallGrass ||
            facts.IsParticle ||
            facts.HasRuntimeSpeedTreeLod ||
            facts.HasClassicBasicShader ||
            facts.HasClassicEnvironmentMap ||
            facts.HasClassicParallax ||
            facts.HasGradientMap)
        {
            return ModernStandardOpaqueShaderVariant.None;
        }

        // A non-null identity selects the Starfield family even when malformed. It must not fall
        // through and accidentally satisfy the unrelated FO76 tuple.
        if (facts.StarfieldMaterialPath is not null)
        {
            return ResolveStarfield(in facts);
        }

        return ResolveFo76(in facts);
    }

    private static ModernStandardOpaqueShaderVariant ResolveFo76(
        in ModernStandardOpaqueShaderFacts facts)
    {
        if ((facts.Game != BethesdaGame.Fallout4 && facts.Game != BethesdaGame.Fallout76) ||
            facts.HasDerivedStarfieldNormal ||
            facts.TextureFeatureMask != ModernSpecularMapFeatureMask ||
            !facts.HasBump ||
            !facts.HasSpecularMap ||
            !facts.ModernEnvironmentMapDeclared ||
            !float.IsFinite(facts.ModernEnvironmentMapScale) ||
            facts.ModernEnvironmentMapScale <= 0f ||
            !facts.WrapTextureU ||
            !facts.WrapTextureV)
        {
            return ModernStandardOpaqueShaderVariant.None;
        }

        if (!facts.AlphaTestEnabled)
        {
            return facts.DoubleSided
                ? ModernStandardOpaqueShaderVariant.None
                : ModernStandardOpaqueShaderVariant.SingleSidedOpaque;
        }

        if (facts.AlphaTestFunction != GreaterAlphaTestFunction)
        {
            return ModernStandardOpaqueShaderVariant.None;
        }

        return facts.DoubleSided
            ? ModernStandardOpaqueShaderVariant.DoubleSidedGreaterCutout
            : ModernStandardOpaqueShaderVariant.SingleSidedGreaterCutout;
    }

    private static ModernStandardOpaqueShaderVariant ResolveStarfield(
        in ModernStandardOpaqueShaderFacts facts)
    {
        // The audited Starfield BSGeometry route is diffuse-lit: the .mat owns diffuse and optional
        // slot-1 normal data, while specular maps/environment maps and every packed texture feature
        // except the audited slot-2 opacity marker are absent. A derived-normal marker must agree
        // exactly with the runtime HasBump uniform. That admits both derived-normal and no-normal
        // materials to one PSO without baking residency or the normal decision into the variant.
        if (facts.Game != BethesdaGame.Starfield ||
            string.IsNullOrWhiteSpace(facts.StarfieldMaterialPath) ||
            !facts.StarfieldMaterialPath.Trim().EndsWith(".mat", StringComparison.OrdinalIgnoreCase) ||
            (facts.TextureFeatureMask & ~StarfieldOpacityFeatureMask) != 0u ||
            ((facts.TextureFeatureMask & StarfieldOpacityFeatureMask) != 0u &&
             !facts.AlphaTestEnabled) ||
            facts.HasBump != facts.HasDerivedStarfieldNormal ||
            facts.HasSpecularMap ||
            !float.IsFinite(facts.SpecularExponent) ||
            facts.SpecularExponent != 0f ||
            facts.ModernEnvironmentMapDeclared ||
            !float.IsFinite(facts.ModernEnvironmentMapScale) ||
            facts.ModernEnvironmentMapScale != 0f ||
            !facts.WrapTextureU ||
            !facts.WrapTextureV)
        {
            return ModernStandardOpaqueShaderVariant.None;
        }

        if (!facts.AlphaTestEnabled)
        {
            return facts.DoubleSided
                ? ModernStandardOpaqueShaderVariant.StarfieldDoubleSidedOpaque
                : ModernStandardOpaqueShaderVariant.StarfieldSingleSidedOpaque;
        }

        if (facts.AlphaTestFunction != GreaterAlphaTestFunction)
        {
            return ModernStandardOpaqueShaderVariant.None;
        }

        return facts.DoubleSided
            ? ModernStandardOpaqueShaderVariant.StarfieldDoubleSidedGreaterCutout
            : ModernStandardOpaqueShaderVariant.StarfieldSingleSidedGreaterCutout;
    }
}
