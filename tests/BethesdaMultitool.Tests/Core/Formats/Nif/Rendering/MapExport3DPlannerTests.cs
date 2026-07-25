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
            0f, 8192f, 0f, 4096f,
            0f, 1000f, 1f, false, 16384, 16384);

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
            0f, 40_000f, 0f, 24_000f, 0f, 4096f, 1f, true, 2048, 16384);

        Assert.True(plan.Cols > 1 && plan.Rows > 1, "a large area at 1× must tile");
        Assert.True(plan.TileWidth <= 2048 && plan.TileHeight <= 2048, "tiles must fit the cap");
        Assert.Equal(plan.Cols * plan.TileWidth, plan.ImageWidth);
        Assert.Equal(plan.Rows * plan.TileHeight, plan.ImageHeight);
    }

    [Fact]
    public void Tiled_HasNoOverallImageCap()
    {
        // Tiled output is per-tile PNGs + manifest — maxImageDim must not shrink it.
        var plan = MapExport3DPlanner.Plan(
            ProjectionMode.Orthographic, 0,
            0f, 120_000f, 0f, 120_000f, 0f, 4096f, 1f, true, 2048, 16384);

        Assert.True(plan.ImageWidth > 16_384, "tiled export must keep the full wanted resolution");
    }

    [Fact]
    public void NonTiled_UnderOneTile_StaysSingleTile()
    {
        // Small worldspace: fits one GPU tile — 1×1 grid, no internal tiling, exactly as before.
        var plan = MapExport3DPlanner.Plan(
            ProjectionMode.Orthographic, 0,
            0f, 4000f, 0f, 3000f, 0f, 1000f, 1f, false, 2048, 16384);

        Assert.Equal(1, plan.Cols);
        Assert.Equal(1, plan.Rows);
        Assert.True(plan.ImageWidth <= 2048 && plan.ImageHeight <= 2048);
    }

    [Fact]
    public void NonTiled_BeyondOneTile_TilesInternallyUpToImageCap()
    {
        // THE regression: a non-tiled export used to collapse to a 2048px-long-edge
        // thumbnail because the per-tile GPU cap doubled as the output cap. It must now keep full
        // resolution via an internal grid — this area wants 20000×12000 px at 1×, under the 16384
        // image cap after long-edge scaling.
        var plan = MapExport3DPlanner.Plan(
            ProjectionMode.Orthographic, 0,
            0f, 40_000f, 0f, 24_000f, 0f, 4096f, 1f, false, 2048, 16384);

        Assert.True(plan.Cols > 1 && plan.Rows > 1, "output beyond one GPU tile must grid internally");
        Assert.True(plan.TileWidth <= 2048 && plan.TileHeight <= 2048, "tiles must fit the GPU cap");
        Assert.Equal(plan.Cols * plan.TileWidth, plan.ImageWidth);
        Assert.Equal(plan.Rows * plan.TileHeight, plan.ImageHeight);
        // Long edge capped to maxImageDim (uniform tile rounding may overshoot by < Cols px).
        Assert.InRange(Math.Max(plan.ImageWidth, plan.ImageHeight), 2049, 16_384 + plan.Cols);
        // 40000:24000 ≈ 1.667 — the aspect must survive the cap.
        Assert.InRange(plan.ImageWidth / (float)plan.ImageHeight, 1.6f, 1.74f);
    }

    [Fact]
    public void NonTiled_ImageCapNeverDropsBelowOneTile()
    {
        // A misconfigured maxImageDim below the tile cap must clamp up, not shrink tiles.
        var plan = MapExport3DPlanner.Plan(
            ProjectionMode.Orthographic, 0,
            0f, 40_000f, 0f, 24_000f, 0f, 4096f, 1f, false, 2048, 512);

        Assert.Equal(1, plan.Cols);
        Assert.Equal(1, plan.Rows);
        Assert.True(plan.ImageWidth <= 2048 && plan.ImageHeight <= 2048);
    }
}