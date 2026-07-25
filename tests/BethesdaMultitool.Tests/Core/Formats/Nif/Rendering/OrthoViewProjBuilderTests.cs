using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Locks the orientation + depth contract of <see cref="OrthoViewProjBuilder" /> (the 3D viewer's
///     Orthographic / Isometric / Trimetric projection modes and the 3D export) without a GPU. The
///     shader consumes the view-projection as <c>mul(uViewProj, worldPos)</c>, which — with the
///     System.Numerics row-major upload Direct3D transposes on load — equals
///     <c>Vector4.Transform(worldPos, vp)</c> on the CPU, so these transforms replicate the GPU's clip
///     coordinates.
/// </summary>
public sealed class OrthoViewProjBuilderTests
{
    // ── CoverRadius: ground footprint + terrain-relief parallax ────────────────────────────────

    private const float CellSize = 4096f;

    private static Vector3 Ndc(Matrix4x4 vp, Vector3 world)
    {
        var c = Vector4.Transform(new Vector4(world, 1f), vp);
        return new Vector3(c.X / c.W, c.Y / c.W, c.Z / c.W);
    }

    [Fact]
    public void TopDown_EastMapsRight_NorthMapsTop()
    {
        // Orthographic mode is straight down (90°); az 0 = north-up. Must match the 2D map's top-down
        // convention (east → screen right, north → screen top), i.e. TopDownViewProjBuilder.
        var vp = OrthoViewProjBuilder.BuildViewProj(Vector3.Zero, 0f, 90f,
            1000f, 1f);

        var west = Ndc(vp, new Vector3(-500f, 0f, 0f));
        var east = Ndc(vp, new Vector3(500f, 0f, 0f));
        Assert.True(east.X > west.X, "world +X (east) must map to a larger clip X (screen right)");

        var south = Ndc(vp, new Vector3(0f, -500f, 0f));
        var north = Ndc(vp, new Vector3(0f, 500f, 0f));
        Assert.True(north.Y > south.Y, "world +Y (north) must map to a larger clip Y (screen top)");
    }

    [Theory]
    [InlineData(90f)] // orthographic
    [InlineData(30f)] // isometric
    [InlineData(25.65891f)] // trimetric
    public void FocusProjectsToClipCenter(float elevationDeg)
    {
        var focus = new Vector3(4096f, -8192f, 256f);
        var vp = OrthoViewProjBuilder.BuildViewProj(focus, 45f, elevationDeg,
            2000f, 1.6f);

        var center = Ndc(vp, focus);
        Assert.InRange(center.X, -0.01f, 0.01f);
        Assert.InRange(center.Y, -0.01f, 0.01f);
    }

    [Fact]
    public void TiltedView_RenderEyeIsDistinctFromCoverCylinderCenter()
    {
        // The cylinder is a footprint-centered streaming/culling volume, not a camera pose. Render
        // consumers such as particle ordering, billboard facing, and SpeedTree LOD must use the eye.
        var focus = new Vector3(4096f, -8192f, 256f);
        const float azimuth = 45f;
        const float elevation = 30f;
        var eye = OrthoViewProjBuilder.EyePosition(focus, azimuth, elevation);
        var expectedEye = focus +
                          OrthoViewProjBuilder.EyeDirection(azimuth, elevation) *
                          OrthoViewProjBuilder.EyeDistance;
        var cylinder = OrthoViewProjBuilder.BuildCoverCylinder(focus, 2048f);

        Assert.InRange(Vector3.Distance(eye, expectedEye), 0f, 0.01f);
        Assert.True(Vector3.Distance(eye, cylinder.Position) > 100_000f,
            "a tilted view's cull center must not be substituted for its rendering eye");
    }

    [Fact]
    public void ReversedZ_GeometryTowardCameraWinsDepth()
    {
        // Reversed-Z: geometry nearer the camera must produce a LARGER clip Z (GreaterEqual depth test
        // + depth cleared to 0). For a tilted view, "toward the camera" is along +toEye from the focus.
        const float az = 45f, el = 30f;
        var focus = Vector3.Zero;
        var vp = OrthoViewProjBuilder.BuildViewProj(focus, az, el, 2000f, 1f);

        var azR = az * (MathF.PI / 180f);
        var elR = el * (MathF.PI / 180f);
        var cosEl = MathF.Cos(elR);
        var toEye = new Vector3(cosEl * MathF.Sin(azR), cosEl * MathF.Cos(azR), MathF.Sin(elR));

        var near = Ndc(vp, toEye * 1000f); // shifted toward the eye
        var far = Ndc(vp, toEye * -1000f); // shifted away from the eye
        Assert.True(near.Z > far.Z,
            "geometry nearer the camera must produce a larger clip Z (reversed-Z wins GreaterEqual)");
    }

    [Fact]
    public void ElevationDegFor_ReturnsModePresets()
    {
        Assert.Equal(0f, OrthoViewProjBuilder.ElevationDegFor(ProjectionMode.None));
        Assert.Equal(90f, OrthoViewProjBuilder.ElevationDegFor(ProjectionMode.Orthographic));
        Assert.Equal(30f, OrthoViewProjBuilder.ElevationDegFor(ProjectionMode.Isometric));
        Assert.Equal(25.65891f, OrthoViewProjBuilder.ElevationDegFor(ProjectionMode.Trimetric));
    }

    [Fact]
    public void AzimuthDegFor_OrthographicSnapsToCardinals()
    {
        Assert.Equal(0f, OrthoViewProjBuilder.AzimuthDegFor(ProjectionMode.Orthographic, 0));
        Assert.Equal(90f, OrthoViewProjBuilder.AzimuthDegFor(ProjectionMode.Orthographic, 1));
        Assert.Equal(180f, OrthoViewProjBuilder.AzimuthDegFor(ProjectionMode.Orthographic, 2));
        Assert.Equal(270f, OrthoViewProjBuilder.AzimuthDegFor(ProjectionMode.Orthographic, 3));
        // Wrap-around (negative + overflow) stays in [0, 360).
        Assert.Equal(270f, OrthoViewProjBuilder.AzimuthDegFor(ProjectionMode.Orthographic, -1));
        Assert.Equal(0f, OrthoViewProjBuilder.AzimuthDegFor(ProjectionMode.Orthographic, 4));
    }

    [Fact]
    public void AzimuthDegFor_IsometricAndTrimetricSnapToDiagonals()
    {
        Assert.Equal(45f, OrthoViewProjBuilder.AzimuthDegFor(ProjectionMode.Isometric, 0));
        Assert.Equal(135f, OrthoViewProjBuilder.AzimuthDegFor(ProjectionMode.Isometric, 1));
        Assert.Equal(45f, OrthoViewProjBuilder.AzimuthDegFor(ProjectionMode.Trimetric, 0));
        Assert.Equal(315f, OrthoViewProjBuilder.AzimuthDegFor(ProjectionMode.Trimetric, 3));
    }

    [Fact]
    public void Orthographic_AzimuthRollsImage_EastToTop()
    {
        // At the top-down extreme the azimuth becomes the image ROLL (the ◄ ► rotate + the export's
        // N/E/S/W "what's at the top"). az 90 must put EAST at the top — a fixed north-up would ignore
        // the azimuth there, leaving the rotate buttons inert in orthographic mode.
        var vp = OrthoViewProjBuilder.BuildViewProj(Vector3.Zero, 90f, 90f,
            1000f, 1f);

        var east = Ndc(vp, new Vector3(500f, 0f, 0f));
        var west = Ndc(vp, new Vector3(-500f, 0f, 0f));
        Assert.True(east.Y > west.Y, "world +X (east) must map to the image top (larger clip Y) at azimuth 90");
    }

    [Fact]
    public void BuildViewProjTile_TopLeftTile_CentersOnNorthwestQuadrant()
    {
        // North-up top-down (az 0), 2×2 tiling: the top-left tile (col 0, row 0) covers the west half
        // (X∈[-1000,0]) × north half (Y∈[0,1000]); that sub-rect's center (−500, +500) must land at the
        // tile's clip center. tileRow counts top→bottom, so row 0 is the NORTH (top) band.
        var vp = OrthoViewProjBuilder.BuildViewProjTile(Vector3.Zero, 0f, 90f, 1000f, 1f,
            0, 0, 2, 2);

        var center = Ndc(vp, new Vector3(-500f, 500f, 0f));
        Assert.InRange(center.X, -0.01f, 0.01f);
        Assert.InRange(center.Y, -0.01f, 0.01f);
    }

    [Fact]
    public void BuildViewProjTile_IsometricTilesSubdivideClipExactly()
    {
        // The seam-free property for TILTED views: a tile renders an off-center sub-rectangle of the
        // SAME global ortho clip volume, so tiles stitch at any elevation. A world point at the full
        // frame's (−0.5, +0.5) clip — the center of the top-left quadrant — must map to the top-left
        // tile's clip center (0, 0).
        const float az = 45f, el = 30f, half = 1000f;
        var focus = new Vector3(123f, -456f, 78f);
        var full = OrthoViewProjBuilder.BuildViewProj(focus, az, el, half, 1f);
        var tile = OrthoViewProjBuilder.BuildViewProjTile(focus, az, el, half, 1f, 0, 0, 2, 2);

        var (right, up) = OrthoViewProjBuilder.CameraBasis(az, el);
        var p = focus - right * (half * 0.5f) + up * (half * 0.5f); // view-space (−500, +500)

        var cFull = Ndc(full, p);
        Assert.InRange(cFull.X, -0.51f, -0.49f);
        Assert.InRange(cFull.Y, 0.49f, 0.51f);

        var cTile = Ndc(tile, p);
        Assert.InRange(cTile.X, -0.02f, 0.02f);
        Assert.InRange(cTile.Y, -0.02f, 0.02f);
    }

    [Fact]
    public void SnapAzimuth_Aligned_StepsFull90()
    {
        // Orthographic increments are the cardinals 0/90/180/270. Stepping from an aligned position is a
        // clean 90° turn each way (with [0,360) wrap).
        Assert.Equal(90f, OrthoViewProjBuilder.SnapAzimuth(0f, ProjectionMode.Orthographic, +1), 3);
        Assert.Equal(270f, OrthoViewProjBuilder.SnapAzimuth(0f, ProjectionMode.Orthographic, -1), 3);
        Assert.Equal(0f, OrthoViewProjBuilder.SnapAzimuth(270f, ProjectionMode.Orthographic, +1), 3);
    }

    [Fact]
    public void SnapAzimuth_OffAxis_SnapsToNearestIncrementInArrowDirection()
    {
        // After a free Shift+drag the azimuth sits off the increments; the next ◄ ► click snaps to the
        // bounding increment in the arrow's direction (right → above, left → below), not a full 90° step.
        Assert.Equal(90f, OrthoViewProjBuilder.SnapAzimuth(30f, ProjectionMode.Orthographic, +1), 3);
        Assert.Equal(0f, OrthoViewProjBuilder.SnapAzimuth(30f, ProjectionMode.Orthographic, -1), 3);
        Assert.Equal(180f, OrthoViewProjBuilder.SnapAzimuth(95f, ProjectionMode.Orthographic, +1), 3);
        Assert.Equal(90f, OrthoViewProjBuilder.SnapAzimuth(95f, ProjectionMode.Orthographic, -1), 3);
    }

    [Fact]
    public void SnapAzimuth_IsoTri_UsesDiagonalIncrements()
    {
        // Iso/tri increments are the diagonals 45/135/225/315. Off-axis 100° snaps right → 135, left → 45.
        Assert.Equal(135f, OrthoViewProjBuilder.SnapAzimuth(100f, ProjectionMode.Isometric, +1), 3);
        Assert.Equal(45f, OrthoViewProjBuilder.SnapAzimuth(100f, ProjectionMode.Isometric, -1), 3);
        // Aligned diagonal steps a full 90°.
        Assert.Equal(225f, OrthoViewProjBuilder.SnapAzimuth(135f, ProjectionMode.Trimetric, +1), 3);
    }

    [Fact]
    public void SlidingFocusAlongEyeDirection_PreservesProjectedXy()
    {
        // The viewer's image-preserving "seat focus on the ground" re-seat relies on this invariant:
        // translating the ortho look-at focus (the eye is recomputed from it) along the eye direction
        // changes only depth, never the projected XY. So a world point's clip XY must be unchanged when
        // the focus slides along EyeDirection — that's why the re-seat can run on every pan without jump.
        const float az = 45f, el = 30f, half = 2000f, aspect = 1.6f;
        var focus = new Vector3(1000f, -500f, 200f);
        var p = new Vector3(1500f, -200f, 350f);

        var before = Ndc(OrthoViewProjBuilder.BuildViewProj(focus, az, el, half, aspect), p);
        var slidFocus = focus + OrthoViewProjBuilder.EyeDirection(az, el) * 5000f;
        var after = Ndc(OrthoViewProjBuilder.BuildViewProj(slidFocus, az, el, half, aspect), p);

        Assert.Equal(before.X, after.X, 2);
        Assert.Equal(before.Y, after.Y, 2);
        // Depth DOES change (the camera moved along its view axis) — the whole point is XY is preserved.
        Assert.True(MathF.Abs(before.Z - after.Z) > 1e-3f,
            "depth should change as the focus slides along the view axis");
    }

    [Fact]
    public void BuildCoverCylinder_CentersOnFocus()
    {
        var cyl = OrthoViewProjBuilder.BuildCoverCylinder(new Vector3(1000f, 2000f, 50f), 5000f);
        Assert.Equal(1000f, cyl.Position.X, 3);
        Assert.Equal(2000f, cyl.Position.Y, 3);
        Assert.Equal(5000f, cyl.Radius, 3);
    }

    /// <summary>The pre-relief formula: visible rectangle's ground-footprint diagonal + 2 cells slack.</summary>
    private static float FlatFootprintRadius(float halfH, float aspect, float elevationDeg)
    {
        var halfW = halfH * MathF.Max(aspect, 1e-4f);
        var sinEl = MathF.Max(MathF.Sin(elevationDeg * (MathF.PI / 180f)), 0.1f);
        var groundHalfH = halfH / sinEl;
        return MathF.Sqrt(halfW * halfW + groundHalfH * groundHalfH) + 2f * CellSize;
    }

    [Theory]
    [InlineData(90f)]
    [InlineData(30f)]
    [InlineData(25.65891f)]
    public void CoverRadius_NoRelief_MatchesFlatFootprintFormula(float elevationDeg)
    {
        var radius = OrthoViewProjBuilder.CoverRadius(
            2048f, 1.6f, elevationDeg, CellSize, 0f, 0f);
        Assert.Equal(FlatFootprintRadius(2048f, 1.6f, elevationDeg), radius, 1);
    }

    [Fact]
    public void CoverRadius_TopDown_IgnoresRelief()
    {
        // cot(90°) is a tiny negative in float — the clamp must make top-down EXACTLY parallax-free,
        // so the orthographic mode's radius (and its streamed set) is unchanged by this feature.
        var flat = OrthoViewProjBuilder.CoverRadius(2048f, 1f, 90f, CellSize, 0f, 0f);
        var withRelief = OrthoViewProjBuilder.CoverRadius(2048f, 1f, 90f, CellSize, 30_000f, 30_000f);
        Assert.Equal(flat, withRelief);
    }

    [Fact]
    public void CoverRadius_TiltedRelief_CoversOnScreenPeak()
    {
        // THE regression: a peak Δz above the focus plane stays on screen while its
        // horizontal distance toward the camera reaches halfH/sin(el) + Δz·cot(el) — beyond the flat
        // footprint radius, so its whole cell culled while visible. Build that worst-case point (bottom
        // screen edge, camera side), prove it renders inside NDC, and assert the old radius missed it
        // while the relief-aware radius covers it. az 0 puts the camera due north, so the point's
        // Chebyshev distance from the focus equals its straight-line distance — no diagonal discount.
        const float az = 0f, el = 30f, halfH = 2048f, aspect = 1f, dz = 8000f;
        var focus = Vector3.Zero;
        var sinEl = MathF.Sin(el * (MathF.PI / 180f));
        var cotEl = MathF.Cos(el * (MathF.PI / 180f)) / sinEl;
        var d = halfH / sinEl + dz * cotEl - 2f; // 2 units inside the bottom screen edge
        var peak = new Vector3(0f, d, dz); // toward the camera (north), Δz above the focus plane

        var vp = OrthoViewProjBuilder.BuildViewProj(focus, az, el, halfH, aspect);
        var ndc = Ndc(vp, peak);
        Assert.InRange(ndc.X, -0.01f, 0.01f);
        Assert.InRange(ndc.Y, -1f, -0.98f); // on screen, at the bottom edge

        var oldRadius = OrthoViewProjBuilder.CoverRadius(halfH, aspect, el, CellSize, 0f, 0f);
        var newRadius = OrthoViewProjBuilder.CoverRadius(halfH, aspect, el, CellSize, dz, 0f);
        Assert.True(d > oldRadius,
            $"bug demo: on-screen peak at {d:F0} must lie OUTSIDE the flat radius {oldRadius:F0}");
        Assert.True(d < newRadius, $"fix: relief-aware radius {newRadius:F0} must cover the on-screen peak at {d:F0}");
    }

    [Fact]
    public void CoverRadius_UsesLargerOfAboveAndBelowRelief()
    {
        // Valleys at the top screen edge are the mirror case of peaks at the bottom — both directions
        // extend the reach, and a stale focus.Z is absorbed by taking the max.
        var above = OrthoViewProjBuilder.CoverRadius(2048f, 1f, 30f, CellSize, 8000f, 0f);
        var below = OrthoViewProjBuilder.CoverRadius(2048f, 1f, 30f, CellSize, 0f, 8000f);
        Assert.Equal(above, below);
    }

    [Fact]
    public void CoverRadius_ReliefReach_IsCappedAt16Cells()
    {
        // Extreme relief must not stream half the map: the parallax reach saturates at 16 cells.
        var flat = OrthoViewProjBuilder.CoverRadius(2048f, 1f, 30f, CellSize, 0f, 0f);
        var capped = OrthoViewProjBuilder.CoverRadius(2048f, 1f, 30f, CellSize, 1_000_000f, 0f);
        Assert.Equal(flat + 16f * CellSize, capped, 1);
    }
}