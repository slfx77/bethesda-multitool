using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

public sealed class WaterTransparencyPartitionTests
{
    /// <summary>A probe with one water body per grid cell, keyed on a 4096-unit grid.</summary>
    private sealed class GridProbe : IWaterHeightProbe
    {
        private readonly Dictionary<(int, int), float> _heights = new();

        internal GridProbe Add(int gx, int gy, float height)
        {
            _heights[(gx, gy)] = height;
            return this;
        }

        public bool TryGetWaterHeightAt(float worldX, float worldY, out float height) =>
            _heights.TryGetValue(
                ((int)MathF.Floor(worldX / 4096f), (int)MathF.Floor(worldY / 4096f)), out height);
    }

    private static GridProbe OneBody(float height) => new GridProbe().Add(0, 0, height);

    [Theory]
    [InlineData(-1f, true)]
    [InlineData(-0.001f, true)]
    [InlineData(0f, false)] // touching the surface is not "wholly below"
    [InlineData(1f, false)]
    public void OnlyBoundsWhollyBelowTheLocalSurfaceDrawBeforeWater(float maxZ, bool expected)
    {
        Assert.Equal(
            expected,
            WaterTransparencyPartition.IsWhollyBelow(OneBody(0f), 100f, 100f, maxZ, cameraZ: 500f));
    }

    [Fact]
    public void GeometryWhereThereIsNoWaterIsNeverSubmerged()
    {
        // XY falls in a grid cell the probe knows nothing about.
        Assert.False(
            WaterTransparencyPartition.IsWhollyBelow(OneBody(1000f), 99999f, 99999f, -5000f, 5000f));
    }

    [Theory]
    [InlineData(float.NaN, 0f, 0f)]
    [InlineData(0f, float.NaN, 0f)]
    [InlineData(0f, 0f, float.NaN)]
    public void NonFiniteInputsStayInThePostWaterComplement(float x, float y, float maxZ)
    {
        Assert.False(
            WaterTransparencyPartition.IsWhollyBelow(OneBody(1000f), x, y, maxZ, cameraZ: 5000f));
    }

    /// <summary>
    ///     The regression this exists for. The predicate used to take a single global plane computed
    ///     as the MAXIMUM height over every gathered water cell — a radius spanning the whole render
    ///     distance with no frustum or Z bound. A distant elevated body then decided the plane for
    ///     every draw, and the accompanying camera-above-plane guard disabled the split outright.
    ///     Measured at Lake Mead: local surface 3000, global max 5600, camera 3200 — the split went
    ///     dead and submerged decals composited over the water. A local lookup is immune.
    /// </summary>
    [Fact]
    public void ADistantElevatedWaterBodyDoesNotAffectClassificationHere()
    {
        // Cell (0,0) is the river at 3000; cell (5,5) holds an elevated pool at 5600.
        var probe = new GridProbe().Add(0, 0, 3000f).Add(5, 5, 5600f);
        const float cameraZ = 3200f; // above the river, BELOW the distant pool

        Assert.True(
            WaterTransparencyPartition.IsWhollyBelow(probe, 100f, 100f, 2950f, cameraZ),
            "a decal on the river bed must still be submerged despite higher water elsewhere");
    }

    /// <summary>
    ///     The camera test is per water body, not global: standing under one surface must not flip
    ///     the classification of geometry beneath a different one. With a submerged camera everything
    ///     would otherwise classify as "below" and a distant water quad would composite over
    ///     near-camera underwater effects — the mirror image of the bug being fixed.
    /// </summary>
    [Fact]
    public void GeometryUnderTheSurfaceTheCameraIsSubmergedInIsNotReordered()
    {
        var probe = OneBody(3000f);
        Assert.False(WaterTransparencyPartition.IsWhollyBelow(probe, 100f, 100f, 2000f, cameraZ: 2500f));
        Assert.True(WaterTransparencyPartition.IsWhollyBelow(probe, 100f, 100f, 2000f, cameraZ: 3500f));
    }

    /// <summary>
    ///     Classification is by the geometry's world-space TOP, not a bounding-sphere apex. FNV's
    ///     blend decals carry NiBound radii of 70-125 units (ssLogoDecal 70.7, DamageDecal01 125.5)
    ///     while being nearly flat, so a decal lying a few units under the surface failed a
    ///     "centre + radius &lt; plane" test and composited on top of the water.
    /// </summary>
    [Fact]
    public void FlatDecalJustBelowSurfaceIsSubmergedDespiteALargeInPlaneRadius()
    {
        var probe = OneBody(1000f);
        const float centreZ = 995f;
        const float sphereRadius = 125.5f; // DamageDecal01 — an in-plane diagonal, not a height
        const float trueTopZ = 995.5f;

        Assert.False(
            WaterTransparencyPartition.IsWhollyBelow(probe, 10f, 10f, centreZ + sphereRadius, 2000f),
            "the old sphere-apex test misclassified this decal as above water");
        Assert.True(WaterTransparencyPartition.IsWhollyBelow(probe, 10f, 10f, trueTopZ, 2000f));
    }

    /// <summary>
    ///     The complementary guarantee: geometry that genuinely straddles the surface stays in the
    ///     post-water pass, or water would be composited over its upper half.
    /// </summary>
    [Fact]
    public void GeometryStraddlingTheSurfaceStaysInThePostWaterPass()
    {
        Assert.False(
            WaterTransparencyPartition.IsWhollyBelow(OneBody(1000f), 10f, 10f, 1010f, cameraZ: 2000f));
    }
}
