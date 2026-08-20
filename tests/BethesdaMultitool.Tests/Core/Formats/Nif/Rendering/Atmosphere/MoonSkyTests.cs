using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Atmosphere;

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
        Assert.Equal(1, MoonSky.PhaseIndex(0, MoonSky.MorrowindPhaseLengthDays, 3));
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
            new MoonSky.MoonOrbit(24f, 0.3f, 60f, 80f, 20f), 7f, 4f);
        Assert.Equal(1f, dir.Length(), 3);
    }

    [Fact]
    public void ComputeMoonDirection_HighAtCulmination_BelowHorizonHalfPeriodLater()
    {
        // PhaseOffsetTurns 0 → the orbital angle is 0 at hour 0 (culmination): elevation peaks (z high).
        // Half a period later the moon is the same angle past π → below the horizon (z < 0).
        var orbit = new MoonSky.MoonOrbit(
            24f, 0f, 70f, 0f, 0f);

        var culmination = MoonSky.ComputeMoonDirection(orbit, 0f, 0f);
        var halfLater = MoonSky.ComputeMoonDirection(orbit, 12f, 0f);

        Assert.True(culmination.Z > 0.9f, $"expected high moon at culmination, got z={culmination.Z}");
        Assert.True(halfLater.Z < -0.9f, $"expected moon below horizon, got z={halfLater.Z}");
    }

    [Fact]
    public void ComputeMoonDirection_DistinctOrbits_TraceDifferentArcs()
    {
        // The headline fix: Masser and Secunda must NOT share a path. At the same instant their two profile
        // orbits give clearly different sky directions.
        var moon = SkyMoonProfile.ForGame(BethesdaGame.Morrowind);
        var masser = MoonSky.ComputeMoonDirection(moon.PrimaryOrbit, 1f, 2f);
        var secunda = MoonSky.ComputeMoonDirection(moon.SecondaryOrbit, 1f, 2f);

        Assert.True(Vector3.Distance(masser, secunda) > 0.1f,
            $"the two moons share an arc: masser={masser} secunda={secunda}");
    }

    [Fact]
    public void ComputeMoonDirection_DayDriftsThePosition_WhenPeriodIsNotADay()
    {
        // A period ≠ 24h means the same hour on consecutive days is a different point on the orbit, so the
        // day slider visibly drifts the arc (the decompiled accumulating-angle behaviour).
        var orbit = new MoonSky.MoonOrbit(24.6f, 0f, 60f, 90f, 25f);
        var day0 = MoonSky.ComputeMoonDirection(orbit, 0f, 0f);
        var day1 = MoonSky.ComputeMoonDirection(orbit, 0f, 1f);

        Assert.True(Vector3.Distance(day0, day1) > 0.01f, "day did not drift the moon position");
    }

    [Fact]
    public void Profile_Morrowind_SelectsPerPhaseTextures()
    {
        var moon = SkyMoonProfile.ForGame(BethesdaGame.Morrowind);

        Assert.True(moon.HasPerPhaseTextures);
        Assert.Equal(@"textures\tx_masser_new.dds", moon.PhaseTexturePath(false, 0));
        Assert.Equal(@"textures\tx_masser_full.dds", moon.PhaseTexturePath(false, 4));
        Assert.Equal(@"textures\tx_secunda_full.dds", moon.PhaseTexturePath(true, 4));
        // Out-of-range phase clamps to the last token (one_wan) rather than throwing.
        Assert.Equal(@"textures\tx_masser_one_wan.dds", moon.PhaseTexturePath(false, 99));
    }

    [Fact]
    public void Profile_Fallout_UsesEngineMasserPhaseSet()
    {
        // FNV's engine moon is "Masser" with the full 8-phase texture set shipped in Textures2.bsa
        // (decompile + archive verified; docs/research/fnv_engine_hdr_imagespace.md §2).
        // The FO3/FNV phase cycle is anchored at FULL on day 0 (Moon::Update member table), the
        // reverse anchor of the TES games' new-first order, and the black masser_new stub phase is
        // hidden rather than drawn.
        var moon = SkyMoonProfile.ForGame(BethesdaGame.FalloutNewVegas);

        Assert.True(moon.HasPerPhaseTextures);
        Assert.Equal(@"textures\sky\masser_full.dds", moon.PhaseTexturePath(false, 0));
        Assert.Equal(@"textures\sky\masser_new.dds", moon.PhaseTexturePath(false, 4));
        Assert.Equal(@"textures\sky\masser_three_wax.dds", moon.PhaseTexturePath(false, 7));
        Assert.Equal(4, moon.HiddenPhaseIndex);
        Assert.Equal(24f, moon.PrimaryOrbit.PeriodHours); // speed 0.25 x 60 deg/h = exact daily orbit
        Assert.Equal(55f, moon.PrimaryOrbit.MaxAltitudeDeg); // 90 - 35 deg engine inclination
        // The engine texture is the masser art; the legacy skymoonfull stays only as a fallback probe.
        Assert.Equal(@"textures\sky\masser_full.dds", moon.PrimaryTextureCandidates[0]);
    }

    [Fact]
    public void FalloutRotatedArm_MatchesRecoveredFNVCardinalVectors()
    {
        const float sin35 = 0.57357645f;
        const float cos35 = 0.81915206f;

        // Moon::Update initializes the arm angle to 90 degrees. These values are expanded independently
        // from the recovered X(-angle) * Z(inclination) matrices and local +Y arm.
        VectorAssert.Equal(new Vector3(sin35, 0f, cos35),
            MoonSky.ComputeFalloutRotatedArmDirection(0.25f, 35f, 0f, 0f), 1e-5f);
        VectorAssert.Equal(new Vector3(sin35, -cos35, 0f),
            MoonSky.ComputeFalloutRotatedArmDirection(0.25f, 35f, 6f, 0f), 1e-5f);
        VectorAssert.Equal(new Vector3(sin35, cos35, 0f),
            MoonSky.ComputeFalloutRotatedArmDirection(0.25f, 35f, 18f, 0f), 1e-5f);
    }

    [Theory]
    [InlineData(19f, 0f)]
    [InlineData(20f, 0f)]
    [InlineData(27.5f, 0.5f)]
    [InlineData(35f, 1f)]
    [InlineData(90f, 1f)]
    [InlineData(145f, 1f)]
    [InlineData(152.5f, 0.5f)]
    [InlineData(160f, 0f)]
    [InlineData(161f, 0f)]
    [InlineData(270f, 0f)]
    public void RotatedArmDiscFade_MatchesRecoveredSkyrimPiecewiseEnvelope(float angle, float expected)
    {
        // Skyrim.esm authors both moons as start=35/end=20. Expected values are direct piecewise
        // reference vectors and do not call the production path to derive themselves.
        Assert.Equal(expected, MoonSky.EvaluateRotatedArmDiscFade(angle, 35f, 20f), 6);
    }

    [Fact]
    public void RotatedArmAngle_UsesRecoveredNinetyDegreeInitialStateAndWraps()
    {
        Assert.Equal(90f, MoonSky.ComputeRotatedArmAngleDegrees(0.25f, 0f, 0f), 6);
        Assert.Equal(180f, MoonSky.ComputeRotatedArmAngleDegrees(0.25f, 6f, 0f), 6);
        Assert.Equal(0f, MoonSky.ComputeRotatedArmAngleDegrees(0.25f, 18f, 0f), 6);
        Assert.Equal(90f, MoonSky.ComputeRotatedArmAngleDegrees(0.25f, 24f, 0f), 6);
        Assert.Equal(0f, MoonSky.ComputeRotatedArmAngleDegrees(0.25f, -6f, 0f), 6);
        Assert.Equal(90f, MoonSky.ComputeRotatedArmAngleDegrees(0.25f, 0f, 7f), 6);
    }

    [Fact]
    public void FalloutMasserExactDailyPeriodDoesNotDriftWithDay()
    {
        var day0 = MoonSky.ComputeFalloutRotatedArmDirection(0.25f, 35f, 19f, 0f);
        var day7 = MoonSky.ComputeFalloutRotatedArmDirection(0.25f, 35f, 19f, 7f);

        VectorAssert.Equal(day0, day7, 1e-5f);
    }

    [Fact]
    public void ProfilesUseDistinctRecoveredFamilyRoutes()
    {
        Assert.Equal(MoonPathFamily.FalloutRotatedArm,
            SkyMoonProfile.ForGame(BethesdaGame.FalloutNewVegas).PathFamily);
        Assert.Equal(MoonPathFamily.SkyrimRotatedArm,
            SkyMoonProfile.ForGame(BethesdaGame.Skyrim).PathFamily);
        Assert.Equal(MoonPathFamily.CreationTriangle,
            SkyMoonProfile.ForGame(BethesdaGame.Fallout4).PathFamily);
    }

    [Fact]
    public void SkyrimMoonsMatchRecoveredGmstCardinalVectorsAndPeriods()
    {
        var profile = SkyMoonProfile.ForGame(BethesdaGame.Skyrim);
        var timing = AtmosphereState.ClimateTiming.Default;

        // TESV.exe defaults passed by Sky::HandleClimateChange:
        // Masser speed=.25 / z-offset=35 degrees; Secunda speed=.30 / z-offset=50 degrees.
        const float sin35 = 0.57357645f;
        const float cos35 = 0.81915206f;
        const float sin50 = 0.76604444f;
        const float cos50 = 0.64278761f;

        VectorAssert.Equal(new Vector3(sin35, 0f, cos35),
            profile.Direction(false, 0f, 0f, timing), 1e-5f);
        VectorAssert.Equal(new Vector3(sin35, -cos35, 0f),
            profile.Direction(false, 6f, 0f, timing), 1e-5f);

        // Secunda advances 18 degrees/hour, so five hours advances the initialized 90-degree arm to 180.
        VectorAssert.Equal(new Vector3(sin50, -cos50, 0f),
            profile.Direction(true, 5f, 0f, timing), 1e-5f);

        // Masser is exactly daily; Secunda's 20-hour period advances 72 degrees between midnights.
        VectorAssert.Equal(profile.Direction(false, 0f, 0f, timing),
            profile.Direction(false, 0f, 7f, timing), 1e-5f);
        var secundaDay0 = profile.Direction(true, 0f, 0f, timing);
        var secundaDay1 = profile.Direction(true, 0f, 1f, timing);
        Assert.True(Vector3.Distance(secundaDay0, secundaDay1) > 0.5f,
            $"Skyrim Secunda did not advance on its recovered 20-hour period: {secundaDay0} vs {secundaDay1}");
    }

    [Fact]
    public void SkyrimProfile_UsesIndependentRecoveredFadeDefaultsAndOverrides()
    {
        var profile = SkyMoonProfile.ForGame(BethesdaGame.Skyrim);

        // At midnight the internal angle is 90 degrees: both shipped profiles are fully visible.
        Assert.Equal(1f, profile.RotatedArmDiscFade(false, 0f, 0f), 6);
        Assert.Equal(1f, profile.RotatedArmDiscFade(true, 0f, 0f), 6);

        // An override is consumed as authored: angle=90 at midnight lies outside a narrow 100/95 window.
        Assert.Equal(0f, profile.RotatedArmDiscFade(
            false, 0f, 0f, 0.25f, 100f, 95f), 6);
    }

    [Fact]
    public void FalloutProfile_UsesRecoveredFadeDefaults()
    {
        var profile = SkyMoonProfile.ForGame(BethesdaGame.FalloutNewVegas);

        // At speed .25 the recovered 90-degree state advances 15 degrees/hour. These times therefore
        // sample the independently recovered FNV end/mid/start angles 45/50/55 exactly.
        Assert.Equal(0f, profile.RotatedArmDiscFade(false, 21f, 0f), 5);
        Assert.Equal(0.5f, profile.RotatedArmDiscFade(false, 64f / 3f, 0f), 5);
        Assert.Equal(1f, profile.RotatedArmDiscFade(false, 65f / 3f, 0f), 5);
    }

    [Fact]
    public void RotatedArmProfileDirection_ConsumesSpeedAndInclinationOverrides()
    {
        var profile = SkyMoonProfile.ForGame(BethesdaGame.FalloutNewVegas);

        // 90 + 3h * .5 * 60 = 180 degrees. A 30-degree inclination then produces this hard-coded
        // X(-180)*Z(+30) column-vector result; expected values do not call the production helper.
        var direction = profile.Direction(false, 3f, 0f, AtmosphereState.ClimateTiming.Default,
            0.5f, 30f);
        VectorAssert.Equal(new Vector3(0.5f, -0.8660254f, 0f), direction, 1e-5f);
    }

    [Fact]
    public void Fallout4MoonUsesRecoveredUnflooredTrianglePath()
    {
        var profile = SkyMoonProfile.ForGame(BethesdaGame.Fallout4);
        var timing = new AtmosphereState.ClimateTiming(5f, 7f, 17f, 19f, 3);
        var direction = profile.Direction(false, 6f, 0f, timing);

        // dayStart = sunrise midpoint - 1h = 5h; at 6h x is inside the recovered day-leg.
        var x = 1f - (6f - 5f) / 14f * 2f;
        var expected = Vector3.Normalize(new Vector3(x * 400f, 25f, 400f - MathF.Abs(x * 400f)));
        VectorAssert.Equal(expected, direction, 1e-5f);
    }
}