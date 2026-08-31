using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Terrain;

public sealed class TerrainCellDrawCullingTests
{
    private static readonly TerrainCellHeightBounds UnitHeightBounds = new(0f, 1f);

    [Fact]
    public void HeightBounds_FromVertices_TracksExactUploadedExtrema()
    {
        TerrainVertex[] vertices =
        [
            new(17.25f, Vector3.UnitZ, 0xFFFFFFFF),
            new(-123.5f, Vector3.UnitZ, 0xFFFFFFFF),
            new(987.75f, Vector3.UnitZ, 0xFFFFFFFF),
            new(-0f, Vector3.UnitZ, 0xFFFFFFFF),
        ];

        var bounds = TerrainCellHeightBounds.FromVertices(vertices);

        Assert.Equal(-123.5f, bounds.MinWorldZ);
        Assert.Equal(987.75f, bounds.MaxWorldZ);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void HeightBounds_NonFiniteVertex_InvalidatesWholeRange(float malformedHeight)
    {
        TerrainVertex[] vertices =
        [
            new(-20f, Vector3.UnitZ, 0xFFFFFFFF),
            new(malformedHeight, Vector3.UnitZ, 0xFFFFFFFF),
            new(30f, Vector3.UnitZ, 0xFFFFFFFF),
        ];

        var bounds = TerrainCellHeightBounds.FromVertices(vertices);

        Assert.True(float.IsNaN(bounds.MinWorldZ));
        Assert.True(float.IsNaN(bounds.MaxWorldZ));
    }

    [Fact]
    public void ShouldDraw_RejectsOnlyAabbWhollyOutsideFrustum()
    {
        var context = new TerrainCellDrawFrustum(
            Frustum.FromViewProjection(Matrix4x4.Identity),
            Vector3.Zero);

        Assert.True(TerrainCellDrawCulling.ShouldDraw(
            context,
            new TerrainCellGrid(-0.5f, -0.5f, 1f, 2),
            UnitHeightBounds));

        // The west edge lies exactly on x=+1. Frustum.IntersectsAabb treats plane contact as an
        // intersection, so this cell must survive.
        Assert.True(TerrainCellDrawCulling.ShouldDraw(
            context,
            new TerrainCellGrid(1f, -0.5f, 0.25f, 2),
            UnitHeightBounds));

        // A cell only half a unit beyond the mathematical plane survives the explicit one-unit
        // numeric guard. This is deliberately conservative around float32 boundary arithmetic.
        Assert.True(TerrainCellDrawCulling.ShouldDraw(
            context,
            new TerrainCellGrid(1.5f, -0.5f, 0.25f, 2),
            UnitHeightBounds));

        Assert.False(TerrainCellDrawCulling.ShouldDraw(
            context,
            new TerrainCellGrid(2.25f, -0.5f, 0.25f, 2),
            UnitHeightBounds));
    }

    [Fact]
    public void ShouldDraw_ConvertsAbsoluteCellBoundsToCameraRelativeSpace()
    {
        var origin = new Vector3(8192f, -12288f, 4096f);
        var context = new TerrainCellDrawFrustum(
            Frustum.FromViewProjection(Matrix4x4.Identity),
            origin);

        Assert.True(TerrainCellDrawCulling.ShouldDraw(
            context,
            new TerrainCellGrid(origin.X - 0.5f, origin.Y - 0.5f, 1f, 2),
            new TerrainCellHeightBounds(origin.Z, origin.Z + 1f)));

        Assert.False(TerrainCellDrawCulling.ShouldDraw(
            context,
            // At an 8192-unit origin the float ULP is much wider than BitIncrement(1), so use an
            // unambiguous two-unit offset and keep this a coordinate-space test, not a rounding test.
            new TerrainCellGrid(origin.X + 3f, origin.Y - 0.5f, 0.25f, 2),
            new TerrainCellHeightBounds(origin.Z, origin.Z + 1f)));
    }

    [Fact]
    public void CreateFrustum_CarriesExplicitCameraRelativeOrigin()
    {
        var origin = new Vector3(8192f, -12288f, 4096f);
        var eyeRelative = new Vector3(1000f, 2000f, 500f);
        var viewProjection = Matrix4x4.CreateLookAt(
                                 eyeRelative,
                                 eyeRelative + Vector3.UnitY,
                                 Vector3.UnitZ) *
                             Matrix4x4.CreatePerspectiveFieldOfView(
                                 MathF.PI / 3f, 16f / 9f, 16f, 400_000f) *
                             ReverseZ;

        var context = TerrainCellDrawCulling.CreateFrustum(viewProjection, origin);

        Assert.True(context.HasValue);
        Assert.Equal(origin, context.Value.RenderOrigin);
    }

    [Fact]
    public void CreateFrustum_CarriesZeroOriginForAbsolutePerspective()
    {
        var cameraWorld = new Vector3(12_345f, -6_789f, 2_048f);
        var viewProjection = Matrix4x4.CreateLookAt(
                                 cameraWorld,
                                 cameraWorld + Vector3.UnitY,
                                 Vector3.UnitZ) *
                             Matrix4x4.CreatePerspectiveFieldOfView(
                                 MathF.PI / 3f, 16f / 9f, 16f, 400_000f);

        var context = TerrainCellDrawCulling.CreateFrustum(viewProjection, Vector3.Zero);

        Assert.True(context.HasValue);
        Assert.Equal(Vector3.Zero, context.Value.RenderOrigin);
    }

    [Fact]
    public void CreateFrustum_CarriesZeroOriginForAbsoluteOrthographicPath()
    {
        var cameraWorld = new Vector3(10_000f, -20_000f, 30_000f);
        var viewProjection = Matrix4x4.CreateLookAt(cameraWorld, Vector3.Zero, Vector3.UnitY) *
                             Matrix4x4.CreateOrthographic(80_000f, 60_000f, 1f, 200_000f);

        var context = TerrainCellDrawCulling.CreateFrustum(viewProjection, Vector3.Zero);

        Assert.True(context.HasValue);
        Assert.Equal(Vector3.Zero, context.Value.RenderOrigin);
    }

    [Fact]
    public void MalformedCameraOrCellData_FailsOpen()
    {
        var validContext = new TerrainCellDrawFrustum(
            Frustum.FromViewProjection(Matrix4x4.Identity),
            Vector3.Zero);
        var validGrid = new TerrainCellGrid(10f, 10f, 1f, 2);

        Assert.True(TerrainCellDrawCulling.ShouldDraw(null, validGrid, UnitHeightBounds));
        Assert.True(TerrainCellDrawCulling.ShouldDraw(
            validContext, validGrid, new TerrainCellHeightBounds(float.NaN, 1f)));
        Assert.True(TerrainCellDrawCulling.ShouldDraw(
            validContext, validGrid, new TerrainCellHeightBounds(2f, 1f)));
        Assert.True(TerrainCellDrawCulling.ShouldDraw(
            validContext, new TerrainCellGrid(10f, 10f, 0f, 2), UnitHeightBounds));
        Assert.True(TerrainCellDrawCulling.ShouldDraw(
            validContext, new TerrainCellGrid(float.PositiveInfinity, 10f, 1f, 2), UnitHeightBounds));

        Assert.Null(TerrainCellDrawCulling.CreateFrustum(default, Vector3.Zero));
        var singularWithFinitePlanes = Matrix4x4.Identity;
        singularWithFinitePlanes.M44 = 0f;
        Assert.Null(TerrainCellDrawCulling.CreateFrustum(singularWithFinitePlanes, Vector3.Zero));
        var nonFinite = Matrix4x4.Identity;
        nonFinite.M11 = float.NaN;
        Assert.Null(TerrainCellDrawCulling.CreateFrustum(nonFinite, Vector3.Zero));
        Assert.Null(TerrainCellDrawCulling.CreateFrustum(Matrix4x4.Identity,
            new Vector3(float.PositiveInfinity, 0f, 0f)));
    }

    private static readonly Matrix4x4 ReverseZ = new(
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, -1f, 0f,
        0f, 0f, 1f, 1f);
}
