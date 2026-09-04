using System;
using BethesdaMultitool.Core.Rendering.Level2D;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Rendering.Level2D;

/// <summary>
///     Pins the diagnostic voxel rasterizer both the PNG export and the in-memory 2D view read
///     through, so the exported file and the viewer cannot disagree about a level.
/// </summary>
public sealed class VoxelLayerRasterizerTests
{
    [Fact]
    public void EmptySpaceIsBlackAndOpaque()
    {
        var (r, g, b) = VoxelLayerRasterizer.ColorFor(0);

        Assert.Equal(0, r);
        Assert.Equal(0, g);
        Assert.Equal(0, b);
    }

    [Fact]
    public void EveryNonEmptyIdGetsAVisibleColour()
    {
        // Id 0 is the ONLY black; a solid id rendering black would read as empty space.
        for (ushort id = 1; id < 512; id++)
        {
            var (r, g, b) = VoxelLayerRasterizer.ColorFor(id);
            Assert.True(r + g + b > 0, $"voxel {id} rendered black");
        }
    }

    [Fact]
    public void AdjacentIdsAreVisuallySeparated()
    {
        // The golden-ratio hue step exists so numerically adjacent ids do not look alike; without
        // it a corridor and its wall would be near-identical shades.
        for (ushort id = 1; id < 64; id++)
        {
            var a = VoxelLayerRasterizer.ColorFor(id);
            var b = VoxelLayerRasterizer.ColorFor((ushort)(id + 1));
            var distance = Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
            Assert.True(distance > 40, $"ids {id} and {id + 1} differ by only {distance}");
        }
    }

    [Fact]
    public void ColourIsStableAcrossCalls()
    {
        Assert.Equal(VoxelLayerRasterizer.ColorFor(97), VoxelLayerRasterizer.ColorFor(97));
    }

    [Fact]
    public void RasterizeProducesOnePixelPerVoxelAtScaleOne()
    {
        ushort[] grid = [0, 1, 2, 3];

        var (pixels, width, height, distinct) =
            VoxelLayerRasterizer.Rasterize(2, 2, 1, (x, z) => grid[(z * 2) + x]);

        Assert.Equal(2, width);
        Assert.Equal(2, height);
        Assert.Equal(4, distinct);
        Assert.Equal(2 * 2 * 4, pixels.Length);

        // Cell (0,0) is id 0 — black — and every pixel is opaque.
        Assert.Equal(0, pixels[0]);
        Assert.Equal(255, pixels[3]);
    }

    [Fact]
    public void RasterizeExpandsEachVoxelIntoAScaleSquareBlock()
    {
        ushort[] grid = [1, 2];

        var (pixels, width, height, _) =
            VoxelLayerRasterizer.Rasterize(2, 1, 3, (x, _) => grid[x]);

        Assert.Equal(6, width);
        Assert.Equal(3, height);

        var left = VoxelLayerRasterizer.ColorFor(1);
        var right = VoxelLayerRasterizer.ColorFor(2);
        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 6; x++)
            {
                var i = ((y * 6) + x) * 4;
                var expected = x < 3 ? left : right;
                Assert.Equal(expected.R, pixels[i]);
                Assert.Equal(expected.G, pixels[i + 1]);
                Assert.Equal(expected.B, pixels[i + 2]);
            }
        }
    }

    [Fact]
    public void DistinctCountReportsWhetherALayerIsAuthored()
    {
        // A layer of nothing but id 0 is a blank plane — the count is what tells a caller so.
        var (_, _, _, distinct) = VoxelLayerRasterizer.Rasterize(4, 4, 1, (_, _) => (ushort)0);

        Assert.Equal(1, distinct);
    }

    [Fact]
    public void ScaleBelowOneIsTreatedAsOne()
    {
        var (_, width, height, _) = VoxelLayerRasterizer.Rasterize(3, 2, 0, (_, _) => (ushort)1);

        Assert.Equal(3, width);
        Assert.Equal(2, height);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    [InlineData(-1, 4)]
    public void ANonPositiveGridIsRejected(int width, int depth)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VoxelLayerRasterizer.Rasterize(width, depth, 1, (_, _) => (ushort)0));
    }
}
