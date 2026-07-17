using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Focused regressions for the walk-mode XY capsule sweep. These exercise the geometry kernel used
///     by the GUI without requiring WinUI input: a wall blocks, diagonal motion slides, and a single
///     very long frame cannot step from one side of a thin wall to the other.
/// </summary>
public sealed class WalkHorizontalCollisionTests
{
    private const float Radius = 24f;
    private const float BodyMinZ = 48f;
    private const float BodyMaxZ = 120f;

    [Fact]
    public void Resolve_DirectMotionIntoWall_StopsAtCapsuleRadius()
    {
        var wall = BuildVerticalWall();

        var allowed = WalkHorizontalCollision.Resolve(
            new Vector2(-100f, 0f),
            new Vector2(200f, 0f),
            BodyMinZ,
            BodyMaxZ,
            Radius,
            [wall]);

        var final = new Vector2(-100f, 0f) + allowed;
        Assert.InRange(final.X, -24.1f, -24f);
        Assert.Equal(0f, final.Y, 3);
    }

    [Fact]
    public void Resolve_DiagonalMotionIntoWall_PreservesTangentSlide()
    {
        var wall = BuildVerticalWall();

        var allowed = WalkHorizontalCollision.Resolve(
            new Vector2(-100f, -50f),
            new Vector2(200f, 100f),
            BodyMinZ,
            BodyMaxZ,
            Radius,
            [wall]);

        var final = new Vector2(-100f, -50f) + allowed;
        Assert.InRange(final.X, -24.1f, -24f);
        Assert.InRange(final.Y, 49.8f, 50.1f);
    }

    [Fact]
    public void Resolve_HighSpeedSweep_CannotTunnelThroughThinWall()
    {
        var wall = BuildVerticalWall();

        var allowed = WalkHorizontalCollision.Resolve(
            new Vector2(-10_000f, 0f),
            new Vector2(20_000f, 0f),
            BodyMinZ,
            BodyMaxZ,
            Radius,
            [wall]);

        var finalX = -10_000f + allowed.X;
        Assert.InRange(finalX, -24.1f, -24f);
    }

    [Fact]
    public void Resolve_WalkableFloor_IsLeftToVerticalGroundSampler()
    {
        var floor = BuildHorizontalFloor(60f);
        var requested = new Vector2(200f, 75f);

        var allowed = WalkHorizontalCollision.Resolve(
            new Vector2(-100f, 0f),
            requested,
            BodyMinZ,
            BodyMaxZ,
            Radius,
            [floor]);

        Assert.Equal(requested, allowed);
    }

    [Fact]
    public void Resolve_WallBelowStepHeight_DoesNotBlockGroundOwnedStep()
    {
        var lowWall = BuildVerticalWall(topZ: 40f);
        var requested = new Vector2(200f, 0f);

        var allowed = WalkHorizontalCollision.Resolve(
            new Vector2(-100f, 0f),
            requested,
            BodyMinZ,
            BodyMaxZ,
            Radius,
            [lowWall]);

        Assert.Equal(requested, allowed);
    }

    private static WalkCollisionInstance BuildVerticalWall(float topZ = 200f)
    {
        var positions = new[]
        {
            new Vector3(0f, -500f, 0f),
            new Vector3(0f, 500f, 0f),
            new Vector3(0f, 500f, topZ),
            new Vector3(0f, -500f, topZ),
        };
        var mesh = new CollisionMesh(positions, [0, 1, 2, 0, 2, 3]);
        return WalkCollisionInstance.FromMesh(mesh, Matrix4x4.Identity);
    }

    private static WalkCollisionInstance BuildHorizontalFloor(float z)
    {
        var positions = new[]
        {
            new Vector3(-500f, -500f, z),
            new Vector3(500f, -500f, z),
            new Vector3(500f, 500f, z),
            new Vector3(-500f, 500f, z),
        };
        var mesh = new CollisionMesh(positions, [0, 1, 2, 0, 2, 3]);
        return WalkCollisionInstance.FromMesh(mesh, Matrix4x4.Identity);
    }
}
