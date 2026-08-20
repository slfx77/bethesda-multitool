using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

public sealed class WaterTransparencyPartitionTests
{
    private static GridProbe OneBody(float height)
    {
        return new GridProbe().Add(0, 0, height);
    }

    [Theory]
    [InlineData(-1f, true)]
    [InlineData(-0.001f, true)]
    [InlineData(0f, false)] // touching the surface is not "wholly below"
    [InlineData(1f, false)]
    public void OnlyBoundsWhollyBelowTheLocalSurfaceDrawBeforeWater(float maxZ, bool expected)
    {
        Assert.Equal(
            expected,
            WaterTransparencyPartition.IsWhollyBelow(OneBody(0f), 100f, 100f, maxZ, 500f));
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
            WaterTransparencyPartition.IsWhollyBelow(OneBody(1000f), x, y, maxZ, 5000f));
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
        Assert.False(WaterTransparencyPartition.IsWhollyBelow(probe, 100f, 100f, 2000f, 2500f));
        Assert.True(WaterTransparencyPartition.IsWhollyBelow(probe, 100f, 100f, 2000f, 3500f));
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
            WaterTransparencyPartition.IsWhollyBelow(OneBody(1000f), 10f, 10f, 1010f, 2000f));
    }

    // ── Wholly-above-all-water (the unified stream's post-drain partition) ────────────────────

    /// <summary>
    ///     The reported defect: smoke plumes standing well above Lake Mead had the camera-cell
    ///     water quad composited over them, because the quad's CENTROID sort key is nearer than
    ///     the plume while the surface itself can never occlude geometry that is above it. A draw
    ///     whose bounds bottom and camera both clear the highest queued surface must classify into
    ///     the after-water pass.
    /// </summary>
    [Theory]
    [InlineData(8400f, true)] // bottom above the 8305 surface → drawn after all water
    [InlineData(8305f, false)] // touching the plane is not "wholly above"
    [InlineData(8200f, false)] // dips below the highest surface → stays interleaved
    public void OnlyBoundsWhollyAboveTheHighestQueuedSurfaceDrawAfterWater(
        float minZ, bool expected)
    {
        Assert.Equal(
            expected,
            WaterTransparencyPartition.IsWhollyAboveAllWater(
                minZ, 9115f, 8305f));
    }

    /// <summary>
    ///     A camera at or below the highest surface can see above-water geometry THROUGH a nearer
    ///     surface (looking out from under a higher body), so the reorder must not fire — the
    ///     mirror of the submerged partition's camera guard.
    /// </summary>
    [Theory]
    [InlineData(8000f)] // camera below the plane
    [InlineData(8305f)] // camera exactly at the plane
    public void ACameraNotAboveTheHighestSurfaceKeepsTheInterleavedOrder(float cameraZ)
    {
        Assert.False(
            WaterTransparencyPartition.IsWhollyAboveAllWater(
                8400f, cameraZ, 8305f));
    }

    /// <summary>
    ///     NaN is the "no water queued this frame" sentinel from the water renderer; the class must
    ///     be empty then (nothing to reorder around), and bad draw bounds must fail closed to the
    ///     interleaved status quo.
    /// </summary>
    [Theory]
    [InlineData(float.NaN, 9115f, 8305f)]
    [InlineData(8400f, float.NaN, 8305f)]
    [InlineData(8400f, 9115f, float.NaN)]
    public void NonFiniteInputsNeverClassifyAboveAllWater(
        float minZ, float cameraZ, float maxSurfaceZ)
    {
        Assert.False(WaterTransparencyPartition.IsWhollyAboveAllWater(minZ, cameraZ, maxSurfaceZ));
    }

    /// <summary>
    ///     Disjointness with the submerged partition, on the Hoover Dam shape that motivates the
    ///     global test: the reservoir (8305) is QUEUED and high, the downstream river (7100) is the
    ///     draw's LOCAL body. Geometry above the river but below the reservoir top can genuinely
    ///     sit behind the reservoir's surface along a through-water sightline, so it must stay in
    ///     the interleaved middle — neither wholly below its local surface nor above all water.
    /// </summary>
    [Fact]
    public void GeometryBelowADistantHigherSurfaceStaysInTheInterleavedMiddle()
    {
        var probe = new GridProbe().Add(0, 0, 7100f).Add(2, 0, 8305f);

        // Downstream mist at 7500: above its local river, below the reservoir surface.
        Assert.False(
            WaterTransparencyPartition.IsWhollyBelow(probe, 100f, 100f, 7800f, 9115f));
        Assert.False(
            WaterTransparencyPartition.IsWhollyAboveAllWater(
                7500f, 9115f, 8305f));
    }

    /// <summary>A probe with one water body per grid cell, keyed on a 4096-unit grid.</summary>
    private sealed class GridProbe : IWaterHeightProbe
    {
        private readonly Dictionary<(int, int), float> _heights = new();

        public bool TryGetWaterHeightAt(float worldX, float worldY, out float height)
        {
            return _heights.TryGetValue(
                ((int)MathF.Floor(worldX / 4096f), (int)MathF.Floor(worldY / 4096f)), out height);
        }

        internal GridProbe Add(int gx, int gy, float height)
        {
            _heights[(gx, gy)] = height;
            return this;
        }
    }
}