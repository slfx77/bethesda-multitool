using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Walk;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Walk;

/// <summary>
///     The walk-mode cold-OBND ground fallback: a down ray slab-tested against the PLACED box.
///     Pins the rotation fix — the old fallback tested the unrotated offset rectangle, offering
///     ground beside rotated pieces and none on top of them.
/// </summary>
public sealed class WalkColdBoundsProbeTests
{
    // Sidewalk-median-like OBND: 40 x 768 footprint, 16-unit curb top.
    private static readonly Vector3 LocalMin = new(-20f, -384f, 0f);
    private static readonly Vector3 LocalMax = new(20f, 384f, 16f);

    [Fact]
    public void RotatedBox_ReturnsTopOverTheTrueFootprint()
    {
        var world = Matrix4x4.CreateRotationZ(MathF.PI / 2f);
        Assert.True(Matrix4x4.Invert(world, out var inverse));

        // (300, 0) sits over the ROTATED footprint (true extent runs along world X).
        var hit = WalkColdBoundsProbe.TryRaycastDownToTop(
            LocalMin, LocalMax, world, inverse, 300f, 0f, 100f, out var top);

        Assert.True(hit);
        Assert.Equal(16f, top, 3);
    }

    [Fact]
    public void RotatedBox_MissesTheUnrotatedGhostFootprint()
    {
        var world = Matrix4x4.CreateRotationZ(MathF.PI / 2f);
        Assert.True(Matrix4x4.Invert(world, out var inverse));

        // (0, 300) was inside the pre-fix unrotated rectangle but is 280 units off the real box.
        var hit = WalkColdBoundsProbe.TryRaycastDownToTop(
            LocalMin, LocalMax, world, inverse, 0f, 300f, 100f, out _);

        Assert.False(hit);
    }

    [Fact]
    public void RayStartingInsideTheBox_IsRejected()
    {
        // Origin below the top = the surface is above the probe window; never a step-up candidate
        // (mirrors the old top-above-window rejection).
        var hit = WalkColdBoundsProbe.TryRaycastDownToTop(
            LocalMin, LocalMax, Matrix4x4.Identity, Matrix4x4.Identity, 0f, 0f,
            8f, out _);

        Assert.False(hit);
    }

    [Fact]
    public void ScaledPlacement_ScalesTheSurfaceHeightThroughTheMatrix()
    {
        var world = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(0f, 0f, 100f);
        Assert.True(Matrix4x4.Invert(world, out var inverse));

        var hit = WalkColdBoundsProbe.TryRaycastDownToTop(
            LocalMin, LocalMax, world, inverse, 0f, 0f, 500f, out var top);

        Assert.True(hit);
        Assert.Equal(132f, top, 3); // 16 * 2 + 100
    }

    [Fact]
    public void TranslatedBox_HitsAtThePlacedHeight()
    {
        var world = Matrix4x4.CreateTranslation(1000f, -2000f, 50f);
        Assert.True(Matrix4x4.Invert(world, out var inverse));

        var hit = WalkColdBoundsProbe.TryRaycastDownToTop(
            LocalMin, LocalMax, world, inverse, 1005f, -2000f, 200f, out var top);

        Assert.True(hit);
        Assert.Equal(66f, top, 3); // 16 + 50
    }
}