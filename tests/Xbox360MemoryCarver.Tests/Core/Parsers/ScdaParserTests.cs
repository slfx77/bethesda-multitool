using System.Text;
using Xbox360MemoryCarver.Core.Formats.Scda;
using Xunit;

namespace Xbox360MemoryCarver.Tests.Core.Parsers;

public class ScdaParserTests
{
    private readonly ScdaFormat _parser = new();

    [Fact]
    public void ParseHeader_ValidScdaRecord_ReturnsParseResult()
    {
        // Arrange - SCDA with valid bytecode starting with control flow opcode (0x10-0x1F)
        var data = new byte[]
        {
            (byte)'S', (byte)'C', (byte)'D', (byte)'A', // Magic
            0x08, 0x00, // Length = 8 (little endian)
            0x10, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 // Bytecode starting with 0x10
        };

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("SCDA", result.Format);
        Assert.Equal(14, result.EstimatedSize); // 6 header + 8 bytecode
        Assert.True(result.Metadata.ContainsKey("bytecodeSize"));
        Assert.Equal(8, result.Metadata["bytecodeSize"]);
    }

    [Fact]
    public void ParseHeader_ValidScdaWithFunctionOpcode_ReturnsParseResult()
    {
        // Arrange - SCDA with bytecode starting with function opcode (0x100-0x1FFF range)
        var data = new byte[]
        {
            (byte)'S', (byte)'C', (byte)'D', (byte)'A', // Magic
            0x06, 0x00, // Length = 6
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05 // Bytecode: word 0x0100 is in valid range
        };

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("SCDA", result.Format);
    }


    [Fact]
    public void ParseHeader_InvalidMagic_ReturnsNull()
    {
        // Arrange
        var data = new byte[]
        {
            (byte)'S', (byte)'C', (byte)'D', (byte)'X', // Wrong magic
            0x04, 0x00,
            0x10, 0x00, 0x00, 0x00
        };

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseHeader_ZeroLength_ReturnsNull()
    {
        // Arrange
        var data = new byte[]
        {
            (byte)'S', (byte)'C', (byte)'D', (byte)'A',
            0x00, 0x00, // Length = 0
            0x10, 0x00, 0x00, 0x00
        };

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseHeader_LengthExceedsData_ReturnsNull()
    {
        // Arrange - Length claims 100 bytes but only 4 available
        var data = new byte[]
        {
            (byte)'S', (byte)'C', (byte)'D', (byte)'A',
            0x64, 0x00, // Length = 100
            0x10, 0x00, 0x00, 0x00
        };

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseHeader_InvalidBytecode_ReturnsNull()
    {
        // Arrange - Bytecode that doesn't start with valid opcode
        var data = new byte[]
        {
            (byte)'S', (byte)'C', (byte)'D', (byte)'A',
            0x04, 0x00, // Length = 4
            0x00, 0x00, 0x00, 0x00 // Invalid: starts with 0x00, word is 0x0000
        };

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseHeader_TooShortBytecode_ReturnsNull()
    {
        // Arrange - Bytecode less than 4 bytes
        var data = new byte[]
        {
            (byte)'S', (byte)'C', (byte)'D', (byte)'A',
            0x02, 0x00, // Length = 2
            0x10, 0x00
        };

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseHeader_DataTooShort_ReturnsNull()
    {
        // Arrange - Less than 10 bytes
        var data = new byte[] { (byte)'S', (byte)'C', (byte)'D', (byte)'A', 0x04, 0x00 };

        // Act
        var result = _parser.Parse(data);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ParseHeader_WithOffset_ParsesCorrectly()
    {
        // Arrange
        var data = new byte[]
        {
            0x00, 0x00, 0x00, 0x00, // Padding
            (byte)'S', (byte)'C', (byte)'D', (byte)'A',
            0x06, 0x00,
            0x10, 0x00, 0x01, 0x02, 0x03, 0x04
        };

        // Act
        var result = _parser.Parse(data, 4);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("SCDA", result.Format);
    }

    [Fact]
    public void ScanForRecords_EmptyData_ReturnsEmptyList()
    {
        // Arrange
        var data = Array.Empty<byte>();

        // Act
        var records = ScdaFormat.ScanForRecords(data);

        // Assert
        Assert.Empty(records.Records);
    }

    [Fact]
    public void ScanForRecords_NoScdaRecords_ReturnsEmptyList()
    {
        // Arrange
        var data = new byte[100];
        Array.Fill(data, (byte)0xFF);

        // Act
        var records = ScdaFormat.ScanForRecords(data);

        // Assert
        Assert.Empty(records.Records);
    }

    [Fact]
    public void ScanForRecords_SingleValidRecord_ReturnsOneRecord()
    {
        // Arrange
        var data = new byte[]
        {
            (byte)'S', (byte)'C', (byte)'D', (byte)'A',
            0x06, 0x00, // Length = 6
            0x10, 0x01, 0x02, 0x03, 0x04, 0x05, // Bytecode
            0x00, 0x00, 0x00, 0x00 // Padding
        };

        // Act
        var records = ScdaFormat.ScanForRecords(data);

        // Assert
        Assert.Single(records.Records);
        Assert.Equal(0, records.Records[0].Offset);
        Assert.Equal(6, records.Records[0].BytecodeLength);
        Assert.False(records.Records[0].HasAssociatedSctx);
    }

    [Fact]
    public void ScanForRecords_WithAssociatedSctx_ParsesSourceText()
    {
        // Arrange - SCDA followed by SCTX within 200 bytes
        var sourceText = "SetStage MyQuest 10";
        var sourceBytes = Encoding.ASCII.GetBytes(sourceText);

        var data = new byte[100];
        var offset = 0;

        // SCDA record
        data[offset++] = (byte)'S';
        data[offset++] = (byte)'C';
        data[offset++] = (byte)'D';
        data[offset++] = (byte)'A';
        data[offset++] = 0x06; // Length low
        data[offset++] = 0x00; // Length high
        data[offset++] = 0x10; // Bytecode start
        data[offset++] = 0x01;
        data[offset++] = 0x02;
        data[offset++] = 0x03;
        data[offset++] = 0x04;
        data[offset++] = 0x05;

        // SCTX record nearby
        data[offset++] = (byte)'S';
        data[offset++] = (byte)'C';
        data[offset++] = (byte)'T';
        data[offset++] = (byte)'X';
        data[offset++] = (byte)sourceBytes.Length; // Length low
        data[offset++] = 0x00; // Length high
        Array.Copy(sourceBytes, 0, data, offset, sourceBytes.Length);

        // Act
        var records = ScdaFormat.ScanForRecords(data);

        // Assert
        Assert.Single(records.Records);
        Assert.True(records.Records[0].HasAssociatedSctx);
        Assert.Equal(sourceText, records.Records[0].SourceText);
    }

    [Fact]
    public void ScanForRecords_WithScroReferences_ParsesFormIds()
    {
        // Arrange - SCDA followed by SCRO records
        var data = new byte[100];
        var offset = 0;

        // SCDA record
        data[offset++] = (byte)'S';
        data[offset++] = (byte)'C';
        data[offset++] = (byte)'D';
        data[offset++] = (byte)'A';
        data[offset++] = 0x06;
        data[offset++] = 0x00;
        data[offset++] = 0x10;
        data[offset++] = 0x01;
        data[offset++] = 0x02;
        data[offset++] = 0x03;
        data[offset++] = 0x04;
        data[offset++] = 0x05;

        // SCRO record with FormID 0x0012AB34
        data[offset++] = (byte)'S';
        data[offset++] = (byte)'C';
        data[offset++] = (byte)'R';
        data[offset++] = (byte)'O';
        data[offset++] = 0x04; // Length = 4
        data[offset++] = 0x00;
        data[offset++] = 0x34; // FormID little-endian
        data[offset++] = 0xAB;
        data[offset++] = 0x12;
        data[offset] = 0x00;

        // Act
        var records = ScdaFormat.ScanForRecords(data);

        // Assert
        Assert.Single(records.Records);
        Assert.Single(records.Records[0].FormIdReferences);
        Assert.Equal(0x0012AB34u, records.Records[0].FormIdReferences[0]);
    }

    [Fact]
    public void ScanForRecords_MultipleRecords_ReturnsAll()
    {
        // Arrange - Two SCDA records
        var data = new byte[50];
        var offset = 0;

        // First SCDA
        data[offset++] = (byte)'S';
        data[offset++] = (byte)'C';
        data[offset++] = (byte)'D';
        data[offset++] = (byte)'A';
        data[offset++] = 0x04;
        data[offset++] = 0x00;
        data[offset++] = 0x10;
        data[offset++] = 0x01;
        data[offset++] = 0x02;
        data[offset++] = 0x03;

        // Padding
        offset += 5;

        // Second SCDA
        data[offset++] = (byte)'S';
        data[offset++] = (byte)'C';
        data[offset++] = (byte)'D';
        data[offset++] = (byte)'A';
        data[offset++] = 0x04;
        data[offset++] = 0x00;
        data[offset++] = 0x15;
        data[offset++] = 0x01;
        data[offset++] = 0x02;
        data[offset] = 0x03;

        // Act
        var records = ScdaFormat.ScanForRecords(data);

        // Assert
        Assert.Equal(2, records.Records.Count);
        Assert.Equal(0, records.Records[0].Offset);
        Assert.Equal(15, records.Records[1].Offset);
    }

    [Fact]
    public void ScdaRecord_BytecodeSize_MatchesBytecodeLength()
    {
        // Arrange
        var record = new ScdaRecord
        {
            Offset = 0,
            Bytecode = new byte[] { 0x10, 0x01, 0x02, 0x03, 0x04 }
        };

        // Assert
        Assert.Equal(5, record.BytecodeSize);
        Assert.Equal(5, record.BytecodeLength);
        Assert.Equal(record.BytecodeSize, record.BytecodeLength);
    }
}