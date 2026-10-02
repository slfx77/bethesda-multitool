// Cases ported from JimmyPCTool / AweMultitool —
//   tests/AweMultitool.Tests/Core/Formats/Canvas/CanvasThumbnailerTests.cs — and restated for this
//   repo's classic-catalogue sizes.

using BethesdaMultitool.Core.AssetBrowse;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>
///     Gates for gallery scaling. Pure arithmetic, so these need no corpus: the cases that matter
///     are easier to state as fixtures than to find in a game.
/// </summary>
public sealed class ThumbnailScalerTests
{
    private static RgbaThumbnail Solid(int width, int height, byte r, byte g, byte b, byte a)
    {
        var rgba = new byte[width * height * ThumbnailScaler.BytesPerPixel];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = r;
            rgba[i + 1] = g;
            rgba[i + 2] = b;
            rgba[i + 3] = a;
        }

        return new RgbaThumbnail(width, height, rgba);
    }

    /// <summary>
    ///     A small image scales by a whole number. Classic sprite sheets are pixel art — a
    ///     fractional scale is exactly what turns them to mush.
    /// </summary>
    [Theory]
    [InlineData(8, 96, 96)]
    [InlineData(16, 96, 96)]
    [InlineData(32, 96, 96)]
    [InlineData(64, 96, 64)] // 96/64 is 1 after integer division, so it is left alone
    public void SmallSquaresScaleByAWholeNumber(int size, int cell, int expected)
    {
        var fitted = ThumbnailScaler.Fit(Solid(size, size, 10, 20, 30, 255), cell);

        Assert.Equal(expected, fitted.Width);
        Assert.Equal(expected, fitted.Height);
        Assert.Equal(fitted.Width * fitted.Height * 4, fitted.Rgba.Length);
    }

    [Fact]
    public void DownscalingKeepsAspectRatio()
    {
        // 320x200 is the classic full-screen shape — the one a naive square fit would squash.
        var fitted = ThumbnailScaler.Fit(Solid(320, 200, 200, 100, 50, 255), 96);

        Assert.Equal(96, fitted.Width);
        Assert.Equal(60, fitted.Height);
        Assert.Equal(96 * 60 * 4, fitted.Rgba.Length);
    }

    [Fact]
    public void ExactFitReturnsTheSameBuffer()
    {
        // Documented aliasing: an exact fit hands back the caller's own array, not a copy.
        var image = Solid(96, 96, 1, 2, 3, 255);
        var fitted = ThumbnailScaler.Fit(image, 96);

        Assert.Same(image.Rgba, fitted.Rgba);
    }

    [Fact]
    public void AnUpscaleShorterThanAWholeFactorIsLeftAlone()
    {
        var image = Solid(64, 64, 1, 2, 3, 255);
        var fitted = ThumbnailScaler.Fit(image, 96);

        Assert.Same(image.Rgba, fitted.Rgba);
        Assert.Equal(64, fitted.Width);
    }

    /// <summary>
    ///     Palettized sources leave a key colour in RGB where they zero alpha, so an unweighted
    ///     average pulls that colour into the visible edge. Here the transparent half is bright
    ///     magenta and the opaque half is black: every surviving pixel must stay black.
    /// </summary>
    [Fact]
    public void TransparentKeyColourDoesNotBleedIntoVisiblePixels()
    {
        const int size = 64;
        var rgba = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var i = (y * size + x) * 4;
                var transparent = x < size / 2;

                rgba[i] = transparent ? (byte)0xFF : (byte)0x00; // magenta key vs black
                rgba[i + 1] = 0x00;
                rgba[i + 2] = transparent ? (byte)0xFF : (byte)0x00;
                rgba[i + 3] = transparent ? (byte)0x00 : (byte)0xFF;
            }
        }

        var fitted = ThumbnailScaler.Fit(new RgbaThumbnail(size, size, rgba), 16);

        for (var i = 0; i < fitted.Rgba.Length; i += 4)
        {
            if (fitted.Rgba[i + 3] == 0)
            {
                continue;
            }

            Assert.Equal(0, fitted.Rgba[i]);
            Assert.Equal(0, fitted.Rgba[i + 2]);
        }
    }

    [Fact]
    public void FullyTransparentInputStaysTransparent()
    {
        var fitted = ThumbnailScaler.Fit(Solid(64, 64, 0xFF, 0x00, 0xFF, 0), 16);

        for (var i = 3; i < fitted.Rgba.Length; i += 4)
        {
            Assert.Equal(0, fitted.Rgba[i]);
        }
    }

    [Fact]
    public void NonSquareUpscaleUsesTheLongestSide()
    {
        // 16x8 into a 64 cell: the LONG side drives the factor, so it lands on 64x32, not 64x64.
        var fitted = ThumbnailScaler.Fit(Solid(16, 8, 9, 9, 9, 255), 64);

        Assert.Equal(64, fitted.Width);
        Assert.Equal(32, fitted.Height);
    }

    [Fact]
    public void UpscaleReplicatesPixelsExactly()
    {
        // Two pixels, one red one green, doubled: each must become a clean 2x2 block.
        var rgba = new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 };
        var fitted = ThumbnailScaler.Fit(new RgbaThumbnail(2, 1, rgba), 4);

        Assert.Equal(4, fitted.Width);
        Assert.Equal(2, fitted.Height);
        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var i = (y * 4 + x) * 4;
                Assert.Equal(x < 2 ? 255 : 0, fitted.Rgba[i]);
                Assert.Equal(x < 2 ? 0 : 255, fitted.Rgba[i + 1]);
            }
        }
    }

    [Fact]
    public void ABufferTooSmallForItsDimensionsIsRejected()
    {
        var truncated = new RgbaThumbnail(64, 64, new byte[16]);

        Assert.Throws<ArgumentException>(() => ThumbnailScaler.Fit(truncated, 32));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-8)]
    public void ANonPositiveCellIsRejected(int cell)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ThumbnailScaler.Fit(Solid(8, 8, 1, 1, 1, 255), cell));
    }
}