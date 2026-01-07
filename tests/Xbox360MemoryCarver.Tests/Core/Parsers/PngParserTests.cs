using Xbox360MemoryCarver.Core.Formats.Png;
using Xunit;

namespace Xbox360MemoryCarver.Tests.Core.Parsers;

/// <summary>
///     Tests for PngFormat.
/// </summary>
public class PngFormatTests
{
    private readonly PngFormat _parser = new();

    #region Offset Tests

    [Fact]
    public void ParseHeader_WithOffset_ParsesCorrectly()
    {
        // Arrange
        var png = CreateMinimalPng();
        var data = new byte[50 + png.Length];
        png.CopyTo(data, 50);

        // Act
        var result = _parser.Parse(data, 50);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("PNG", result.Format);
    }

    #endregion

    #region Helper Methods

    private static byte[] CreateMinimalPng()
    {
        // Minimal valid PNG structure:
        // - PNG signature (8 bytes)
        // - IHDR chunk (13 bytes data + 12 bytes overhead = 25 bytes)
        // - IEND chunk (0 bytes data + 12 bytes overhead = 12 bytes)
        var data = new List<byte>();

        // PNG signature
        data.AddRange([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        // IHDR chunk
        data.AddRange([0x00, 0x00, 0x00, 0x0D]); // Length: 13
        data.AddRange([0x49, 0x48, 0x44, 0x52]); // "IHDR"
        data.AddRange([0x00, 0x00, 0x00, 0x01]); // Width: 1
        data.AddRange([0x00, 0x00, 0x00, 0x01]); // Height: 1
        data.Add(0x08); // Bit depth: 8
        data.Add(0x02); // Color type: RGB
        data.Add(0x00); // Compression method
        data.Add(0x00); // Filter method
        data.Add(0x00); // Interlace method
        data.AddRange([0x90, 0x77, 0x53, 0xDE]); // CRC (dummy)

        // IEND chunk
        data.AddRange([0x00, 0x00, 0x00, 0x00]); // Length: 0
        data.AddRange([0x49, 0x45, 0x4E, 0x44]); // "IEND"
        data.AddRange([0xAE, 0x42, 0x60, 0x82]); // CRC

        return [.. data];
    }

    #endregion

    #region Magic Bytes Tests

    [Fact]
    public void ParseHeader_ValidPngSignature_ReturnsResult()
    {
        // Arrange
        var data = CreateMinimalPng();

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("PNG", result.Format);
    }

    [Fact]
    public void ParseHeader_InvalidMagic_ReturnsNull()
    {
        // Arrange
        var data = new byte[100];
        data[0] = 0x00; // Wrong magic

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseHeader_InsufficientData_ReturnsNull()
    {
        // Arrange - only 4 bytes, need at least 8 for PNG signature
        byte[] data = [0x89, 0x50, 0x4E, 0x47];

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region IEND Detection Tests

    [Fact]
    public void ParseHeader_FindsIendChunk_ReturnsCorrectSize()
    {
        // Arrange
        var data = CreateMinimalPng();

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        // Size should include IEND chunk + CRC (8 bytes after IEND position)
        Assert.True(result.EstimatedSize > 8);
    }

    [Fact]
    public void ParseHeader_NoIendFound_ReturnsNull()
    {
        // Arrange - PNG header but no IEND chunk
        byte[] data =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG signature
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 // No IEND
        ];

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    #endregion
}