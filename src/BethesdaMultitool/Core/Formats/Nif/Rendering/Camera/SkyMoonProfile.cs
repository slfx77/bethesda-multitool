using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Per-game configuration for the v3 textured-sky moon billboards. Each Bethesda engine draws a
///     different NUMBER of moons (Morrowind/Oblivion/Skyrim have two — Masser + Secunda; Fallout 3 / New
///     Vegas / 4 / 76 have one) from different ASSETS at different apparent SIZES, so the viewer cannot
///     share one moon constant across games. The renderer is otherwise game-agnostic; this profile is the
///     single place the moon's count, textures, and fallback size are decided from <see cref="BethesdaGame" />.
///     <para>
///         Texture paths are *candidates* probed against the loaded game's own archives (first existing
///         wins), so the wrong game's asset can never be drawn even when two games share a file name. Asset
///         names were verified against each retail archive (Morrowind.bsa, Oblivion/Skyrim Textures BSAs,
///         FO4/FO76 *Textures*.ba2): Morrowind moons live at <c>textures\tx_*_full.dds</c>; Oblivion and
///         Skyrim use the Creation slots <c>textures\sky\masser_full.dds</c> / <c>secunda_full.dds</c>;
///         FO4/FO76 ship both leftover Skyrim sets but the engine's single moon is the SECUNDA artwork
///         (the small white-gray disc — matched against an in-game FO4 screenshot); FO3/FNV's engine
///         moon is "Masser" too (decompile-verified; FNV ships the 8-phase <c>masser_*</c> set — the
///         old <c>skymoonfull.dds</c> is an unreferenced legacy asset, kept as a fallback candidate).
///     </para>
///     <para>
///         The half-size fractions here are FALLBACKS. FNV's symbol-backed <c>Moon::Initialize</c> and
///         Skyrim's retail implementation both build a <c>±iMasserSize</c>/<c>±iSecundaSize</c> quad and
///         translate its rotated-arm node to local <c>(0,512,0)</c>. Their apparent half-size is therefore
///         the authored size divided by the fixed 512-unit arm, independently of <c>fSunXExtreme</c>.
///         FO4's recovered triangle path is a separate family and continues to use its authored
///         <c>fSunXExtreme</c> radius. Because the sizes are GMSTs they can vary per mod, so the viewer reads
///         them from the loaded ESM at runtime (see <see cref="HalfSizeFractionFromGmst" />) and falls back
///         to these constants when the required GMSTs are absent (e.g. Morrowind TES3, or DMP captures
///         without a settings table). Fractions are relative to the billboard radius
///         (<see cref="D3D12.SkyBillboardRenderer12.Radius" />).
///     </para>
/// </summary>
public sealed record SkyMoonProfile
{
    /// <summary>Recovered orbit implementation used by this engine family.</summary>
    public MoonPathFamily PathFamily { get; init; }

    /// <summary>Fallback primary-moon angle where the recovered rotated-arm disc reaches full opacity.</summary>
    public float PrimaryAngleFadeStartDegrees { get; init; }

    /// <summary>Fallback primary-moon angle where the recovered rotated-arm disc becomes invisible.</summary>
    public float PrimaryAngleFadeEndDegrees { get; init; }

    /// <summary>Fallback Secunda angle where the recovered rotated-arm disc reaches full opacity.</summary>
    public float SecondaryAngleFadeStartDegrees { get; init; }

    /// <summary>Fallback Secunda angle where the recovered rotated-arm disc becomes invisible.</summary>
    public float SecondaryAngleFadeEndDegrees { get; init; }

    /// <summary>Number of moons this engine draws (0 = no billboard moon for this game).</summary>
    public int MoonCount { get; init; }

    /// <summary>Primary (Masser / the single Fallout moon) texture candidates, first existing wins.</summary>
    public IReadOnlyList<string> PrimaryTextureCandidates { get; init; } = [];

    /// <summary>Second moon (Secunda) texture candidates — only used when <see cref="MoonCount" /> ≥ 2.</summary>
    public IReadOnlyList<string> SecondaryTextureCandidates { get; init; } = [];

    /// <summary>Fallback primary-moon half-extent (fraction of the billboard radius) when the engine's
    /// authored size inputs aren't available.</summary>
    public float PrimaryHalfSizeFraction { get; init; }

    /// <summary>Fallback second-moon half-extent (fraction of the billboard radius).</summary>
    public float SecondaryHalfSizeFraction { get; init; }

    /// <summary>
    ///     True when this game's PRIMARY (only) moon is drawn from the Secunda asset slot, so the
    ///     runtime GMST size read should take <c>iSecundaSize</c> rather than <c>iMasserSize</c>.
    ///     FO4/FO76: the engine's single moon is the Secunda artwork; Masser is the unused leftover.
    /// </summary>
    public bool PrimaryUsesSecundaSize { get; init; }

    /// <summary>Primary moon's sky orbit (distinct from <see cref="SecondaryOrbit" /> so two moons don't
    /// share an arc). Single-moon games use only this.</summary>
    public MoonSky.MoonOrbit PrimaryOrbit { get; init; }

    /// <summary>Second moon's sky orbit — only used when <see cref="HasSecondMoon" />.</summary>
    public MoonSky.MoonOrbit SecondaryOrbit { get; init; }

    /// <summary>Days per moon phase for this game (8 phases × this = full lunar cycle). Morrowind = 3
    /// (24-day cycle). Only meaningful where <see cref="HasPerPhaseTextures" />.</summary>
    public int PhaseLengthDays { get; init; } = MoonSky.MorrowindPhaseLengthDays;

    /// <summary>Day offset of the second moon's phase from the first, so the two moons aren't phase-locked
    /// (seeded; visually calibrated against OpenMW).</summary>
    public int SecondaryPhaseOffsetDays { get; init; }

    /// <summary>Format string for the primary moon's per-phase texture (<c>{0}</c> = a <see cref="PhaseTokens" />
    /// entry), e.g. Morrowind's <c>textures\tx_masser_{0}.dds</c>. Null when the game ships no per-phase
    /// moon textures — the renderer then draws the single full-moon texture for every phase.</summary>
    public string? PrimaryPhaseTexturePattern { get; init; }

    /// <summary>Format string for the second moon's per-phase texture (<c>{0}</c> = a phase token).</summary>
    public string? SecondaryPhaseTexturePattern { get; init; }

    /// <summary>The 8 phase tokens in phase-index order (0 = new … 4 = full … 7 = waning crescent), spliced
    /// into the phase-texture patterns. Empty when the game has no per-phase textures.</summary>
    public IReadOnlyList<string> PhaseTokens { get; init; } = [];

    /// <summary>True when this engine draws at least one billboard moon.</summary>
    public bool HasMoon => MoonCount >= 1;

    /// <summary>True when this engine draws a second moon (Secunda).</summary>
    public bool HasSecondMoon => MoonCount >= 2;

    /// <summary>True when the game ships distinct per-phase moon textures (Morrowind, Oblivion — both
    /// moons — FO3/FNV's masser set, and FO4/76's secunda, all archive-verified). Skyrim shades phases
    /// in-shader, so it reuses the single full-moon texture for every phase.</summary>
    public bool HasPerPhaseTextures => PrimaryPhaseTexturePattern is not null && PhaseTokens.Count == MoonSky.PhaseCount;

    /// <summary>Evaluates this profile's game-family-specific moon path.</summary>
    public System.Numerics.Vector3 Direction(
        bool secondary, float gameHour, float day, AtmosphereState.ClimateTiming climate,
        float? rotatedArmSpeedOverride = null, float? rotatedArmInclinationOverride = null)
    {
        var orbit = secondary ? SecondaryOrbit : PrimaryOrbit;
        return PathFamily switch
        {
            MoonPathFamily.FalloutRotatedArm or MoonPathFamily.SkyrimRotatedArm =>
                MoonSky.ComputeRotatedArmDirection(
                speed: rotatedArmSpeedOverride ?? RotatedArmSpeed(secondary),
                inclinationDegrees: rotatedArmInclinationOverride ?? RotatedArmInclination(secondary),
                gameHour,
                day),
            MoonPathFamily.CreationTriangle => AtmosphereState.Fo4MoonPathDirection(
                gameHour,
                climate.SunriseBeginHour,
                climate.SunriseEndHour,
                climate.SunsetBeginHour,
                climate.SunsetEndHour),
            _ => MoonSky.ComputeMoonDirection(orbit, gameHour, day),
        };
    }

    /// <summary>Fallback speed GMST for a recovered rotated-arm moon.</summary>
    public float RotatedArmSpeed(bool secondary)
    {
        var orbit = secondary ? SecondaryOrbit : PrimaryOrbit;
        return 6f / MathF.Max(orbit.PeriodHours, 0.001f);
    }

    /// <summary>Fallback Z-offset/inclination GMST for a recovered rotated-arm moon.</summary>
    public float RotatedArmInclination(bool secondary)
    {
        var orbit = secondary ? SecondaryOrbit : PrimaryOrbit;
        return 90f - orbit.MaxAltitudeDeg;
    }

    /// <summary>
    ///     Evaluates the recovered per-moon angular opacity envelope. Runtime GMST values override the
    ///     profile defaults so plugin-authored fade and speed changes affect both position and opacity.
    /// </summary>
    public float RotatedArmDiscFade(
        bool secondary, float gameHour, float day,
        float? speedOverride = null, float? fadeStartOverride = null, float? fadeEndOverride = null)
    {
        if (PathFamily is not (MoonPathFamily.FalloutRotatedArm or MoonPathFamily.SkyrimRotatedArm))
        {
            return 1f;
        }

        var start = fadeStartOverride ??
                    (secondary ? SecondaryAngleFadeStartDegrees : PrimaryAngleFadeStartDegrees);
        var end = fadeEndOverride ??
                  (secondary ? SecondaryAngleFadeEndDegrees : PrimaryAngleFadeEndDegrees);
        return MoonSky.ComputeRotatedArmDiscFade(
            speedOverride ?? RotatedArmSpeed(secondary), gameHour, day, start, end);
    }

    /// <summary>
    ///     Phase index whose shipped texture is a deliberately-black stub the engine relies on a dark
    ///     night sky to hide (FO3/FNV <c>masser_new.dds</c> = 94-byte black disc; the Oblivion
    ///     <c>*_new.dds</c> stubs are the same family). The viewer skips the moon draw at this phase —
    ///     drawing the stub renders an opaque black quad over the stars. Null = draw every phase.
    /// </summary>
    public int? HiddenPhaseIndex { get; init; }

    /// <summary>The per-phase texture path for a moon at <paramref name="phaseIndex" /> (0..7), or null when
    /// this game has no per-phase textures (caller falls back to the full-moon texture). The index is
    /// clamped into range so a bad phase can't throw.</summary>
    public string? PhaseTexturePath(bool secondary, int phaseIndex)
    {
        var pattern = secondary ? SecondaryPhaseTexturePattern : PrimaryPhaseTexturePattern;
        if (pattern is null || PhaseTokens.Count != MoonSky.PhaseCount)
        {
            return null;
        }

        var idx = Math.Clamp(phaseIndex, 0, MoonSky.PhaseCount - 1);
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, pattern, PhaseTokens[idx]);
    }

    /// <summary>A game the viewer draws no billboard moon for (Starfield / Unknown).</summary>
    public static readonly SkyMoonProfile None = new() { MoonCount = 0 };

    /// <summary>
    ///     Local Y translation used by the recovered FNV and Skyrim rotated-arm moon nodes. This is an
    ///     executable constant, not <c>fSunXExtreme</c>.
    /// </summary>
    public const float RotatedArmLength = 512f;

    /// <summary>
    ///     Resolves an authored moon quad half-extent to this profile's apparent half-size fraction.
    ///     Recovered FNV/Skyrim rotated arms divide by their fixed 512-unit node translation; the FO4
    ///     triangle family and unrecovered calibrated families retain the authored/fallback sky radius.
    /// </summary>
    public float? HalfSizeFractionFromGmst(int? moonSizeSetting, float? sunXExtreme)
    {
        if (moonSizeSetting is not int size)
        {
            return null;
        }

        return PathFamily is MoonPathFamily.FalloutRotatedArm or MoonPathFamily.SkyrimRotatedArm
            ? size / RotatedArmLength
            : FractionFromGmst(size, sunXExtreme);
    }

    /// <summary>
    ///     Radius-based moon apparent-size as a fraction of the billboard radius:
    ///     <paramref name="moonSizeSetting" /> (the <c>iMasserSize</c>/<c>iSecundaSize</c> ±size quad
    ///     half-extent, in world units) divided by <paramref name="sunXExtreme" /> (the <c>fSunXExtreme</c>
    ///     path radius). Returns null when either GMST is missing or the radius is non-positive. Do not use
    ///     this directly for recovered FNV/Skyrim rotated arms; use <see cref="HalfSizeFractionFromGmst" />.
    /// </summary>
    public static float? FractionFromGmst(int? moonSizeSetting, float? sunXExtreme)
        => moonSizeSetting is int size && sunXExtreme is float dome && dome > 0f ? size / dome : null;

    // Verified asset paths (see class remarks). Candidate order = preference; the probe skips any the
    // loaded archives don't ship.
    // FO3/FNV: the engine moon is "Masser" (Sky::HandleClimateChange passes the Oblivion-holdover base
    // name — MemDebug rdata, docs/research/fnv_engine_hdr_imagespace.md §2) and FNV Textures2.bsa ships
    // the full masser_* 8-phase set; skymoonfull.dds is a legacy asset no Moon code references, kept
    // only as a fallback for archives that lack the masser set.
    private static readonly string[] FalloutSingleMoon =
        [@"textures\sky\masser_full.dds", @"textures\sky\skymoonfull.dds"];
    private static readonly string[] CreationMasser = [@"textures\sky\masser_full.dds"];
    private static readonly string[] CreationSecunda = [@"textures\sky\secunda_full.dds"];
    private static readonly string[] MorrowindMasser = [@"textures\tx_masser_full.dds"];
    private static readonly string[] MorrowindSecunda = [@"textures\tx_secunda_full.dds"];

    // Morrowind ships 8 per-phase textures per moon (tx_masser_<token>.dds / tx_secunda_<token>.dds),
    // in waxing→full→waning order; phase 0 = new, 4 = full (the phase-anchor day is calibrated in M5).
    // The Creation engines reuse the SAME token names (sky\masser_<token>.dds), so FO4's moon shares
    // this list.
    private static readonly string[] MorrowindPhaseTokens =
        ["new", "one_wax", "half_wax", "three_wax", "full", "three_wan", "half_wan", "one_wan"];

    private static readonly SkyMoonProfile Morrowind = new()
    {
        PathFamily = MoonPathFamily.CalibratedTesOrbit,
        MoonCount = 2,
        PrimaryTextureCandidates = MorrowindMasser,
        SecondaryTextureCandidates = MorrowindSecunda,
        // TES3 has no iMasserSize/fSunXExtreme GMST, so these fallbacks are always used. Eyeball-tuned:
        // Masser dwarfs Secunda in Morrowind's sky (big red moon vs. small white one).
        PrimaryHalfSizeFraction = 0.150f,
        SecondaryHalfSizeFraction = 0.080f,
        // Two distinct arcs (the headline fix): Masser climbs higher and culminates toward a different
        // compass bearing than Secunda, and a slightly longer Secunda period makes them drift apart over
        // days. Seeded from the decompiled orbit FORM; the literal Morrowind constants aren't in the shared
        // calendar code, so M5 calibrates these against OpenMW night-sky captures.
        PrimaryOrbit = new MoonSky.MoonOrbit(
            PeriodHours: 24f, PhaseOffsetTurns: 0f, MaxAltitudeDeg: 72f, PeakAzimuthDeg: 100f, AzSwingDeg: 22f),
        SecondaryOrbit = new MoonSky.MoonOrbit(
            PeriodHours: 24.6f, PhaseOffsetTurns: 0.14f, MaxAltitudeDeg: 56f, PeakAzimuthDeg: 55f, AzSwingDeg: 30f),
        PhaseLengthDays = MoonSky.MorrowindPhaseLengthDays,
        SecondaryPhaseOffsetDays = 11, // seed: Secunda nearly opposite Masser → clearly different phase
        PrimaryPhaseTexturePattern = @"textures\tx_masser_{0}.dds",
        SecondaryPhaseTexturePattern = @"textures\tx_secunda_{0}.dds",
        PhaseTokens = MorrowindPhaseTokens,
    };

    private static readonly SkyMoonProfile Oblivion = new()
    {
        PathFamily = MoonPathFamily.CalibratedTesOrbit,
        MoonCount = 2,
        PrimaryTextureCandidates = CreationMasser,
        SecondaryTextureCandidates = CreationSecunda,
        // Oblivion.esm has iMasserSize=100 but NO fSunXExtreme/iSecundaSize, so the runtime read uses the
        // FNV-calibrated dome (800) → Masser 100/800 = 0.125, Secunda ≈ 0.069. These fallbacks match that
        // (NOT the old 0.225, which rendered the moons gigantic in-viewer) for the no-GameSettings case.
        PrimaryHalfSizeFraction = 0.125f,
        SecondaryHalfSizeFraction = 0.069f,
        // Two distinct arcs. Seeded; calibrate if needed.
        PrimaryOrbit = new MoonSky.MoonOrbit(
            PeriodHours: 24f, PhaseOffsetTurns: 0f, MaxAltitudeDeg: 70f, PeakAzimuthDeg: 95f, AzSwingDeg: 22f),
        SecondaryOrbit = new MoonSky.MoonOrbit(
            PeriodHours: 24.6f, PhaseOffsetTurns: 0.16f, MaxAltitudeDeg: 54f, PeakAzimuthDeg: 60f, AzSwingDeg: 28f),
        // Oblivion ships the full 8-texture phase set for BOTH moons (sky\masser_<token>.dds /
        // sky\secunda_<token>.dds — archive-verified) with the same token names Morrowind/FO4 use.
        // Phase length comes from the active climate's TNAM (ClimateTiming.MoonPhaseDays) at the
        // consumption site; PhaseLengthDays is the no-climate fallback.
        PrimaryPhaseTexturePattern = @"textures\sky\masser_{0}.dds",
        SecondaryPhaseTexturePattern = @"textures\sky\secunda_{0}.dds",
        PhaseTokens = MorrowindPhaseTokens,
        PhaseLengthDays = MoonSky.MorrowindPhaseLengthDays,
    };

    // A plain nightly arc for the single-moon Fallout games (no second moon, no per-phase textures).
    private static readonly MoonSky.MoonOrbit SingleNightlyOrbit = new(
        PeriodHours: 24f, PhaseOffsetTurns: 0f, MaxAltitudeDeg: 68f, PeakAzimuthDeg: 90f, AzSwingDeg: 20f);

    // FO3/FNV phase-token order, decompile-verified (Moon::Update indexes its 8 texture-name members by
    // (phase+3)·8; the ctor's token→member assignments put FULL at phase 0 and NEW at phase 4 — the
    // reverse anchor of the TES games' new-first cycle). masser_new.dds ships as a stub (black disc);
    // day 0 = full moon in FO3/FNV.
    private static readonly string[] FalloutPhaseTokens =
        ["full", "three_wan", "half_wan", "one_wan", "new", "one_wax", "half_wax", "three_wax"];

    // FO3/FNV: single moon ("Masser"). Fallback size = the FNV shipped iMasserSize 85 divided by the
    // fixed 512-unit Moon node arm = 0.166015625. Orbit constants from Sky::HandleClimateChange's ctor args
    // (static_data_dump.txt): speed 0.25 × 60°/h = 15°/h → an exact 24h period (the FNV moon rises at
    // the same time nightly — the day slider changes PHASE, not position), inclination 35° → culmination
    // ≈ 55°. Direction evaluates the recovered rotated-arm matrix, including its 90-degree initial state.
    private static readonly SkyMoonProfile Fallout = new()
    {
        PathFamily = MoonPathFamily.FalloutRotatedArm,
        // FNV/FO3 Moon.cpp constructor defaults recovered from the symbol-backed Xbox build.
        PrimaryAngleFadeStartDegrees = 55f,
        PrimaryAngleFadeEndDegrees = 45f,
        MoonCount = 1,
        PrimaryTextureCandidates = FalloutSingleMoon,
        PrimaryHalfSizeFraction = 85f / RotatedArmLength,
        PrimaryOrbit = new MoonSky.MoonOrbit(
            PeriodHours: 24f, PhaseOffsetTurns: 0f, MaxAltitudeDeg: 55f, PeakAzimuthDeg: 90f, AzSwingDeg: 20f),
        PrimaryPhaseTexturePattern = @"textures\sky\masser_{0}.dds",
        PhaseTokens = FalloutPhaseTokens,
        HiddenPhaseIndex = 4, // masser_new.dds is a black stub — skip the draw at new moon
    };

    // Skyrim: fallback = the shipped Skyrim.esm GMSTs divided by the fixed 512-unit Moon node arm
    // (iMasserSize 90 / 512, iSecundaSize 40 / 512). TESV.exe 1.9.32 Moon::Update and
    // Sky::HandleClimateChange prove
    // that both moons use the rotated-arm matrices, with the executable's GMST defaults passed to the
    // constructor: Masser speed=.25 / z-offset=35 degrees, Secunda speed=.30 / z-offset=50 degrees.
    // Thus their periods are exactly 24h and 20h and their maximum altitudes are 55 and 40 degrees.
    private static readonly SkyMoonProfile Skyrim = new()
    {
        PathFamily = MoonPathFamily.SkyrimRotatedArm,
        // Skyrim.esm authors both Masser and Secunda's f*AngleFadeStart/End as 35/20 degrees.
        PrimaryAngleFadeStartDegrees = 35f,
        PrimaryAngleFadeEndDegrees = 20f,
        SecondaryAngleFadeStartDegrees = 35f,
        SecondaryAngleFadeEndDegrees = 20f,
        MoonCount = 2,
        PrimaryTextureCandidates = CreationMasser,
        SecondaryTextureCandidates = CreationSecunda,
        PrimaryHalfSizeFraction = 90f / RotatedArmLength,
        SecondaryHalfSizeFraction = 40f / RotatedArmLength,
        // Skyrim renders moon phases via its own shader, not per-phase textures, so the full-moon texture
        // is used for every phase here (no PhaseTexturePattern). The unused azimuth fields remain zero;
        // Direction maps period/max-altitude back to the recovered speed/inclination pair.
        PrimaryOrbit = new MoonSky.MoonOrbit(
            PeriodHours: 24f, PhaseOffsetTurns: 0f, MaxAltitudeDeg: 55f, PeakAzimuthDeg: 0f, AzSwingDeg: 0f),
        SecondaryOrbit = new MoonSky.MoonOrbit(
            PeriodHours: 20f, PhaseOffsetTurns: 0f, MaxAltitudeDeg: 40f, PeakAzimuthDeg: 0f, AzSwingDeg: 0f),
    };

    // FO4/FO76: single moon drawn from the Creation SECUNDA slot. The FO4 BA2s ship ONLY the Skyrim
    // Masser_*/Secunda_* sets (full name-table sweep — no Moon_full/other moon asset exists), and the
    // in-game night sky shows the small white-gray cratered disc + soft halo that is exactly FO4's
    // Secunda_full.DDS artwork (user in-game screenshot matched against the extracted texture
    // 2026-07-06). Masser is the unused leftover of the pair — drawing it rendered the red-brown TES
    // "Mars moon". The complete 8-texture secunda phase set ships and the engine cycles phases.
    // Fallback size = FO4's shipped GMSTs (iSecundaSize 75 / fSunXExtreme 600 = 0.125); the runtime
    // GMST read overrides, taking iSecundaSize for the primary via PrimaryUsesSecundaSize.
    private static readonly SkyMoonProfile FalloutCreation = new()
    {
        PathFamily = MoonPathFamily.CreationTriangle,
        MoonCount = 1,
        PrimaryTextureCandidates = CreationSecunda,
        PrimaryHalfSizeFraction = 0.125f,
        PrimaryUsesSecundaSize = true,
        PrimaryOrbit = SingleNightlyOrbit,
        PrimaryPhaseTexturePattern = @"textures\sky\secunda_{0}.dds",
        PhaseTokens = MorrowindPhaseTokens,
    };

    /// <summary>The moon configuration for the loaded game. Unknown/Starfield → <see cref="None" />.</summary>
    public static SkyMoonProfile ForGame(BethesdaGame game) => game switch
    {
        BethesdaGame.Morrowind => Morrowind,
        BethesdaGame.Oblivion => Oblivion,
        BethesdaGame.Fallout3 => Fallout,
        BethesdaGame.FalloutNewVegas => Fallout,
        BethesdaGame.Skyrim => Skyrim,
        BethesdaGame.Fallout4 => FalloutCreation,
        BethesdaGame.Fallout76 => FalloutCreation,
        _ => None,
    };
}

/// <summary>Moon trajectory family selected explicitly by game identity.</summary>
public enum MoonPathFamily
{
    /// <summary>TES-family fallback pending a recovered per-game binary oracle.</summary>
    CalibratedTesOrbit,
    /// <summary>FO3/FNV rotated-arm matrices recovered from <c>Moon::Update</c>.</summary>
    FalloutRotatedArm,
    /// <summary>Skyrim's two per-GMST rotated-arm paths recovered from TESV <c>Moon::Update</c>.</summary>
    SkyrimRotatedArm,
    /// <summary>FO4/FO76 triangle-wave position recovered from <c>Moon::Update</c>.</summary>
    CreationTriangle,
}
