using BethesdaMultitool.Core.Formats.Esm.Analysis;
using ImageMagick;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Analysis;

/// <summary>
///     Locks the correctness contract of <see cref="PngWriter" />'s tuned encode settings (zlib level
///     2 + Sub filter instead of ImageMagick's slow defaults): compression tuning may change bytes on
///     disk, but decoded pixels must round-trip exactly and the container must stay 8-bit.
/// </summary>
public sealed class PngWriterTests
{
    /// <summary>Deterministic non-uniform RGBA pattern so a filter/compression bug can't hide in flat color.</summary>
    private static byte[] MakeRgbaPattern(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                pixels[i] = (byte)((x * 7) + (y * 3));
                pixels[i + 1] = (byte)((x * 13) ^ (y * 5));
                pixels[i + 2] = (byte)(x + (y * 11));
                pixels[i + 3] = (byte)(255 - ((x + y) % 32)); // varied alpha too
            }
        }
        return pixels;
    }

    [Fact]
    public void EncodeRgba_RoundTripsPixelsExactly_AndStays8Bit()
    {
        const int width = 64, height = 48;
        var pixels = MakeRgbaPattern(width, height);

        var png = PngWriter.EncodeRgba(pixels, width, height);

        // PNG layout: 8-byte signature, then IHDR (4 len + 4 type + 4 width + 4 height + bit depth).
        Assert.Equal(8, png[24]);

        using var decoded = new MagickImage(png);
        Assert.Equal((uint)width, decoded.Width);
        Assert.Equal((uint)height, decoded.Height);
        var roundTripped = decoded.GetPixels().ToByteArray(PixelMapping.RGBA);
        Assert.Equal(pixels, roundTripped);
    }

    [Fact]
    public void EncodeRgba_IsDeterministicAndOmitsWallClockMetadata()
    {
        var pixels = MakeRgbaPattern(16, 12);

        var first = PngWriter.EncodeRgba(pixels, 16, 12);
        var second = PngWriter.EncodeRgba(pixels, 16, 12);

        Assert.Equal(first, second);
        var containerText = System.Text.Encoding.Latin1.GetString(first);
        Assert.DoesNotContain("date:create", containerText);
        Assert.DoesNotContain("date:modify", containerText);
    }

    [Fact]
    public void SaveRgba_StreamsToFile_PixelIdenticalToInput()
    {
        const int width = 32, height = 32;
        var pixels = MakeRgbaPattern(width, height);
        var path = Path.Combine(Path.GetTempPath(), $"pngwriter-test-{Guid.NewGuid():N}.png");
        try
        {
            PngWriter.SaveRgba(pixels, width, height, path);

            var png = File.ReadAllBytes(path);
            Assert.Equal(8, png[24]); // 8-bit IHDR depth
            using var decoded = new MagickImage(png);
            var roundTripped = decoded.GetPixels().ToByteArray(PixelMapping.RGBA);
            Assert.Equal(pixels, roundTripped);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
