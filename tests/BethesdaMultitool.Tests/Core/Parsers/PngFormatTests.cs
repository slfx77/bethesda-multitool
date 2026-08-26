using BethesdaMultitool.Core.Formats.Png;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Parsers;

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

    [Fact]
    public void Parse_IendBeyondOld64KWindow_FindsTrueSizeInWideSpan()
    {
        // Arrange - IEND at ~200KB, unreachable under the old 64KB parse window
        const int iendPosition = 200 * 1024;
        var data = new byte[300 * 1024];
        var png = CreateMinimalPng();
        Array.Copy(png, data, png.Length - 12); // signature + IHDR, no IEND
        data[iendPosition] = 0x49; // "IEND"
        data[iendPosition + 1] = 0x45;
        data[iendPosition + 2] = 0x4E;
        data[iendPosition + 3] = 0x44;

        // Act
        var result = _parser.Parse(data);

        // Assert - true size found (IEND position + type + CRC), no fallback flagged
        Assert.NotNull(result);
        Assert.Equal(iendPosition + 8, result.EstimatedSize);
        Assert.False(result.Metadata.ContainsKey("boundaryFallback"));
    }

    [Fact]
    public void Parse_NoIendButValidIhdr_ReturnsEstimateWithBoundaryFallback()
    {
        // Arrange - valid signature + IHDR, IEND never appears in the span
        var data = new byte[128 * 1024];
        var png = CreateMinimalPng();
        Array.Copy(png, data, png.Length - 12); // signature + IHDR, no IEND

        // Act
        var result = _parser.Parse(data);

        // Assert - the file is no longer silently dropped
        Assert.NotNull(result);
        Assert.Equal(data.Length, result.EstimatedSize);
        Assert.True(Assert.IsType<bool>(result.Metadata["boundaryFallback"]));
        Assert.Equal("IEND not found in parse window", result.Metadata["boundaryFallbackReason"]);
    }

    [Fact]
    public void Parse_NoIendAndNoValidIhdr_ReturnsNull()
    {
        // Arrange - PNG signature followed by garbage (no IHDR tag, no IEND)
        var data = new byte[1024];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(data, 0);

        // Act
        var result = _parser.Parse(data);

        // Assert - invalid headers still return null
        Assert.Null(result);
    }

    #endregion
}