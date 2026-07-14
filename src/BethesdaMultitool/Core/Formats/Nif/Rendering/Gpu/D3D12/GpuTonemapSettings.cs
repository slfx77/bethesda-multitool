using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>How the tonemap pass maps the float scene target to the 8-bit display.</summary>
internal enum GpuTonemapMode
{
    /// <summary>Plain clamp — bit-identical to the pre-HDR 8-bit pipeline (Morrowind: pre-HDR engine).</summary>
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
    ///     <c>TargetLUM / max(sum(adaptedAvgColor), TargetLUM)</c> followed by the IMGS cinematic
    ///     transform (saturation → tint → contrast/brightness). Operates on gamma-space values exactly
    ///     like the engine. Bloom (BrightPassBlur) is a follow-up. Also used for Oblivion with the FNV
    ///     default parameters as a labeled stand-in (shared engine lineage; Oblivion authors HDR via INI).
    /// </summary>
    EngineFo3Fnv = 2,
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
    ///     Temporal eye-adaptation blend factor for THIS frame: the weight of the PREVIOUS adapted
    ///     average (engine ADAPT pass: <c>k = EyeAdaptSpeed^clamp(15·dt, 0, 1)</c>, new = k·prev +
    ///     (1−k)·current). 0 = instant adaptation — the right value for single-frame headless captures
    ///     and the first live frame; the live frame path computes it from the frame delta +
    ///     <see cref="EyeAdaptSpeed" />. Smooths the sparse-grid average against camera motion (the
    ///     "interior lighting flickers while moving" report).
    /// </summary>
    public float AdaptFactor { get; init; }

    /// <summary>IMGS HDR: eye adaptation speed (temporal blend base; FNV defaults 0.9).</summary>
    public float EyeAdaptSpeed { get; init; }

    /// <summary>IMGS Cinematic: 0 = grayscale, 1 = full color.</summary>
    public float Saturation { get; init; }

    /// <summary>IMGS Cinematic: contrast pivot ("Avg Lum Value").</summary>
    public float ContrastAvgLum { get; init; }

    /// <summary>IMGS Cinematic: contrast multiplier.</summary>
    public float Contrast { get; init; }

    /// <summary>IMGS Cinematic: brightness multiplier.</summary>
    public float Brightness { get; init; }

    public float TintR { get; init; }

    public float TintG { get; init; }

    public float TintB { get; init; }

    /// <summary>IMGS Cinematic tint value: blend toward luma·tintColor (the FNV golden-tan filter ≈ 0.6).</summary>
    public float TintAmount { get; init; }

    /// <summary>Shipped FNV DefaultImageSpaceExterior (0x161) with neutral exposure.</summary>
    public static GpuTonemapSettings EngineExteriorDefaults { get; } = new()
    {
        Mode = GpuTonemapMode.EngineFo3Fnv,
        Exposure = 1f,
        EyeAdaptSpeed = 0.9f,
        TargetLum = 1.2f,
        UpperLumClamp = 1.0f,
        Saturation = 0.85f,
        ContrastAvgLum = 0.125f,
        Contrast = 1.2f,
        Brightness = 0.9f,
        TintR = 0.603922f,
        TintG = 0.537255f,
        TintB = 0.388235f,
        TintAmount = 0.6f,
    };

    /// <summary>Shipped FNV DefaultImageSpaceInterior (0x160): neutral cinematic.</summary>
    public static GpuTonemapSettings EngineInteriorDefaults { get; } = new()
    {
        Mode = GpuTonemapMode.EngineFo3Fnv,
        Exposure = 1f,
        EyeAdaptSpeed = 0.9f,
        TargetLum = 1.0f,
        UpperLumClamp = 1.0f,
        Saturation = 1f,
        ContrastAvgLum = 0.5f,
        Contrast = 1f,
        Brightness = 1f,
        TintR = 1f,
        TintG = 1f,
        TintB = 1f,
        TintAmount = 0f,
    };

    public static GpuTonemapSettings GammaAcesDefaults { get; } = new()
    {
        Mode = GpuTonemapMode.GammaAces,
        Exposure = 1f,
        TargetLum = 1f,
        UpperLumClamp = 1f,
        Saturation = 1f,
        ContrastAvgLum = 0.5f,
        Contrast = 1f,
        Brightness = 1f,
        TintR = 1f,
        TintG = 1f,
        TintB = 1f,
        TintAmount = 0f,
    };

    /// <summary>
    ///     Default operator per game family: FO3/FNV/Oblivion = the engine HDR stage (exterior params),
    ///     Morrowind = legacy clamp (pre-HDR engine), everything else = gamma-corrected ACES.
    ///     <c>FALLOUT_VIEWER_TONEMAP=off|aces|engine</c> overrides for A/Bs.
    /// </summary>
    public static GpuTonemapSettings ForGame(BethesdaGame game, bool interior = false)
    {
        var settings = game switch
        {
            BethesdaGame.Morrowind => GammaAcesDefaults with { Mode = GpuTonemapMode.LegacyClamp },
            BethesdaGame.Oblivion or BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas =>
                interior ? EngineInteriorDefaults : EngineExteriorDefaults,
            _ => GammaAcesDefaults,
        };
        return ApplyOverrides(settings);
    }

    /// <summary>Env overrides: mode swap for A/Bs + the existing exposure knob.</summary>
    public static GpuTonemapSettings ApplyOverrides(GpuTonemapSettings settings)
    {
        var mode = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_TONEMAP");
        settings = mode?.ToLowerInvariant() switch
        {
            "off" => settings with { Mode = GpuTonemapMode.LegacyClamp },
            "aces" => settings with { Mode = GpuTonemapMode.GammaAces },
            "engine" => settings with { Mode = GpuTonemapMode.EngineFo3Fnv },
            _ => settings,
        };

        var raw = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_EXPOSURE");
        if (raw != null
            && float.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var exposure)
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
