using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     Locks the tonemap parameter presets to the shipped FalloutNV.esm imagespace records
///     (DefaultImageSpaceInterior 0x160 / DefaultImageSpaceExterior 0x161 — see
///     docs/research/fnv_engine_hdr_imagespace.md) and the bloom gating rules: bloom is engine-mode
///     only this cut, and FALLOUT_VIEWER_BLOOM=0|off kills it for A/Bs.
/// </summary>
public sealed class GpuTonemapSettingsTests
{
    [Theory]
    // --- Sdr (the launcher's "None"): FO3/FNV keep their standalone cinematic grade. ---
    [InlineData(BethesdaGame.Fallout3, (int)GpuTonemapGuiMode.Sdr, true, -1,
        (int)GpuTonemapMode.CinematicFo3Fnv, false, true)]
    [InlineData(BethesdaGame.FalloutNewVegas, (int)GpuTonemapGuiMode.Sdr, true, -1,
        (int)GpuTonemapMode.CinematicFo3Fnv, false, true)]
    [InlineData(BethesdaGame.Fallout3, (int)GpuTonemapGuiMode.Sdr, false, -1,
        (int)GpuTonemapMode.LegacyClamp, false, true)]
    [InlineData(BethesdaGame.FalloutNewVegas, (int)GpuTonemapGuiMode.Sdr, true, (int)GpuTonemapMode.LegacyClamp,
        (int)GpuTonemapMode.LegacyClamp, false, true)]
    [InlineData(BethesdaGame.FalloutNewVegas, (int)GpuTonemapGuiMode.Sdr, true, (int)GpuTonemapMode.GammaAces,
        (int)GpuTonemapMode.GammaAces, false, true)]
    [InlineData(BethesdaGame.FalloutNewVegas, (int)GpuTonemapGuiMode.Sdr, true, (int)GpuTonemapMode.EngineFo3Fnv,
        (int)GpuTonemapMode.EngineFo3Fnv, true, true)]
    [InlineData(BethesdaGame.FalloutNewVegas, (int)GpuTonemapGuiMode.Sdr, true, (int)GpuTonemapMode.CreationModern,
        (int)GpuTonemapMode.CreationModern, false, true)]
    [InlineData(BethesdaGame.Oblivion, (int)GpuTonemapGuiMode.Sdr, true, -1,
        (int)GpuTonemapMode.LegacyClamp, false, true)]
    [InlineData(BethesdaGame.Morrowind, (int)GpuTonemapGuiMode.Sdr, true, -1,
        (int)GpuTonemapMode.LegacyClamp, false, true)]
    [InlineData(BethesdaGame.Skyrim, (int)GpuTonemapGuiMode.Sdr, true, -1,
        (int)GpuTonemapMode.LegacyClamp, false, true)]
    [InlineData(BethesdaGame.Fallout4, (int)GpuTonemapGuiMode.Sdr, true, -1,
        (int)GpuTonemapMode.LegacyClamp, false, true)]
    // --- SdrBloom (the launcher's middle "Bloom"): SDR clamp + classic bright-pass bloom. ---
    [InlineData(BethesdaGame.FalloutNewVegas, (int)GpuTonemapGuiMode.SdrBloom, true, -1,
        (int)GpuTonemapMode.ClassicSdrBloom, true, true)]
    [InlineData(BethesdaGame.Fallout3, (int)GpuTonemapGuiMode.SdrBloom, true, -1,
        (int)GpuTonemapMode.ClassicSdrBloom, true, true)]
    [InlineData(BethesdaGame.Oblivion, (int)GpuTonemapGuiMode.SdrBloom, true, -1,
        (int)GpuTonemapMode.ClassicSdrBloom, true, true)]
    // Defensive: the UI hides/coerces Bloom for non-classic games (LoadData coercion), but the
    // finalize policy itself is game-agnostic — pin what the code DOES if the state ever leaks.
    [InlineData(BethesdaGame.Skyrim, (int)GpuTonemapGuiMode.SdrBloom, true, -1,
        (int)GpuTonemapMode.ClassicSdrBloom, true, true)]
    // A diagnostic operator override still wins under SdrBloom; bloom stays truthful per operator.
    [InlineData(BethesdaGame.FalloutNewVegas, (int)GpuTonemapGuiMode.SdrBloom, true, (int)GpuTonemapMode.EngineFo3Fnv,
        (int)GpuTonemapMode.EngineFo3Fnv, true, true)]
    [InlineData(BethesdaGame.FalloutNewVegas, (int)GpuTonemapGuiMode.SdrBloom, true, (int)GpuTonemapMode.GammaAces,
        (int)GpuTonemapMode.GammaAces, false, true)]
    [InlineData(BethesdaGame.FalloutNewVegas, (int)GpuTonemapGuiMode.SdrBloom, true, (int)GpuTonemapMode.LegacyClamp,
        (int)GpuTonemapMode.LegacyClamp, false, true)]
    // The FALLOUT_VIEWER_HDR=0 kill-switch path is unchanged: no operator can be honored.
    [InlineData(BethesdaGame.FalloutNewVegas, (int)GpuTonemapGuiMode.SdrBloom, false, -1,
        (int)GpuTonemapMode.LegacyClamp, false, true)]
    // --- Hdr: each family's engine HDR operator; authored scene multipliers survive. ---
    [InlineData(BethesdaGame.FalloutNewVegas, (int)GpuTonemapGuiMode.Hdr, true, -1,
        (int)GpuTonemapMode.EngineFo3Fnv, true, false)]
    [InlineData(BethesdaGame.FalloutNewVegas, (int)GpuTonemapGuiMode.Hdr, true, (int)GpuTonemapMode.LegacyClamp,
        (int)GpuTonemapMode.LegacyClamp, false, false)]
    [InlineData(BethesdaGame.FalloutNewVegas, (int)GpuTonemapGuiMode.Hdr, true, (int)GpuTonemapMode.GammaAces,
        (int)GpuTonemapMode.GammaAces, false, false)]
    [InlineData(BethesdaGame.FalloutNewVegas, (int)GpuTonemapGuiMode.Hdr, false, (int)GpuTonemapMode.EngineFo3Fnv,
        (int)GpuTonemapMode.LegacyClamp, false, true)]
    public void FinalizeViewerPostProcessing_GuiModeMatrixIsExplicitlyGameScoped(
        BethesdaGame game,
        int guiModeValue,
        bool tonemapAvailable,
        int operatorOverrideValue,
        int expectedMode,
        bool expectedBloom,
        bool expectedNeutralEmissive)
    {
        const ulong historyKey = 0x1234_5678_9ABC_DEF0;
        var authored = GpuTonemapSettings.EngineExteriorDefaults with
        {
            Mode = game == BethesdaGame.Skyrim
                ? GpuTonemapMode.GammaAces
                : GpuTonemapMode.EngineFo3Fnv,
            EmissiveMult = 7f,
            BloomEnabled = true,
            Saturation = 0.37f,
            ContrastAvgLum = 0.19f,
            Contrast = 1.47f,
            Brightness = 0.83f,
            TintR = 0.2f,
            TintG = 0.4f,
            TintB = 0.6f,
            TintAmount = 0.7f
        };

        var operatorOverride = operatorOverrideValue < 0
            ? null
            : (GpuTonemapMode?)operatorOverrideValue;
        var result = GpuTonemapSettings.FinalizeViewerPostProcessing(
            authored, game, (GpuTonemapGuiMode)guiModeValue, tonemapAvailable, operatorOverride,
            true, historyKey);

        Assert.Equal((GpuTonemapMode)expectedMode, result.Mode);
        Assert.Equal(historyKey, result.HistoryKey);
        // Final policy must not discard the already-resolved IMGS/IMAD grade.
        Assert.Equal(authored.Saturation, result.Saturation);
        Assert.Equal(authored.ContrastAvgLum, result.ContrastAvgLum);
        Assert.Equal(authored.Contrast, result.Contrast);
        Assert.Equal(authored.Brightness, result.Brightness);
        Assert.Equal(authored.TintAmount, result.TintAmount);
        Assert.Equal(expectedBloom, result.BloomEnabled);
        Assert.Equal(expectedNeutralEmissive ? 1f : 7f, result.EmissiveMult);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void FinalizeViewerPostProcessing_SdrBloomRequiresBothAuthoredAndGuiBloom(
        bool authoredBloom, bool guiBloom, bool expectedBloom)
    {
        var authored = GpuTonemapSettings.EngineExteriorDefaults with { BloomEnabled = authoredBloom };

        var result = GpuTonemapSettings.FinalizeViewerPostProcessing(
            authored, BethesdaGame.FalloutNewVegas, GpuTonemapGuiMode.SdrBloom,
            true, null, guiBloom, 0);

        Assert.Equal(GpuTonemapMode.ClassicSdrBloom, result.Mode);
        Assert.Equal(expectedBloom, result.BloomEnabled);
        Assert.Equal(1f, result.EmissiveMult);
    }

    [Fact]
    public void ClassicSdrBloomTraits_SdrDisplayThatStillRunsTheAdaptationChainForBloom()
    {
        // SDR display (no HDR operator), but the classic reduction + adaptation still run: the
        // bright pass thresholds against the adapted average even though the mode-5 composite
        // never samples uAvgLum.
        var traits = GpuTonemapModeTraits.For(GpuTonemapMode.ClassicSdrBloom, true);

        Assert.False(traits.IsHdrDisplayOperator);
        Assert.True(traits.UsesClassicReduction);
        Assert.True(traits.UsesAdaptation);
        Assert.True(traits.AllowsClassicBloom);

        Assert.True(GpuTonemapModeTraits.IsBloomActive(
            GpuTonemapMode.ClassicSdrBloom, true, true, 1.5f));
        Assert.False(GpuTonemapModeTraits.IsBloomActive(
            GpuTonemapMode.ClassicSdrBloom, true, false, 1.5f));
        Assert.False(GpuTonemapModeTraits.IsBloomActive(
            GpuTonemapMode.ClassicSdrBloom, true, true, 0f));
        Assert.False(GpuTonemapModeTraits.IsBloomActive(
            GpuTonemapMode.ClassicSdrBloom, false, true, 1.5f));
    }

    [Fact]
    public void WithoutImagespaceModifiers_NeutralizesEverySceneAndGradeConsumer()
    {
        var poisoned = GpuTonemapSettings.EngineExteriorDefaults with
        {
            EmissiveMult = 2f,
            SunlightScale = 3f,
            GrassScale = 4f,
            SkyScale = 5f,
            Saturation = 0.2f,
            ContrastAvgLum = 0.3f,
            Contrast = 1.4f,
            Brightness = 1.5f,
            TintR = 0.6f,
            TintG = 0.7f,
            TintB = 0.8f,
            TintAmount = 0.9f,
            CinematicFlags = ImageSpaceCinematicFlags.None
        };

        var neutral = GpuTonemapSettings.WithoutImagespaceModifiers(poisoned);

        Assert.Equal(1f, neutral.EmissiveMult);
        Assert.Equal(1f, neutral.SunlightScale);
        Assert.Equal(1f, neutral.GrassScale);
        Assert.Equal(1f, neutral.SkyScale);
        Assert.Equal(1f, neutral.Saturation);
        Assert.Equal(0.5f, neutral.ContrastAvgLum);
        Assert.Equal(1f, neutral.Contrast);
        Assert.Equal(1f, neutral.Brightness);
        Assert.Equal(Vector3.One, new Vector3(neutral.TintR, neutral.TintG, neutral.TintB));
        Assert.Equal(0f, neutral.TintAmount);
        Assert.Equal(ImageSpaceCinematicFlags.All, neutral.CinematicFlags);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("bogus", null)]
    [InlineData("OFF", (int)GpuTonemapMode.LegacyClamp)]
    [InlineData("aces", (int)GpuTonemapMode.GammaAces)]
    [InlineData("engine", (int)GpuTonemapMode.EngineFo3Fnv)]
    [InlineData("modern", (int)GpuTonemapMode.CreationModern)]
    public void ParseTonemapModeOverride_RecognizesOnlyAdvertisedValues(string? value, int? expected)
    {
        var parsed = GpuTonemapSettings.ParseTonemapModeOverride(value);

        Assert.Equal(expected is null ? null : (GpuTonemapMode?)expected.Value, parsed);
    }

    [Theory]
    [InlineData(false, (int)GpuTonemapMode.EngineFo3Fnv, true, 1f, false, false, false)]
    [InlineData(true, (int)GpuTonemapMode.LegacyClamp, true, 1f, false, false, false)]
    [InlineData(true, (int)GpuTonemapMode.GammaAces, true, 1f, false, false, false)]
    [InlineData(true, (int)GpuTonemapMode.EngineFo3Fnv, true, 1f, true, true, true)]
    [InlineData(true, (int)GpuTonemapMode.EngineFo3Fnv, false, 1f, true, true, false)]
    [InlineData(true, (int)GpuTonemapMode.EngineFo3Fnv, true, 0f, true, true, false)]
    [InlineData(true, (int)GpuTonemapMode.CreationModern, true, 1f, false, true, false)]
    [InlineData(true, (int)GpuTonemapMode.CinematicFo3Fnv, true, 1f, false, false, false)]
    // ClassicSdrBloom schedules the full classic chain (the bright pass thresholds against the
    // adapted average) even though its composite is an SDR clamp.
    [InlineData(true, (int)GpuTonemapMode.ClassicSdrBloom, true, 1f, true, true, true)]
    [InlineData(true, (int)GpuTonemapMode.ClassicSdrBloom, false, 1f, true, true, false)]
    [InlineData(false, (int)GpuTonemapMode.ClassicSdrBloom, true, 1f, false, false, false)]
    public void ExecutionPlan_OnlyEngineModeSchedulesClassicHdrWork(
        bool enabled,
        int mode,
        bool bloomEnabled,
        float brightScale,
        bool expectedEngine,
        bool expectedAdaptive,
        bool expectedBloom)
    {
        var plan = GpuTonemapExecutionPlan.Create(
            enabled, (GpuTonemapMode)mode, bloomEnabled, brightScale);

        Assert.Equal(expectedEngine, plan.EngineMode);
        Assert.Equal(expectedAdaptive, plan.AdaptiveMode);
        Assert.Equal(expectedBloom, plan.BloomActive);
        Assert.Equal(1, GpuTonemapModeTraits.CompositeDrawCount);
    }

    [Fact]
    public void CinematicOnlyTraits_AreGradeWithoutHdrOrBloomSemantics()
    {
        var traits = GpuTonemapModeTraits.For(GpuTonemapMode.CinematicFo3Fnv, true);

        Assert.False(traits.IsHdrDisplayOperator);
        Assert.False(traits.UsesClassicReduction);
        Assert.False(traits.UsesAdaptation);
        Assert.False(traits.AllowsClassicBloom);
        Assert.False(GpuTonemapModeTraits.IsBloomActive(
            GpuTonemapMode.CinematicFo3Fnv, true, true, 999f));
    }

    [Fact]
    public void ModeTraits_ProfilerEffectiveStateDistinguishesHdrFromStandaloneGrade()
    {
        var cinematic = GpuTonemapModeTraits.For(GpuTonemapMode.CinematicFo3Fnv, true);
        var engine = GpuTonemapModeTraits.For(GpuTonemapMode.EngineFo3Fnv, true);
        var killed = GpuTonemapModeTraits.For(GpuTonemapMode.EngineFo3Fnv, false);

        Assert.False(cinematic.IsHdrDisplayOperator);
        Assert.True(engine.IsHdrDisplayOperator);
        Assert.False(killed.IsHdrDisplayOperator);
        Assert.False(killed.AllowsClassicBloom);
    }

    [Fact]
    public void OblivionWeatherFactory_UsesHnamWithoutFnvCinematicGrade()
    {
        var hdr = new WeatherHdr
        {
            EyeAdaptSpeed = 0.25f,
            BlurRadius = 3f,
            BlurPasses = 0f,
            EmissiveMult = 0.75f,
            TargetLum = 0.5f,
            UpperLumClamp = 2f,
            BrightScale = 0f,
            BrightClamp = 0.2f
        };

        var s = GpuTonemapSettings.ForOblivionWeather(hdr);
        Assert.Equal(0.25f, s.EyeAdaptSpeed);
        Assert.Equal(0.5f, s.TargetLum);
        Assert.Equal(0.2f, s.BrightClamp);
        // Sky::UpdateHDRValues substitutes engine defaults for authored <= 0 fields — even on
        // active weathers. An authored 0 never reaches the shader.
        Assert.Equal(GpuTonemapSettings.EngineTes4Defaults.BlurPasses, s.BlurPasses);
        Assert.Equal(GpuTonemapSettings.EngineTes4Defaults.BrightScale, s.BrightScale);
        Assert.Equal(1f, s.Saturation);
        Assert.Equal(1f, s.Contrast);
        Assert.Equal(1f, s.Brightness);
        Assert.Equal(0f, s.TintAmount);
    }

    [Fact]
    public void OblivionWeatherFactory_AllZeroPlaceholderFallsOutToEngineDefaults()
    {
        // Retail DefaultWeather (0x0000015E) has a required but wholly zero HNAM. Per-field
        // substitution handles it with no special case: every field takes the engine default.
        var s = GpuTonemapSettings.ForOblivionWeather(new WeatherHdr());

        Assert.Equal(GpuTonemapSettings.EngineTes4Defaults, s);
    }

    [Fact]
    public void EngineTes4Defaults_MatchRecoveredBlurShaderHdrSettings()
    {
        // Sky::UpdateHDRValues field mapping + Oblivion_default.ini [BlurShaderHDR]
        // (tools/GhidraProject/tes4_hdr_engine_defaults_decompiled.txt).
        var s = GpuTonemapSettings.EngineTes4Defaults;

        Assert.Equal(GpuTonemapMode.EngineFo3Fnv, s.Mode);
        Assert.True(s.BloomEnabled);
        Assert.Equal(0.7f, s.EyeAdaptSpeed); // fEyeAdaptSpeed
        Assert.Equal(4f, s.BlurRadius); // fBlurRadius (FNV widened to 8)
        Assert.Equal(2f, s.BlurPasses); // iNumBlurpasses
        Assert.Equal(1f, s.EmissiveMult); // fEmissiveHDRMult
        Assert.Equal(1.2f, s.TargetLum); // fTargetLUM
        Assert.Equal(1f, s.UpperLumClamp); // fUpperLUMClamp
        Assert.Equal(1.5f, s.BrightScale); // fBrightScale
        Assert.Equal(0.35f, s.BrightClamp); // fBrightClamp
        // Neutral cinematic — TES4 has no IMGS grade; FNV's tint must not leak in.
        Assert.Equal(1f, s.Saturation);
        Assert.Equal(0f, s.TintAmount);
        Assert.Equal(1f, s.SunlightScale);
    }

    [Fact]
    public void RecoveredAdaptationReference_WeightsCurrentScene()
    {
        // Independent CPU oracle for ISHDRADAPT: (1-k)*previous + k*current.
        const float previous = 0.2f;
        const float current = 1.0f;
        const float k = 0.25f;
        var adapted = (1f - k) * previous + k * current;
        Assert.Equal(0.4f, adapted, 6);
    }

    [Fact]
    public void RecoveredCinematicReference_AppliesBrightnessInsideContrast()
    {
        // Independent scalar oracle for Contrast*(Brightness*color-pivot)+pivot.
        const float color = 0.6f;
        const float brightness = 0.9f;
        const float contrast = 1.2f;
        const float pivot = 0.125f;
        var output = contrast * (brightness * color - pivot) + pivot;
        Assert.Equal(0.623f, output, 6);
    }

    [Fact]
    public void ExteriorCinematicMask_IsRetainedAsSourceMetadata()
    {
        var flags = GpuTonemapSettings.EngineExteriorDefaults.CinematicFlags;
        Assert.True(flags.HasFlag(ImageSpaceCinematicFlags.Saturation));
        Assert.True(flags.HasFlag(ImageSpaceCinematicFlags.Contrast));
        Assert.True(flags.HasFlag(ImageSpaceCinematicFlags.Tint));
        Assert.False(flags.HasFlag(ImageSpaceCinematicFlags.Brightness));
    }

    [Fact]
    public void CnamWithoutStoredMask_RetainsCurrentMaskForTelemetry()
    {
        var exterior = GpuTonemapSettings.EngineExteriorDefaults;
        var cnam = new ImageSpaceCinematic
        {
            HasExplicitFlags = false,
            Flags = ImageSpaceCinematicFlags.All,
            Saturation = 0.875f,
            Contrast = 1.02f,
            Brightness = 0f
        };

        var resolved = GpuTonemapSettings.ResolveCinematicFlags(exterior.CinematicFlags, cnam);

        Assert.Equal(exterior.CinematicFlags, resolved);
        Assert.False(resolved.HasFlag(ImageSpaceCinematicFlags.Brightness));
    }

    [Fact]
    public void CinematicWithStoredMask_ReplacesDestinationMaskForTelemetry()
    {
        var explicitMask = new ImageSpaceCinematic
        {
            HasExplicitFlags = true,
            Flags = ImageSpaceCinematicFlags.Brightness
        };

        Assert.Equal(
            ImageSpaceCinematicFlags.Brightness,
            GpuTonemapSettings.ResolveCinematicFlags(
                GpuTonemapSettings.EngineExteriorDefaults.CinematicFlags,
                explicitMask));
    }

    [Fact]
    public void EngineExteriorDefaults_MatchShippedImageSpace0x161()
    {
        var s = GpuTonemapSettings.EngineExteriorDefaults;

        Assert.True(s.BloomEnabled);
        Assert.Equal(8f, s.BlurRadius);
        Assert.Equal(2f, s.BlurPasses);
        Assert.Equal(1.5f, s.BrightScale);
        Assert.Equal(0.35f, s.BrightClamp);
        Assert.Equal(1.2f, s.EmissiveMult);
        Assert.Equal(1.2f, s.TargetLum);
        Assert.Equal(1.3f, s.SunlightScale);
    }

    [Fact]
    public void EngineInteriorDefaults_MatchShippedImageSpace0x160()
    {
        var s = GpuTonemapSettings.EngineInteriorDefaults;

        Assert.True(s.BloomEnabled);
        Assert.Equal(7f, s.BlurRadius);
        Assert.Equal(2f, s.BlurPasses);
        Assert.Equal(2f, s.BrightScale);
        Assert.Equal(0.35f, s.BrightClamp);
        Assert.Equal(1f, s.EmissiveMult);
        Assert.Equal(1.0f, s.TargetLum);
        Assert.Equal(1.5f, s.SunlightScale);
    }

    [Theory]
    [InlineData(BethesdaGame.FalloutNewVegas, false, true, 1.43f)]
    [InlineData(BethesdaGame.Fallout3, false, true, 1.43f)]
    [InlineData(BethesdaGame.FalloutNewVegas, true, true, 1f)]
    [InlineData(BethesdaGame.FalloutNewVegas, false, false, 1f)]
    [InlineData(BethesdaGame.Oblivion, false, true, 1f)]
    public void ResolveSceneSunlightScale_ClassicConsumerIsExteriorHdrDirectionalOnly(
        BethesdaGame game, bool isInterior, bool hdrActive, float expected)
    {
        var settings = GpuTonemapSettings.EngineExteriorDefaults with { SunlightScale = 1.43f };

        Assert.Equal(expected, GpuTonemapSettings.ResolveSceneSunlightScale(
            settings, game, hdrActive, isInterior), 6);
    }

    [Fact]
    public void ResolveSceneSunlightScale_ModernFamilySurvivesDisplayOperatorOverride()
    {
        var settings = GpuTonemapSettings.ModernNeutralDefaults(ImageSpaceModernFamily.Fallout4) with
        {
            Mode = GpuTonemapMode.GammaAces,
            SunlightScale = 2.5f
        };

        Assert.Equal(2.5f, GpuTonemapSettings.ResolveSceneSunlightScale(
            settings, BethesdaGame.Fallout4, true, true));
    }

    [Fact]
    public void ResolveSceneScales_SkyrimEncodePhysicalValuesForGammaScene()
    {
        var gamma = GpuTonemapSettings.ModernNeutralDefaults(ImageSpaceModernFamily.Skyrim) with
        {
            Mode = GpuTonemapMode.GammaAces,
            SunlightScale = 2.7f,
            SkyScale = 0.235f
        };
        var expectedSun = MathF.Pow(2.7f, 1f / 2.2f);
        var expectedSky = MathF.Pow(0.235f, 1f / 2.2f);

        Assert.Equal(expectedSun, GpuTonemapSettings.ResolveSceneSunlightScale(
            gamma, BethesdaGame.Skyrim, true, false), 6);
        Assert.Equal(expectedSky, GpuTonemapSettings.ResolveSceneSkyScale(
            gamma, BethesdaGame.Skyrim, true, false), 6);

        var legacyClamp = gamma with { Mode = GpuTonemapMode.LegacyClamp };
        Assert.Equal(expectedSun, GpuTonemapSettings.ResolveSceneSunlightScale(
            legacyClamp, BethesdaGame.Skyrim, true, false), 6);
        Assert.Equal(expectedSky, GpuTonemapSettings.ResolveSceneSkyScale(
            legacyClamp, BethesdaGame.Skyrim, true, false), 6);

        var modern = gamma with { Mode = GpuTonemapMode.CreationModern };
        Assert.Equal(2.7f, GpuTonemapSettings.ResolveSceneSunlightScale(
            modern, BethesdaGame.Skyrim, true, false));
        Assert.Equal(0.235f, GpuTonemapSettings.ResolveSceneSkyScale(
            modern, BethesdaGame.Skyrim, true, false));

        Assert.Equal(1f, GpuTonemapSettings.ResolveSceneSunlightScale(
            gamma, BethesdaGame.Skyrim, false, false));
        Assert.Equal(1f, GpuTonemapSettings.ResolveSceneSkyScale(
            gamma, BethesdaGame.Skyrim, false, false));
    }

    [Fact]
    public void GammaAcesDefaults_BloomOff()
    {
        // Skyrim/FO4/76 bloom rides their imagespace port; the ACES stand-in must not bloom.
        Assert.False(GpuTonemapSettings.GammaAcesDefaults.BloomEnabled);
        Assert.Equal(1f, GpuTonemapSettings.GammaAcesDefaults.EmissiveMult);
    }

    [Fact]
    public void ResolveEmissiveMult_PreservesAuthoredZero()
    {
        Assert.Equal(0f, GpuTonemapSettings.ResolveEmissiveMult(
            1.2f, 0f, true,
            true));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void ResolveEmissiveMult_InactivePathIsNeutral(
        bool hdrEnabled, bool imagespaceModifiersEnabled)
    {
        Assert.Equal(1f, GpuTonemapSettings.ResolveEmissiveMult(
            1.2f, 4f, hdrEnabled,
            imagespaceModifiersEnabled));
    }

    [Fact]
    public void ResolveEmissiveMult_MissingAuthoredValueUsesFamilyDefault()
    {
        Assert.Equal(1.2f, GpuTonemapSettings.ResolveEmissiveMult(
            1.2f, null, true,
            true));
    }

    [Fact]
    public void ApplyOverrides_TonemapOperatorDoesNotChangeEmissiveMult()
    {
        var previous = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_TONEMAP");
        try
        {
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_TONEMAP", "aces");
            var s = GpuTonemapSettings.ApplyOverrides(
                GpuTonemapSettings.EngineExteriorDefaults with { EmissiveMult = 0f });

            Assert.Equal(GpuTonemapMode.GammaAces, s.Mode);
            Assert.Equal(0f, s.EmissiveMult);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_TONEMAP", previous);
        }
    }

    [Theory]
    [InlineData(BethesdaGame.FalloutNewVegas, (int)GpuTonemapMode.EngineFo3Fnv, true)]
    [InlineData(BethesdaGame.Fallout3, (int)GpuTonemapMode.EngineFo3Fnv, true)]
    [InlineData(BethesdaGame.Oblivion, (int)GpuTonemapMode.EngineFo3Fnv, true)]
    [InlineData(BethesdaGame.Skyrim, (int)GpuTonemapMode.GammaAces, false)]
    [InlineData(BethesdaGame.Morrowind, (int)GpuTonemapMode.LegacyClamp, false)]
    public void ForGame_BloomFollowsEngineMode(BethesdaGame game, int expectedMode, bool expectedBloom)
    {
        var s = GpuTonemapSettings.ForGame(game);

        Assert.Equal((GpuTonemapMode)expectedMode, s.Mode);
        Assert.Equal(expectedBloom, s.BloomEnabled);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("off")]
    [InlineData("OFF")]
    public void ApplyOverrides_BloomKillSwitch(string value)
    {
        var previous = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_BLOOM");
        try
        {
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_BLOOM", value);
            var s = GpuTonemapSettings.ApplyOverrides(GpuTonemapSettings.EngineExteriorDefaults);
            Assert.False(s.BloomEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_BLOOM", previous);
        }
    }

    [Fact]
    public void ApplyOverrides_NoEnv_KeepsBloom()
    {
        var previous = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_BLOOM");
        try
        {
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_BLOOM", null);
            var s = GpuTonemapSettings.ApplyOverrides(GpuTonemapSettings.EngineExteriorDefaults);
            Assert.True(s.BloomEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_BLOOM", previous);
        }
    }

    [Fact]
    public void ApplyOverrides_ForceOn_EnablesBloomOnAcesPreset()
    {
        var previous = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_BLOOM");
        try
        {
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_BLOOM", "1");
            var s = GpuTonemapSettings.ApplyOverrides(GpuTonemapSettings.GammaAcesDefaults);
            Assert.True(s.BloomEnabled);
            // The ACES preset carries usable engine bloom params so the force-on actually blooms.
            Assert.True(s.BrightScale > 0f);
            Assert.True(s.BlurPasses >= 1f);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_BLOOM", previous);
        }
    }

    [Fact]
    public void ApplyOverrides_ForceOnDoesNotBorrowClassicBloomForModernMode()
    {
        var previous = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_BLOOM");
        try
        {
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_BLOOM", "1");
            var s = GpuTonemapSettings.ApplyOverrides(
                GpuTonemapSettings.ModernNeutralDefaults(ImageSpaceModernFamily.Fallout4));
            Assert.False(s.BloomEnabled);
            Assert.Equal(1f, s.AdaptFactor);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_BLOOM", previous);
        }
    }

    [Fact]
    public void ModernPipeline_IsDefaultOffAndExplicitlyOptedIn()
    {
        var previous = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_MODERN_IMAGESPACE");
        var previousTonemap = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_TONEMAP");
        try
        {
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_MODERN_IMAGESPACE", null);
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_TONEMAP", null);
            var skyrimDefault = GpuTonemapSettings.ForGame(BethesdaGame.Skyrim);
            Assert.Equal(GpuTonemapMode.GammaAces, skyrimDefault.Mode);
            Assert.Equal(ImageSpaceModernFamily.Skyrim, skyrimDefault.ModernFamily);
            Assert.Equal(GpuTonemapMode.GammaAces, GpuTonemapSettings.ForGame(BethesdaGame.Fallout4).Mode);
            Assert.Null(GpuTonemapSettings.ForGame(BethesdaGame.Fallout4).ModernFamily);
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_MODERN_IMAGESPACE", "1");
            var skyrimEnabled = GpuTonemapSettings.ForGame(BethesdaGame.Skyrim);
            Assert.Equal(GpuTonemapMode.CreationModern, skyrimEnabled.Mode);
            Assert.Equal(ImageSpaceModernFamily.Skyrim, skyrimEnabled.ModernFamily);
            var enabled = GpuTonemapSettings.ForGame(BethesdaGame.Fallout4);
            Assert.Equal(GpuTonemapMode.CreationModern, enabled.Mode);
            Assert.Equal(ImageSpaceModernFamily.Fallout4, enabled.ModernFamily);
            Assert.False(enabled.BloomEnabled); // modern bloom topology is not borrowed from FNV
        }
        finally
        {
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_MODERN_IMAGESPACE", previous);
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_TONEMAP", previousTonemap);
        }
    }

    [Fact]
    public void ModernReference_UsesAuthoredExposureClampAndCinematicOnly()
    {
        var settings = GpuTonemapSettings.ModernNeutralDefaults(ImageSpaceModernFamily.Fallout4) with
        {
            AutoExposureMin = 0.5f,
            AutoExposureMax = 2f,
            MiddleGray = 0.25f,
            Saturation = 1f,
            Brightness = 1f,
            Contrast = 1f,
            TintAmount = 0f
        };

        // Independent semantic oracle: clamp(middleGray / adaptedLuma, min, max).
        Assert.Equal(0.5f, ModernImageSpaceReference.ResolveExposure(1f, settings), 6);
        Assert.Equal(2f, ModernImageSpaceReference.ResolveExposure(0.01f, settings), 6);
        var output = ModernImageSpaceReference.Apply(new Vector3(0.1f, 0.2f, 0.3f), 0.125f, settings);
        Assert.Equal(0.2f, output.X, 6);
        Assert.Equal(0.4f, output.Y, 6);
        Assert.Equal(0.6f, output.Z, 6);
    }
}