using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>How the tonemap pass maps the float scene target to the 8-bit display.</summary>
internal enum GpuTonemapMode
{
    /// <summary>
    ///     Plain clamp in the display composite. The static <c>FALLOUT_VIEWER_HDR=0</c> kill-switch
    ///     additionally restores the legacy 8-bit scene target; a live GUI mode change leaves the
    ///     float target in place.
    /// </summary>
    LegacyClamp = 0,

    /// <summary>
    ///     Gamma-corrected ACES filmic: decode 2.2 → exposure → curve → encode 1/2.2. Stand-in for the
    ///     Creation-era games (Skyrim/FO4/FO76) until their imagespace stage is ported. The decode/encode
    ///     pair fixes the "washed out" look of running ACES directly on the gamma-space scene values.
    /// </summary>
    GammaAces = 1,

    /// <summary>
    ///     The FO3/FNV engine HDR stage (decompile-grounded, see
    ///     docs/research/fnv_engine_hdr_imagespace.md): steady-state eye-adapt exposure
    ///     <c>TargetLUM / max(sum(adaptedAvgColor), TargetLUM)</c> plus the BrightPassBlur bloom term
    ///     <c>bloom·(0.5/denom)</c>, followed by the IMGS cinematic transform (saturation → tint →
    ///     contrast/brightness). Operates on gamma-space values exactly like the recovered FNV path.
    ///     FO3 and Oblivion currently share this classic architecture pending their binary-oracle gates;
    ///     Oblivion supplies WTHR HNAM parameters and a neutral cinematic grade.
    /// </summary>
    EngineFo3Fnv = 2,

    /// <summary>
    ///     Default-off Skyrim/FO4-family increment. Authored auto exposure and cinematic values are
    ///     active; unrecovered filmic/LUT/bloom topology is deliberately identity/disabled.
    /// </summary>
    CreationModern = 3,

    /// <summary>
    ///     FO3/FNV's non-HDR standalone cinematic effect: clamp the scene to its LDR input first,
    ///     then apply the currently supported IMGS/IMAD saturation, tint, contrast, and brightness
    ///     grade. It deliberately performs no exposure, adaptation, bloom, or HDR scene scaling.
    /// </summary>
    CinematicFo3Fnv = 4,

    /// <summary>
    ///     The classic launcher's middle "Bloom" state (
    ///     <c>
    ///         !bDoHighDynamicRange &amp;&amp;
    ///         bUseBlurShader
    ///     </c>
    ///     ): SDR display + bloom. Retail's LDR <c>[BlurShader]</c> topology is
    ///     UNRECOVERED (the shipped section has no bright-pass parameters at all), so this is a
    ///     documented stand-in: the classic reduction/adapt/bright-pass chain still runs — the
    ///     bright pass needs the adapted average for its threshold — but the composite clamps the
    ///     scene at neutral exposure (no eye-adapt scaling, no HDR display operator) and adds the
    ///     bloom term over it, then applies the classic grade (neutral for Oblivion — no IMGS).
    /// </summary>
    ClassicSdrBloom = 5
}

/// <summary>
///     The viewer GUI's post-processing selector — the retail launcher's three-way radio
///     (None / Bloom / HDR; <c>OblivionLauncher</c>/<c>Fallout3Launcher</c>/<c>FalloutNVLauncher</c>
///     ship the identical block). Replaces the old two-ToggleSwitch approximation: retail has no
///     HDR-with-bloom-off launcher state, so bloom-under-HDR remains a diagnostic-only gate.
/// </summary>
internal enum GpuTonemapGuiMode
{
    /// <summary>The launcher's "None": SDR clamp (FO3/FNV keep their standalone cinematic grade).</summary>
    Sdr = 0,

    /// <summary>The launcher's "Bloom": SDR + classic bloom (<see cref="GpuTonemapMode.ClassicSdrBloom" />).</summary>
    SdrBloom = 1,

    /// <summary>The launcher's "HDR": each family's engine HDR operator.</summary>
    Hdr = 2
}

/// <summary>
///     Pure execution traits shared by the D3D12 pass and profiler. The fullscreen composite always
///     runs; these flags describe only the optional work recorded before it.
/// </summary>
internal readonly record struct GpuTonemapModeTraits(
    bool IsHdrDisplayOperator,
    bool UsesClassicReduction,
    bool UsesAdaptation,
    bool AllowsClassicBloom)
{
    internal const int CompositeDrawCount = 1;

    internal static GpuTonemapModeTraits For(GpuTonemapMode mode, bool enabled)
    {
        if (!enabled) return default;
        return mode switch
        {
            GpuTonemapMode.GammaAces => new GpuTonemapModeTraits(true, false, false, false),
            GpuTonemapMode.EngineFo3Fnv => new GpuTonemapModeTraits(true, true, true, true),
            GpuTonemapMode.CreationModern => new GpuTonemapModeTraits(true, false, true, false),
            // The standalone cinematic effect is an LDR grade, not an HDR display operator.
            GpuTonemapMode.CinematicFo3Fnv => new GpuTonemapModeTraits(false, false, false, false),
            // SDR display, but the classic reduction + adaptation still run: the bloom
            // bright-pass thresholds against the adapted average (the mode-5 composite itself
            // never samples it, so adaptation cannot change the base image).
            GpuTonemapMode.ClassicSdrBloom => new GpuTonemapModeTraits(false, true, true, true),
            _ => default
        };
    }

    internal static bool IsBloomActive(
        GpuTonemapMode mode, bool enabled, bool bloomEnabled, float brightScale)
    {
        return For(mode, enabled).AllowsClassicBloom && bloomEnabled && brightScale > 0f;
    }
}

/// <summary>Pure scheduling decision for the optional draws before the fullscreen composite.</summary>
internal readonly record struct GpuTonemapExecutionPlan(
    bool EngineMode,
    bool AdaptiveMode,
    bool BloomActive)
{
    internal static GpuTonemapExecutionPlan Create(
        bool enabled, GpuTonemapMode mode, bool bloomEnabled, float brightScale)
    {
        var traits = GpuTonemapModeTraits.For(mode, enabled);
        return new GpuTonemapExecutionPlan(
            traits.UsesClassicReduction,
            traits.UsesAdaptation,
            traits.AllowsClassicBloom && bloomEnabled && brightScale > 0f);
    }
}

/// <summary>
///     Per-frame tonemap parameters. Engine-mode fields mirror the FO3/FNV IMGS DNAM values that feed
///     the engine's ISHDR* shader chain; defaults are the shipped <c>DefaultImageSpaceExterior</c>
///     (0x161) / <c>DefaultImageSpaceInterior</c> (0x160) records from FalloutNV.esm.
/// </summary>
internal readonly record struct GpuTonemapSettings
{
    public GpuTonemapMode Mode { get; init; }

    /// <summary>Debug multiplier applied before the operator (FALLOUT_VIEWER_EXPOSURE; 1 = neutral).</summary>
    public float Exposure { get; init; }

    /// <summary>IMGS HDR: the luminance the eye adapts toward; exposure = TargetLum/max(L, TargetLum).</summary>
    public float TargetLum { get; init; }

    /// <summary>IMGS HDR: length clamp on the adapted average scene color.</summary>
    public float UpperLumClamp { get; init; }

    /// <summary>
    ///     Temporal eye-adaptation blend factor for THIS frame: the weight of the CURRENT scene
    ///     average (engine ADAPT pass: <c>k = EyeAdaptSpeed^clamp(15·dt, 0, 1)</c>, new = (1−k)·prev +
    ///     k·current). 1 = instant adaptation — the right value for single-frame headless captures
    ///     and the first live frame; the live frame path computes it from the frame delta +
    ///     <see cref="EyeAdaptSpeed" />. It also stabilizes the modern path's still-provisional sparse
    ///     average against camera motion.
    /// </summary>
    public float AdaptFactor { get; init; }

    /// <summary>
    ///     Stable identity of the explicit eye-adaptation clear generation. Routine classic CELL,
    ///     worldspace, image-space, weather, interior, and modifier changes deliberately keep the same
    ///     value, matching the recovered FNV <c>bClearAdaptedLight</c> contract. The GPU pass separately
    ///     invalidates history for adaptive-mode and render-target lifecycle changes.
    /// </summary>
    public ulong HistoryKey { get; init; }

    /// <summary>IMGS HDR: eye adaptation speed (temporal blend base; FNV defaults 0.9).</summary>
    public float EyeAdaptSpeed { get; init; }

    /// <summary>
    ///     IMGS HDR: Lighting30 material-emittance/glow brightness multiplier (hdrData[3]). The recovered
    ///     FNV engine reads it only from Lighting30Shader::SetupGeometryConstants_Emittance; NoLighting
    ///     effects do not consume it. The reference Lighting30 path reads it from the atmosphere CB.
    ///     Shipped FNV values are 1.0 interior / 1.2 exterior.
    /// </summary>
    public float EmissiveMult { get; init; }

    /// <summary>IMGS Cinematic: 0 = grayscale, 1 = full color.</summary>
    public float Saturation { get; init; }

    /// <summary>IMGS Cinematic: contrast pivot ("Avg Lum Value").</summary>
    public float ContrastAvgLum { get; init; }

    /// <summary>IMGS Cinematic: contrast multiplier.</summary>
    public float Contrast { get; init; }

    /// <summary>IMGS Cinematic: brightness multiplier.</summary>
    public float Brightness { get; init; }

    /// <summary>
    ///     FO3/FNV authored cinematic-enable mask retained from IMGS for lossless round-tripping and
    ///     telemetry. The recovered shipped HDR/cinematic pixel shaders do not consume these enables;
    ///     classic grading therefore applies all four authored values unconditionally.
    /// </summary>
    public ImageSpaceCinematicFlags CinematicFlags { get; init; }

    public float TintR { get; init; }

    public float TintG { get; init; }

    public float TintB { get; init; }

    /// <summary>IMGS Cinematic tint value: blend toward luma·tintColor (the FNV golden-tan filter ≈ 0.6).</summary>
    public float TintAmount { get; init; }

    /// <summary>
    ///     BrightPassBlur bloom stage on/off. Engine-mode only this cut (Skyrim/FO4 bloom rides their
    ///     imagespace port). Runtime-flippable with no pipeline rebuild — the pass is simply skipped.
    /// </summary>
    public bool BloomEnabled { get; init; }

    /// <summary>
    ///     IMGS HDR: diagonal blur-row radius in bloom texels. The recovered FNV path truncates the
    ///     authored float, then clamps to 1..7 to select the 3..15-tap shader family.
    /// </summary>
    public float BlurRadius { get; init; }

    /// <summary>
    ///     Authored IMGS/HNAM blur-pass scalar, retained losslessly. The recovered runtime topology has
    ///     one DS16 and one BPBLUR draw; this value is not used as a repeated-pass count.
    /// </summary>
    public float BlurPasses { get; init; }

    /// <summary>IMGS HDR: bright-pass gain applied per tap after the threshold subtract.</summary>
    public float BrightScale { get; init; }

    /// <summary>IMGS HDR: bright-pass threshold — per tap <c>max(src − BrightClamp, 0) · BrightScale</c>.</summary>
    public float BrightClamp { get; init; }

    public ImageSpaceModernFamily? ModernFamily { get; init; }
    public float TonemapE { get; init; }
    public float AutoExposureMin { get; init; }
    public float AutoExposureMax { get; init; }
    public float MiddleGray { get; init; }
    public float White { get; init; }
    public float EyeAdaptStrength { get; init; }
    public float ReceiveBloomThreshold { get; init; }

    /// <summary>
    ///     Scene directional-light multiplier. FO3/FNV source this from classic IMGS
    ///     <c>hdrData[11] SunlightDimmer</c>; Creation-era records call the corresponding field
    ///     SunlightScale. The recovered classic consumer is exterior HDR directional light only;
    ///     it is evaluated before the display imagespace pass and therefore remains active during
    ///     tonemap-operator A/Bs.
    /// </summary>
    public float SunlightScale { get; init; }

    /// <summary>
    ///     Scene GRASS multiplier — classic IMGS <c>hdrData[12] GrassDimmer</c>. This is not a
    ///     display term: FO3/FNV's <c>TallGrassShader::SetupGeometryConstants</c> writes it into the
    ///     grass vertex program's <c>AddlParams.x</c> (<c>c7.x</c>), which the shipped
    ///     <c>GRASS2000/2002.vso</c> apply as the whole sun-term multiplier
    ///     (<c>mul oT3.xyz, r1, c7.x</c>). It is deliberately separate from
    ///     <see cref="SunlightScale" />, which grass never receives — that one is applied inside
    ///     <c>BSShaderLightingProperty</c>, i.e. to statics/actors/LAND only.
    /// </summary>
    public float GrassScale { get; init; }

    public float SkyScale { get; init; }
    public string? LutTexturePath { get; init; }

    /// <summary>Shipped FNV DefaultImageSpaceExterior (0x161) with neutral exposure.</summary>
    public static GpuTonemapSettings EngineExteriorDefaults { get; } = new()
    {
        Mode = GpuTonemapMode.EngineFo3Fnv,
        Exposure = 1f,
        EyeAdaptSpeed = 0.9f,
        EmissiveMult = 1.2f,
        TargetLum = 1.2f,
        UpperLumClamp = 1.0f,
        Saturation = 0.85f,
        ContrastAvgLum = 0.125f,
        Contrast = 1.2f,
        Brightness = 0.9f,
        // Retain the shipped exterior metadata. The shipped composite shader does not read this mask.
        CinematicFlags = ImageSpaceCinematicFlags.Saturation |
                         ImageSpaceCinematicFlags.Contrast |
                         ImageSpaceCinematicFlags.Tint,
        TintR = 0.603922f,
        TintG = 0.537255f,
        TintB = 0.388235f,
        TintAmount = 0.6f,
        BloomEnabled = true,
        BlurRadius = 8f,
        BlurPasses = 2f,
        BrightScale = 1.5f,
        BrightClamp = 0.35f,
        SunlightScale = 1.3f,
        GrassScale = 1.3f
    };

    /// <summary>Shipped FNV DefaultImageSpaceInterior (0x160): neutral cinematic.</summary>
    public static GpuTonemapSettings EngineInteriorDefaults { get; } = new()
    {
        Mode = GpuTonemapMode.EngineFo3Fnv,
        Exposure = 1f,
        EyeAdaptSpeed = 0.9f,
        EmissiveMult = 1f,
        TargetLum = 1.0f,
        UpperLumClamp = 1.0f,
        Saturation = 1f,
        ContrastAvgLum = 0.5f,
        Contrast = 1f,
        Brightness = 1f,
        CinematicFlags = ImageSpaceCinematicFlags.All,
        TintR = 1f,
        TintG = 1f,
        TintB = 1f,
        TintAmount = 0f,
        BloomEnabled = true,
        BlurRadius = 7f,
        BlurPasses = 2f,
        BrightScale = 2f,
        BrightClamp = 0.35f,
        SunlightScale = 1.5f,
        GrassScale = 1.5f
    };

    /// <summary>
    ///     TES4 engine-default HDR values. Semantics + field mapping recovered from the remaster-PDB
    ///     decompile of <c>Sky::UpdateHDRValues</c> (per-field "authored ≤ 0 → engine-default
    ///     Setting" substitution); numeric defaults are the shipped <c>Oblivion_default.ini</c>
    ///     <c>[BlurShaderHDR]</c> values — see
    ///     <c>tools/GhidraProject/tes4_hdr_engine_defaults_decompiled.txt</c>. The bright-pass trio
    ///     matches FNV (which inherited TES4's settings); TES4 differs on BlurRadius 4 (FNV 8),
    ///     EyeAdaptSpeed 0.7, and UpperLumClamp 1.0. Cinematic grade is neutral — TES4 has no IMGS,
    ///     so none of FNV's exterior tint/saturation may leak in.
    /// </summary>
    public static GpuTonemapSettings EngineTes4Defaults { get; } = new()
    {
        Mode = GpuTonemapMode.EngineFo3Fnv,
        Exposure = 1f,
        EyeAdaptSpeed = 0.7f, // fEyeAdaptSpeed
        EmissiveMult = 1f, // fEmissiveHDRMult
        TargetLum = 1.2f, // fTargetLUM
        UpperLumClamp = 1f, // fUpperLUMClamp
        Saturation = 1f,
        ContrastAvgLum = 0.5f,
        Contrast = 1f,
        Brightness = 1f,
        CinematicFlags = ImageSpaceCinematicFlags.All,
        TintR = 1f,
        TintG = 1f,
        TintB = 1f,
        TintAmount = 0f,
        BloomEnabled = true,
        BlurRadius = 4f, // fBlurRadius
        BlurPasses = 2f, // iNumBlurpasses
        BrightScale = 1.5f, // fBrightScale
        BrightClamp = 0.35f, // fBrightClamp
        // TES4 HNAM's SunlightDimmer is retained on the record; this bounded consumer is recovered
        // only for the FO3/FNV scene-light path, so stay neutral here.
        SunlightScale = 1f,
        GrassScale = 1f
    };

    public static GpuTonemapSettings GammaAcesDefaults { get; } = new()
    {
        Mode = GpuTonemapMode.GammaAces,
        Exposure = 1f,
        EmissiveMult = 1f,
        TargetLum = 1f,
        UpperLumClamp = 1f,
        Saturation = 1f,
        ContrastAvgLum = 0.5f,
        Contrast = 1f,
        Brightness = 1f,
        CinematicFlags = ImageSpaceCinematicFlags.All,
        TintR = 1f,
        TintG = 1f,
        TintB = 1f,
        TintAmount = 0f,
        // Bloom params carry the engine exterior values but stay DISABLED: the ACES stand-in has no
        // bloom until the Skyrim/FO4 imagespace port. FALLOUT_VIEWER_BLOOM=1 (+TONEMAP=engine)
        // force-enables for NIF-harness A/Bs.
        BloomEnabled = false,
        BlurRadius = 8f,
        BlurPasses = 2f,
        BrightScale = 1.5f,
        BrightClamp = 0.35f,
        SunlightScale = 1f,
        GrassScale = 1f,
        SkyScale = 1f
    };

    internal static bool ModernPipelineEnabled
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_MODERN_IMAGESPACE");
            return value == "1" || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    ///     Resolves the recovered Lighting30 material-emittance multiplier without conflating an authored
    ///     zero with a missing value. Disabling either HDR itself or imagespace modifiers restores the
    ///     neutral multiplier because the engine applies this global only in its HDR emittance path.
    /// </summary>
    internal static float ResolveEmissiveMult(
        float familyDefault, float? authoredValue, bool hdrEnabled, bool imagespaceModifiersEnabled)
    {
        return !hdrEnabled || !imagespaceModifiersEnabled ? 1f : authoredValue ?? familyDefault;
    }

    /// <summary>
    ///     Whether the GUI's HDR-off state selects the standalone classic cinematic effect. The
    ///     explicit game check is intentional: Oblivion shares parts of the classic HDR pass but has
    ///     no IMGS record type or standalone cinematic effect.
    /// </summary>
    internal static bool UsesCinematicOnly(
        BethesdaGame game, bool guiHdrEnabled, bool tonemapAvailable)
    {
        return tonemapAvailable && !guiHdrEnabled &&
               game is BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas;
    }

    /// <summary>
    ///     Applies live viewer gates after base IMGS, weather IMAD, and diagnostic overrides have
    ///     resolved. Centralizing this final step prevents an early-return path or an HDR IMAD channel
    ///     from leaking bloom/emissive scene scaling back into the HDR-off cinematic effect.
    /// </summary>
    internal static GpuTonemapSettings FinalizeViewerPostProcessing(
        GpuTonemapSettings settings,
        BethesdaGame game,
        GpuTonemapGuiMode guiMode,
        bool tonemapAvailable,
        GpuTonemapMode? operatorOverride,
        bool guiBloomEnabled,
        ulong historyKey)
    {
        if (!tonemapAvailable)
        {
            return settings with
            {
                // FALLOUT_VIEWER_HDR=0 removes the float target/tonemap infrastructure, so no
                // display-operator override can be honored on this path.
                Mode = GpuTonemapMode.LegacyClamp,
                BloomEnabled = false,
                EmissiveMult = 1f,
                HistoryKey = historyKey
            };
        }

        if (guiMode == GpuTonemapGuiMode.SdrBloom)
        {
            // The launcher's middle state: SDR display + classic bloom. Scene-side HDR
            // multipliers neutralize exactly like HDR-off (retail's parallel [BlurShader] set
            // ships fSunlightDimmer=1.0 where [BlurShaderHDR] ships 1.3).
            var mode = operatorOverride ?? GpuTonemapMode.ClassicSdrBloom;
            return settings with
            {
                Mode = mode,
                BloomEnabled = mode is GpuTonemapMode.ClassicSdrBloom or GpuTonemapMode.EngineFo3Fnv
                               && settings.BloomEnabled && guiBloomEnabled,
                EmissiveMult = 1f,
                HistoryKey = historyKey
            };
        }

        if (guiMode == GpuTonemapGuiMode.Sdr)
        {
            var mode = operatorOverride ?? (UsesCinematicOnly(game, false, tonemapAvailable)
                ? GpuTonemapMode.CinematicFo3Fnv
                : GpuTonemapMode.LegacyClamp);
            return settings with
            {
                // An explicit FALLOUT_VIEWER_TONEMAP override is a diagnostic display-pass A/B and
                // wins over the GUI's automatic cinematic choice. The GUI engine-HDR gate still
                // neutralizes scene-side HDR multipliers.
                Mode = mode,
                BloomEnabled = mode == GpuTonemapMode.EngineFo3Fnv
                               && settings.BloomEnabled && guiBloomEnabled,
                EmissiveMult = 1f,
                HistoryKey = historyKey
            };
        }

        if (operatorOverride is { } forcedMode)
        {
            // Display-operator A/Bs do not change the engine-HDR scene gate. Preserve authored
            // scene multipliers while truthfully disabling classic bloom for non-engine operators.
            return settings with
            {
                Mode = forcedMode,
                BloomEnabled = forcedMode == GpuTonemapMode.EngineFo3Fnv
                               && settings.BloomEnabled && guiBloomEnabled,
                HistoryKey = historyKey
            };
        }

        return settings with
        {
            BloomEnabled = settings.BloomEnabled && guiBloomEnabled,
            HistoryKey = historyKey
        };
    }

    /// <summary>Returns a neutral imagespace overlay while retaining unrelated HDR parameters.</summary>
    internal static GpuTonemapSettings WithoutImagespaceModifiers(GpuTonemapSettings settings)
    {
        return settings with
        {
            EmissiveMult = 1f,
            Saturation = 1f,
            ContrastAvgLum = 0.5f,
            Contrast = 1f,
            Brightness = 1f,
            TintR = 1f,
            TintG = 1f,
            TintB = 1f,
            TintAmount = 0f,
            CinematicFlags = ImageSpaceCinematicFlags.All,
            SunlightScale = 1f,
            GrassScale = 1f,
            SkyScale = 1f
        };
    }

    /// <summary>
    ///     Resolves the scene-light consumer separately from the authored/retained IMGS value.
    ///     FO3/FNV's 3.x and legacy 1.x/2.x directional setup both guard SunlightDimmer with
    ///     <c>bHDR &amp;&amp; !bInterior</c>. Creation-era SunlightScale keeps its existing scene route,
    ///     identified by retained family metadata so a display-operator A/B does not suppress it.
    /// </summary>
    internal static float ResolveSceneSunlightScale(
        GpuTonemapSettings settings, BethesdaGame game, bool hdrActive, bool isInterior)
    {
        if (!hdrActive) return 1f;
        if (settings.ModernFamily is not null)
        {
            return game == BethesdaGame.Skyrim && settings.Mode != GpuTonemapMode.CreationModern
                ? EncodePhysicalScaleForGammaScene(settings.SunlightScale)
                : settings.SunlightScale;
        }

        var classicExterior = !isInterior && GameProfiles.For(game).UsesClassicHdrImagespace;
        return classicExterior ? settings.SunlightScale : 1f;
    }

    /// <summary>
    ///     Resolves the GRASS sun multiplier. Same <c>bHDR &amp;&amp; !bInterior</c> guard as
    ///     <see cref="ResolveSceneSunlightScale" /> — the write lives in the same
    ///     <c>TallGrassShader::SetupGeometryConstants</c> HDR branch — but it is a distinct constant
    ///     and it REPLACES rather than compounds the sunlight dimmer: retail hands the grass program
    ///     the sun light's colour raw and multiplies the sun term by this value alone. Only the
    ///     classic FO3/FNV grass program consumes it; every other game returns neutral.
    /// </summary>
    internal static float ResolveSceneGrassScale(
        GpuTonemapSettings settings, BethesdaGame game, bool hdrActive, bool isInterior)
    {
        if (!hdrActive || isInterior) return 1f;
        return game is BethesdaGame.FalloutNewVegas or BethesdaGame.Fallout3
            ? settings.GrassScale
            : 1f;
    }

    /// <summary>
    ///     Resolves the Creation-era non-imagespace sky multiplier separately from the display operator.
    ///     Skyrim's default fallback scene is gamma-encoded, so a physical linear multiplier must be
    ///     gamma-encoded before it is applied to the sky colors. CreationModern retains the authored raw
    ///     value because that diagnostic path owns its own (still incomplete) imagespace equation.
    /// </summary>
    internal static float ResolveSceneSkyScale(
        GpuTonemapSettings settings, BethesdaGame game, bool hdrActive, bool isInterior)
    {
        if (!hdrActive || settings.ModernFamily is null) return 1f;
        if (game == BethesdaGame.Skyrim && settings.Mode != GpuTonemapMode.CreationModern)
        {
            return EncodePhysicalScaleForGammaScene(settings.SkyScale);
        }

        return settings.Mode == GpuTonemapMode.CreationModern ? settings.SkyScale : 1f;
    }

    /// <summary>
    ///     The GammaAces input is gamma-encoded and is linearized with pow(color, 2.2) in the composite.
    ///     Encoding a physical scale by 1/2.2 therefore makes that later linear value change by exactly
    ///     the authored multiplier instead of accidentally raising the multiplier itself to 2.2.
    /// </summary>
    internal static float EncodePhysicalScaleForGammaScene(float authoredScale)
    {
        if (!float.IsFinite(authoredScale)) return 1f;
        return MathF.Pow(MathF.Max(authoredScale, 0f), 1f / 2.2f);
    }

    public static GpuTonemapSettings ModernNeutralDefaults(ImageSpaceModernFamily family)
    {
        return GammaAcesDefaults with
        {
            Mode = GpuTonemapMode.CreationModern,
            ModernFamily = family,
            // The recovered manager path proves the authored exposure values are retained, but not
            // Skyrim/FO4's temporal response equation. Instant replacement is the neutral behavior:
            // it avoids silently borrowing the classic FO3/FNV adaptation curve.
            AdaptFactor = 1f,
            UpperLumClamp = 65504f,
            AutoExposureMin = 1f,
            AutoExposureMax = 1f,
            MiddleGray = 1f,
            White = 1f,
            EyeAdaptStrength = 1f,
            SunlightScale = 1f,
            GrassScale = 1f,
            SkyScale = 1f,
            BloomEnabled = false
        };
    }

    public static GpuTonemapSettings ApplyModernImageSpace(
        GpuTonemapSettings settings, ImageSpaceRecord imageSpace)
    {
        if (imageSpace.ModernHdr is { } hdr)
        {
            settings = settings with
            {
                ModernFamily = hdr.Family,
                EyeAdaptSpeed = hdr.EyeAdaptSpeed,
                BlurRadius = hdr.BloomBlurRadius ?? settings.BlurRadius,
                BrightClamp = hdr.BloomThreshold,
                BrightScale = hdr.BloomScale,
                TonemapE = hdr.TonemapE ?? 0f,
                AutoExposureMax = hdr.AutoExposureMax ?? 1f,
                AutoExposureMin = hdr.AutoExposureMin ?? 1f,
                MiddleGray = hdr.MiddleGray ?? 1f,
                White = hdr.White ?? 1f,
                EyeAdaptStrength = hdr.EyeAdaptStrength ?? 1f,
                ReceiveBloomThreshold = hdr.ReceiveBloomThreshold ?? 0f,
                SunlightScale = hdr.SunlightScale,
                GrassScale = hdr.SunlightScale,
                SkyScale = hdr.SkyScale,
                // The recovered FO4 code proves these values are blended and handed to the manager,
                // but not the bloom render topology. Keep it disabled until that shader oracle lands.
                BloomEnabled = false
            };
        }

        if (imageSpace.Cinematic is { } cinematic)
        {
            settings = settings with
            {
                Saturation = cinematic.Saturation,
                Brightness = cinematic.Brightness,
                Contrast = cinematic.Contrast,
                ContrastAvgLum = cinematic.ContrastAvgLum,
                CinematicFlags = ResolveCinematicFlags(settings.CinematicFlags, cinematic)
            };
        }

        if (imageSpace.Tint is { } tint)
        {
            settings = settings with
            {
                TintAmount = tint.Amount,
                TintR = tint.Red,
                TintG = tint.Green,
                TintB = tint.Blue
            };
        }

        return settings with { LutTexturePath = imageSpace.LutTexturePath };
    }

    /// <summary>
    ///     Lossless loader-state projection for telemetry: the 148- and 152-byte classic DNAM layouts
    ///     store a mask, while the 132-byte DNAM and Creation-era CNAM/packed cinematic layouts do not.
    ///     Absent source metadata retains the current value. The shipped classic composite shader does
    ///     not consume the resolved value.
    /// </summary>
    internal static ImageSpaceCinematicFlags ResolveCinematicFlags(
        ImageSpaceCinematicFlags current,
        ImageSpaceCinematic cinematic)
    {
        return cinematic.HasExplicitFlags ? cinematic.Flags : current;
    }

    /// <summary>
    ///     Default operator per game family: FO3/FNV = their IMGS-driven engine HDR stage; Oblivion =
    ///     the same recovered HDR operator with neutral cinematic grading (its values come from WTHR HNAM),
    ///     Morrowind = legacy clamp (pre-HDR engine), everything else = gamma-corrected ACES.
    ///     <c>FALLOUT_VIEWER_TONEMAP=off|aces|engine|modern</c> overrides for A/Bs.
    /// </summary>
    public static GpuTonemapSettings ForGame(BethesdaGame game, bool interior = false)
    {
        var settings = game switch
        {
            BethesdaGame.Morrowind => GammaAcesDefaults with { Mode = GpuTonemapMode.LegacyClamp },
            BethesdaGame.Oblivion => ForOblivionWeather(null),
            BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas =>
                interior ? EngineInteriorDefaults : EngineExteriorDefaults,
            // Retain Skyrim's semantic family even while GammaAces remains the default display
            // operator. This lets its non-imagespace SunlightScale/SkyScale resolve independently
            // without enabling the unrecovered cinematic/exposure pipeline.
            BethesdaGame.Skyrim => ModernNeutralDefaults(ImageSpaceModernFamily.Skyrim) with
            {
                Mode = ModernPipelineEnabled ? GpuTonemapMode.CreationModern : GpuTonemapMode.GammaAces
            },
            BethesdaGame.Fallout4 or BethesdaGame.Fallout76 when ModernPipelineEnabled =>
                ModernNeutralDefaults(ImageSpaceModernFamily.Fallout4),
            _ => GammaAcesDefaults
        };
        return ApplyOverrides(settings);
    }

    /// <summary>
    ///     Oblivion HDR factory. TES4 has no IMGS cinematic grade (applying FNV's exterior tint was
    ///     the source of the washed-out/olive image) and no interior/exterior HDR split — one
    ///     engine-default Setting set covers both. <c>Sky::UpdateHDRValues</c> copies each active
    ///     weather's HNAM float with a per-field "authored ≤ 0 → engine default" substitution, so an
    ///     authored 0 never reaches the shader (a 0 BrightClamp turned the bright pass into an
    ///     everything-pass — the bloomed, clipped "posterized" horizon). TES4's all-zero
    ///     DefaultWeather placeholder falls out naturally: every field substitutes.
    /// </summary>
    public static GpuTonemapSettings ForOblivionWeather(WeatherHdr? hdr)
    {
        var settings = EngineTes4Defaults;
        if (hdr is null) return settings;
        return settings with
        {
            EyeAdaptSpeed = PositiveOr(hdr.EyeAdaptSpeed, settings.EyeAdaptSpeed),
            BlurRadius = PositiveOr(hdr.BlurRadius, settings.BlurRadius),
            BlurPasses = PositiveOr(hdr.BlurPasses, settings.BlurPasses),
            EmissiveMult = PositiveOr(hdr.EmissiveMult, settings.EmissiveMult),
            TargetLum = PositiveOr(hdr.TargetLum, settings.TargetLum),
            UpperLumClamp = PositiveOr(hdr.UpperLumClamp, settings.UpperLumClamp),
            BrightScale = PositiveOr(hdr.BrightScale, settings.BrightScale),
            BrightClamp = PositiveOr(hdr.BrightClamp, settings.BrightClamp)
        };
    }

    private static float PositiveOr(float authored, float engineDefault)
    {
        return authored > 0f ? authored : engineDefault;
    }

    /// <summary>Parses the diagnostic display-operator override, if one is configured.</summary>
    internal static GpuTonemapMode? ParseTonemapModeOverride(string? value)
    {
        return value?.ToLowerInvariant() switch
        {
            "off" => GpuTonemapMode.LegacyClamp,
            "aces" => GpuTonemapMode.GammaAces,
            "engine" => GpuTonemapMode.EngineFo3Fnv,
            "modern" => GpuTonemapMode.CreationModern,
            _ => null
        };
    }

    /// <summary>Env overrides: mode swap for A/Bs, bloom kill-switch, + the existing exposure knob.</summary>
    public static GpuTonemapSettings ApplyOverrides(GpuTonemapSettings settings)
    {
        if (ParseTonemapModeOverride(
                Environment.GetEnvironmentVariable("FALLOUT_VIEWER_TONEMAP")) is { } mode)
        {
            settings = settings with { Mode = mode };
        }

        var bloom = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_BLOOM");
        if (bloom is "0" || string.Equals(bloom, "off", StringComparison.OrdinalIgnoreCase))
        {
            settings = settings with { BloomEnabled = false };
        }
        else if (bloom is "1" || string.Equals(bloom, "on", StringComparison.OrdinalIgnoreCase))
        {
            settings = settings with { BloomEnabled = true };
        }

        // Modern bloom topology has not been recovered. Keep this bounded opt-in from accidentally
        // running the FO3/FNV chain even when the global diagnostic bloom override is enabled.
        if (settings.Mode == GpuTonemapMode.CreationModern)
        {
            settings = settings with { BloomEnabled = false };
        }

        var raw = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_EXPOSURE");
        if (raw != null
            && float.TryParse(raw, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var exposure)
            && exposure > 0f)
        {
            settings = settings with { Exposure = exposure };
        }
        else if (settings.Exposure <= 0f)
        {
            settings = settings with { Exposure = 1f };
        }

        return settings;
    }
}
