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
///         FO4/FO76 *Textures*.ba2): Morrowind moons live at <c>textures\tx_*_full.dds</c>; Oblivion,
///         Skyrim, FO4 and FO76 all use the Creation slot <c>textures\sky\masser_full.dds</c> /
///         <c>secunda_full.dds</c> (FO4/FO76 ship both assets but render only the primary); FO3/FNV use
///         <c>textures\sky\skymoonfull.dds</c>.
///     </para>
///     <para>
///         The half-size fractions here are FALLBACKS. The engine's true moon size is a pair of GameSettings
///         — <c>iMasserSize</c> / <c>iSecundaSize</c> (the ±size billboard quad's world half-extent) divided
///         by <c>fSunXExtreme</c> (the sky-dome horizontal radius the moon orbits at). This was verified by
///         decompiling FNV's <c>Moon.cpp</c> (360 MemDebug) and Skyrim's <c>TESV Moon::Initialize</c>: both
///         build a <c>±size</c> quad and orbit the same sun/moon box. Because they are GMSTs they vary per
///         game and even per mod (FNV ships 85 / dome 800 → 0.106; Skyrim 90 / dome 400 → 0.225), so the
///         viewer reads them from the loaded ESM at runtime (see <see cref="FractionFromGmst" />) and only
///         falls back to these constants when the GMSTs are absent (e.g. Morrowind TES3, or DMP captures
///         without a settings table). The fractions are relative to the billboard radius
///         (<see cref="D3D12.SkyBillboardRenderer12.Radius" />).
///     </para>
/// </summary>
public sealed record SkyMoonProfile
{
    /// <summary>Number of moons this engine draws (0 = no billboard moon for this game).</summary>
    public int MoonCount { get; init; }

    /// <summary>Primary (Masser / the single Fallout moon) texture candidates, first existing wins.</summary>
    public IReadOnlyList<string> PrimaryTextureCandidates { get; init; } = [];

    /// <summary>Second moon (Secunda) texture candidates — only used when <see cref="MoonCount" /> ≥ 2.</summary>
    public IReadOnlyList<string> SecondaryTextureCandidates { get; init; } = [];

    /// <summary>Fallback primary-moon half-extent (fraction of the billboard radius) when the engine
    /// <c>iMasserSize</c>/<c>fSunXExtreme</c> GMSTs aren't available.</summary>
    public float PrimaryHalfSizeFraction { get; init; }

    /// <summary>Fallback second-moon half-extent (fraction of the billboard radius).</summary>
    public float SecondaryHalfSizeFraction { get; init; }

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

    /// <summary>True when the game ships distinct per-phase moon textures (only Morrowind, verified). Other
    /// games reuse the single full-moon texture for every phase.</summary>
    public bool HasPerPhaseTextures => PrimaryPhaseTexturePattern is not null && PhaseTokens.Count == MoonSky.PhaseCount;

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
    ///     The engine's exact moon apparent-size as a fraction of the billboard radius:
    ///     <paramref name="moonSizeSetting" /> (the <c>iMasserSize</c>/<c>iSecundaSize</c> ±size quad
    ///     half-extent, in world units) divided by <paramref name="sunXExtreme" /> (the <c>fSunXExtreme</c>
    ///     sky-dome horizontal radius). Returns null when either GMST is missing or the dome radius is
    ///     non-positive (caller then uses the per-game fallback fraction). See class remarks for the
    ///     decompilation this model came from.
    /// </summary>
    public static float? FractionFromGmst(int? moonSizeSetting, float? sunXExtreme)
        => moonSizeSetting is int size && sunXExtreme is float dome && dome > 0f ? size / dome : null;

    // Verified asset paths (see class remarks). Candidate order = preference; the probe skips any the
    // loaded archives don't ship.
    private static readonly string[] FalloutSingleMoon = [@"textures\sky\skymoonfull.dds"];
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
        MoonCount = 2,
        PrimaryTextureCandidates = CreationMasser,
        SecondaryTextureCandidates = CreationSecunda,
        // Oblivion.esm has iMasserSize=100 but NO fSunXExtreme/iSecundaSize, so the runtime read uses the
        // FNV-calibrated dome (800) → Masser 100/800 = 0.125, Secunda ≈ 0.069. These fallbacks match that
        // (NOT the old 0.225, which rendered the moons gigantic in-viewer) for the no-GameSettings case.
        PrimaryHalfSizeFraction = 0.125f,
        SecondaryHalfSizeFraction = 0.069f,
        // Two distinct arcs (no per-phase textures → full moon every phase). Seeded; calibrate if needed.
        PrimaryOrbit = new MoonSky.MoonOrbit(
            PeriodHours: 24f, PhaseOffsetTurns: 0f, MaxAltitudeDeg: 70f, PeakAzimuthDeg: 95f, AzSwingDeg: 22f),
        SecondaryOrbit = new MoonSky.MoonOrbit(
            PeriodHours: 24.6f, PhaseOffsetTurns: 0.16f, MaxAltitudeDeg: 54f, PeakAzimuthDeg: 60f, AzSwingDeg: 28f),
    };

    // A plain nightly arc for the single-moon Fallout games (no second moon, no per-phase textures).
    private static readonly MoonSky.MoonOrbit SingleNightlyOrbit = new(
        PeriodHours: 24f, PhaseOffsetTurns: 0f, MaxAltitudeDeg: 68f, PeakAzimuthDeg: 90f, AzSwingDeg: 20f);

    // FO3/FNV: single moon. Fallback = the FNV shipped GMSTs (iMasserSize 85 / fSunXExtreme 800 = 0.106).
    private static readonly SkyMoonProfile Fallout = new()
    {
        MoonCount = 1,
        PrimaryTextureCandidates = FalloutSingleMoon,
        PrimaryHalfSizeFraction = 0.106f,
        PrimaryOrbit = SingleNightlyOrbit,
    };

    // Skyrim: fallback = the shipped Skyrim.esm GMSTs (iMasserSize 90 / 400 = 0.225, iSecundaSize 40 / 400 = 0.10).
    private static readonly SkyMoonProfile Skyrim = new()
    {
        MoonCount = 2,
        PrimaryTextureCandidates = CreationMasser,
        SecondaryTextureCandidates = CreationSecunda,
        PrimaryHalfSizeFraction = 0.225f,
        SecondaryHalfSizeFraction = 0.100f,
        // Two distinct arcs. Skyrim renders moon phases via its own shader, not per-phase textures, so the
        // full-moon texture is used for every phase here (no PhaseTexturePattern).
        PrimaryOrbit = new MoonSky.MoonOrbit(
            PeriodHours: 24f, PhaseOffsetTurns: 0f, MaxAltitudeDeg: 74f, PeakAzimuthDeg: 100f, AzSwingDeg: 24f),
        SecondaryOrbit = new MoonSky.MoonOrbit(
            PeriodHours: 24.5f, PhaseOffsetTurns: 0.18f, MaxAltitudeDeg: 58f, PeakAzimuthDeg: 62f, AzSwingDeg: 30f),
    };

    // FO4/FO76: single moon drawn from the Creation Masser slot — VANILLA-FAITHFUL: Bethesda reuses
    // Skyrim's Masser artwork as the Fallout 4 moon (verified 2026-07-06: the FO4 BA2s ship the full
    // Masser_*/Secunda_* phase sets and NO other moon asset, and FO4's own Masser_full.DDS *is* the
    // red-brown TES art — the in-game "Mars moon" the modding community re-textures). The complete
    // 8-texture phase set ships, and the engine cycles phases, so per-phase textures are wired here
    // (same token names as Morrowind's). Fallback size; runtime GMSTs override.
    private static readonly SkyMoonProfile FalloutCreation = new()
    {
        MoonCount = 1,
        PrimaryTextureCandidates = CreationMasser,
        PrimaryHalfSizeFraction = 0.100f,
        PrimaryOrbit = SingleNightlyOrbit,
        PrimaryPhaseTexturePattern = @"textures\sky\masser_{0}.dds",
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
