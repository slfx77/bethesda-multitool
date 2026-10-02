using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Camera;

public sealed class CameraExteriorFramingTests
{
    // Actual resident replay viewport, rather than a widescreen assumption.
    private const float Aspect = 657f / 642f;

    [Theory]
    [InlineData(4096f, 2, 0)]
    [InlineData(8192f, 2, 0)]
    [InlineData(100f, 2, 0)]
    [InlineData(4096f, -37, 48)]
    [InlineData(8192f, -37, 48)]
    [InlineData(100f, -37, 48)]
    public void Selected_ground_cell_and_corners_are_visible(float cellSize, int gridX, int gridY)
    {
        var target = new Vector2((gridX + 0.5f) * cellSize, (gridY + 0.5f) * cellSize);
        var camera = new CameraState();
        camera.FrameExterior(target, cellSize);

        // Use the live camera-relative projection and actual terrain draw admission.
        var viewProjection = camera.GetViewMatrixCameraRelative() * camera.GetProjectionMatrix(Aspect);
        var frustum = TerrainCellDrawCulling.CreateFrustum(viewProjection, camera.Position);
        Assert.NotNull(frustum);
        Assert.True(TerrainCellDrawCulling.ShouldDraw(frustum,
            new TerrainCellGrid(gridX * cellSize, gridY * cellSize, cellSize / 32f, 33), new(0f, 0f)));

        var center = Project(new Vector3(target, 0f), camera, viewProjection);
        Assert.InRange(MathF.Abs(center.X), 0f, 0.00001f);
        Assert.InRange(MathF.Abs(center.Y), 0f, 0.00001f);
        foreach (var x in new[] { gridX * cellSize, (gridX + 1) * cellSize })
        foreach (var y in new[] { gridY * cellSize, (gridY + 1) * cellSize })
        {
            var corner = Project(new Vector3(x, y, 0f), camera, viewProjection);
            Assert.InRange(corner.X, -1f, 1f);
            Assert.InRange(corner.Y, -1f, 1f);
            Assert.InRange(corner.Z, 0f, 1f);
        }
    }

    [Fact]
    public void Retained_replay_pose_culls_all_five_uploaded_cells_but_framed_target_does_not()
    {
        // Observed GUI pose and synthetic LAND bounds from replay-001.
        var camera = new CameraState { Position = new Vector3(10240f, -6144f, 32768f), Yaw = 0f, Pitch = -MathF.PI / 6f };
        var oldFrustum = TerrainCellDrawCulling.CreateFrustum(
            camera.GetViewMatrixCameraRelative() * camera.GetProjectionMatrix(Aspect), camera.Position);
        Assert.NotNull(oldFrustum);
        for (var x = 0; x < 5; x++)
        {
            Assert.False(TerrainCellDrawCulling.ShouldDraw(oldFrustum, new TerrainCellGrid(x * 4096f, 0f, 128f, 33), new(0f, 0f)));
        }

        camera.FrameExterior(new Vector2(10240f, 2048f), 4096f);
        var correctedFrustum = TerrainCellDrawCulling.CreateFrustum(
            camera.GetViewMatrixCameraRelative() * camera.GetProjectionMatrix(Aspect), camera.Position);
        Assert.NotNull(correctedFrustum);
        Assert.True(TerrainCellDrawCulling.ShouldDraw(correctedFrustum, new TerrainCellGrid(8192f, 0f, 128f, 33), new(0f, 0f)));
    }

    [Fact]
    public void Framing_preserves_projection_and_clip_settings()
    {
        var camera = new CameraState { FovYRadians = 0.9f, NearPlane = 2f, FarPlane = 123456f };
        camera.FrameExterior(new Vector2(125f, -250f), 100f);
        Assert.Equal(0.9f, camera.FovYRadians);
        Assert.Equal(2f, camera.NearPlane);
        Assert.Equal(123456f, camera.FarPlane);
    }

    private static Vector3 Project(Vector3 world, CameraState camera, Matrix4x4 viewProjection)
    {
        var clip = Vector4.Transform(new Vector4(world - camera.Position, 1f), viewProjection);
        Assert.True(clip.W > 0f);
        return new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
    }
}
