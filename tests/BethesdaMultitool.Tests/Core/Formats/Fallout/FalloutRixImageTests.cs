using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using BethesdaMultitool.Core.Formats.Fallout;
using BethesdaMultitool.Core.Imaging;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Fallout;

/// <summary>
///     Vectors for Fallout's <c>.RIX</c> splash plates, shaped after the five retail images measured
///     2026-09-06 — every one exactly 307,978 bytes of 640x480.
/// </summary>
public sealed class FalloutRixImageTests
{
    private static byte[] Rix(ushort width = 4, ushort height = 3, byte paletteValue = 63, int sizeBias = 0)
    {
        var b = new byte[FalloutRixImage.HeaderLength + Palette.RgbByteCount + (width * height) + sizeBias];
        Encoding.ASCII.GetBytes(FalloutRixImage.Magic).CopyTo(b, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), width);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), height);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), 0x00AF);

        // A 6-bit VGA palette: no component exceeds 63.
        for (var i = 0; i < Palette.RgbByteCount; i++)
        {
            b[FalloutRixImage.HeaderLength + i] = paletteValue;
        }

        return b;
    }

    [Fact]
    public void Parse_ReadsTheDimensionsLittleEndian()
    {
        // ⚠⚠ The whole trap in one assertion. Retail plates are 640x480; read big-endian the same
        // bytes give 32770x57345, which is why this is worth pinning rather than assuming Fallout's
        // usual big-endian convention.
        var b = Rix(640, 480);
        Assert.Equal(307_978, b.Length);

        var image = FalloutRixImage.Parse(b, "SPLASH0.RIX");
        Assert.Equal((640, 480), (image.Bitmap.Width, image.Bitmap.Height));
    }

    [Fact]
    public void Parse_PromotesTheSixBitPalette()
    {
        // 63 is full intensity in 6-bit VGA; left unpromoted the splash renders four times too dark.
        var image = FalloutRixImage.Parse(Rix(paletteValue: 63), "T.RIX");

        Assert.Equal(255, image.Palette.Rgba[0]);
        Assert.Equal(255, image.Palette.Rgba[1]);
        Assert.Equal(255, image.Palette.Rgba[2]);
    }

    [Fact]
    public void Parse_RequiresTheExactSize()
    {
        // The arithmetic is exact — header + palette + width*height — so a byte either way is wrong.
        Assert.Throws<InvalidDataException>(() => FalloutRixImage.Parse(Rix(sizeBias: 1), "BAD.RIX"));
        Assert.Throws<InvalidDataException>(() => FalloutRixImage.Parse(Rix(sizeBias: -1), "BAD.RIX"));
    }

    [Fact]
    public void Parse_CarriesEveryPixelThrough()
    {
        var b = Rix(4, 3);
        var start = FalloutRixImage.HeaderLength + Palette.RgbByteCount;
        for (var i = 0; i < 12; i++)
        {
            b[start + i] = (byte)(i * 2);
        }

        var image = FalloutRixImage.Parse(b, "T.RIX");
        Assert.Equal(12, image.Bitmap.Indices.Length);
        Assert.Equal(22, image.Bitmap.Indices[11]);
    }

    [Fact]
    public void Parse_RejectsAMissingMagicAndZeroDimensions()
    {
        Assert.Throws<InvalidDataException>(() => FalloutRixImage.Parse("FRM....."u8.ToArray(), "x.frm"));

        var zero = Rix();
        BinaryPrimitives.WriteUInt16LittleEndian(zero.AsSpan(4), 0);
        Assert.Throws<InvalidDataException>(() => FalloutRixImage.Parse(zero, "BAD.RIX"));
    }

    [Fact]
    public void IsRixImage_ChecksTheMagic()
    {
        Assert.True(FalloutRixImage.IsRixImage(Rix()));
        Assert.False(FalloutRixImage.IsRixImage("RIX2\0\0\0\0\0\0"u8.ToArray()));
    }
}
