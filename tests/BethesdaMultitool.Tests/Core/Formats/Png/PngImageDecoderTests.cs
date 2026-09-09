using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Png;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Png;

/// <summary>
///     Header parsing and RGBA decode for <see cref="PngImageDecoder" />.
///     <para>
///         The IHDR tests build their bytes by hand so the expected values are known
///         independently of any decoder. CRCs are left zero deliberately: <c>ReadInfo</c> reads
///         the header without validating checksums, and pinning that is part of the contract —
///         a browser should be able to describe a slightly damaged file.
///     </para>
///     <para>
///         The pixel test is a ROUND TRIP through the unrelated <see cref="PngWriter" /> encode
///         path rather than a self-check: it starts from a hand-written RGBA buffer whose every
///         byte is known, so a channel-order swap, a dropped alpha or a width/height transposition
///         all fail it.
///     </para>
/// </summary>
public class PngImageDecoderTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Builds a header-only PNG: signature + IHDR + optional tRNS + IEND.</summary>
    private static byte[] BuildHeader(
        int width, int height, byte bitDepth, byte colourType, byte interlace, bool withTrns)
    {
        var bytes = new List<byte>();
        bytes.AddRange(PngSignature);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = bitDepth;
        ihdr[9] = colourType;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = interlace;
        AppendChunk(bytes, "IHDR"u8, ihdr);

        if (withTrns)
        {
            AppendChunk(bytes, "tRNS"u8, [0x00, 0xFF]);
        }

        AppendChunk(bytes, "IEND"u8, []);
        return [.. bytes];
    }

    private static void AppendChunk(List<byte> bytes, ReadOnlySpan<byte> type, ReadOnlySpan<byte> body)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)body.Length);
        bytes.AddRange(length);
        bytes.AddRange(type);
        bytes.AddRange(body);
        bytes.AddRange([0, 0, 0, 0]); // CRC — not validated by ReadInfo.
    }

    [Fact]
    public void HasPngSignature_RecognizesThePngMagic()
    {
        Assert.True(PngImageDecoder.HasPngSignature(BuildHeader(1, 1, 8, 3, 0, false)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(7)]
    public void HasPngSignature_RejectsTruncatedOrWrongMagic(int keptBytes)
    {
        var truncated = PngSignature.AsSpan(0, keptBytes).ToArray();
        Assert.False(PngImageDecoder.HasPngSignature(truncated));
    }

    [Fact]
    public void HasPngSignature_RejectsAnotherFormat()
    {
        Assert.False(PngImageDecoder.HasPngSignature("BIKi"u8));
    }

    /// <summary>The three paletted depths every Travels image uses.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void ReadInfo_ReadsPalettedHeadersAtEveryShippedDepth(byte depth)
    {
        var info = PngImageDecoder.ReadInfo(BuildHeader(176, 208, depth, 3, 0, false));

        Assert.NotNull(info);
        Assert.Equal(176, info.Value.Width);
        Assert.Equal(208, info.Value.Height);
        Assert.Equal(depth, info.Value.BitDepth);
        Assert.Equal(3, info.Value.ColourType);
        Assert.True(info.Value.IsPalettized);
        Assert.Equal("palette", info.Value.ColourTypeName);
        Assert.False(info.Value.Interlaced);
    }

    /// <summary>
    ///     Interlacing must be reported, not swallowed — one retail Stormhold image is Adam7 and
    ///     a reader that treats it as sequential produces a scrambled picture with no error.
    /// </summary>
    [Fact]
    public void ReadInfo_ReportsAdam7Interlacing()
    {
        Assert.True(PngImageDecoder.ReadInfo(BuildHeader(32, 32, 8, 3, 1, false))!.Value.Interlaced);
        Assert.False(PngImageDecoder.ReadInfo(BuildHeader(32, 32, 8, 3, 0, false))!.Value.Interlaced);
    }

    [Fact]
    public void ReadInfo_DetectsTheTransparencyChunk()
    {
        Assert.True(PngImageDecoder.ReadInfo(BuildHeader(8, 8, 8, 3, 0, true))!.Value.HasTransparencyChunk);
        Assert.False(PngImageDecoder.ReadInfo(BuildHeader(8, 8, 8, 3, 0, false))!.Value.HasTransparencyChunk);
    }

    [Fact]
    public void ReadInfo_ReturnsNullForNonPngData()
    {
        Assert.Null(PngImageDecoder.ReadInfo("not a png at all, really"u8));
    }

    [Fact]
    public void ReadInfo_ReturnsNullWhenTruncatedInsideIhdr()
    {
        var truncated = BuildHeader(16, 16, 8, 3, 0, false).AsSpan(0, 20).ToArray();
        Assert.Null(PngImageDecoder.ReadInfo(truncated));
    }

    /// <summary>A zero dimension is not a decodable image, whatever the rest of the header says.</summary>
    [Fact]
    public void ReadInfo_ReturnsNullForZeroDimensions()
    {
        Assert.Null(PngImageDecoder.ReadInfo(BuildHeader(0, 16, 8, 3, 0, false)));
        Assert.Null(PngImageDecoder.ReadInfo(BuildHeader(16, 0, 8, 3, 0, false)));
    }

    [Fact]
    public void Decode_ThrowsForNonPngData()
    {
        var bytes = "definitely not a png"u8.ToArray();
        Assert.Throws<InvalidDataException>(() => PngImageDecoder.Decode(bytes));
    }

    /// <summary>
    ///     Round trip: encode a known RGBA buffer, decode it back, and compare byte for byte.
    ///     The four pixels are deliberately asymmetric in every channel so red/blue transposition
    ///     and row/column transposition cannot both pass.
    /// </summary>
    [Fact]
    public void Decode_RoundTripsRgbaPixelsExactly()
    {
        byte[] source =
        [
            255, 0, 0, 255, // red, opaque
            0, 255, 0, 128, // green, half alpha
            0, 0, 255, 255, // blue, opaque
            10, 20, 30, 40 // arbitrary, low alpha
        ];

        var encoded = PngWriter.EncodeRgba(source, 2, 2);
        var info = PngImageDecoder.ReadInfo(encoded);
        Assert.NotNull(info);
        Assert.Equal(2, info.Value.Width);
        Assert.Equal(2, info.Value.Height);

        var decoded = PngImageDecoder.Decode(encoded);

        Assert.Equal(2, decoded.Width);
        Assert.Equal(2, decoded.Height);
        Assert.Equal(1, decoded.MipCount);
        Assert.Equal(source, decoded.Pixels);
    }

    /// <summary>An image with no alpha still has to yield four channels, all fully opaque.</summary>
    [Fact]
    public void Decode_AddsAnOpaqueAlphaChannelWhenTheSourceHasNone()
    {
        byte[] opaque =
        [
            200, 100, 50, 255,
            50, 100, 200, 255
        ];

        var decoded = PngImageDecoder.Decode(PngWriter.EncodeRgba(opaque, 2, 1));

        Assert.Equal(2, decoded.Width);
        Assert.Equal(1, decoded.Height);
        Assert.Equal(255, decoded.Pixels[3]);
        Assert.Equal(255, decoded.Pixels[7]);
    }
}