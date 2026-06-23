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

    /// <summary>True when this engine draws at least one billboard moon.</summary>
    public bool HasMoon => MoonCount >= 1;

    /// <summary>True when this engine draws a second moon (Secunda).</summary>
    public bool HasSecondMoon => MoonCount >= 2;

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

    private static readonly SkyMoonProfile Morrowind = new()
    {
        MoonCount = 2,
        PrimaryTextureCandidates = MorrowindMasser,
        SecondaryTextureCandidates = MorrowindSecunda,
        // TES3 has no iMasserSize/fSunXExtreme GMST, so these fallbacks are always used. Eyeball-tuned:
        // Masser dwarfs Secunda in Morrowind's sky (big red moon vs. small white one).
        PrimaryHalfSizeFraction = 0.150f,
        SecondaryHalfSizeFraction = 0.080f,
    };

    private static readonly SkyMoonProfile Oblivion = new()
    {
        MoonCount = 2,
        PrimaryTextureCandidates = CreationMasser,
        SecondaryTextureCandidates = CreationSecunda,
        // Fallback; the actual Oblivion.esm GMSTs override this at runtime. Creation default ≈ Skyrim's.
        PrimaryHalfSizeFraction = 0.225f,
        SecondaryHalfSizeFraction = 0.100f,
    };

    // FO3/FNV: single moon. Fallback = the FNV shipped GMSTs (iMasserSize 85 / fSunXExtreme 800 = 0.106).
    private static readonly SkyMoonProfile Fallout = new()
    {
        MoonCount = 1,
        PrimaryTextureCandidates = FalloutSingleMoon,
        PrimaryHalfSizeFraction = 0.106f,
    };

    // Skyrim: fallback = the shipped Skyrim.esm GMSTs (iMasserSize 90 / 400 = 0.225, iSecundaSize 40 / 400 = 0.10).
    private static readonly SkyMoonProfile Skyrim = new()
    {
        MoonCount = 2,
        PrimaryTextureCandidates = CreationMasser,
        SecondaryTextureCandidates = CreationSecunda,
        PrimaryHalfSizeFraction = 0.225f,
        SecondaryHalfSizeFraction = 0.100f,
    };

    // FO4/FO76: single moon drawn from the Creation Masser slot (the Boston/Appalachia moon reuses the
    // Skyrim slot name; the secunda asset ships but is not rendered). Fallback; runtime GMSTs override.
    private static readonly SkyMoonProfile FalloutCreation = new()
    {
        MoonCount = 1,
        PrimaryTextureCandidates = CreationMasser,
        PrimaryHalfSizeFraction = 0.100f,
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
