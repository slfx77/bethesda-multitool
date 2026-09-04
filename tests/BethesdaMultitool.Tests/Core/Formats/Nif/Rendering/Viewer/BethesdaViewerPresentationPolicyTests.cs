using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Viewer;

public sealed class BethesdaViewerPresentationPolicyTests
{
    // The purpose enum is internal, so a public theory parameter cannot name it (CS0051).
    // Passing the ordinal and casting is the repo's established workaround.
    [Theory]
    [InlineData((int)BethesdaViewerScenePurpose.NpcAppearance)]
    [InlineData((int)BethesdaViewerScenePurpose.CreatureAppearance)]
    public void ActorAppearanceStartsHeadOn(int purposeValue)
    {
        var orbit = BethesdaViewerPresentationPolicy.ResolveInitialOrbit(
            (BethesdaViewerScenePurpose)purposeValue,
            dedicatedRawSky: false);

        Assert.Equal(90f, orbit.AzimuthDegrees);
        Assert.Equal(0f, orbit.ElevationDegrees);
    }

    [Theory]
    [InlineData((int)BethesdaViewerScenePurpose.Unspecified)]
    [InlineData((int)BethesdaViewerScenePurpose.RawNif)]
    [InlineData((int)BethesdaViewerScenePurpose.WorldReference)]
    public void NonActorSceneRetainsThreeQuarterDefault(int purposeValue)
    {
        var orbit = BethesdaViewerPresentationPolicy.ResolveInitialOrbit(
            (BethesdaViewerScenePurpose)purposeValue,
            dedicatedRawSky: false);

        Assert.Equal(315f, orbit.AzimuthDegrees);
        Assert.Equal(30f, orbit.ElevationDegrees);
    }

    [Fact]
    public void DedicatedRawSkyRetainsHorizonFramingOverride()
    {
        var orbit = BethesdaViewerPresentationPolicy.ResolveInitialOrbit(
            BethesdaViewerScenePurpose.RawNif,
            dedicatedRawSky: true);

        Assert.Equal(315f, orbit.AzimuthDegrees);
        Assert.Equal(-30f, orbit.ElevationDegrees);
    }

    [Theory]
    [InlineData((int)BethesdaViewerScenePurpose.NpcAppearance)]
    [InlineData((int)BethesdaViewerScenePurpose.CreatureAppearance)]
    public void ActorPointerOrbitUsesRequestedDirectManipulationDirection(int purposeValue)
    {
        var degrees = BethesdaViewerPresentationPolicy.OrbitDegreesForPointerDelta(
            new Vector2(20f, 10f),
            (BethesdaViewerScenePurpose)purposeValue);

        Assert.Equal(new Vector2(7f, -3.5f), degrees);
    }

    [Fact]
    public void RawMeshPointerOrbitRetainsInspectionDirection()
    {
        var degrees = BethesdaViewerPresentationPolicy.OrbitDegreesForPointerDelta(
            new Vector2(20f, 10f),
            BethesdaViewerScenePurpose.RawNif);

        Assert.Equal(new Vector2(-7f, 3.5f), degrees);
    }

    [Theory]
    [InlineData(float.NaN, 1f)]
    [InlineData(1f, float.PositiveInfinity)]
    public void NonFinitePointerOrbitIsIgnored(float x, float y)
    {
        Assert.Equal(
            Vector2.Zero,
            BethesdaViewerPresentationPolicy.OrbitDegreesForPointerDelta(
                new Vector2(x, y),
                BethesdaViewerScenePurpose.NpcAppearance));
    }

    [Theory]
    [InlineData((int)BethesdaViewerScenePurpose.NpcAppearance)]
    [InlineData((int)BethesdaViewerScenePurpose.CreatureAppearance)]
    public void ActorSceneClearTonemapsToLegacyPurpleNavyBackground(int purposeValue)
    {
        var sceneClear = BethesdaViewerPresentationPolicy.ResolveSceneClearColor(
            (BethesdaViewerScenePurpose)purposeValue);
        var displayed = new Vector3(
            ApplyGammaAces(sceneClear.X),
            ApplyGammaAces(sceneClear.Y),
            ApplyGammaAces(sceneClear.Z));

        Assert.Equal(26f / 255f, displayed.X, 5);
        Assert.Equal(26f / 255f, displayed.Y, 5);
        Assert.Equal(46f / 255f, displayed.Z, 5);
        Assert.Equal(1f, sceneClear.W);
    }

    [Fact]
    public void RawMeshSceneClearRetainsNeutralInspectionBackground()
    {
        Assert.Equal(
            new Vector4(0.025f, 0.03f, 0.04f, 1f),
            BethesdaViewerPresentationPolicy.ResolveSceneClearColor(
                BethesdaViewerScenePurpose.RawNif));
    }

    private static float ApplyGammaAces(float gammaEncodedSceneValue)
    {
        var linear = MathF.Pow(gammaEncodedSceneValue, 2.2f);
        var mapped = Math.Clamp(
            linear * (2.51f * linear + 0.03f) /
            (linear * (2.43f * linear + 0.59f) + 0.14f),
            0f,
            1f);
        return MathF.Pow(mapped, 1f / 2.2f);
    }
}
