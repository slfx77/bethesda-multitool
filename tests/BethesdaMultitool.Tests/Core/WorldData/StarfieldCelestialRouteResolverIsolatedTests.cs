using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.WorldData;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

public sealed class StarfieldCelestialRouteResolverIsolatedTests
{
    private const uint PrimaryStarFormId = 0x0005E5CB;
    private const uint BinaryStarFormId = 0x0005E5CC;
    private const uint RootSunPresetFormId = 0x00134249;
    private const uint DiffSunPresetFormId = 0x0013424A;

    [Fact]
    public void Resolve_TreatsSolStyleSystemZeroAsAuthoredScalarKey()
    {
        var sol = Star(PrimaryStarFormId, 0, RootSunPresetFormId);

        var route = StarfieldCelestialRouteResolver.Resolve(
            PlanetCandidate(0),
            StarfieldStarDataIndex.Build([sol]),
            SunIndex(Root(RootSunPresetFormId, 20_000)));

        Assert.True(route.IsResolved, route.FailureDetail);
        Assert.Equal(StarfieldCelestialRouteStatus.Resolved, route.Status);
        Assert.Equal(0u, route.SystemId);
        Assert.Same(sol, route.PrimaryStar!.Star);
        Assert.Equal(RootSunPresetFormId, route.PrimaryStar.AuthoredSunPresetFormId);
        Assert.True(route.PrimaryStar.SunPreset!.IsResolved);
        Assert.Equal(20_000f, route.PrimaryStar.EffectiveSunPreset!.SunIlluminance);
        Assert.Null(route.BinaryStar);
    }

    [Fact]
    public void Resolve_PreservesEveryAmbiguousSystemCandidateWithoutChoosingPreset()
    {
        var first = Star(0x100, 7, RootSunPresetFormId);
        var second = Star(0x200, 7, DiffSunPresetFormId);

        var route = StarfieldCelestialRouteResolver.Resolve(
            PlanetCandidate(7),
            StarfieldStarDataIndex.Build([first, second]),
            new Dictionary<uint, StarfieldSunPresetRecord>());

        Assert.False(route.IsResolved);
        Assert.Equal(StarfieldCelestialRouteStatus.StarDataResolutionFailed, route.Status);
        Assert.Equal(StarfieldStarDataResolutionStatus.AmbiguousSystem, route.StarData.Status);
        Assert.Equal([0x100u, 0x200u], route.StarData.ConflictingFormIds);
        Assert.Null(route.PrimaryStar);
        Assert.Null(route.BinaryStar);
        Assert.Contains("2 STDT", route.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_PreservesMissingAndAuthoredZeroPnamWithoutSunpLookup()
    {
        var missing = Star(0x100, 1, null);
        var zero = Star(0x200, 2, 0);
        var emptySunIndex = new Dictionary<uint, StarfieldSunPresetRecord>();

        var missingRoute = StarfieldCelestialRouteResolver.Resolve(
            PlanetCandidate(1),
            StarfieldStarDataIndex.Build([missing]),
            emptySunIndex);
        var zeroRoute = StarfieldCelestialRouteResolver.Resolve(
            PlanetCandidate(2),
            StarfieldStarDataIndex.Build([zero]),
            emptySunIndex);

        Assert.True(missingRoute.IsResolved, missingRoute.FailureDetail);
        Assert.Null(missingRoute.PrimaryStar!.AuthoredSunPresetFormId);
        Assert.False(missingRoute.PrimaryStar.RequiresSunPresetResolution);
        Assert.Null(missingRoute.PrimaryStar.SunPreset);

        Assert.True(zeroRoute.IsResolved, zeroRoute.FailureDetail);
        Assert.True(zeroRoute.PrimaryStar!.AuthoredSunPresetFormId.HasValue);
        Assert.Equal(0u, zeroRoute.PrimaryStar.AuthoredSunPresetFormId.Value);
        Assert.False(zeroRoute.PrimaryStar.RequiresSunPresetResolution);
        Assert.Null(zeroRoute.PrimaryStar.SunPreset);
    }

    [Fact]
    public void Resolve_PropagatesNonzeroPnamSunpFailure()
    {
        const uint missingSunPreset = 0x00DEAD00;
        var route = StarfieldCelestialRouteResolver.Resolve(
            PlanetCandidate(3),
            StarfieldStarDataIndex.Build(
                [Star(PrimaryStarFormId, 3, missingSunPreset)]),
            new Dictionary<uint, StarfieldSunPresetRecord>());

        Assert.False(route.IsResolved);
        Assert.Equal(StarfieldCelestialRouteStatus.SunPresetResolutionFailed, route.Status);
        Assert.True(route.StarData.IsResolved);
        Assert.NotNull(route.PrimaryStar);
        Assert.Equal(missingSunPreset, route.PrimaryStar.AuthoredSunPresetFormId);
        Assert.Equal(
            StarfieldSunPresetResolutionStatus.TargetNotFound,
            route.PrimaryStar.SunPreset!.Status);
        Assert.Null(route.PrimaryStar.EffectiveSunPreset);
        Assert.Contains("00DEAD00", route.FailureDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ExposesEffectiveInheritedSunPreset()
    {
        var root = Root(RootSunPresetFormId, 20_000);
        var diff = new StarfieldSunPresetRecord
        {
            FormId = DiffSunPresetFormId,
            ParentFormId = RootSunPresetFormId,
            PayloadKind = StarfieldSunPresetPayloadKind.Diff,
            Patch = new StarfieldSunPresetPatch
            {
                ParentFormId = RootSunPresetFormId,
                SunIlluminance = 0,
                SunDiskTexture = string.Empty
            }
        };
        var route = StarfieldCelestialRouteResolver.Resolve(
            PlanetCandidate(4),
            StarfieldStarDataIndex.Build(
                [Star(PrimaryStarFormId, 4, DiffSunPresetFormId)]),
            SunIndex(root, diff));

        Assert.True(route.IsResolved, route.FailureDetail);
        Assert.Equal(
            [RootSunPresetFormId, DiffSunPresetFormId],
            route.PrimaryStar!.SunPreset!.InheritanceChain);
        Assert.Equal(0f, route.PrimaryStar.EffectiveSunPreset!.SunIlluminance);
        Assert.Equal(string.Empty, route.PrimaryStar.EffectiveSunPreset.SunDiskTexture);
        Assert.Equal(0.2f, route.PrimaryStar.EffectiveSunPreset.SunColor!.Y);
    }

    [Fact]
    public void Resolve_PreservesBinaryStarAndResolvesBothNonzeroPnamValues()
    {
        const uint binarySunPreset = 0x0013424B;
        var primary = Star(
            PrimaryStarFormId,
            5,
            RootSunPresetFormId,
            BinaryStarFormId);
        var binary = Star(
            BinaryStarFormId,
            6,
            binarySunPreset);

        var route = StarfieldCelestialRouteResolver.Resolve(
            PlanetCandidate(5),
            StarfieldStarDataIndex.Build([primary, binary]),
            SunIndex(
                Root(RootSunPresetFormId, 20_000),
                Root(binarySunPreset, 8_000)));

        Assert.True(route.IsResolved, route.FailureDetail);
        Assert.Same(primary, route.PrimaryStar!.Star);
        Assert.Same(binary, route.BinaryStar!.Star);
        Assert.Equal(20_000f, route.PrimaryStar.EffectiveSunPreset!.SunIlluminance);
        Assert.Equal(8_000f, route.BinaryStar.EffectiveSunPreset!.SunIlluminance);
    }

    [Fact]
    public void Resolve_PreservesAmbiguousBinaryStarCandidatesWithoutPickingOne()
    {
        var primary = Star(
            PrimaryStarFormId,
            9,
            RootSunPresetFormId,
            BinaryStarFormId);
        var firstBinary = Star(BinaryStarFormId, 10, null);
        var secondBinary = Star(BinaryStarFormId, 11, 0);

        var route = StarfieldCelestialRouteResolver.Resolve(
            PlanetCandidate(9),
            StarfieldStarDataIndex.Build([primary, firstBinary, secondBinary]),
            SunIndex(Root(RootSunPresetFormId, 20_000)));

        Assert.False(route.IsResolved);
        Assert.Equal(StarfieldCelestialRouteStatus.StarDataResolutionFailed, route.Status);
        Assert.Equal(StarfieldStarDataResolutionStatus.AmbiguousBinaryStar, route.StarData.Status);
        Assert.Equal([BinaryStarFormId, BinaryStarFormId], route.StarData.ConflictingFormIds);
        Assert.Same(primary, route.StarData.Primary);
        Assert.Null(route.PrimaryStar);
        Assert.Null(route.BinaryStar);
    }

    [Fact]
    public void Resolve_EvaluatesBinaryPnamEvenWhenPrimarySunpFails()
    {
        const uint missingPrimarySunPreset = 0x00DEAD00;
        const uint binarySunPreset = 0x0013424B;
        var primary = Star(
            PrimaryStarFormId,
            7,
            missingPrimarySunPreset,
            BinaryStarFormId);
        var binary = Star(
            BinaryStarFormId,
            8,
            binarySunPreset);

        var route = StarfieldCelestialRouteResolver.Resolve(
            PlanetCandidate(7),
            StarfieldStarDataIndex.Build([primary, binary]),
            SunIndex(Root(binarySunPreset, 8_000)));

        Assert.False(route.IsResolved);
        Assert.Equal(StarfieldCelestialRouteStatus.SunPresetResolutionFailed, route.Status);
        Assert.Equal(
            StarfieldSunPresetResolutionStatus.TargetNotFound,
            route.PrimaryStar!.SunPreset!.Status);
        Assert.True(route.BinaryStar!.SunPreset!.IsResolved);
        Assert.Equal(8_000f, route.BinaryStar.EffectiveSunPreset!.SunIlluminance);
    }

    private static StarfieldPlanetWorldspaceCandidate PlanetCandidate(uint systemId)
    {
        var worldspace = new StarfieldPlanetWorldspaceEntry(0d, 0d, 0x100);
        var body = new StarfieldPlanetBodyData(
            2,
            systemId,
            0,
            3,
            new StarfieldPlanetAtmosphereData(0, 0, 0, 0));
        var planet = new StarfieldResolvedPlanetData(
            0x200,
            "SyntheticPlanet",
            [worldspace],
            body,
            1);
        return new StarfieldPlanetWorldspaceCandidate(planet, worldspace);
    }

    private static StarfieldStarDataRecord Star(
        uint formId,
        uint systemId,
        uint? sunPresetFormId,
        uint? binaryStarFormId = null)
    {
        return new StarfieldStarDataRecord
        {
            FormId = formId,
            Routing = new StarfieldStarDataRouting
            {
                SystemId = systemId,
                BinaryStarFormId = binaryStarFormId,
                SunPresetFormId = sunPresetFormId
            }
        };
    }

    private static StarfieldSunPresetRecord Root(uint formId, float illuminance)
    {
        return new StarfieldSunPresetRecord
        {
            FormId = formId,
            PayloadKind = StarfieldSunPresetPayloadKind.FullObject,
            Patch = new StarfieldSunPresetPatch
            {
                ParentFormId = 0,
                SunColor = Color(0.1f, 0.2f, 0.3f, 1),
                SunIlluminance = illuminance,
                SunGlareColor = Color(0.4f, 0.5f, 0.6f, 1),
                SunDiskTexture = "Data/Textures/Sky/SunDisk_color.dds",
                SunDiskScreenSizeMin = 0.02f,
                SunDiskScreenSizeMax = 0.138f,
                DuskDawnPreset = new StarfieldSunPresetDawnDuskPatch
                {
                    DirectionalColor = Color(0.7f, 0.8f, 0.9f, 1),
                    TransitionStartAngle = 50,
                    TransitionEndAngle = 80
                },
                NightPreset = new StarfieldSunPresetNightPatch
                {
                    DirectionalColor = Color(10, 11, 12, 1),
                    DirectionalIlluminance = 100,
                    GlareColor = Color(0, 0, 0, 1)
                }
            }
        };
    }

    private static StarfieldSunPresetFloat4Patch Color(float x, float y, float z, float w)
    {
        return new StarfieldSunPresetFloat4Patch { X = x, Y = y, Z = z, W = w };
    }

    private static Dictionary<uint, StarfieldSunPresetRecord> SunIndex(
        params StarfieldSunPresetRecord[] records)
    {
        return records.ToDictionary(record => record.FormId);
    }
}