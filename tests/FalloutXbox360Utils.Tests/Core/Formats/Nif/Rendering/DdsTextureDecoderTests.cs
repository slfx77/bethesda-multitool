using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Dds;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class DdsTextureDecoderTests
{
    [Theory]
    [InlineData("DXT1", 8)]
    [InlineData("DXT3", 16)]
    [InlineData("DXT5", 16)]
    [InlineData("ATI2", 16)]
    public void Decode_CompressedMipChain_ReturnsAllLevels(
        string fourCc,
        int bytesPerBlock)
    {
        var ddsData = CreateCompressedDds(
            width: 8,
            height: 8,
            fourCc,
            mipCount: 4,
            bytesPerBlock);

        var decoded = DdsTextureDecoder.Decode(ddsData);

        Assert.NotNull(decoded);
        Assert.Equal(4, decoded.MipCount);
        AssertMipLevel(decoded.GetMipLevel(0), 8, 8);
        AssertMipLevel(decoded.GetMipLevel(1), 4, 4);
        AssertMipLevel(decoded.GetMipLevel(2), 2, 2);
        AssertMipLevel(decoded.GetMipLevel(3), 1, 1);
    }

    [Fact]
    public void FromBaseLevel_GeneratesDownsampledMipChain()
    {
        var pixels = new byte[]
        {
            255, 0, 0, 255,
            0, 255, 0, 255,
            0, 0, 255, 255,
            255, 255, 255, 255
        };

        var decoded = DecodedTexture.FromBaseLevel(pixels, 2, 2);
        var mipLevel = decoded.GetMipLevel(1);

        Assert.Equal(2, decoded.MipCount);
        AssertMipLevel(mipLevel, 1, 1);
        Assert.Equal(
            new byte[] { 128, 128, 128, 255 },
            mipLevel.Pixels);
    }

    private static void AssertMipLevel(
        DecodedTextureMipLevel mipLevel,
        int width,
        int height)
    {
        Assert.Equal(width, mipLevel.Width);
        Assert.Equal(height, mipLevel.Height);
        Assert.Equal(width * height * 4, mipLevel.Pixels.Length);
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
            for (var i = 0; i < levelSize; i++)
            {
                data[pos + i] = (byte)(mipLevel + 1);
            }

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
