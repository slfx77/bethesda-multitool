using Xunit;
using Xbox360MemoryCarver.Core.Parsers;

namespace Xbox360MemoryCarver.Tests.Core.Parsers;

/// <summary>
/// Tests for SignatureBoundaryScanner utility methods.
/// </summary>
public class SignatureBoundaryScannerTests
{
    #region IsPngSignature Tests

    [Fact]
    public void IsPngSignature_ValidPngHeader_ReturnsTrue()
    {
        // Arrange - PNG magic bytes
        byte[] data = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        // Act
        var result = SignatureBoundaryScanner.IsPngSignature(data, 0);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsPngSignature_InvalidData_ReturnsFalse()
    {
        // Arrange
        byte[] data = [0x00, 0x00, 0x00, 0x00];

        // Act
        var result = SignatureBoundaryScanner.IsPngSignature(data, 0);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsPngSignature_WithOffset_ReturnsTrue()
    {
        // Arrange
        byte[] data = [0x00, 0x00, 0x89, 0x50, 0x4E, 0x47];

        // Act
        var result = SignatureBoundaryScanner.IsPngSignature(data, 2);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsPngSignature_InsufficientData_ReturnsFalse()
    {
        // Arrange
        byte[] data = [0x89, 0x50, 0x4E]; // Only 3 bytes

        // Act
        var result = SignatureBoundaryScanner.IsPngSignature(data, 0);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region IsValidRiffHeader Tests

    [Fact]
    public void IsValidRiffHeader_ValidWaveHeader_ReturnsTrue()
    {
        // Arrange - RIFF....WAVE
        var data = new byte[12];
        "RIFF"u8.CopyTo(data.AsSpan(0));
        // Size: 1000 (little-endian)
        data[4] = 0xE8;
        data[5] = 0x03;
        data[6] = 0x00;
        data[7] = 0x00;
        "WAVE"u8.CopyTo(data.AsSpan(8));

        // Act
        var result = SignatureBoundaryScanner.IsValidRiffHeader(data, 0);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsValidRiffHeader_TooSmallSize_ReturnsFalse()
    {
        // Arrange - RIFF with size < 36
        var data = new byte[12];
        "RIFF"u8.CopyTo(data.AsSpan(0));
        data[4] = 0x10; // Size: 16
        data[5] = 0x00;
        data[6] = 0x00;
        data[7] = 0x00;
        "WAVE"u8.CopyTo(data.AsSpan(8));

        // Act
        var result = SignatureBoundaryScanner.IsValidRiffHeader(data, 0);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsValidRiffHeader_NotWaveFormat_ReturnsFalse()
    {
        // Arrange - RIFF....AVI (not WAVE)
        var data = new byte[12];
        "RIFF"u8.CopyTo(data.AsSpan(0));
        data[4] = 0xE8;
        data[5] = 0x03;
        data[6] = 0x00;
        data[7] = 0x00;
        "AVI "u8.CopyTo(data.AsSpan(8));

        // Act
        var result = SignatureBoundaryScanner.IsValidRiffHeader(data, 0);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsValidRiffHeader_InsufficientData_ReturnsFalse()
    {
        // Arrange
        byte[] data = [0x52, 0x49, 0x46, 0x46, 0x00, 0x00]; // Only 6 bytes

        // Act
        var result = SignatureBoundaryScanner.IsValidRiffHeader(data, 0);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region IsKnownSignature Tests

    [Theory]
    [InlineData("3XDO")]
    [InlineData("3XDR")]
    [InlineData("RIFF")]
    [InlineData("XEX2")]
    [InlineData("XUIS")]
    [InlineData("XUIB")]
    [InlineData("XDBF")]
    [InlineData("TES4")]
    [InlineData("LIPS")]
    [InlineData("scn ")]
    [InlineData("DDS ")]
    public void IsKnownSignature_KnownSignatures_ReturnsTrue(string signature)
    {
        // Arrange
        var data = new byte[4];
        System.Text.Encoding.ASCII.GetBytes(signature).CopyTo(data, 0);

        // Act
        var result = SignatureBoundaryScanner.IsKnownSignature(data, 0);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsKnownSignature_PngSignature_ReturnsTrue()
    {
        // Arrange
        byte[] data = [0x89, 0x50, 0x4E, 0x47];

        // Act
        var result = SignatureBoundaryScanner.IsKnownSignature(data, 0);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsKnownSignature_GamebryoSignature_ReturnsTrue()
    {
        // Arrange
        var data = "Gamebryo File Format"u8.ToArray();

        // Act
        var result = SignatureBoundaryScanner.IsKnownSignature(data, 0);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsKnownSignature_UnknownSignature_ReturnsFalse()
    {
        // Arrange
        byte[] data = [0x00, 0x00, 0x00, 0x00];

        // Act
        var result = SignatureBoundaryScanner.IsKnownSignature(data, 0);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region FindNextSignature Tests

    [Fact]
    public void FindNextSignature_FindsDdxSignature()
    {
        // Arrange - Some data followed by 3XDO signature
        var data = new byte[100];
        "3XDO"u8.CopyTo(data.AsSpan(50));

        // Act
        var result = SignatureBoundaryScanner.FindNextSignature(data, 0, 10, 100);

        // Assert
        Assert.Equal(50, result);
    }

    [Fact]
    public void FindNextSignature_RespectsMinSize()
    {
        // Arrange - Signature at position 20, but minSize is 30
        var data = new byte[100];
        "3XDO"u8.CopyTo(data.AsSpan(20));

        // Act
        var result = SignatureBoundaryScanner.FindNextSignature(data, 0, 30, 100);

        // Assert
        Assert.Equal(-1, result); // Should not find it because it's before minSize
    }

    [Fact]
    public void FindNextSignature_RespectsMaxSize()
    {
        // Arrange - Signature at position 80, but maxSize is 50
        var data = new byte[100];
        "3XDO"u8.CopyTo(data.AsSpan(80));

        // Act
        var result = SignatureBoundaryScanner.FindNextSignature(data, 0, 10, 50);

        // Assert
        Assert.Equal(-1, result); // Should not find it because it's after maxSize
    }

    [Fact]
    public void FindNextSignature_ExcludesSignature()
    {
        // Arrange - Two signatures: exclude 3XDO, should find 3XDR
        var data = new byte[100];
        "3XDO"u8.CopyTo(data.AsSpan(30));
        "3XDR"u8.CopyTo(data.AsSpan(50));

        // Act
        var result = SignatureBoundaryScanner.FindNextSignature(data, 0, 10, 100, excludeSignature: "3XDO"u8);

        // Assert
        Assert.Equal(50, result);
    }

    [Fact]
    public void FindNextSignature_NoSignatureFound_ReturnsMinusOne()
    {
        // Arrange - No signatures in data
        var data = new byte[100];

        // Act
        var result = SignatureBoundaryScanner.FindNextSignature(data, 0, 10, 100);

        // Assert
        Assert.Equal(-1, result);
    }

    #endregion

    #region FindBoundary Tests

    [Fact]
    public void FindBoundary_FindsSignatureAndReturnsBoundary()
    {
        // Arrange
        var data = new byte[200];
        "3XDO"u8.CopyTo(data.AsSpan(100));

        // Act
        var result = SignatureBoundaryScanner.FindBoundary(data, 0, 10, 200, 150);

        // Assert
        Assert.Equal(100, result);
    }

    [Fact]
    public void FindBoundary_NoSignature_ReturnsDefaultSize()
    {
        // Arrange
        var data = new byte[200];

        // Act
        var result = SignatureBoundaryScanner.FindBoundary(data, 0, 10, 200, 150);

        // Assert
        Assert.Equal(150, result);
    }

    [Fact]
    public void FindBoundary_DefaultSizeExceedsAvailable_ReturnsAvailable()
    {
        // Arrange
        var data = new byte[100];

        // Act
        var result = SignatureBoundaryScanner.FindBoundary(data, 0, 10, 200, 500);

        // Assert
        Assert.Equal(100, result); // Capped at available data
    }

    #endregion
}
