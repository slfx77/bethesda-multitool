using BethesdaMultitool.Core.Imaging;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Imaging;

/// <summary>
///     Tests for <see cref="IndexedBitmap" />: constructor validation and the
///     palette-resolved RGBA output feeding the DecodedTexture seam.
/// </summary>
public class IndexedBitmapTests
{
    /// <summary>Builds a 768-byte RGB block with specific entries set, the rest zero.</summary>
    private static byte[] BuildRgb768(params (int Index, byte R, byte G, byte B)[] entries)
    {
        var rgb = new byte[Palette.RgbByteCount];
        foreach (var (index, r, g, b) in entries)
        {
            rgb[index * 3] = r;
            rgb[index * 3 + 1] = g;
            rgb[index * 3 + 2] = b;
        }

        return rgb;
    }

    [Theory]
    [InlineData(2, 2, 3)]
    [InlineData(2, 2, 5)]
    [InlineData(1, 1, 0)]
    public void Ctor_RejectsIndicesLengthMismatch(int width, int height, int indicesLength)
    {
        Assert.Throws<ArgumentException>(
            () => new IndexedBitmap(width, height, new byte[indicesLength]));
    }

    [Fact]
    public void Ctor_RejectsNegativeDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IndexedBitmap(-1, 2, new byte[2]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IndexedBitmap(2, -1, new byte[2]));
    }

    [Fact]
    public void Ctor_StoresDimensionsAndOffsets()
    {
        var bitmap = new IndexedBitmap(3, 2, new byte[6], xOffset: -4, yOffset: 7);

        Assert.Equal(3, bitmap.Width);
        Assert.Equal(2, bitmap.Height);
        Assert.Equal(-4, bitmap.XOffset);
        Assert.Equal(7, bitmap.YOffset);
    }

    [Fact]
    public void ToDecodedTexture_ResolvesEveryPixel_AllSixteenBytesPinned()
    {
        var palette = Palette.FromRgb8(BuildRgb768(
            (0, 10, 20, 30),
            (1, 40, 50, 60),
            (2, 70, 80, 90),
            (3, 200, 210, 220)));
        // Row-major 2x2: (0,0)=entry0, (1,0)=entry1, (0,1)=entry2, (1,1)=entry3.
        var bitmap = new IndexedBitmap(2, 2, [0, 1, 2, 3]);

        var texture = bitmap.ToDecodedTexture(palette);

        Assert.Equal(2, texture.Width);
        Assert.Equal(2, texture.Height);
        Assert.Equal(1, texture.MipCount);
        byte[] expected =
        [
            10, 20, 30, 255, // (0,0)
            40, 50, 60, 255, // (1,0)
            70, 80, 90, 255, // (0,1)
            200, 210, 220, 255 // (1,1)
        ];
        Assert.Equal(expected, texture.Pixels);
    }

    [Fact]
    public void ToDecodedTexture_TransparentPaletteIndex_YieldsAlphaZeroPixel()
    {
        var palette = Palette.FromRgb8(BuildRgb768((0, 1, 2, 3), (5, 100, 110, 120)))
            .WithTransparentIndex(0);
        var bitmap = new IndexedBitmap(2, 1, [0, 5]);

        var texture = bitmap.ToDecodedTexture(palette);

        byte[] expected =
        [
            1, 2, 3, 0, // transparent index 0: colour kept, alpha 0
            100, 110, 120, 255
        ];
        Assert.Equal(expected, texture.Pixels);
    }

    [Fact]
    public void ToDecodedTexture_RepeatedIndex_SamplesSamePaletteEntry()
    {
        var palette = Palette.FromRgb8(BuildRgb768((7, 11, 22, 33)));
        var bitmap = new IndexedBitmap(1, 3, [7, 7, 7]);

        var texture = bitmap.ToDecodedTexture(palette);

        byte[] expected = [11, 22, 33, 255, 11, 22, 33, 255, 11, 22, 33, 255];
        Assert.Equal(expected, texture.Pixels);
    }
}
