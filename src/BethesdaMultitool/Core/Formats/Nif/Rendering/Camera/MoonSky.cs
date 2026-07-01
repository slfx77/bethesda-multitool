using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Day-driven moon phase math for the v3 textured sky, derived from the Skyrim (TESV.exe 1.9.32)
///     decompilation of <c>Moon::UpdatePhase</c> / <c>Calendar</c> — Skyrim shares Morrowind's calendar,
///     and we have full Skyrim symbols where Morrowind has none. This is the engine-exact phase formula;
///     the per-moon orbit geometry (azimuth speed, inclination) is a separate, visually-calibrated concern
///     handled by the renderer/profile (Morrowind moon constants aren't recoverable from the shared
///     calendar code and are tuned against OpenMW as a black-box oracle).
///     <para>
///         Decompiled <c>Moon::UpdatePhase</c> (TESV.exe @0x00626980):
///         <c>phase = (round(GetDaysPassed()) mod (phaseLength*8)) / phaseLength</c>, where
///         <c>phaseLength</c> = <c>TESClimate::GetMoonPhaseDays()</c> (climate byte+0x45 &amp; 0x3F). The
///         8 phases are one full lunar cycle; for Morrowind's documented 24-day cycle that is
///         <c>phaseLength = 3</c> (8 × 3). Masser/Secunda differ by a per-moon day offset so they are not
///         locked to the same phase (the lore-correct behaviour vanilla Morrowind notably failed to do).
///     </para>
/// </summary>
public static class MoonSky
{
    /// <summary>The number of distinct moon phases in one lunar cycle (engine constant).</summary>
    public const int PhaseCount = 8;

    /// <summary>Morrowind's documented lunar cycle is 24 days → 3 days per phase across 8 phases.</summary>
    public const int MorrowindPhaseLengthDays = 3;

    /// <summary>
    ///     The engine moon-phase index (0..7) for a given day count, per the decompiled
    ///     <c>Moon::UpdatePhase</c>: <c>(round(daysPassed) + offset) mod (phaseLength*8)) / phaseLength</c>.
    ///     <paramref name="phaseOffsetDays" /> lets the two moons sit at different phases (Secunda is
    ///     offset from Masser). The modulo is normalised to be non-negative so negative day counts (or a
    ///     negative offset) still map into 0..7.
    /// </summary>
    public static int PhaseIndex(double daysPassed, int phaseLengthDays, int phaseOffsetDays = 0)
    {
        if (phaseLengthDays < 1)
        {
            phaseLengthDays = 1;
        }

        var day = (long)Math.Round(daysPassed, MidpointRounding.AwayFromZero) + phaseOffsetDays;
        var cycle = (long)phaseLengthDays * PhaseCount;
        var wrapped = ((day % cycle) + cycle) % cycle; // non-negative modulo
        return (int)(wrapped / phaseLengthDays);
    }

    private const float Deg2Rad = MathF.PI / 180f;

    /// <summary>
    ///     A single moon's sky orbit, derived in form from the decompiled <c>Moon::Update</c> (the moon
    ///     accumulates a sky angle that advances with elapsed game time, and its node is rotated by that
    ///     angle plus a fixed inclination). The literal Morrowind constants aren't recoverable from the
    ///     shared calendar code, so these are seeded plausibly and visually calibrated against OpenMW. Two
    ///     moons differ by their period, starting offset, peak altitude and compass orientation, so they
    ///     trace <em>distinct</em> arcs (vanilla Morrowind drew both on the same path — the bug being fixed).
    /// </summary>
    /// <param name="PeriodHours">Apparent orbital period — how long the moon takes to go once around the
    /// sky. Near 24h keeps it roughly nightly; a small per-moon difference makes the two moons separate and
    /// each drift across days.</param>
    /// <param name="PhaseOffsetTurns">Starting fraction (0..1) along the orbit at day 0 / hour 0, so the two
    /// moons aren't co-located.</param>
    /// <param name="MaxAltitudeDeg">Peak elevation above the horizon at culmination (the orbit inclination).</param>
    /// <param name="PeakAzimuthDeg">Compass azimuth the moon culminates toward (0 = +X / east-ish in the
    /// viewer's world basis), so the two arcs lean different ways.</param>
    /// <param name="AzSwingDeg">How far the azimuth swings to either side of the peak across the arc.</param>
    public readonly record struct MoonOrbit(
        float PeriodHours,
        float PhaseOffsetTurns,
        float MaxAltitudeDeg,
        float PeakAzimuthDeg,
        float AzSwingDeg);

    /// <summary>
    ///     The unit sky direction (+Z up) of a moon at the given game hour and day, from its
    ///     <see cref="MoonOrbit" />. The orbital angle advances with absolute elapsed hours
    ///     (<c>day*24 + hour</c>), so the day slider drifts the arc and the two moons — given different
    ///     orbits — never share a path. Elevation peaks at the culmination (hour/day where the accumulated
    ///     angle is 0) and dips below the horizon half an orbit later; the renderer skips a moon whose
    ///     direction is below the horizon.
    /// </summary>
    public static Vector3 ComputeMoonDirection(MoonOrbit orbit, float gameHour, float day)
    {
        var absHours = (day * 24f) + gameHour;
        var turn = (absHours / MathF.Max(orbit.PeriodHours, 0.001f)) + orbit.PhaseOffsetTurns;
        var theta = turn * MathF.Tau;

        var el = orbit.MaxAltitudeDeg * Deg2Rad * MathF.Cos(theta);
        var az = (orbit.PeakAzimuthDeg * Deg2Rad) + (orbit.AzSwingDeg * Deg2Rad * MathF.Sin(theta));
        var cosE = MathF.Cos(el);
        return Vector3.Normalize(new Vector3(MathF.Cos(az) * cosE, MathF.Sin(az) * cosE, MathF.Sin(el)));
    }
}
