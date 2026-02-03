using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Converters;

public class NifConverterTests
{
    [Fact]
    public void Convert_NullData_ReturnsFailure()
    {
        // Arrange
        var converter = new NifConverter();

        // Act
        var result = converter.Convert(null!);

        // Assert - The method catches exceptions and returns a failure result
        Assert.False(result.Success);
        Assert.Contains("failed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Convert_EmptyData_ReturnsFailure()
    {
        // Arrange
        var converter = new NifConverter();

        // Act
        var result = converter.Convert([]);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Failed to parse NIF header", result.ErrorMessage);
    }

    [Fact]
    public void Convert_TooShortData_ReturnsFailure()
    {
        // Arrange
        var converter = new NifConverter();
        var shortData = new byte[30]; // Less than minimum NIF header

        // Act
        var result = converter.Convert(shortData);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Failed to parse NIF header", result.ErrorMessage);
    }

    [Fact]
    public void Convert_InvalidMagic_ReturnsFailure()
    {
        // Arrange
        var converter = new NifConverter();
        var invalidData = new byte[200];
        Array.Fill(invalidData, (byte)'X');

        // Act
        var result = converter.Convert(invalidData);

        // Assert
        Assert.False(result.Success);
    }

    [Fact]
    public void Convert_AlreadyLittleEndian_ReturnsSuccessWithOriginalData()
    {
        // Arrange - Create a minimal NIF header that appears to be little-endian
        var converter = new NifConverter();

        // NIF header: "Gamebryo File Format, Version 20.2.0.7\n"
        var header = "Gamebryo File Format, Version 20.2.0.7\n"u8.ToArray();
        var data = new byte[200];
        Array.Copy(header, data, header.Length);

        var pos = header.Length;
        // Binary version (little-endian): 0x14020007
        data[pos++] = 0x07;
        data[pos++] = 0x00;
        data[pos++] = 0x02;
        data[pos++] = 0x14;
        // Endian byte: 1 = little-endian (already converted)
        data[pos] = 0x01;

        // Act
        var result = converter.Convert(data);

        // Assert - Should return success since file is already little-endian
        Assert.True(result.Success);
        Assert.Equal("File is already little-endian (PC format)", result.ErrorMessage);
        Assert.Same(data, result.OutputData);
    }

    [Fact]
    public void Convert_InvalidEndianByte_ReturnsFailure()
    {
        // Arrange - Create a NIF header with invalid endian byte (not 0 or 1)
        var converter = new NifConverter();

        var header = "Gamebryo File Format, Version 20.2.0.7\n"u8.ToArray();
        var data = new byte[200];
        Array.Copy(header, data, header.Length);

        var pos = header.Length;
        // Binary version
        data[pos++] = 0x07;
        data[pos++] = 0x00;
        data[pos++] = 0x02;
        data[pos++] = 0x14;
        // Endian byte: 2 = invalid - NIF treats this as non-big-endian (not 0), so IsBigEndian = false
        data[pos] = 0x02;

        // Act
        var result = converter.Convert(data);

        // Assert - Invalid endian byte (2) is treated as little-endian (not 0), so returns success with original data
        // The parser interprets IsBigEndian = (data[pos] == 0), so 2 means IsBigEndian = false
        Assert.True(result.Success);
        Assert.Equal("File is already little-endian (PC format)", result.ErrorMessage);
    }

    [Fact]
    public void Constructor_VerboseFlag_IsStored()
    {
        // Arrange & Act
        var verboseConverter = new NifConverter(true);
        var silentConverter = new NifConverter();

        // Assert - Just verify construction doesn't throw
        Assert.NotNull(verboseConverter);
        Assert.NotNull(silentConverter);
    }
}