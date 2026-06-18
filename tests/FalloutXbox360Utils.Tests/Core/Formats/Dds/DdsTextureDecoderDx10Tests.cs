using FalloutXbox360Utils.Core.Formats.Dds;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Dds;

/// <summary>
///     Tests DX10-header DDS decoding. Fallout 4 / Fallout 76 BA2 textures synthesize a DX10 header
///     (DXGI format enum) for sRGB and BC6H/BC7 formats; the decoder previously handled only classic
///     FourCC tags and returned null for every DX10 DDS, so FO76 textures rendered white. BC1/BC3 sRGB
///     route to the existing block decoders here.
/// </summary>
public class DdsTextureDecoderDx10Tests
{
    private const uint DxgiBc1UnormSrgb = 72;
    private const uint DxgiBc3UnormSrgb = 78;
    private const uint DxgiBc7UnormSrgb = 99;

    [Theory]
    [InlineData(DxgiBc1UnormSrgb, 8)]  // BC1: 8 bytes / 4x4 block
    [InlineData(DxgiBc3UnormSrgb, 16)] // BC3: 16 bytes / 4x4 block
    public void Decode_Dx10BlockCompressed_Succeeds(uint dxgiFormat, int blockBytes)
    {
        var dds = BuildDx10Dds(dxgiFormat, blockBytes);

        var result = DdsTextureDecoder.Decode(dds);

        Assert.NotNull(result);
        Assert.Equal(4, result!.Width);
        Assert.Equal(4, result.Height);
        Assert.Single(result.MipLevels);
        Assert.Equal(4 * 4 * 4, result.Pixels.Length); // RGBA8
    }

    [Fact]
    public void Decode_Dx10Bc7_Decodes()
    {
        // BC7 (DXGI 98/99) now decodes via BCnEncoder.Net. Use a well-formed single-block bitstream:
        // byte 0 = 0x40 selects BC7 mode 6 (single subset), the rest of the block is zero.
        var dds = BuildDx10Dds(DxgiBc7UnormSrgb, blockBytes: 16, firstBlockByte: 0x40);

        var result = DdsTextureDecoder.Decode(dds);

        Assert.NotNull(result);
        Assert.Equal(4, result!.Width);
        Assert.Equal(4, result.Height);
        Assert.Equal(4 * 4 * 4, result.Pixels.Length);
    }

    /// <summary>Builds a 4×4, single-mip DDS with a DX10 header carrying <paramref name="dxgiFormat" />.</summary>
    private static byte[] BuildDx10Dds(uint dxgiFormat, int blockBytes, byte firstBlockByte = 0)
    {
        var dds = new byte[148 + blockBytes]; // 128 header + 20 DX10 header + one 4x4 block
        dds[148] = firstBlockByte; // block-0 byte 0 (e.g. BC7 mode selector)
        // "DDS " magic + header size 124.
        dds[0] = (byte)'D';
        dds[1] = (byte)'D';
        dds[2] = (byte)'S';
        dds[3] = (byte)' ';
        WriteU32(dds, 4, 124);
        WriteU32(dds, 8, 0x1007); // CAPS | HEIGHT | WIDTH | PIXELFORMAT
        WriteU32(dds, 12, 4);     // height
        WriteU32(dds, 16, 4);     // width
        WriteU32(dds, 28, 1);     // mip count
        WriteU32(dds, 76, 32);    // pixel-format size
        WriteU32(dds, 80, 0x4);   // DDPF_FOURCC
        dds[84] = (byte)'D';
        dds[85] = (byte)'X';
        dds[86] = (byte)'1';
        dds[87] = (byte)'0';
        // DX10 extended header at offset 128.
        WriteU32(dds, 128, dxgiFormat);
        WriteU32(dds, 132, 3); // D3D10_RESOURCE_DIMENSION_TEXTURE2D
        WriteU32(dds, 140, 1); // array size
        // Block bytes left zero — a valid (all-zero) block that decodes without throwing.
        return dds;
    }

    private static void WriteU32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }
}
