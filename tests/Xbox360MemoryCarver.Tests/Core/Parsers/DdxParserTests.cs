using System.Text;
using Xbox360MemoryCarver.Core.Formats.Ddx;
using Xunit;

namespace Xbox360MemoryCarver.Tests.Core.Parsers;

/// <summary>
///     Tests for DdxFormat.
/// </summary>
public class DdxFormatTests
{
    private readonly DdxFormat _parser = new();

    #region Size Estimation Tests

    [Fact]
    public void ParseHeader_ReturnsPositiveEstimatedSize()
    {
        // Arrange
        var data = Create3XdoHeader(256, 256);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.EstimatedSize > 0);
    }

    #endregion

    #region Magic Bytes Tests

    [Fact]
    public void ParseHeader_3XDOMagic_ReturnsResult()
    {
        // Arrange
        var data = Create3XdoHeader(256, 256);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("3XDO", result.Format);
        // DDX is implicitly Xbox 360 format
        Assert.True(result.Metadata.ContainsKey("width"));
    }

    [Fact]
    public void ParseHeader_3XDRMagic_ReturnsResult()
    {
        // Arrange
        var data = Create3XdrHeader(256, 256);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("3XDR", result.Format);
        // DDX is implicitly Xbox 360 format
        Assert.True(result.Metadata.ContainsKey("width"));
    }

    [Fact]
    public void ParseHeader_InvalidMagic_ReturnsNull()
    {
        // Arrange
        var data = new byte[100];
        "XXXX"u8.CopyTo(data.AsSpan(0));

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region GPU Format Validation Tests

    [Fact]
    public void ParseHeader_UnknownGpuFormat_ReturnsNull()
    {
        // Arrange - create header with an unknown/invalid GPU format byte (0xFF)
        var data = CreateDdxHeaderWithFormat("3XDO", 256, 256, 4, 0xFF);

        // Act
        var result = _parser.Parse(data);

        // Assert - unknown GPU formats should be rejected to reduce false positives
        Assert.Null(result);
    }

    [Theory]
    [InlineData(0x12, "DXT1")] // Known DXT1 format
    [InlineData(0x52, "DXT1")] // Known DXT1 format (alternate)
    [InlineData(0x14, "DXT5")] // Known DXT5 format
    [InlineData(0x54, "DXT5")] // Known DXT5 format (alternate)
    public void ParseHeader_KnownGpuFormat_ReturnsResult(int formatByte, string expectedFormat)
    {
        // Arrange
        var data = CreateDdxHeaderWithFormat("3XDO", 256, 256, 4, (byte)formatByte);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedFormat, result.Metadata["formatName"]);
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
    [InlineData(256, 512)] // Non-square
    [InlineData(1024, 256)] // Non-square
    public void ParseHeader_ValidDimensions_ReturnsCorrectDimensions(int width, int height)
    {
        // Arrange
        var data = Create3XdoHeader(width, height);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(width, result.Metadata["width"]);
        Assert.Equal(height, result.Metadata["height"]);
    }

    [Fact]
    public void ParseHeader_OversizedDimensions_ReturnsNull()
    {
        // Arrange - dimensions > 4096 are invalid
        var data = Create3XdoHeader(8192, 8192);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region Version Tests

    [Fact]
    public void ParseHeader_ValidVersion_ReturnsResult()
    {
        // Arrange - version 4 is common
        var data = Create3XdoHeader(256, 256);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(4, result.Metadata["version"]);
    }

    [Fact]
    public void ParseHeader_InvalidVersion_ReturnsNull()
    {
        // Arrange - version < 3 is invalid
        var data = Create3XdoHeader(256, 256, 2);

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region Helper Methods

    private static byte[] Create3XdoHeader(int width, int height, ushort version = 4)
    {
        return CreateDdxHeader("3XDO", width, height, version);
    }

    private static byte[] Create3XdrHeader(int width, int height, ushort version = 4)
    {
        return CreateDdxHeader("3XDR", width, height, version);
    }

    private static byte[] CreateDdxHeader(string magic, int width, int height, ushort version)
    {
        return CreateDdxHeaderWithFormat(magic, width, height, version, 0x52); // DXT1 format
    }

    private static byte[] CreateDdxHeaderWithFormat(string magic, int width, int height, ushort version, byte gpuFormat)
    {
        // Create a minimal DDX header (0x44 = 68 bytes minimum)
        var data = new byte[200];

        // Magic at 0x00
        Encoding.ASCII.GetBytes(magic).CopyTo(data, 0);

        // Version at 0x07 (little-endian)
        data[7] = (byte)(version & 0xFF);
        data[8] = (byte)((version >> 8) & 0xFF);

        // Flags at 0x24 - must have high bit set (>= 0x80)
        data[0x24] = 0x80;

        // Format dword at 0x28 (big-endian) - includes mip count
        // Low byte is format, bits 16-19 are mip count - 1
        uint formatDword = gpuFormat; // GPU format with 1 mip level (mip count - 1 = 0)
        data[0x28] = (byte)((formatDword >> 24) & 0xFF);
        data[0x29] = (byte)((formatDword >> 16) & 0xFF);
        data[0x2A] = (byte)((formatDword >> 8) & 0xFF);
        data[0x2B] = (byte)(formatDword & 0xFF);

        // Size dword at 0x2C (big-endian)
        // Bits 0-12: width - 1
        // Bits 13-25: height - 1
        // Clamp to valid range for the encoding
        var encodedWidth = Math.Max(0, Math.Min(width - 1, 0x1FFF));
        var encodedHeight = Math.Max(0, Math.Min(height - 1, 0x1FFF));
        var sizeDword = (uint)(encodedWidth & 0x1FFF) | (uint)((encodedHeight & 0x1FFF) << 13);
        data[0x2C] = (byte)((sizeDword >> 24) & 0xFF);
        data[0x2D] = (byte)((sizeDword >> 16) & 0xFF);
        data[0x2E] = (byte)((sizeDword >> 8) & 0xFF);
        data[0x2F] = (byte)(sizeDword & 0xFF);

        return data;
    }

    #endregion
}