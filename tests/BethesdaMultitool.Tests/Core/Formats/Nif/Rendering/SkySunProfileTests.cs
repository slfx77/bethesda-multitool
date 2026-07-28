using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Independent projection vectors for recovered Sun::Initialize/Sun::Update geometry. Expected
///     values are calculated from the authored source vertices and raw center coordinates, not by
///     calling the production triangle or scaling helpers.
/// </summary>
public sealed class SkySunProfileTests
{
    private const float ViewerRadius = 30000f;
    private static readonly AtmosphereState.ClimateTiming FalloutTiming = new(6f, 8f, 18f, 20f);
    private static readonly AtmosphereState.ClimateTiming CreationTiming = new(5f, 7f, 17f, 19f);

    [Theory]
    [InlineData(BethesdaGame.Fallout3, 750f, 800f, 800f, -100f)]
    [InlineData(BethesdaGame.FalloutNewVegas, 750f, 800f, 800f, -100f)]
    [InlineData(BethesdaGame.Skyrim, 425f, 600f, 400f, 25f)]
    [InlineData(BethesdaGame.Fallout4, 425f, 600f, 400f, 25f)]
    [InlineData(BethesdaGame.Oblivion, 250f, 350f, 400f, 25f)]
    public void ForGame_RecoveredProfilesExposeRetailDefaults(
        BethesdaGame game, float disc, float glare, float xExtreme, float yExtreme)
    {
        var profile = SkySunProfile.ForGame(game);

        Assert.True(profile.HasRecoveredTriangleProjection);
        Assert.Equal(disc, profile.DefaultDiscHalfExtent);
        Assert.Equal(glare, profile.DefaultGlareHalfExtent);
        Assert.Equal(xExtreme, profile.DefaultSunXExtreme);
        Assert.Equal(yExtreme, profile.DefaultSunYExtreme);
        Assert.Equal(2f, profile.DefaultAlphaTransitionHours);
    }

    [Fact]
    public void Tes4TrianglePosition_DayWindowPadsRawBeginAndEnd()
    {
        // TES4 Sun::Update (symbolized Remaster decompile): day leg = sunriseBegin−1h .. sunsetEnd+1h.
        // Climate 6/7/18/19 with trans 2h → edges 5..20; x=+1 (east, +400) exactly at hour 5, x=0
        // (near-zenith) at the 12.5 span midpoint, x=−1 (west, −400) at hour 20.
        var profile = SkySunProfile.ForGame(BethesdaGame.Oblivion);
        var timing = new AtmosphereState.ClimateTiming(6f, 7f, 18f, 19f);

        var dawnEdge = profile.ResolveTrianglePosition(5f, timing);
        var midday = profile.ResolveTrianglePosition(12.5f, timing);
        var duskEdge = profile.ResolveTrianglePosition(20f, timing);

        AssertClose(400f, dawnEdge.X);
        AssertClose(0f, dawnEdge.Z);
        AssertClose(0f, midday.X);
        AssertClose(400f, midday.Z);
        AssertClose(25f, midday.Y);
        AssertClose(-400f, duskEdge.X);
        AssertClose(0f, duskEdge.Z);
    }

    [Fact]
    public void Tes4TrianglePosition_NoonApexIsNearZenith()
    {
        // The recovered constants (folded fSunXExtreme=400, fSunYExtreme=25) put the midday sun at
        // atan(400/25) ≈ 86° elevation — the near-overhead apex the 50° analytic stand-in missed.
        var profile = SkySunProfile.ForGame(BethesdaGame.Oblivion);
        var midday = profile.ResolveTrianglePosition(12.5f, new AtmosphereState.ClimateTiming(6f, 7f, 18f, 19f));

        var elevation = MathF.Atan2(midday.Z, MathF.Sqrt(midday.X * midday.X + midday.Y * midday.Y));
        Assert.True(elevation > 80f * (MathF.PI / 180f),
            $"expected a near-zenith noon apex, got {elevation * 180f / MathF.PI:F1} deg");
    }

    [Fact]
    public void Tes4Visibility_RampsAcrossClimateHalfWindows()
    {
        // TES4-exact visibility (retail decompile): fade out sunsetBegin→sunsetMid, night until
        // sunriseMid, fade in sunriseMid→sunriseEnd, no fSunAlphaTransTime term. Climate 6/8/18/20:
        // sunriseMid=7, sunsetMid=19.
        var profile = SkySunProfile.ForGame(BethesdaGame.Oblivion);
        var timing = new AtmosphereState.ClimateTiming(6f, 8f, 18f, 20f);

        Assert.Equal(1f, profile.ResolveVisibility(12f, timing));
        AssertClose(0.5f, profile.ResolveVisibility(18.5f, timing)); // halfway down the sunset ramp
        Assert.Equal(0f, profile.ResolveVisibility(19f, timing)); // sunset midpoint → hidden
        Assert.Equal(0f, profile.ResolveVisibility(2f, timing)); // deep night
        Assert.Equal(0f, profile.ResolveVisibility(7f, timing)); // sunrise midpoint → still hidden
        AssertClose(0.5f, profile.ResolveVisibility(7.5f, timing)); // halfway up the sunrise ramp
        Assert.Equal(1f, profile.ResolveVisibility(8.5f, timing));
    }

    [Fact]
    public void Tes4SunPathDirection_ServesDayLegAndKeepsNightConvention()
    {
        var noon = AtmosphereState.Tes4SunPathDirection(12.5f, 6f, 7f, 18f, 19f);
        var night = AtmosphereState.Tes4SunPathDirection(2f, 6f, 7f, 18f, 19f);

        Assert.True(noon.Z > 0.99f, $"noon direction should be near-zenith, got Z={noon.Z:F3}");
        Assert.Equal(new Vector3(0f, 0f, -1f), night);
    }

    [Fact]
    public void ResolveBillboardHalfSizes_FnvNoonPreservesDirectQuadProjection()
    {
        // Climate 6/8/18/20 with a 2h transition produces day edges 6..20 and x=0 at 13:00.
        // Raw center = (0,-100,800), while retail PC INI half-extents are 750 and 800.
        var distance = MathF.Sqrt(100f * 100f + 800f * 800f);
        var expectedDisc = ViewerRadius * 750f / distance;
        var expectedGlare = ViewerRadius * 800f / distance;

        var actual = SkySunProfile.ForGame(BethesdaGame.FalloutNewVegas)
            .ResolveBillboardHalfSizes(ViewerRadius, 13f, FalloutTiming);

        AssertClose(expectedDisc, actual.Disc);
        AssertClose(expectedGlare, actual.Glare);
    }

    [Fact]
    public void ResolveBillboardHalfSizes_FnvQuarterLegTracksChangingRawCenterDistance()
    {
        // At 09:30, x=+0.5 on the recovered 6..20 day leg: raw center=(400,-100,400).
        // This distance is intentionally not the noon distance; a constant viewer fraction cannot
        // preserve both projections.
        var distance = MathF.Sqrt(400f * 400f + 100f * 100f + 400f * 400f);
        var expectedDisc = ViewerRadius * 750f / distance;

        var profile = SkySunProfile.ForGame(BethesdaGame.FalloutNewVegas);
        var quarter = profile.ResolveBillboardHalfSizes(ViewerRadius, 9.5f, FalloutTiming);
        var noon = profile.ResolveBillboardHalfSizes(ViewerRadius, 13f, FalloutTiming);

        AssertClose(expectedDisc, quarter.Disc);
        Assert.True(quarter.Disc > noon.Disc,
            "the authored quad subtends a larger angle where the triangle path is closer");
    }

    [Fact]
    public void ResolveBillboardHalfSizes_CreationDefaultsMatchRecoveredFo4AndSkyrimVectors()
    {
        // Creation timing 5/7/17/19 produces a 5..19 day leg and noon center=(0,25,400).
        var distance = MathF.Sqrt(25f * 25f + 400f * 400f);
        var expectedDisc = ViewerRadius * 425f / distance;
        var expectedGlare = ViewerRadius * 600f / distance;

        foreach (var game in new[] { BethesdaGame.Skyrim, BethesdaGame.Fallout4 })
        {
            var actual = SkySunProfile.ForGame(game)
                .ResolveBillboardHalfSizes(ViewerRadius, 12f, CreationTiming);
            AssertClose(expectedDisc, actual.Disc);
            AssertClose(expectedGlare, actual.Glare);
        }
    }

    [Fact]
    public void ResolveBillboardHalfSizes_UsesGmstAndIniOverridesWithoutClippingAuthoredValues()
    {
        // Raw noon center=(0,50,600); authored INI extents deliberately exceed the defaults.
        var distance = MathF.Sqrt(50f * 50f + 600f * 600f);
        var expectedDisc = ViewerRadius * 900f / distance;
        var expectedGlare = ViewerRadius * 1200f / distance;

        var actual = SkySunProfile.ForGame(BethesdaGame.Fallout4).ResolveBillboardHalfSizes(
            ViewerRadius, 12f, CreationTiming,
            600f,
            50f,
            900f,
            1200f);

        AssertClose(expectedDisc, actual.Disc);
        AssertClose(expectedGlare, actual.Glare);
    }

    [Fact]
    public void ResolveBillboardHalfSizes_ZeroTransitionOverrideUsesClimateMidpoints()
    {
        // With zero transition padding, creation timing 5/7/17/19 has a 6..18 day leg. At 09:00,
        // x=+0.5 and raw center=(200,25,200). Treating zero as missing would use the default 5..19
        // leg and produce a different projection.
        var distance = MathF.Sqrt(200f * 200f + 25f * 25f + 200f * 200f);
        var expectedDisc = ViewerRadius * 425f / distance;

        var actual = SkySunProfile.ForGame(BethesdaGame.Skyrim).ResolveBillboardHalfSizes(
            ViewerRadius, 9f, CreationTiming, alphaTransitionHours: 0f);

        AssertClose(expectedDisc, actual.Disc);
    }

    [Fact]
    public void ResolveBillboardHalfSizes_ZeroIniExtentsRemainZero()
    {
        var actual = SkySunProfile.ForGame(BethesdaGame.FalloutNewVegas).ResolveBillboardHalfSizes(
            ViewerRadius, 13f, FalloutTiming, discHalfExtent: 0f, glareHalfExtent: 0f);

        Assert.Equal(0f, actual.Disc);
        Assert.Equal(0f, actual.Glare);
    }

    [Fact]
    public void ResolveTrianglePosition_ZeroXExtremeIsAnAuthoredOverride()
    {
        var actual = SkySunProfile.ForGame(BethesdaGame.Fallout4).ResolveTrianglePosition(
            12f, CreationTiming, 0f, 25f);

        Assert.Equal(new Vector3(0f, 25f, 0f), actual);
    }

    [Theory]
    [InlineData(BethesdaGame.Morrowind)]
    [InlineData(BethesdaGame.Fallout76)]
    [InlineData(BethesdaGame.Starfield)]
    [InlineData(BethesdaGame.Unknown)]
    public void ResolveBillboardHalfSizes_UnrecoveredFamilyRetainsExplicitLegacyCalibration(BethesdaGame game)
    {
        var actual = SkySunProfile.ForGame(game)
            .ResolveBillboardHalfSizes(ViewerRadius, 12f, CreationTiming);

        Assert.False(SkySunProfile.ForGame(game).HasRecoveredTriangleProjection);
        Assert.Equal(ViewerRadius * SkySunProfile.LegacyDiscHalfSizeFraction, actual.Disc);
        Assert.Equal(ViewerRadius * SkySunProfile.LegacyGlareHalfSizeFraction, actual.Glare);
    }

    [Fact]
    public void Resolve_FnvDirectionConsumesLoadedPathGmstOverrides()
    {
        // Independently normalized raw noon vector (0,300,600).
        var expected = Vector3.Normalize(new Vector3(0f, 300f, 600f));
        var actual = AtmosphereState.Resolve(
            13f,
            climate: FalloutTiming,
            game: BethesdaGame.FalloutNewVegas,
            sunXExtreme: 600f,
            sunYExtreme: 300f).SunWorldDirection;

        AssertVectorClose(expected, actual);
    }

    [Fact]
    public void Resolve_FnvDirectionConsumesLoadedAlphaTransitionOverride()
    {
        // With zero padding the day leg is 7..19; 10:00 is x=+0.5 => (400,-100,400).
        var expected = Vector3.Normalize(new Vector3(400f, -100f, 400f));
        var actual = AtmosphereState.Resolve(
            10f,
            climate: FalloutTiming,
            game: BethesdaGame.FalloutNewVegas,
            sunAlphaTransitionHours: 0f).SunWorldDirection;

        AssertVectorClose(expected, actual);
    }

    [Fact]
    public void Resolve_SkyrimUsesRecoveredTrianglePathInsteadOfAnalyticArc()
    {
        // Independently normalized TESV noon center (0,25,400): z≈.998, not the old 50° analytic apex.
        var expected = Vector3.Normalize(new Vector3(0f, 25f, 400f));
        var actual = AtmosphereState.Resolve(
            12f,
            climate: CreationTiming,
            game: BethesdaGame.Skyrim).SunWorldDirection;

        AssertVectorClose(expected, actual);
    }

    [Theory]
    [InlineData(6.75f, 0f)]
    [InlineData(7.25f, 0.25f)]
    [InlineData(7.75f, 0.5f)]
    [InlineData(8.75f, 1f)]
    [InlineData(17.25f, 1f)]
    [InlineData(17.75f, 0.75f)]
    [InlineData(18.25f, 0.5f)]
    [InlineData(19.25f, 0f)]
    public void ResolveVisibility_UsesRecoveredMidpointsAndTransitionWidth(float hour, float expected)
    {
        // Independent Sun::Update vector: climate midpoints are 7.75 and 18.25. The 2-hour
        // fSunAlphaTransTime therefore produces 6.75..8.75 and 17.25..19.25 alpha ramps; it does not
        // use either raw climate window as the ramp endpoints.
        var timing = new AtmosphereState.ClimateTiming(5.5f, 10f, 16f, 20.5f);

        var actual = SkySunProfile.ForGame(BethesdaGame.FalloutNewVegas)
            .ResolveVisibility(hour, timing);

        Assert.Equal(expected, actual, 6);
    }

    [Fact]
    public void ResolveVisibility_ZeroTransitionProducesHardMidpointDayLeg()
    {
        var profile = SkySunProfile.ForGame(BethesdaGame.Skyrim);

        Assert.Equal(0f, profile.ResolveVisibility(5.999f, CreationTiming, 0f));
        Assert.Equal(1f, profile.ResolveVisibility(6f, CreationTiming, 0f));
        Assert.Equal(1f, profile.ResolveVisibility(18f, CreationTiming, 0f));
        Assert.Equal(0f, profile.ResolveVisibility(18.001f, CreationTiming, 0f));
    }

    [Fact]
    public void Resolve_Fo4KeepsRawBillboardDirectionSeparateFromFlooredLightDirection()
    {
        // At the recovered day-leg edge, raw SunPos=(400,25,0). Fallout 4 writes this unfloored
        // position to both billboard nodes, but floors a normalized copy for the directional light.
        var expectedBillboard = Vector3.Normalize(new Vector3(400f, 25f, 0f));
        var resolved = AtmosphereState.Resolve(
            5f,
            climate: CreationTiming,
            game: BethesdaGame.Fallout4);

        AssertVectorClose(expectedBillboard, resolved.SunBillboardDirection);
        Assert.Equal(0f, resolved.SunBillboardDirection.Z, 6);
        Assert.True(resolved.SunWorldDirection.Z > 0.45f,
            $"FO4's light-only shadow floor was not applied: {resolved.SunWorldDirection}");
        Assert.True(Vector3.Distance(resolved.SunBillboardDirection, resolved.SunWorldDirection) > 0.4f);
    }

    [Fact]
    public void Resolve_UsesRecoveredVisibilityInsteadOfRawClimateWindow()
    {
        // Raw sunrise begins at 5.5, but Sun::Update's 2-hour alpha ramp does not begin until 6.75
        // because this deliberately wide climate has a 7.75 midpoint.
        var timing = new AtmosphereState.ClimateTiming(5.5f, 10f, 16f, 20.5f);
        var resolved = AtmosphereState.Resolve(
            6f,
            climate: timing,
            game: BethesdaGame.FalloutNewVegas);

        Assert.Equal(0f, resolved.SunIntensity);
        Assert.Equal(0f, resolved.SunDiscDrawAlpha);
    }

    private static void AssertClose(float expected, float actual)
    {
        var tolerance = MathF.Max(1e-4f, MathF.Abs(expected) * 1e-5f);
        Assert.InRange(MathF.Abs(expected - actual), 0f, tolerance);
    }

    private static void AssertVectorClose(Vector3 expected, Vector3 actual)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, 1e-5f);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, 1e-5f);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0f, 1e-5f);
    }
}