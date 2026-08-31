using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

public sealed class ModernStandardOpaqueShaderPolicyTests
{
    [Theory]
    [InlineData(BethesdaGame.Starfield, null, false, true)]
    [InlineData(BethesdaGame.Starfield, "1", false, true)]
    [InlineData(BethesdaGame.Starfield, "0", false, false)]
    [InlineData(BethesdaGame.Starfield, "", false, false)]
    [InlineData(BethesdaGame.Starfield, "unexpected", false, false)]
    [InlineData(BethesdaGame.Fallout4, null, false, false)]
    [InlineData(BethesdaGame.Fallout4, "1", true, false)]
    [InlineData(BethesdaGame.Fallout76, null, false, false)]
    [InlineData(BethesdaGame.Fallout76, "0", false, false)]
    [InlineData(BethesdaGame.Fallout76, "1", true, false)]
    [InlineData(BethesdaGame.Unknown, "1", false, false)]
    public void Activation_is_game_scoped_and_preserves_the_Starfield_zero_escape_hatch(
        BethesdaGame game,
        string? overrideValue,
        bool expectedFallout,
        bool expectedStarfield)
    {
        var activation = ModernStandardShaderActivationPolicy.Resolve(game, overrideValue);

        Assert.Equal(expectedFallout, activation.FalloutModernStandardRequested);
        Assert.Equal(expectedStarfield, activation.StarfieldDiffuseLitRequested);
    }

    [Fact]
    public void Existing_fo76_variant_numeric_values_remain_stable()
    {
        Assert.Equal(0, (int)ModernStandardOpaqueShaderVariant.None);
        Assert.Equal(1, (int)ModernStandardOpaqueShaderVariant.SingleSidedOpaque);
        Assert.Equal(2, (int)ModernStandardOpaqueShaderVariant.SingleSidedGreaterCutout);
        Assert.Equal(3, (int)ModernStandardOpaqueShaderVariant.DoubleSidedGreaterCutout);
    }

    [Theory]
    [InlineData(false, false, 0, 1)]
    [InlineData(false, false, 255, 1)]
    [InlineData(true, false, 4, 2)]
    [InlineData(true, true, 4, 3)]
    [InlineData(false, true, 4, 0)]
    [InlineData(true, false, 3, 0)]
    [InlineData(true, true, 5, 0)]
    public void Fo76_alpha_mode_selects_only_the_three_supported_variants(
        bool alphaTestEnabled,
        bool doubleSided,
        byte alphaTestFunction,
        int expected)
    {
        var facts = EligibleFacts() with
        {
            AlphaTestEnabled = alphaTestEnabled,
            AlphaTestFunction = alphaTestFunction,
            DoubleSided = doubleSided
        };

        Assert.Equal((ModernStandardOpaqueShaderVariant)expected,
            ModernStandardOpaqueShaderPolicy.Resolve(facts));
    }

    [Theory]
    [InlineData(BethesdaGame.Fallout4, 2)]
    [InlineData(BethesdaGame.Fallout76, 2)]
    [InlineData(BethesdaGame.Unknown, 0)]
    [InlineData(BethesdaGame.Starfield, 0)]
    public void Fo_family_is_limited_to_fo4_and_fo76(BethesdaGame game, int expected)
    {
        var facts = EligibleFacts() with { Game = game };

        Assert.Equal(
            (ModernStandardOpaqueShaderVariant)expected,
            ModernStandardOpaqueShaderPolicy.Resolve(facts));
    }

    [Theory]
    [InlineData(false, false, false, false, 4)]
    [InlineData(true, true, false, false, 4)]
    [InlineData(false, false, false, true, 5)]
    [InlineData(true, true, false, true, 5)]
    [InlineData(false, false, true, false, 6)]
    [InlineData(true, true, true, false, 6)]
    [InlineData(false, false, true, true, 7)]
    [InlineData(true, true, true, true, 7)]
    public void Starfield_normal_uniform_and_alpha_mode_select_four_variants(
        bool hasBump,
        bool hasDerivedStarfieldNormal,
        bool alphaTestEnabled,
        bool doubleSided,
        int expected)
    {
        var facts = StarfieldFacts() with
        {
            HasBump = hasBump,
            HasDerivedStarfieldNormal = hasDerivedStarfieldNormal,
            AlphaTestEnabled = alphaTestEnabled,
            DoubleSided = doubleSided
        };

        Assert.Equal(
            (ModernStandardOpaqueShaderVariant)expected,
            ModernStandardOpaqueShaderPolicy.Resolve(facts));
    }

    [Fact]
    public void Starfield_cutout_admits_only_the_audited_slot2_opacity_feature_bit()
    {
        var opacityCutout = StarfieldFacts() with
        {
            TextureFeatureMask = 1u << 15,
            AlphaTestEnabled = true,
            AlphaTestFunction = 4,
            DoubleSided = false
        };

        Assert.Equal(
            ModernStandardOpaqueShaderVariant.StarfieldSingleSidedGreaterCutout,
            ModernStandardOpaqueShaderPolicy.Resolve(opacityCutout));
        Assert.Equal(
            ModernStandardOpaqueShaderVariant.None,
            ModernStandardOpaqueShaderPolicy.Resolve(
                opacityCutout with { TextureFeatureMask = (1u << 15) | 1u }));
        Assert.Equal(
            ModernStandardOpaqueShaderVariant.None,
            ModernStandardOpaqueShaderPolicy.Resolve(
                opacityCutout with { AlphaTestEnabled = false }));
    }

    [Theory]
    [InlineData(DeniedStarfieldFact.WrongGame)]
    [InlineData(DeniedStarfieldFact.WrongGameFo76Tuple)]
    [InlineData(DeniedStarfieldFact.MissingMaterialIdentity)]
    [InlineData(DeniedStarfieldFact.MalformedMaterialIdentity)]
    [InlineData(DeniedStarfieldFact.DerivedNormalWithoutBump)]
    [InlineData(DeniedStarfieldFact.BumpWithoutDerivedNormal)]
    [InlineData(DeniedStarfieldFact.UnsupportedTextureFeatureMask)]
    [InlineData(DeniedStarfieldFact.SpecularMap)]
    [InlineData(DeniedStarfieldFact.NonZeroSpecularExponent)]
    [InlineData(DeniedStarfieldFact.DeclaredModernEnvironmentMap)]
    [InlineData(DeniedStarfieldFact.NonZeroModernEnvironmentMapScale)]
    [InlineData(DeniedStarfieldFact.ClampTextureU)]
    [InlineData(DeniedStarfieldFact.ClampTextureV)]
    [InlineData(DeniedStarfieldFact.NonGreaterAlphaFunction)]
    public void Starfield_family_fails_closed_on_unaudited_material_facts(
        DeniedStarfieldFact deniedFact)
    {
        var facts = DenyOneStarfieldFact(StarfieldFacts(), deniedFact);

        Assert.Equal(
            ModernStandardOpaqueShaderVariant.None,
            ModernStandardOpaqueShaderPolicy.Resolve(facts));
    }

    [Theory]
    [InlineData(DeniedFact.Heatmap)]
    [InlineData(DeniedFact.ScatteredGrass)]
    [InlineData(DeniedFact.AlphaBlend)]
    [InlineData(DeniedFact.Decal)]
    [InlineData(DeniedFact.Emissive)]
    [InlineData(DeniedFact.Lighting30)]
    [InlineData(DeniedFact.Lighting30Glow)]
    [InlineData(DeniedFact.EffectFalloff)]
    [InlineData(DeniedFact.NonNeutralEffectTint)]
    [InlineData(DeniedFact.SoftParticle)]
    [InlineData(DeniedFact.Billboard)]
    [InlineData(DeniedFact.LeafBillboard)]
    [InlineData(DeniedFact.TallGrass)]
    [InlineData(DeniedFact.Particle)]
    [InlineData(DeniedFact.RuntimeSpeedTreeLod)]
    [InlineData(DeniedFact.ClassicBasicShader)]
    [InlineData(DeniedFact.ClassicEnvironmentMap)]
    [InlineData(DeniedFact.ClassicParallax)]
    [InlineData(DeniedFact.GradientMap)]
    [InlineData(DeniedFact.UnsupportedTextureFeatureMask)]
    [InlineData(DeniedFact.NoBump)]
    [InlineData(DeniedFact.NoSpecularMap)]
    [InlineData(DeniedFact.NoDeclaredModernEnvironmentMap)]
    [InlineData(DeniedFact.NonPositiveModernEnvironmentMapScale)]
    [InlineData(DeniedFact.ClampTextureU)]
    [InlineData(DeniedFact.ClampTextureV)]
    [InlineData(DeniedFact.NonGreaterAlphaFunction)]
    public void Changing_any_single_required_fact_fails_closed(DeniedFact deniedFact)
    {
        var facts = DenyOneFact(EligibleFacts(), deniedFact);

        Assert.Equal(
            ModernStandardOpaqueShaderVariant.None,
            ModernStandardOpaqueShaderPolicy.Resolve(facts));
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(0f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Modern_environment_map_scale_must_be_positive_and_finite(float scale)
    {
        var facts = EligibleFacts() with { ModernEnvironmentMapScale = scale };

        Assert.Equal(
            ModernStandardOpaqueShaderVariant.None,
            ModernStandardOpaqueShaderPolicy.Resolve(facts));
    }

    [Fact]
    public void Current_texture_residency_is_not_an_admission_fact()
    {
        var factNames = typeof(ModernStandardOpaqueShaderFacts)
            .GetProperties()
            .Select(static property => property.Name)
            .ToArray();

        Assert.DoesNotContain(
            factNames,
            static name => name.Contains("Resident", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("Ready", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(nameof(ModernStandardOpaqueShaderFacts.ModernEnvironmentMapDeclared), factNames);
        Assert.Equal(
            ModernStandardOpaqueShaderVariant.SingleSidedGreaterCutout,
            ModernStandardOpaqueShaderPolicy.Resolve(EligibleFacts()));
    }

    private static ModernStandardOpaqueShaderFacts EligibleFacts() => new(
        Game: BethesdaGame.Fallout76,
        HeatmapEnabled: false,
        IsScatteredGrass: false,
        AlphaBlend: false,
        IsDecal: false,
        IsEmissive: false,
        IsLighting30: false,
        HasLighting30GlowMap: false,
        HasEffectFalloff: false,
        IsEffectTintNeutral: true,
        HasSoftParticle: false,
        IsBillboard: false,
        IsLeafBillboard: false,
        IsTallGrass: false,
        IsParticle: false,
        HasRuntimeSpeedTreeLod: false,
        HasClassicBasicShader: false,
        HasClassicEnvironmentMap: false,
        HasClassicParallax: false,
        HasGradientMap: false,
        StarfieldMaterialPath: null,
        HasDerivedStarfieldNormal: false,
        TextureFeatureMask: 1u,
        HasBump: true,
        HasSpecularMap: true,
        SpecularExponent: 16f,
        ModernEnvironmentMapDeclared: true,
        ModernEnvironmentMapScale: 1f,
        WrapTextureU: true,
        WrapTextureV: true,
        AlphaTestEnabled: true,
        AlphaTestFunction: 4,
        DoubleSided: false);

    private static ModernStandardOpaqueShaderFacts StarfieldFacts() => EligibleFacts() with
    {
        Game = BethesdaGame.Starfield,
        StarfieldMaterialPath = @"materials\architecture\wall.mat",
        HasDerivedStarfieldNormal = true,
        TextureFeatureMask = 0u,
        HasBump = true,
        HasSpecularMap = false,
        SpecularExponent = 0f,
        ModernEnvironmentMapDeclared = false,
        ModernEnvironmentMapScale = 0f
    };

    private static ModernStandardOpaqueShaderFacts DenyOneFact(
        ModernStandardOpaqueShaderFacts facts,
        DeniedFact deniedFact) => deniedFact switch
    {
        DeniedFact.Heatmap => facts with { HeatmapEnabled = true },
        DeniedFact.ScatteredGrass => facts with { IsScatteredGrass = true },
        DeniedFact.AlphaBlend => facts with { AlphaBlend = true },
        DeniedFact.Decal => facts with { IsDecal = true },
        DeniedFact.Emissive => facts with { IsEmissive = true },
        DeniedFact.Lighting30 => facts with { IsLighting30 = true },
        DeniedFact.Lighting30Glow => facts with { HasLighting30GlowMap = true },
        DeniedFact.EffectFalloff => facts with { HasEffectFalloff = true },
        DeniedFact.NonNeutralEffectTint => facts with { IsEffectTintNeutral = false },
        DeniedFact.SoftParticle => facts with { HasSoftParticle = true },
        DeniedFact.Billboard => facts with { IsBillboard = true },
        DeniedFact.LeafBillboard => facts with { IsLeafBillboard = true },
        DeniedFact.TallGrass => facts with { IsTallGrass = true },
        DeniedFact.Particle => facts with { IsParticle = true },
        DeniedFact.RuntimeSpeedTreeLod => facts with { HasRuntimeSpeedTreeLod = true },
        DeniedFact.ClassicBasicShader => facts with { HasClassicBasicShader = true },
        DeniedFact.ClassicEnvironmentMap => facts with { HasClassicEnvironmentMap = true },
        DeniedFact.ClassicParallax => facts with { HasClassicParallax = true },
        DeniedFact.GradientMap => facts with { HasGradientMap = true },
        // Bit 16 is the regular-lighting BGSM emission route. The modern-standard specialization
        // does not implement that additive overlay and must fall back to the generic shader.
        DeniedFact.UnsupportedTextureFeatureMask => facts with { TextureFeatureMask = 1u | (1u << 16) },
        DeniedFact.NoBump => facts with { HasBump = false },
        DeniedFact.NoSpecularMap => facts with { HasSpecularMap = false },
        DeniedFact.NoDeclaredModernEnvironmentMap => facts with { ModernEnvironmentMapDeclared = false },
        DeniedFact.NonPositiveModernEnvironmentMapScale => facts with { ModernEnvironmentMapScale = 0f },
        DeniedFact.ClampTextureU => facts with { WrapTextureU = false },
        DeniedFact.ClampTextureV => facts with { WrapTextureV = false },
        DeniedFact.NonGreaterAlphaFunction => facts with { AlphaTestFunction = 3 },
        _ => throw new ArgumentOutOfRangeException(nameof(deniedFact), deniedFact, null)
    };

    private static ModernStandardOpaqueShaderFacts DenyOneStarfieldFact(
        ModernStandardOpaqueShaderFacts facts,
        DeniedStarfieldFact deniedFact) => deniedFact switch
    {
        DeniedStarfieldFact.WrongGame => facts with { Game = BethesdaGame.Fallout76 },
        // Also keep every FO76-specific requirement eligible: the non-null .mat identity must
        // select the Starfield branch instead of falling through to the otherwise-eligible tuple.
        DeniedStarfieldFact.WrongGameFo76Tuple => EligibleFacts() with
        {
            StarfieldMaterialPath = facts.StarfieldMaterialPath
        },
        DeniedStarfieldFact.MissingMaterialIdentity => facts with { StarfieldMaterialPath = null },
        DeniedStarfieldFact.MalformedMaterialIdentity => facts with
        {
            StarfieldMaterialPath = @"materials\architecture\wall.bgsm"
        },
        DeniedStarfieldFact.DerivedNormalWithoutBump => facts with { HasBump = false },
        DeniedStarfieldFact.BumpWithoutDerivedNormal => facts with
        {
            HasDerivedStarfieldNormal = false
        },
        DeniedStarfieldFact.UnsupportedTextureFeatureMask => facts with { TextureFeatureMask = 1u },
        DeniedStarfieldFact.SpecularMap => facts with { HasSpecularMap = true },
        DeniedStarfieldFact.NonZeroSpecularExponent => facts with { SpecularExponent = 16f },
        DeniedStarfieldFact.DeclaredModernEnvironmentMap => facts with
        {
            ModernEnvironmentMapDeclared = true
        },
        DeniedStarfieldFact.NonZeroModernEnvironmentMapScale => facts with
        {
            ModernEnvironmentMapScale = 1f
        },
        DeniedStarfieldFact.ClampTextureU => facts with { WrapTextureU = false },
        DeniedStarfieldFact.ClampTextureV => facts with { WrapTextureV = false },
        DeniedStarfieldFact.NonGreaterAlphaFunction => facts with { AlphaTestFunction = 3 },
        _ => throw new ArgumentOutOfRangeException(nameof(deniedFact), deniedFact, null)
    };

    public enum DeniedFact
    {
        Heatmap,
        ScatteredGrass,
        AlphaBlend,
        Decal,
        Emissive,
        Lighting30,
        Lighting30Glow,
        EffectFalloff,
        NonNeutralEffectTint,
        SoftParticle,
        Billboard,
        LeafBillboard,
        TallGrass,
        Particle,
        RuntimeSpeedTreeLod,
        ClassicBasicShader,
        ClassicEnvironmentMap,
        ClassicParallax,
        GradientMap,
        UnsupportedTextureFeatureMask,
        NoBump,
        NoSpecularMap,
        NoDeclaredModernEnvironmentMap,
        NonPositiveModernEnvironmentMapScale,
        ClampTextureU,
        ClampTextureV,
        NonGreaterAlphaFunction
    }

    public enum DeniedStarfieldFact
    {
        WrongGame,
        WrongGameFo76Tuple,
        MissingMaterialIdentity,
        MalformedMaterialIdentity,
        DerivedNormalWithoutBump,
        BumpWithoutDerivedNormal,
        UnsupportedTextureFeatureMask,
        SpecularMap,
        NonZeroSpecularExponent,
        DeclaredModernEnvironmentMap,
        NonZeroModernEnvironmentMapScale,
        ClampTextureU,
        ClampTextureV,
        NonGreaterAlphaFunction
    }
}
