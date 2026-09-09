using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.VanBuren;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.VanBuren;

/// <summary>
///     Vectors for the Van Buren uncompressed image payload, shaped after the 1,526 shipped in the
///     prototype's <c>.grp</c> archives, measured 2026-09-06.
/// </summary>
public sealed class VanBurenImageFileTests
{
    private static byte[] Image(int width, int height, int bpp, int trailer = 0,
        uint format = VanBurenImageFile.UncompressedFormat)
    {
        var pixels = width * height * (bpp / 8);
        var b = new byte[VanBurenImageFile.HeaderLength + pixels + trailer];
        BinaryPrimitives.WriteUInt32LittleEndian(b, format);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(14), (ushort)height);
        b[16] = (byte)bpp;
        return b;
    }

    [Theory]
    [InlineData(256, 256, 24)]
    [InlineData(64, 64, 24)]
    [InlineData(128, 128, 32)]
    [InlineData(32, 32, 24)]
    public void Parse_ReadsTheShippedImageShapes(int width, int height, int bpp)
    {
        var image = VanBurenImageFile.Parse(Image(width, height, bpp), "tex");

        Assert.Equal(width, image.Width);
        Assert.Equal(height, image.Height);
        Assert.Equal(bpp, image.BitsPerPixel);
        Assert.Equal(VanBurenImageFile.HeaderLength, image.PixelOffset);
        Assert.Equal(width * height * (bpp / 8), image.PixelLength);
    }

    [Fact]
    public void Parse_ReportsTheTwentySixByteTrailerRatherThanFoldingItIn()
    {
        // ⚠ 939 of the 1,526 shipped images carry a 26-byte trailer after their pixels. The payload
        // length is therefore NOT 18 + w*h*bpp/8 in general, and must never be used to derive the
        // dimensions.
        Assert.Equal(26, VanBurenImageFile.Parse(Image(64, 64, 24, 26), "tex").TrailerLength);
        Assert.Equal(0, VanBurenImageFile.Parse(Image(64, 64, 24), "tex").TrailerLength);
    }

    [Fact]
    public void Parse_RejectsTheCompressedSiblingFamily()
    {
        // ⚠ Format 0x000A0000 holds 85 payloads whose size does not satisfy the pixel relation —
        // compressed, matching the .rle names the EMAP manifests carry. Not decoded.
        var b = Image(64, 64, 24, format: VanBurenImageFile.CompressedFormat);

        Assert.False(VanBurenImageFile.IsImage(b));
        Assert.Throws<InvalidDataException>(() => VanBurenImageFile.Parse(b, "rle"));
    }

    [Fact]
    public void Parse_RejectsABitDepthTheGameNeverShips()
    {
        // Only 24 and 32 occur. Accepting others lets a payload that merely opens with the same
        // dword yield an enormous bogus image.
        var b = Image(64, 64, 24);
        b[16] = 8;

        Assert.Throws<InvalidDataException>(() => VanBurenImageFile.Parse(b, "tex"));
    }

    [Fact]
    public void Parse_RejectsAHeaderThatClaimsMorePixelsThanExist()
    {
        var b = Image(64, 64, 24);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), 4096);

        Assert.Throws<InvalidDataException>(() => VanBurenImageFile.Parse(b, "tex"));
    }

    [Fact]
    public void DecodeRgb_SwapsTheStoredBgrOrder()
    {
        // ⚠ Stored BGR, returned RGB. Getting this backwards produces a plausible-looking image
        // with the red and blue channels exchanged, which is easy to miss on grey textures.
        var b = Image(1, 1, 24);
        b[VanBurenImageFile.HeaderLength] = 0x10; // B
        b[VanBurenImageFile.HeaderLength + 1] = 0x20; // G
        b[VanBurenImageFile.HeaderLength + 2] = 0x30; // R

        var rgb = VanBurenImageFile.DecodeRgb(b, VanBurenImageFile.Parse(b, "tex"));

        Assert.Equal([0x30, 0x20, 0x10], rgb);
    }

    [Fact]
    public void DecodeRgb_HandlesThirtyTwoBitPixelsByDroppingTheFourthChannel()
    {
        var b = Image(1, 1, 32);
        b[VanBurenImageFile.HeaderLength] = 0x11;
        b[VanBurenImageFile.HeaderLength + 1] = 0x22;
        b[VanBurenImageFile.HeaderLength + 2] = 0x33;
        b[VanBurenImageFile.HeaderLength + 3] = 0x44;

        Assert.Equal([0x33, 0x22, 0x11], VanBurenImageFile.DecodeRgb(b, VanBurenImageFile.Parse(b, "tex")));
    }
}