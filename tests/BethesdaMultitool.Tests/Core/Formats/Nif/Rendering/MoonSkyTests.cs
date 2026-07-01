using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Pins the engine moon-phase formula derived from the Skyrim (TESV.exe) <c>Moon::UpdatePhase</c>
///     decompilation: <c>phase = (round(daysPassed) + offset) mod (phaseLength*8)) / phaseLength</c>.
/// </summary>
public class MoonSkyTests
{
    [Theory]
    // phaseLength = 3 (Morrowind's 24-day, 8-phase cycle): each phase spans 3 days, cycle repeats at 24.
    [InlineData(0, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    [InlineData(5, 1)]
    [InlineData(6, 2)]
    [InlineData(21, 7)]
    [InlineData(23, 7)]
    [InlineData(24, 0)] // full cycle wraps back to phase 0
    [InlineData(27, 1)]
    public void PhaseIndex_MorrowindCycle_MatchesEngineFormula(double daysPassed, int expected)
    {
        Assert.Equal(expected, MoonSky.PhaseIndex(daysPassed, MoonSky.MorrowindPhaseLengthDays));
    }

    [Fact]
    public void PhaseIndex_RoundsDaysToNearest()
    {
        // round(2.6) = 3 → phase 1, not the floor (2 → phase 0). The engine rounds GetDaysPassed.
        Assert.Equal(1, MoonSky.PhaseIndex(2.6, MoonSky.MorrowindPhaseLengthDays));
        Assert.Equal(0, MoonSky.PhaseIndex(2.4, MoonSky.MorrowindPhaseLengthDays));
    }

    [Fact]
    public void PhaseIndex_OffsetSeparatesTheTwoMoons()
    {
        // A per-moon day offset shifts the phase so Masser and Secunda aren't locked together.
        Assert.Equal(0, MoonSky.PhaseIndex(0, MoonSky.MorrowindPhaseLengthDays));
        Assert.Equal(1, MoonSky.PhaseIndex(0, MoonSky.MorrowindPhaseLengthDays, phaseOffsetDays: 3));
    }

    [Fact]
    public void PhaseIndex_NegativeDaysWrapNonNegative()
    {
        // -1 day with a 24-day cycle → 23 → phase 7 (no negative index leaks out).
        Assert.Equal(7, MoonSky.PhaseIndex(-1, MoonSky.MorrowindPhaseLengthDays));
    }

    [Fact]
    public void PhaseIndex_ClampsZeroPhaseLength()
    {
        // A degenerate phaseLength must not divide-by-zero; treat as 1.
        Assert.InRange(MoonSky.PhaseIndex(5, 0), 0, MoonSky.PhaseCount - 1);
    }

    [Fact]
    public void ComputeMoonDirection_ReturnsUnitVector()
    {
        var dir = MoonSky.ComputeMoonDirection(
            new MoonSky.MoonOrbit(24f, 0.3f, 60f, 80f, 20f), gameHour: 7f, day: 4f);
        Assert.Equal(1f, dir.Length(), 3);
    }

    [Fact]
    public void ComputeMoonDirection_HighAtCulmination_BelowHorizonHalfPeriodLater()
    {
        // PhaseOffsetTurns 0 → the orbital angle is 0 at hour 0 (culmination): elevation peaks (z high).
        // Half a period later the moon is the same angle past π → below the horizon (z < 0).
        var orbit = new MoonSky.MoonOrbit(
            PeriodHours: 24f, PhaseOffsetTurns: 0f, MaxAltitudeDeg: 70f, PeakAzimuthDeg: 0f, AzSwingDeg: 0f);

        var culmination = MoonSky.ComputeMoonDirection(orbit, gameHour: 0f, day: 0f);
        var halfLater = MoonSky.ComputeMoonDirection(orbit, gameHour: 12f, day: 0f);

        Assert.True(culmination.Z > 0.9f, $"expected high moon at culmination, got z={culmination.Z}");
        Assert.True(halfLater.Z < -0.9f, $"expected moon below horizon, got z={halfLater.Z}");
    }

    [Fact]
    public void ComputeMoonDirection_DistinctOrbits_TraceDifferentArcs()
    {
        // The headline fix: Masser and Secunda must NOT share a path. At the same instant their two profile
        // orbits give clearly different sky directions.
        var moon = SkyMoonProfile.ForGame(BethesdaGame.Morrowind);
        var masser = MoonSky.ComputeMoonDirection(moon.PrimaryOrbit, gameHour: 1f, day: 2f);
        var secunda = MoonSky.ComputeMoonDirection(moon.SecondaryOrbit, gameHour: 1f, day: 2f);

        Assert.True(Vector3.Distance(masser, secunda) > 0.1f,
            $"the two moons share an arc: masser={masser} secunda={secunda}");
    }

    [Fact]
    public void ComputeMoonDirection_DayDriftsThePosition_WhenPeriodIsNotADay()
    {
        // A period ≠ 24h means the same hour on consecutive days is a different point on the orbit, so the
        // day slider visibly drifts the arc (the decompiled accumulating-angle behaviour).
        var orbit = new MoonSky.MoonOrbit(24.6f, 0f, 60f, 90f, 25f);
        var day0 = MoonSky.ComputeMoonDirection(orbit, gameHour: 0f, day: 0f);
        var day1 = MoonSky.ComputeMoonDirection(orbit, gameHour: 0f, day: 1f);

        Assert.True(Vector3.Distance(day0, day1) > 0.01f, "day did not drift the moon position");
    }

    [Fact]
    public void Profile_Morrowind_SelectsPerPhaseTextures()
    {
        var moon = SkyMoonProfile.ForGame(BethesdaGame.Morrowind);

        Assert.True(moon.HasPerPhaseTextures);
        Assert.Equal(@"textures\tx_masser_new.dds", moon.PhaseTexturePath(secondary: false, 0));
        Assert.Equal(@"textures\tx_masser_full.dds", moon.PhaseTexturePath(secondary: false, 4));
        Assert.Equal(@"textures\tx_secunda_full.dds", moon.PhaseTexturePath(secondary: true, 4));
        // Out-of-range phase clamps to the last token (one_wan) rather than throwing.
        Assert.Equal(@"textures\tx_masser_one_wan.dds", moon.PhaseTexturePath(secondary: false, 99));
    }

    [Fact]
    public void Profile_Fallout_HasNoPerPhaseTextures()
    {
        // Single-moon (and the other multi-moon games) ship no per-phase moon art, so the renderer uses the
        // full-moon texture for every phase.
        var moon = SkyMoonProfile.ForGame(BethesdaGame.FalloutNewVegas);

        Assert.False(moon.HasPerPhaseTextures);
        Assert.Null(moon.PhaseTexturePath(secondary: false, 0));
    }
}
