using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Locks the orientation contract of <see cref="TopDownViewProjBuilder" /> (the 2D map's
///     top-down "Rendered models" overlay) without needing a GPU. The shader consumes the
///     view-projection as <c>mul(uViewProj, worldPos)</c>, which — with the System.Numerics
///     row-major upload Direct3D transposes on load — equals <c>Vector4.Transform(worldPos, vp)</c>
///     on the CPU, so these transforms replicate the GPU's clip coordinates. East must map to screen
///     right and north to the top (D3D clip +Y = render-target row 0), matching the 2D map's canvas
///     convention (X right, north up) so the readback needs no flip.
/// </summary>
public sealed class TopDownViewProjBuilderTests
{
    private static Vector4 Clip(Matrix4x4 vp, float worldX, float worldNorthY)
    {
        return Vector4.Transform(new Vector4(worldX, worldNorthY, 0f, 1f), vp);
    }

    [Fact]
    public void BuildViewProj_EastMapsToScreenRight()
    {
        var vp = TopDownViewProjBuilder.BuildViewProj(0f, 1000f, 0f, 1000f);
        var west = Clip(vp, 0f, 500f);
        var east = Clip(vp, 1000f, 500f);
        Assert.True(east.X / east.W > west.X / west.W,
            "world +X (east) must map to a larger clip X (screen right)");
    }

    [Fact]
    public void BuildViewProj_NorthMapsToScreenTop()
    {
        var vp = TopDownViewProjBuilder.BuildViewProj(0f, 1000f, 0f, 1000f);
        var south = Clip(vp, 500f, 0f);
        var north = Clip(vp, 500f, 1000f);
        Assert.True(north.Y / north.W > south.Y / south.W,
            "world +Y (north) must map to a larger clip Y (screen top / readback row 0)");
    }

    [Fact]
    public void BuildViewProj_RectCornersFillNdc()
    {
        var vp = TopDownViewProjBuilder.BuildViewProj(-2000f, 6000f, 1000f, 9000f);
        var sw = Clip(vp, -2000f, 1000f);
        var ne = Clip(vp, 6000f, 9000f);
        Assert.InRange(sw.X / sw.W, -1.001f, -0.999f);
        Assert.InRange(sw.Y / sw.W, -1.001f, -0.999f);
        Assert.InRange(ne.X / ne.W, 0.999f, 1.001f);
        Assert.InRange(ne.Y / ne.W, 0.999f, 1.001f);
    }

    [Fact]
    public void BuildViewProj_HigherZIsNearerCamera()
    {
        // Reversed-Z: taller geometry must win the GreaterEqual depth test → LARGER clip Z.
        var vp = TopDownViewProjBuilder.BuildViewProj(0f, 1000f, 0f, 1000f);
        var low = Clip3(vp, 500f, 500f, 0f);
        var high = Clip3(vp, 500f, 500f, 5000f);
        Assert.True(high.Z / high.W > low.Z / low.W,
            "higher world Z must produce a larger clip Z (reversed-Z: nearer the top-down camera wins GreaterEqual)");

        static Vector4 Clip3(Matrix4x4 vp, float x, float y, float z)
        {
            return Vector4.Transform(new Vector4(x, y, z, 1f), vp);
        }
    }

    [Fact]
    public void BuildCoverCylinder_CentersAndCoversRect()
    {
        var cyl = TopDownViewProjBuilder.BuildCoverCylinder(0f, 1000f, 0f, 2000f, 100f);
        Assert.Equal(500f, cyl.Position.X, 3);
        Assert.Equal(1000f, cyl.Position.Y, 3);
        var halfDiag = MathF.Sqrt(500f * 500f + 1000f * 1000f);
        Assert.True(cyl.Radius >= halfDiag, "radius must reach the rect corners");
        Assert.Equal(halfDiag + 100f, cyl.Radius, 2);
    }
}