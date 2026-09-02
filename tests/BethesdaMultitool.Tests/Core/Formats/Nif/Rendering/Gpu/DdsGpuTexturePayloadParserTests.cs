using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.DXGI;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

public sealed class DdsGpuTexturePayloadParserTests
{
    [Theory]
    [InlineData("DXT1", "BC1", 8)]
    [InlineData("DXT3", "BC2", 16)]
    [InlineData("DXT5", "BC3", 16)]
    [InlineData("ATI1", "BC4", 8)]
    [InlineData("ATI2", "BC5", 16)]
    [InlineData("BC4U", "BC4", 8)]
    [InlineData("BC5U", "BC5", 16)]
    [InlineData("BC4S", "BC4S", 8)]
    [InlineData("BC5S", "BC5S", 16)]
    public void Parse_CompressedMipChain_ReturnsGpuPayload(
        string fourCc,
        string expectedFormatName,
        int bytesPerBlock)
    {
        var expectedFormat = Enum.Parse<GpuTexturePayloadFormat>(expectedFormatName);
        var ddsData = CreateCompressedDds(
            8,
            8,
            fourCc,
            4,
            bytesPerBlock);

        var payload = DdsGpuTexturePayloadParser.Parse(ddsData);

        Assert.NotNull(payload);
        Assert.Equal(expectedFormat, payload!.Format);
        Assert.Equal(8, payload.Width);
        Assert.Equal(8, payload.Height);
        Assert.Equal(4, payload.MipCount);
        Assert.Equal(ExpectedNormalDecodeMode(expectedFormat), payload.NormalDecodeMode);

        AssertMip(payload.MipLevels[0], 8, 8, GetCompressedLevelSize(8, 8, bytesPerBlock), 1);
        AssertMip(payload.MipLevels[1], 4, 4, GetCompressedLevelSize(4, 4, bytesPerBlock), 2);
        AssertMip(payload.MipLevels[2], 2, 2, GetCompressedLevelSize(2, 2, bytesPerBlock), 3);
        AssertMip(payload.MipLevels[3], 1, 1, GetCompressedLevelSize(1, 1, bytesPerBlock), 4);
    }

    [Fact]
    public void Parse_UnsupportedFourCc_ReturnsNullForFallbackDecoder()
    {
        var ddsData = CreateCompressedDds(
            8,
            8,
            "ABCD",
            1,
            16);

        Assert.Null(DdsGpuTexturePayloadParser.Parse(ddsData));
    }

    [Theory]
    [InlineData(71u, "BC1", 8)] // BC1_UNORM
    [InlineData(72u, "BC1", 8)] // BC1_UNORM_SRGB
    [InlineData(77u, "BC3", 16)] // BC3_UNORM
    [InlineData(80u, "BC4", 8)] // BC4_UNORM
    [InlineData(81u, "BC4S", 8)] // BC4_SNORM
    [InlineData(83u, "BC5", 16)] // BC5_UNORM
    [InlineData(84u, "BC5S", 16)] // BC5_SNORM
    [InlineData(98u, "BC7", 16)] // BC7_UNORM
    [InlineData(99u, "BC7", 16)] // BC7_UNORM_SRGB
    public void Parse_Dx10Header_ReturnsGpuPayload(uint dxgiFormat, string expectedFormatName, int bytesPerBlock)
    {
        var expectedFormat = Enum.Parse<GpuTexturePayloadFormat>(expectedFormatName);
        var ddsData = CreateDx10Dds(8, 8, dxgiFormat, 4, bytesPerBlock);

        var payload = DdsGpuTexturePayloadParser.Parse(ddsData);

        Assert.NotNull(payload);
        Assert.Equal(expectedFormat, payload!.Format);
        Assert.Equal(ExpectedNormalDecodeMode(expectedFormat), payload.NormalDecodeMode);
        Assert.Equal(4, payload.MipCount);
        AssertMip(payload.MipLevels[0], 8, 8, GetCompressedLevelSize(8, 8, bytesPerBlock), 1);
        AssertMip(payload.MipLevels[3], 1, 1, GetCompressedLevelSize(1, 1, bytesPerBlock), 4);
    }

    [Fact]
    public void SignedBcPayloadsKeepSignedDxgiViews()
    {
        Assert.Equal(Format.BC4_SNorm,
            GpuTextureFormatHelpers12.ToDxgiFormat(GpuTexturePayloadFormat.BC4S));
        Assert.Equal(Format.BC5_SNorm,
            GpuTextureFormatHelpers12.ToDxgiFormat(GpuTexturePayloadFormat.BC5S));
    }

    [Theory]
    [InlineData(87u)] // B8G8R8A8_UNORM — no BCn payload mapping
    [InlineData(95u)] // BC6H_UF16 — unsupported
    public void Parse_Dx10UnsupportedFormat_ReturnsNullForFallbackDecoder(uint dxgiFormat)
    {
        var ddsData = CreateDx10Dds(8, 8, dxgiFormat, 1, 16);

        Assert.Null(DdsGpuTexturePayloadParser.Parse(ddsData));
    }

    [Fact]
    public void Parse_Dx10Cubemap_ReturnsSixFacePayload()
    {
        // DX10 cube: miscFlag 0x4, arraySize 1, six face-major mip chains in the payload.
        var ddsData = CreateDx10Dds(8, 8, 98u, 2, 16, 0x4, 6);

        var payload = DdsGpuTexturePayloadParser.Parse(ddsData);

        Assert.NotNull(payload);
        Assert.True(payload!.IsCubemap);
        Assert.Equal(6, payload.ArraySize);
        Assert.Equal(2, payload.MipCount);
        Assert.Equal(12, payload.MipLevels.Count); // 6 faces × 2 mips, face-major
        Assert.Equal(8, payload.MipLevels[0].Width);
        Assert.Equal(4, payload.MipLevels[1].Width); // face 0 mip 1
        Assert.Equal(8, payload.MipLevels[2].Width); // face 1 mip 0
    }

    [Fact]
    public void Parse_Dx10CubemapTruncated_ReturnsNullForFallbackDecoder()
    {
        // Only one face's worth of data behind a cube header — a partial cube would desync the
        // face-major subresource layout, so the parser must reject rather than upload it.
        var ddsData = CreateDx10Dds(8, 8, 98u, 1, 16, 0x4);

        Assert.Null(DdsGpuTexturePayloadParser.Parse(ddsData));
    }

    [Fact]
    public void Parse_LegacyUncompressedBgraCubemap_SwizzlesToRgbaCubePayload()
    {
        // Mirrors FO4's shipped mipblur_DefaultOutside1.dds as extracted from the v7 BA2: NO
        // FourCC (ddspf flags 0x41 = RGB|ALPHAPIXELS, 32-bit BGRA masks) and the cubemap + face
        // bits folded into dwCaps @108 with dwCaps2 cleared (caps = 0x0040FE08).
        const int width = 2, height = 2, mips = 2;
        var faceBytes = width * height * 4 + 4; // 2×2 mip0 + 1×1 mip1
        var data = new byte[128 + faceBytes * 6];
        Encoding.ASCII.GetBytes("DDS ").CopyTo(data, 0);
        WriteUInt32(data, 4, 124);
        WriteUInt32(data, 8, 0x0002100F);
        WriteUInt32(data, 12, height);
        WriteUInt32(data, 16, width);
        WriteUInt32(data, 28, mips);
        WriteUInt32(data, 76, 32); // ddspf size
        WriteUInt32(data, 80, 0x41); // DDPF_RGB | DDPF_ALPHAPIXELS
        WriteUInt32(data, 88, 32); // bit count
        WriteUInt32(data, 92, 0x00FF0000); // R mask (BGRA memory order)
        WriteUInt32(data, 96, 0x0000FF00); // G mask
        WriteUInt32(data, 100, 0x000000FF); // B mask
        WriteUInt32(data, 104, 0xFF000000); // A mask
        WriteUInt32(data, 108, 0x0040FE08); // caps: TEXTURE|MIPMAP|COMPLEX + folded cube/face bits
        WriteUInt32(data, 112, 0); // caps2 cleared (BA2 v7 layout)
        for (var i = 128; i < data.Length; i += 4)
        {
            data[i] = 0x10; // B
            data[i + 1] = 0x20; // G
            data[i + 2] = 0x30; // R
            data[i + 3] = 0x40; // A
        }

        var payload = DdsGpuTexturePayloadParser.Parse(data);

        Assert.NotNull(payload);
        Assert.True(payload!.IsCubemap);
        Assert.Equal(GpuTexturePayloadFormat.Rgba8, payload.Format);
        Assert.Equal(2, payload.MipCount);
        Assert.Equal(12, payload.MipLevels.Count);
        // BGRA memory order swizzled to the RGBA the R8G8B8A8_UNorm upload expects.
        Assert.Equal(0x30, payload.MipLevels[0].Bytes[0]);
        Assert.Equal(0x20, payload.MipLevels[0].Bytes[1]);
        Assert.Equal(0x10, payload.MipLevels[0].Bytes[2]);
        Assert.Equal(0x40, payload.MipLevels[0].Bytes[3]);
    }

    private static void AssertMip(
        GpuTextureMipPayload mip,
        int width,
        int height,
        int byteLength,
        byte expectedFill)
    {
        Assert.Equal(width, mip.Width);
        Assert.Equal(height, mip.Height);
        Assert.Equal(byteLength, mip.Bytes.Length);
        Assert.All(mip.Bytes, value => Assert.Equal(expectedFill, value));
    }

    private static GpuNormalDecodeMode ExpectedNormalDecodeMode(GpuTexturePayloadFormat format) =>
        format switch
        {
            GpuTexturePayloadFormat.BC5 => GpuNormalDecodeMode.Bc5ReconstructZ,
            GpuTexturePayloadFormat.BC5S => GpuNormalDecodeMode.Bc5SignedReconstructZ,
            _ => GpuNormalDecodeMode.None
        };

    private static byte[] CreateCompressedDds(
        int width,
        int height,
        string fourCc,
        int mipCount,
        int bytesPerBlock)
    {
        var totalPixelDataSize = 0;
        var mipWidth = width;
        var mipHeight = height;
        for (var mipLevel = 0; mipLevel < mipCount; mipLevel++)
        {
            totalPixelDataSize += GetCompressedLevelSize(
                mipWidth,
                mipHeight,
                bytesPerBlock);
            mipWidth = Math.Max(1, mipWidth >> 1);
            mipHeight = Math.Max(1, mipHeight >> 1);
        }

        var data = new byte[128 + totalPixelDataSize];
        WriteHeader(data, width, height, fourCc, mipCount);

        var pos = 128;
        mipWidth = width;
        mipHeight = height;
        for (var mipLevel = 0; mipLevel < mipCount; mipLevel++)
        {
            var levelSize = GetCompressedLevelSize(
                mipWidth,
                mipHeight,
                bytesPerBlock);
            Array.Fill(data, (byte)(mipLevel + 1), pos, levelSize);
            pos += levelSize;
            mipWidth = Math.Max(1, mipWidth >> 1);
            mipHeight = Math.Max(1, mipHeight >> 1);
        }

        return data;
    }

    private static byte[] CreateDx10Dds(
        int width,
        int height,
        uint dxgiFormat,
        int mipCount,
        int bytesPerBlock,
        uint miscFlag = 0,
        int faces = 1)
    {
        var faceDataSize = 0;
        var mipWidth = width;
        var mipHeight = height;
        for (var mipLevel = 0; mipLevel < mipCount; mipLevel++)
        {
            faceDataSize += GetCompressedLevelSize(mipWidth, mipHeight, bytesPerBlock);
            mipWidth = Math.Max(1, mipWidth >> 1);
            mipHeight = Math.Max(1, mipHeight >> 1);
        }

        var data = new byte[148 + faceDataSize * faces];
        WriteHeader(data, width, height, "DX10", mipCount);
        WriteUInt32(data, 128, dxgiFormat);
        WriteUInt32(data, 132, 3); // resourceDimension = TEXTURE2D
        WriteUInt32(data, 136, miscFlag);
        WriteUInt32(data, 140, 1); // arraySize (1 per cube; the misc flag carries the 6 faces)
        WriteUInt32(data, 144, 0); // miscFlags2

        var pos = 148;
        for (var face = 0; face < faces; face++)
        {
            mipWidth = width;
            mipHeight = height;
            for (var mipLevel = 0; mipLevel < mipCount; mipLevel++)
            {
                var levelSize = GetCompressedLevelSize(mipWidth, mipHeight, bytesPerBlock);
                Array.Fill(data, (byte)(mipLevel + 1), pos, levelSize);
                pos += levelSize;
                mipWidth = Math.Max(1, mipWidth >> 1);
                mipHeight = Math.Max(1, mipHeight >> 1);
            }
        }

        return data;
    }

    private static int GetCompressedLevelSize(
        int width,
        int height,
        int bytesPerBlock)
    {
        var blocksWide = (width + 3) / 4;
        var blocksHigh = (height + 3) / 4;
        return blocksWide * blocksHigh * bytesPerBlock;
    }

    private static void WriteHeader(
        byte[] data,
        int width,
        int height,
        string fourCc,
        int mipCount)
    {
        Encoding.ASCII.GetBytes("DDS ").CopyTo(data, 0);
        WriteUInt32(data, 4, 124);
        WriteUInt32(data, 8, 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000);
        WriteUInt32(data, 12, (uint)height);
        WriteUInt32(data, 16, (uint)width);
        WriteUInt32(data, 28, (uint)mipCount);
        WriteUInt32(data, 76, 32);
        WriteUInt32(data, 80, 0x4);
        Encoding.ASCII.GetBytes(fourCc).CopyTo(data, 84);
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(offset, 4),
            value);
    }
}
