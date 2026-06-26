using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Locks the 3D export's framing + tiling math (<see cref="MapExport3DPlanner" />): the
///     player-anchored scale, the centered top-down framing, aspect-preserving single-image capping, and
///     the tile grid composing the full image.
/// </summary>
public sealed class MapExport3DPlannerTests
{
    [Fact]
    public void Scale1x_RendersPlayerHeightAt64Px()
    {
        Assert.Equal(0.5f, MapExport3DPlanner.BaseScalePxPerUnit, 3);
        Assert.Equal(
            64f, MapExport3DPlanner.BaseScalePxPerUnit * MapExport3DPlanner.PlayerModelHeightUnits, 3);
    }

    [Fact]
    public void TopDown_FramesRectCenteredWithHalfHeight()
    {
        // 8192 × 4096 world rect (2×1 cells), top-down north-up, 1×, single image.
        var plan = MapExport3DPlanner.Plan(
            ProjectionMode.Orthographic, 0,
            worldMinX: 0f, worldMaxX: 8192f, worldMinY: 0f, worldMaxY: 4096f,
            worldMinZ: 0f, worldMaxZ: 1000f, scale: 1f, tiled: false, maxTileDim: 16384);

        Assert.Equal(4096f, plan.Focus.X, 1);
        Assert.Equal(2048f, plan.Focus.Y, 1);
        Assert.Equal(90f, plan.Elevation);
        // OrthoHalfHeight ≈ half the Y extent (× a small framing margin); the Z span is irrelevant
        // for a straight-down view (the up axis is horizontal).
        Assert.InRange(plan.OrthoHalfHeight, 2048f, 2048f * 1.05f);
        // Square pixels: the final image aspect matches the projection aspect.
        Assert.Equal(plan.ImageWidth / (float)plan.ImageHeight, plan.Aspect, 2);
        // 2:1 world rect → ~2:1 image.
        Assert.InRange(plan.ImageWidth / (float)plan.ImageHeight, 1.9f, 2.1f);
    }

    [Fact]
    public void Tiled_SplitsIntoCappedTilesComposingTheImage()
    {
        var plan = MapExport3DPlanner.Plan(
            ProjectionMode.Orthographic, 0,
            0f, 40_000f, 0f, 24_000f, 0f, 4096f, scale: 1f, tiled: true, maxTileDim: 2048);

        Assert.True(plan.Cols > 1 && plan.Rows > 1, "a large area at 1× must tile");
        Assert.True(plan.TileWidth <= 2048 && plan.TileHeight <= 2048, "tiles must fit the cap");
        Assert.Equal(plan.Cols * plan.TileWidth, plan.ImageWidth);
        Assert.Equal(plan.Rows * plan.TileHeight, plan.ImageHeight);
    }

    [Fact]
    public void NonTiled_CapsLongEdgePreservingAspect()
    {
        // Wide area, single image: the long edge caps to one tile and the aspect is preserved (not
        // squashed to a square by capping each axis independently).
        var plan = MapExport3DPlanner.Plan(
            ProjectionMode.Orthographic, 0,
            0f, 40_000f, 0f, 24_000f, 0f, 4096f, scale: 1f, tiled: false, maxTileDim: 2048);

        Assert.Equal(1, plan.Cols);
        Assert.Equal(1, plan.Rows);
        Assert.True(plan.ImageWidth <= 2048 && plan.ImageHeight <= 2048);
        // 40000:24000 ≈ 1.667 — must survive the cap.
        Assert.InRange(plan.ImageWidth / (float)plan.ImageHeight, 1.6f, 1.74f);
    }
}
