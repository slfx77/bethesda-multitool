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
    [InlineData(DxgiBc1UnormSrgb, 8)] // BC1: 8 bytes / 4x4 block
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
        var dds = BuildDx10Dds(DxgiBc7UnormSrgb, 16, 0x40);

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
        WriteU32(dds, 12, 4); // height
        WriteU32(dds, 16, 4); // width
        WriteU32(dds, 28, 1); // mip count
        WriteU32(dds, 76, 32); // pixel-format size
        WriteU32(dds, 80, 0x4); // DDPF_FOURCC
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

    [Fact]
    public void Decode_Bc5SignedFlatNormal_ProducesMidGrayWithZUp()
    {
        // BC5S (signed) is the Fallout 4/76 tangent-space normal map format. An all-zero block means
        // both endpoints/indices are 0 → signed value 0 → mid-gray X/Y; the reconstructed Z points up.
        // (Decoding it as UNSIGNED would put the zero point at 0/black, which is the bug being fixed.)
        var result = DdsTextureDecoder.Decode(BuildClassicDds("BC5S", new byte[16], 16));

        Assert.NotNull(result);
        var px = result!.Pixels; // RGBA of pixel 0
        Assert.InRange(px[0], 126, 130); // X ≈ 0 → 128
        Assert.InRange(px[1], 126, 130); // Y ≈ 0 → 128
        Assert.InRange(px[2], 250, 255); // Z ≈ +1 → ~255
        Assert.Equal(255, px[3]);
    }

    [Fact]
    public void Decode_Bc5SignedMaxRed_ProducesFullRedChannel()
    {
        // Red-channel endpoints both +127 (= +1.0); green block zero. Index 0 selects endpoint 0.
        var block = new byte[16];
        block[0] = 127;
        block[1] = 127;
        var result = DdsTextureDecoder.Decode(BuildClassicDds("BC5S", block, 16));

        Assert.NotNull(result);
        Assert.InRange(result!.Pixels[0], 253, 255); // X = +1 → ~255
    }

    /// <summary>Builds a 4×4, single-mip DDS with a classic <paramref name="fourCc" /> pixel format.</summary>
    private static byte[] BuildClassicDds(string fourCc, byte[] block, int blockBytes)
    {
        var dds = new byte[128 + blockBytes];
        dds[0] = (byte)'D';
        dds[1] = (byte)'D';
        dds[2] = (byte)'S';
        dds[3] = (byte)' ';
        WriteU32(dds, 4, 124);
        WriteU32(dds, 8, 0x1007);
        WriteU32(dds, 12, 4); // height
        WriteU32(dds, 16, 4); // width
        WriteU32(dds, 28, 1); // mip count
        WriteU32(dds, 76, 32); // pixel-format size
        WriteU32(dds, 80, 0x4); // DDPF_FOURCC
        for (var i = 0; i < 4; i++)
        {
            dds[84 + i] = (byte)fourCc[i];
        }

        Array.Copy(block, 0, dds, 128, Math.Min(block.Length, blockBytes));
        return dds;
    }
}