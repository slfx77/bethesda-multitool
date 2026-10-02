using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.RenderWare;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.RenderWare;

public sealed class RwPspTextureMipTests
{
    [Theory]
    [InlineData(RwPspPixelFormat.Rgb565)]
    [InlineData(RwPspPixelFormat.Rgba5551)]
    [InlineData(RwPspPixelFormat.Rgba4444)]
    [InlineData(RwPspPixelFormat.Rgba8888)]
    [InlineData(RwPspPixelFormat.Indexed4)]
    [InlineData(RwPspPixelFormat.Indexed8)]
    internal void AuthoredLowerMipsSurviveDecodingAndViewerConversion(RwPspPixelFormat format)
    {
        var body = AuthoredRaster(format);
        var original = (byte[])body.Clone();
        var texture = RwPspTexture.TryParse(body);
        Assert.NotNull(texture);
        Assert.Equal(original, body);

        // A different solid color at each level cannot be reproduced by generating
        // smaller images from level zero. Padding contains a separate sentinel.
        var decoded = texture.ToDecodedTexture();
        Assert.Equal(6, texture.MipCount);
        Assert.Equal(6, decoded.MipCount);
        Assert.Same(texture.Rgba, decoded.Pixels);
        for (var level = 0; level < 6; level++)
        {
            var mip = decoded.MipLevels[level];
            Assert.Equal(Math.Max(1, 33 >> level), mip.Width);
            Assert.Equal(level == 0 ? 3 : 1, mip.Height);
            Assert.Equal(mip.Width * mip.Height * 4, mip.Pixels.Length);
            var expected = ExpectedPixel(format, level + 1);
            for (var at = 0; at < mip.Pixels.Length; at += 4)
                Assert.Equal(expected, mip.Pixels.AsSpan(at, 4).ToArray());
        }

        // Parsed levels own their bytes instead of borrowing the input span.
        Array.Fill(body, (byte)0);
        Assert.Equal(ExpectedPixel(format, 6), decoded.MipLevels[5].Pixels);
    }

    [Fact]
    public void OddFourBitWidthKeepsTheFinalNibbleAndNextRowSeparate()
    {
        Assert.Equal(32, RwPspTexture.Pitch(33, RwPspPixelFormat.Indexed4));
        var body = AuthoredRaster(RwPspPixelFormat.Indexed4);
        const int pixelStart = 0xAC + 64;
        body[pixelStart + 16] = 0xF2;
        body[pixelStart + 32] = 0x33;
        var texture = RwPspTexture.TryParse(body);
        Assert.NotNull(texture);
        Assert.Equal(ExpectedPixel(RwPspPixelFormat.Indexed4, 2), texture.Rgba.AsSpan(32 * 4, 4).ToArray());
        Assert.Equal(ExpectedPixel(RwPspPixelFormat.Indexed4, 3), texture.Rgba.AsSpan(33 * 4, 4).ToArray());
    }

    [Fact]
    public void PartialLastMipIsDeclined()
    {
        var body = AuthoredRaster(RwPspPixelFormat.Indexed8);
        Assert.Null(RwPspTexture.TryParse(body.AsSpan(0, body.Length - 1)));
    }

    private static byte[] AuthoredRaster(RwPspPixelFormat format)
    {
        var bits = format switch
        {
            RwPspPixelFormat.Indexed4 => 4,
            RwPspPixelFormat.Indexed8 => 8,
            RwPspPixelFormat.Rgba8888 => 32,
            _ => 16
        };
        var paletteEntries = format switch
        {
            RwPspPixelFormat.Indexed4 => 16,
            RwPspPixelFormat.Indexed8 => 256,
            _ => 0
        };
        // Independent bit/row accounting, including the final half-byte at width 33.
        var pitches = Enumerable.Range(0, 6)
            .Select(level => Math.Max(16, ((Math.Max(1, 33 >> level) * bits + 127) / 128) * 16))
            .ToArray();
        var body = new byte[0xAC + paletteEntries * 4 + pitches[0] * 3 + pitches.Skip(1).Sum()];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), (3u << 16) | 33u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0x5C), (uint)format);
        for (var entry = 1; entry <= 6 && entry < paletteEntries; entry++)
            ExpectedPixel(format, entry).CopyTo(body, 0xAC + entry * 4);
        var offset = 0xAC + paletteEntries * 4;
        for (var level = 0; level < 6; level++)
        {
            var width = Math.Max(1, 33 >> level);
            var height = level == 0 ? 3 : 1;
            var pitch = pitches[level];
            body.AsSpan(offset, pitch * height).Fill(0xCC);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    WriteTexel(body.AsSpan(offset + y * pitch, pitch), x, format, level + 1);
                }
            }
            offset += pitch * height;
        }
        return body;
    }

    private static void WriteTexel(Span<byte> row, int x, RwPspPixelFormat format, int value)
    {
        switch (format)
        {
            case RwPspPixelFormat.Indexed4:
                row[x / 2] = (byte)(value * 0x11);
                break;
            case RwPspPixelFormat.Indexed8:
                row[x] = (byte)value;
                break;
            case RwPspPixelFormat.Rgba8888:
                ExpectedPixel(format, value).CopyTo(row[(x * 4)..]);
                break;
            default:
                var packed = format switch
                {
                    RwPspPixelFormat.Rgba5551 => 0x8000 | value,
                    RwPspPixelFormat.Rgba4444 => 0xF000 | value,
                    _ => value
                };
                BinaryPrimitives.WriteUInt16LittleEndian(row[(x * 2)..], (ushort)packed);
                break;
        }
    }

    private static byte[] ExpectedPixel(RwPspPixelFormat format, int value)
    {
        var red = format switch
        {
            RwPspPixelFormat.Rgb565 or RwPspPixelFormat.Rgba5551 => (value << 3) | (value >> 2),
            RwPspPixelFormat.Rgba4444 => value * 17,
            _ => value
        };
        return [(byte)red, 0, 0, 255];
    }
}
