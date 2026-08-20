using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.SpeedTree;

/// <summary>
///     The six <c>fLeaf*</c> game settings that parameterize the engine's leaf rock/rustle driver.
///     Both engines run the IDENTICAL formula per quantity —
///     <c>swayFac(k) = k·sway·0.5 + (1−k)</c> (FNV PC <c>FUN_006658b0</c>; Oblivion
///     <c>FUN_0055e060</c>, the literal <c>FLD1/FSUBRP</c> pair in the raw disasm — see
///     <c>tools/GhidraProject/speedtree_oblivion_wind_decompiled.txt</c>) — only the setting
///     VALUES differ per game:
///     <list type="bullet">
///         <item>
///             FNV / FO3 ship NO fLeaf* GMST records or INI entries → compiled defaults
///             (Geck.exe initializers): timescales 2.0/0.5, all four influences 1.0
///             (⇒ amounts pulse with gustiness: <c>S·sway/2</c>, mean ≈ 0.64·S).
///         </item>
///         <item>
///             Oblivion.esm SHIPS them as GMSTs: amount influences 0.01 (⇒ amounts are a
///             steady ≈ S, barely gust-modulated), rock timer at 1.0·(0.425·sway+0.15), rustle
///             timer at 0.75·(0.75·sway−0.5) — which runs BACKWARDS in calm moments (k=1.5 makes
///             the 1−k term negative); that is authentic engine behavior, not a bug.
///         </item>
///     </list>
/// </summary>
public sealed record SpeedTreeWindProfile(
    float RockTimeScale,
    float RustleTimeScale,
    float RockSpeedSwayInfluence,
    float RustleSpeedSwayInfluence,
    float RockAmountSwayInfluence,
    float RustleAmountSwayInfluence)
{
    /// <summary>FNV/FO3 compiled defaults (no GMST records ship; Geck.exe initializers).</summary>
    public static SpeedTreeWindProfile FalloutNewVegas { get; } = new(
        2.0f,
        0.5f,
        1.0f,
        1.0f,
        1.0f,
        1.0f);

    /// <summary>Oblivion.esm shipped GMST values (probe-read from the retail master).</summary>
    public static SpeedTreeWindProfile Oblivion { get; } = new(
        1.0f,
        0.75f,
        0.85f,
        1.5f,
        0.01f,
        0.01f);

    /// <summary>
    ///     Profile for a loaded game. Only Oblivion overrides the compiled defaults via esm GMSTs;
    ///     everything else (FNV, FO3, unknown, bare-.spt renders) uses the FNV-era defaults.
    /// </summary>
    public static SpeedTreeWindProfile For(BethesdaGame game)
    {
        return game == BethesdaGame.Oblivion ? Oblivion : FalloutNewVegas;
    }

    /// <summary>The engine sway factor: <c>k·sway·0.5 + (1−k)</c>.</summary>
    public static float SwayFactor(float influence, float sway)
    {
        return (influence * sway * 0.5f) + (1f - influence);
    }
}
