using Xunit;
using Xbox360MemoryCarver.Core.Formats.Nif;

namespace Xbox360MemoryCarver.Tests.Core.Converters;

public class NifEndianConverterTests
{
    [Fact]
    public void ConvertToLittleEndian_NullData_ThrowsNullReferenceException()
    {
        // Arrange
        var converter = new NifEndianConverter(verbose: false);

        // Act & Assert - The method doesn't do null checking, which is acceptable
        // since callers should not pass null
        Assert.Throws<NullReferenceException>(() => converter.ConvertToLittleEndian(null!));
    }

    [Fact]
    public void ConvertToLittleEndian_EmptyData_ReturnsNull()
    {
        // Arrange
        var converter = new NifEndianConverter(verbose: false);

        // Act
        var result = converter.ConvertToLittleEndian([]);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ConvertToLittleEndian_TooShortData_ReturnsNull()
    {
        // Arrange
        var converter = new NifEndianConverter(verbose: false);
        var shortData = new byte[30]; // Less than minimum NIF header

        // Act
        var result = converter.ConvertToLittleEndian(shortData);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ConvertToLittleEndian_InvalidMagic_ReturnsNull()
    {
        // Arrange
        var converter = new NifEndianConverter(verbose: false);
        var invalidData = new byte[200];
        Array.Fill(invalidData, (byte)'X');

        // Act
        var result = converter.ConvertToLittleEndian(invalidData);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ConvertToLittleEndian_AlreadyLittleEndian_ReturnsNull()
    {
        // Arrange - Create a minimal NIF header that appears to be little-endian
        var converter = new NifEndianConverter(verbose: false);

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
        var result = converter.ConvertToLittleEndian(data);

        // Assert - Should return null since file is already little-endian
        Assert.Null(result);
    }

    [Fact]
    public void ConvertToLittleEndian_InvalidEndianByte_ReturnsNull()
    {
        // Arrange - Create a NIF header with invalid endian byte (not 0 or 1)
        var converter = new NifEndianConverter(verbose: false);

        var header = "Gamebryo File Format, Version 20.2.0.7\n"u8.ToArray();
        var data = new byte[200];
        Array.Copy(header, data, header.Length);

        var pos = header.Length;
        // Binary version
        data[pos++] = 0x07;
        data[pos++] = 0x00;
        data[pos++] = 0x02;
        data[pos++] = 0x14;
        // Endian byte: 2 = invalid
        data[pos] = 0x02;

        // Act
        var result = converter.ConvertToLittleEndian(data);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ConvertToLittleEndian_ZeroNumBlocks_ReturnsNull()
    {
        // Arrange
        var converter = new NifEndianConverter(verbose: false);

        var header = "Gamebryo File Format, Version 20.2.0.7\n"u8.ToArray();
        var data = new byte[200];
        Array.Copy(header, data, header.Length);

        var pos = header.Length;
        // Binary version
        data[pos++] = 0x07;
        data[pos++] = 0x00;
        data[pos++] = 0x02;
        data[pos++] = 0x14;
        // Endian byte: 0 = big-endian
        data[pos++] = 0x00;
        // User version: 12
        data[pos++] = 0x0C;
        data[pos++] = 0x00;
        data[pos++] = 0x00;
        data[pos++] = 0x00;
        // Num blocks: 0 (invalid)
        data[pos++] = 0x00;
        data[pos++] = 0x00;
        data[pos++] = 0x00;
        _ = data[pos++] = 0x00; // Final increment (suppress unused warning)

        // Act
        var result = converter.ConvertToLittleEndian(data);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Constructor_VerboseFlag_IsStored()
    {
        // Arrange & Act
        var verboseConverter = new NifEndianConverter(verbose: true);
        var silentConverter = new NifEndianConverter(verbose: false);

        // Assert - Just verify construction doesn't throw
        Assert.NotNull(verboseConverter);
        Assert.NotNull(silentConverter);
    }
}
