using Xbox360MemoryCarver.Core.Formats;
using Xbox360MemoryCarver.Core.Formats.Nif;
using Xunit;

namespace Xbox360MemoryCarver.Tests.Core.Formats;

public class NifFormatTests
{
    private readonly NifFormat _format = new();

    [Fact]
    public void FormatId_ReturnsNif()
    {
        Assert.Equal("nif", _format.FormatId);
    }

    [Fact]
    public void DisplayName_ReturnsNIF()
    {
        Assert.Equal("NIF", _format.DisplayName);
    }

    [Fact]
    public void Extension_ReturnsDotNif()
    {
        Assert.Equal(".nif", _format.Extension);
    }

    [Fact]
    public void Category_ReturnsModel()
    {
        Assert.Equal(FileCategory.Model, _format.Category);
    }

    [Fact]
    public void Signatures_ContainsGamebryoMagic()
    {
        Assert.Single(_format.Signatures);
        Assert.Equal("nif", _format.Signatures[0].Id);
        Assert.Equal("Gamebryo File Format"u8.ToArray(), _format.Signatures[0].MagicBytes);
    }

    [Fact]
    public void Parse_InvalidMagic_ReturnsNull()
    {
        // Arrange
        var data = new byte[100];
        Array.Fill(data, (byte)'X');

        // Act
        var result = _format.Parse(data);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Parse_TooShortData_ReturnsNull()
    {
        // Arrange
        var data = new byte[30];

        // Act
        var result = _format.Parse(data);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Parse_ValidHeader_ReturnsParseResult()
    {
        // Arrange - Create a minimal valid NIF header
        var header = "Gamebryo File Format, Version 20.2.0.7\n"u8.ToArray();
        var data = new byte[200];
        Array.Copy(header, data, header.Length);

        var pos = header.Length;
        // Binary version: 0x14020007 (little-endian)
        data[pos++] = 0x07;
        data[pos++] = 0x00;
        data[pos++] = 0x02;
        data[pos++] = 0x14;
        // Endian byte: 1 = little-endian
        data[pos++] = 0x01;
        // User version: 12
        data[pos++] = 0x0C;
        data[pos++] = 0x00;
        data[pos++] = 0x00;
        data[pos++] = 0x00;
        // Num blocks: 1
        data[pos++] = 0x01;
        data[pos++] = 0x00;
        data[pos++] = 0x00;
        data[pos] = 0x00; // Final byte - no increment needed

        // Act
        var result = _format.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("NIF", result.Format);
        Assert.True(result.Metadata.ContainsKey("version"));
        Assert.Equal("20.2.0.7", result.Metadata["version"]);
    }

    [Fact]
    public void Parse_InvalidVersionFormat_ReturnsNull()
    {
        // Arrange - Header with invalid version string
        var header = "Gamebryo File Format, Version invalid\n"u8.ToArray();
        var data = new byte[100];
        Array.Copy(header, data, header.Length);

        // Act
        var result = _format.Parse(data);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Parse_MissingVersionPrefix_ReturnsNull()
    {
        // Arrange - Header without ", Version "
        var header = "Gamebryo File Format 20.2.0.7\n"u8.ToArray();
        var data = new byte[100];
        Array.Copy(header, data, header.Length);

        // Act
        var result = _format.Parse(data);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void CanConvert_BigEndianNif_ReturnsTrue()
    {
        // Arrange
        var metadata = new Dictionary<string, object> { ["bigEndian"] = true };

        // Act
        var canConvert = _format.CanConvert("nif", metadata);

        // Assert
        Assert.True(canConvert);
    }

    [Fact]
    public void CanConvert_LittleEndianNif_ReturnsFalse()
    {
        // Arrange
        var metadata = new Dictionary<string, object> { ["bigEndian"] = false };

        // Act
        var canConvert = _format.CanConvert("nif", metadata);

        // Assert
        Assert.False(canConvert);
    }

    [Fact]
    public void CanConvert_NoMetadata_ReturnsFalse()
    {
        // Act
        var canConvert = _format.CanConvert("nif", null);

        // Assert
        Assert.False(canConvert);
    }

    [Fact]
    public void TargetExtension_ReturnsDotNif()
    {
        Assert.Equal(".nif", _format.TargetExtension);
    }

    [Fact]
    public void TargetFolder_ReturnsModelsConverted()
    {
        Assert.Equal("models_converted", _format.TargetFolder);
    }

    [Fact]
    public void IsInitialized_ReturnsTrue()
    {
        Assert.True(_format.IsInitialized);
    }

    [Fact]
    public void Initialize_ReturnsTrue()
    {
        // Act
        var result = _format.Initialize();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task ConvertAsync_InvalidData_ReturnsFailure()
    {
        // Arrange
        var invalidData = new byte[] { 0x00, 0x01, 0x02, 0x03 };

        // Act
        var result = await _format.ConvertAsync(invalidData);

        // Assert
        Assert.False(result.Success);
        Assert.Null(result.OutputData);
    }
}