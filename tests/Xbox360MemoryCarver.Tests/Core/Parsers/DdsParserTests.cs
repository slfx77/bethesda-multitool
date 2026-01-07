using System.Text;
using Xbox360MemoryCarver.Core.Formats.Dds;
using Xunit;

namespace Xbox360MemoryCarver.Tests.Core.Parsers;

/// <summary>
///     Tests for DdsFormat.
/// </summary>
public class DdsFormatTests
{
    private readonly DdsFormat _parser = new();

    #region FourCC Tests

    [Theory]
    [InlineData("DXT1")]
    [InlineData("DXT3")]
    [InlineData("DXT5")]
    public void ParseHeader_ValidFourCC_ReturnsCorrectFormat(string fourcc)
    {
        // Arrange
        var data = CreateDdsHeader(256, 256, fourcc);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(fourcc, (string)result.Metadata["fourCc"]);
    }

    #endregion

    #region Endianness Detection Tests

    [Fact]
    public void ParseHeader_LittleEndian_DetectsCorrectly()
    {
        // Arrange
        var data = CreateDdsHeader(256, 256, "DXT1");

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("little", result.Metadata["endianness"]);
        Assert.False((bool)result.Metadata["isXbox360"]);
    }

    #endregion

    #region Magic Bytes Tests

    [Fact]
    public void ParseHeader_ValidDdsMagic_ReturnsResult()
    {
        // Arrange
        var data = CreateDdsHeader(256, 256, "DXT1");

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("DDS", result.Format);
    }

    [Fact]
    public void ParseHeader_InvalidMagic_ReturnsNull()
    {
        // Arrange
        var data = new byte[128];
        "XXXX"u8.CopyTo(data.AsSpan(0));

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseHeader_InsufficientData_ReturnsNull()
    {
        // Arrange - DDS header requires 128 bytes minimum
        var data = new byte[64];
        "DDS "u8.CopyTo(data.AsSpan(0));

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region Dimension Tests

    [Theory]
    [InlineData(64, 64)]
    [InlineData(128, 128)]
    [InlineData(256, 256)]
    [InlineData(512, 512)]
    [InlineData(1024, 1024)]
    [InlineData(2048, 2048)]
    [InlineData(4096, 4096)]
    public void ParseHeader_ValidDimensions_ReturnsCorrectDimensions(int width, int height)
    {
        // Arrange
        var data = CreateDdsHeader(width, height, "DXT1");

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(width, result.Metadata["width"]);
        Assert.Equal(height, result.Metadata["height"]);
    }

    [Fact]
    public void ParseHeader_ZeroDimensions_ReturnsNull()
    {
        // Arrange
        var data = CreateDdsHeader(0, 0, "DXT1");

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseHeader_OversizedDimensions_ReturnsNull()
    {
        // Arrange - dimensions > 16384 are invalid
        var data = CreateDdsHeader(32768, 32768, "DXT1");

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region Size Estimation Tests

    [Fact]
    public void ParseHeader_DXT1_ReturnsCorrectSize()
    {
        // Arrange - DXT1 is 8 bytes per 4x4 block
        // 256x256 = 64x64 blocks = 4096 blocks * 8 bytes = 32768 bytes + 128 header
        var data = CreateDdsHeader(256, 256, "DXT1", 1);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(32768 + 128, result.EstimatedSize);
    }

    [Fact]
    public void ParseHeader_DXT5_ReturnsCorrectSize()
    {
        // Arrange - DXT5 is 16 bytes per 4x4 block
        // 256x256 = 64x64 blocks = 4096 blocks * 16 bytes = 65536 bytes + 128 header
        var data = CreateDdsHeader(256, 256, "DXT5", 1);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(65536 + 128, result.EstimatedSize);
    }

    [Fact]
    public void ParseHeader_WithMipmaps_IncludesMipmapSize()
    {
        // Arrange - 256x256 DXT1 with mipmaps
        var data = CreateDdsHeader(256, 256, "DXT1", 9); // 256 -> 1 = 9 levels

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        // Size should be larger than just the base level
        Assert.True(result.EstimatedSize > 32768 + 128);
    }

    #endregion

    #region Helper Methods

    private static byte[] CreateDdsHeader(int width, int height, string fourcc, int mipCount = 1)
    {
        var data = new byte[256]; // Extra space beyond header

        // Magic "DDS "
        "DDS "u8.CopyTo(data.AsSpan(0));

        // Header size (124 for standard DDS)
        WriteUInt32LE(data, 4, 124);

        // Flags
        WriteUInt32LE(data, 8, 0x1 | 0x2 | 0x4 | 0x1000); // CAPS, HEIGHT, WIDTH, PIXELFORMAT

        // Height
        WriteUInt32LE(data, 12, (uint)height);

        // Width
        WriteUInt32LE(data, 16, (uint)width);

        // Pitch or linear size
        WriteUInt32LE(data, 20, 0);

        // Depth
        WriteUInt32LE(data, 24, 0);

        // Mip map count
        WriteUInt32LE(data, 28, (uint)mipCount);

        // Reserved (44 bytes at offset 32)

        // Pixel format starts at offset 76
        // Size of pixel format structure (32)
        WriteUInt32LE(data, 76, 32);

        // Pixel format flags (DDPF_FOURCC = 0x4)
        WriteUInt32LE(data, 80, 0x4);

        // FourCC at offset 84
        Encoding.ASCII.GetBytes(fourcc).CopyTo(data, 84);

        // RGB bit count (0 for compressed)
        WriteUInt32LE(data, 88, 0);

        return data;
    }

    private static void WriteUInt32LE(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value & 0xFF);
        data[offset + 1] = (byte)((value >> 8) & 0xFF);
        data[offset + 2] = (byte)((value >> 16) & 0xFF);
        data[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    #endregion
}