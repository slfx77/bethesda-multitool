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
        var vp = OrthoViewProjBuilder.BuildViewProj(Vector3.Zero, azimuthDeg: 0f, elevationDeg: 90f,
            orthoHalfHeight: 1000f, aspect: 1f);

        var west = Ndc(vp, new Vector3(-500f, 0f, 0f));
        var east = Ndc(vp, new Vector3(500f, 0f, 0f));
        Assert.True(east.X > west.X, "world +X (east) must map to a larger clip X (screen right)");

        var south = Ndc(vp, new Vector3(0f, -500f, 0f));
        var north = Ndc(vp, new Vector3(0f, 500f, 0f));
        Assert.True(north.Y > south.Y, "world +Y (north) must map to a larger clip Y (screen top)");
    }

    [Theory]
    [InlineData(90f)]   // orthographic
    [InlineData(30f)]   // isometric
    [InlineData(25.65891f)] // trimetric
    public void FocusProjectsToClipCenter(float elevationDeg)
    {
        var focus = new Vector3(4096f, -8192f, 256f);
        var vp = OrthoViewProjBuilder.BuildViewProj(focus, azimuthDeg: 45f, elevationDeg,
            orthoHalfHeight: 2000f, aspect: 1.6f);

        var center = Ndc(vp, focus);
        Assert.InRange(center.X, -0.01f, 0.01f);
        Assert.InRange(center.Y, -0.01f, 0.01f);
    }

    [Fact]
    public void ReversedZ_GeometryTowardCameraWinsDepth()
    {
        // Reversed-Z: geometry nearer the camera must produce a LARGER clip Z (GreaterEqual depth test
        // + depth cleared to 0). For a tilted view, "toward the camera" is along +toEye from the focus.
        const float az = 45f, el = 30f;
        var focus = Vector3.Zero;
        var vp = OrthoViewProjBuilder.BuildViewProj(focus, az, el, orthoHalfHeight: 2000f, aspect: 1f);

        var azR = az * (MathF.PI / 180f);
        var elR = el * (MathF.PI / 180f);
        var cosEl = MathF.Cos(elR);
        var toEye = new Vector3(cosEl * MathF.Sin(azR), cosEl * MathF.Cos(azR), MathF.Sin(elR));

        var near = Ndc(vp, toEye * 1000f);   // shifted toward the eye
        var far = Ndc(vp, toEye * -1000f);   // shifted away from the eye
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
    public void BuildCoverCylinder_CentersOnFocus()
    {
        var cyl = OrthoViewProjBuilder.BuildCoverCylinder(new Vector3(1000f, 2000f, 50f), 5000f);
        Assert.Equal(1000f, cyl.Position.X, 3);
        Assert.Equal(2000f, cyl.Position.Y, 3);
        Assert.Equal(5000f, cyl.Radius, 3);
    }
}
