using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class DdsGpuTexturePayloadParserTests
{
    [Theory]
    [InlineData("DXT1", "BC1", 8)]
    [InlineData("DXT3", "BC2", 16)]
    [InlineData("DXT5", "BC3", 16)]
    [InlineData("ATI1", "BC4", 8)]
    [InlineData("ATI2", "BC5", 16)]
    public void Parse_CompressedMipChain_ReturnsGpuPayload(
        string fourCc,
        string expectedFormatName,
        int bytesPerBlock)
    {
        var expectedFormat = Enum.Parse<GpuTexturePayloadFormat>(expectedFormatName);
        var ddsData = CreateCompressedDds(
            width: 8,
            height: 8,
            fourCc,
            mipCount: 4,
            bytesPerBlock);

        var payload = DdsGpuTexturePayloadParser.Parse(ddsData);

        Assert.NotNull(payload);
        Assert.Equal(expectedFormat, payload!.Format);
        Assert.Equal(8, payload.Width);
        Assert.Equal(8, payload.Height);
        Assert.Equal(4, payload.MipCount);
        Assert.Equal(expectedFormat == GpuTexturePayloadFormat.BC5
            ? GpuNormalDecodeMode.Bc5ReconstructZ
            : GpuNormalDecodeMode.None, payload.NormalDecodeMode);

        AssertMip(payload.MipLevels[0], 8, 8, GetCompressedLevelSize(8, 8, bytesPerBlock), 1);
        AssertMip(payload.MipLevels[1], 4, 4, GetCompressedLevelSize(4, 4, bytesPerBlock), 2);
        AssertMip(payload.MipLevels[2], 2, 2, GetCompressedLevelSize(2, 2, bytesPerBlock), 3);
        AssertMip(payload.MipLevels[3], 1, 1, GetCompressedLevelSize(1, 1, bytesPerBlock), 4);
    }

    [Fact]
    public void Parse_UnsupportedFourCc_ReturnsNullForFallbackDecoder()
    {
        var ddsData = CreateCompressedDds(
            width: 8,
            height: 8,
            fourCc: "ABCD",
            mipCount: 1,
            bytesPerBlock: 16);

        Assert.Null(DdsGpuTexturePayloadParser.Parse(ddsData));
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
