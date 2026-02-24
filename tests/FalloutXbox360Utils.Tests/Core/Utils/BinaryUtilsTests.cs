using FalloutXbox360Utils.Core.Utils;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Utils;

/// <summary>
///     Tests for BinaryUtils helper methods.
/// </summary>
public class BinaryUtilsTests
{
    #region FormatSize Tests

    [Theory]
    [InlineData(0, "0.00 B")]
    [InlineData(512, "512.00 B")]
    [InlineData(1024, "1.00 KB")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(1048576, "1.00 MB")]
    [InlineData(1073741824, "1.00 GB")]
    public void FormatSize_ReturnsCorrectFormat(long size, string expected)
    {
        // Act
        var result = BinaryUtils.FormatSize(size);

        // Assert
        Assert.Equal(expected, result);
    }

    #endregion

    #region AlignOffset Tests

    [Theory]
    [InlineData(0, 4, 0)]
    [InlineData(1, 4, 4)]
    [InlineData(4, 4, 4)]
    [InlineData(5, 4, 8)]
    [InlineData(100, 16, 112)]
    public void AlignOffset_ReturnsAlignedValue(long offset, int alignment, long expected)
    {
        // Act
        var result = BinaryUtils.AlignOffset(offset, alignment);

        // Assert
        Assert.Equal(expected, result);
    }

    #endregion

    #region SwapBytes16 Tests

    [Fact]
    public void SwapBytes16_SwapsCorrectly()
    {
        // Arrange - AA BB CC DD -> BB AA DD CC
        byte[] data = [0xAA, 0xBB, 0xCC, 0xDD];

        // Act
        BinaryUtils.SwapBytes16(data);

        // Assert
        Assert.Equal([0xBB, 0xAA, 0xDD, 0xCC], data);
    }

    #endregion

    #region SwapBytes32 Tests

    [Fact]
    public void SwapBytes32_SwapsCorrectly()
    {
        // Arrange - 12 34 56 78 -> 78 56 34 12
        byte[] data = [0x12, 0x34, 0x56, 0x78];

        // Act
        BinaryUtils.SwapBytes32(data);

        // Assert
        Assert.Equal([0x78, 0x56, 0x34, 0x12], data);
    }

    #endregion

    #region ReadUInt32LE Tests

    [Theory]
    [MemberData(nameof(ReadUInt32LEData))]
    public void ReadUInt32LE_ReadsCorrectly(byte[] data, int offset, uint expected)
    {
        Assert.Equal(expected, BinaryUtils.ReadUInt32LE(data, offset));
    }

    public static TheoryData<byte[], int, uint> ReadUInt32LEData =>
        new()
        {
            { new byte[] { 0x78, 0x56, 0x34, 0x12 }, 0, 0x12345678u },
            { new byte[] { 0x00, 0x00, 0x78, 0x56, 0x34, 0x12 }, 2, 0x12345678u },
            { new byte[] { 0x00, 0x00, 0x00, 0x00 }, 0, 0u },
            { new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, 0, uint.MaxValue }
        };

    #endregion

    #region ReadUInt32BE Tests

    [Fact]
    public void ReadUInt32BE_ReadsBigEndianCorrectly()
    {
        // Arrange - 0x12345678 in big-endian
        byte[] data = [0x12, 0x34, 0x56, 0x78];

        // Act
        var result = BinaryUtils.ReadUInt32BE(data);

        // Assert
        Assert.Equal(0x12345678u, result);
    }

    [Fact]
    public void ReadUInt32BE_WithOffset_ReadsCorrectly()
    {
        // Arrange
        byte[] data = [0x00, 0x00, 0x12, 0x34, 0x56, 0x78];

        // Act
        var result = BinaryUtils.ReadUInt32BE(data, 2);

        // Assert
        Assert.Equal(0x12345678u, result);
    }

    #endregion

    #region ReadUInt16LE/BE Tests

    [Fact]
    public void ReadUInt16LE_ReadsLittleEndianCorrectly()
    {
        // Arrange - 0x1234 in little-endian
        byte[] data = [0x34, 0x12];

        // Act
        var result = BinaryUtils.ReadUInt16LE(data);

        // Assert
        Assert.Equal((ushort)0x1234, result);
    }

    [Fact]
    public void ReadUInt16BE_ReadsBigEndianCorrectly()
    {
        // Arrange - 0x1234 in big-endian
        byte[] data = [0x12, 0x34];

        // Act
        var result = BinaryUtils.ReadUInt16BE(data);

        // Assert
        Assert.Equal((ushort)0x1234, result);
    }

    #endregion

    #region ReadUInt64LE/BE Tests

    [Fact]
    public void ReadUInt64LE_ReadsLittleEndianCorrectly()
    {
        // Arrange - 0x123456789ABCDEF0 in little-endian
        byte[] data = [0xF0, 0xDE, 0xBC, 0x9A, 0x78, 0x56, 0x34, 0x12];

        // Act
        var result = BinaryUtils.ReadUInt64LE(data);

        // Assert
        Assert.Equal(0x123456789ABCDEF0ul, result);
    }

    [Fact]
    public void ReadUInt64BE_ReadsBigEndianCorrectly()
    {
        // Arrange - 0x123456789ABCDEF0 in big-endian
        byte[] data = [0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0];

        // Act
        var result = BinaryUtils.ReadUInt64BE(data);

        // Assert
        Assert.Equal(0x123456789ABCDEF0ul, result);
    }

    #endregion

    #region IsPrintableText Tests

    [Fact]
    public void IsPrintableText_AllPrintable_ReturnsTrue()
    {
        // Arrange
        var data = "Hello, World!"u8.ToArray();

        // Act
        var result = BinaryUtils.IsPrintableText(data);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsPrintableText_WithTabs_ReturnsTrue()
    {
        // Arrange
        var data = "Hello\tWorld"u8.ToArray();

        // Act
        var result = BinaryUtils.IsPrintableText(data);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsPrintableText_WithNewlines_ReturnsTrue()
    {
        // Arrange
        var data = "Hello\r\nWorld"u8.ToArray();

        // Act
        var result = BinaryUtils.IsPrintableText(data);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsPrintableText_BinaryData_ReturnsFalse()
    {
        // Arrange - mostly non-printable bytes
        byte[] data = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05];

        // Act
        var result = BinaryUtils.IsPrintableText(data);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsPrintableText_EmptyData_ReturnsFalse()
    {
        // Arrange
        byte[] data = [];

        // Act
        var result = BinaryUtils.IsPrintableText(data);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region SanitizeFilename Tests

    [Fact]
    public void SanitizeFilename_ValidFilename_ReturnsUnchanged()
    {
        // Arrange
        const string filename = "valid_filename.txt";

        // Act
        var result = BinaryUtils.SanitizeFilename(filename);

        // Assert
        Assert.Equal("valid_filename.txt", result);
    }

    [Fact]
    public void SanitizeFilename_WithInvalidChars_ReplacesWithUnderscore()
    {
        // Arrange
        const string filename = "file<name>:test.txt";

        // Act
        var result = BinaryUtils.SanitizeFilename(filename);

        // Assert
        Assert.DoesNotContain("<", result);
        Assert.DoesNotContain(">", result);
        Assert.DoesNotContain(":", result);
    }

    [Fact]
    public void SanitizeFilename_NullInput_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => BinaryUtils.SanitizeFilename(null!));
    }

    #endregion

    #region FindPattern Tests

    [Theory]
    [MemberData(nameof(FindPatternData))]
    public void FindPattern_VariousInputs(byte[] data, byte[] pattern, int startOffset, int expected)
    {
        Assert.Equal(expected, BinaryUtils.FindPattern(data, pattern, startOffset));
    }

    public static TheoryData<byte[], byte[], int, int> FindPatternData =>
        new()
        {
            { new byte[] { 0x00, 0x00, 0x41, 0x42, 0x43, 0x00 }, new byte[] { 0x41, 0x42, 0x43 }, 0, 2 },
            { new byte[] { 0x00, 0x00, 0x41, 0x42, 0x43, 0x00 }, new byte[] { 0x44, 0x45, 0x46 }, 0, -1 },
            { new byte[] { 0x41, 0x42, 0x00, 0x41, 0x42, 0x00 }, new byte[] { 0x41, 0x42 }, 2, 3 },
            { new byte[] { 0x00, 0x00, 0x00 }, Array.Empty<byte>(), 0, -1 },
            { new byte[] { 0x00, 0x00, 0xFF, 0x00 }, new byte[] { 0xFF }, 0, 2 },
            { new byte[] { 0x00, 0x00, 0x00, 0x41, 0x42, 0x43 }, new byte[] { 0x41, 0x42, 0x43 }, 0, 3 },
            { new byte[] { 0x41, 0x42 }, new byte[] { 0x41, 0x42, 0x43, 0x44 }, 0, -1 },
            { new byte[] { 0x41, 0x42, 0x43, 0x00, 0x00 }, new byte[] { 0x41, 0x42, 0x43 }, 2, -1 }
        };

    #endregion

    #region ExtractNullTerminatedString Tests

    [Theory]
    [MemberData(nameof(ExtractNullTermStringData))]
    public void ExtractNullTerminatedString_VariousInputs(byte[] data, int offset, int maxLength, string? expected)
    {
        Assert.Equal(expected, BinaryUtils.ExtractNullTerminatedString(data, offset, maxLength));
    }

    public static TheoryData<byte[], int, int, string?> ExtractNullTermStringData =>
        new()
        {
            // Valid string
            { new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x00, 0x00 }, 0, 256, "Hello" },
            // With offset
            { new byte[] { 0x00, 0x00, 0x48, 0x69, 0x00 }, 2, 256, "Hi" },
            // No null terminator within maxLength
            { new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F }, 0, 5, null },
            // Offset beyond data
            { new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x00 }, 100, 256, null },
            // Non-printable content
            { new byte[] { 0x00, 0x01, 0x02, 0x03, 0x00 }, 0, 256, null },
            // Empty string (immediate null terminator)
            { new byte[] { 0x00, 0x41, 0x42, 0x43 }, 0, 256, null },
            // File path
            { "textures/architecture/wall.dds\0"u8.ToArray(), 0, 256, "textures/architecture/wall.dds" }
        };

    #endregion
}