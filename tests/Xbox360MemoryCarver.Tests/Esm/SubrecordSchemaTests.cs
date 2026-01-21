using EsmAnalyzer.Conversion.Schema;
using Xunit;

namespace Xbox360MemoryCarver.Tests.Esm;

/// <summary>
///     Tests for the SubrecordSchemaRegistry and SubrecordSchemaProcessor.
///     These tests validate the new schema-based conversion system.
/// </summary>
public sealed class SubrecordSchemaTests
{
    #region Schema Matching Tests

    [Fact]
    public void FindSchema_EDID_ReturnsStringSchema()
    {
        var schema = SubrecordSchemaRegistry.FindSchema("EDID", "NPC_", 20);

        Assert.NotNull(schema);
        Assert.True(schema.IsString);
    }

    [Fact]
    public void FindSchema_NAME_ReturnsFormIdSchema()
    {
        var schema = SubrecordSchemaRegistry.FindSchema("NAME", "REFR", 4);

        Assert.NotNull(schema);
        Assert.False(schema.IsString);
        Assert.Single(schema.Fields);
        Assert.Equal(SubrecordFieldType.UInt32, schema.Fields[0].Type);
    }

    [Fact]
    public void FindSchema_OBND_Returns12ByteSchema()
    {
        var schema = SubrecordSchemaRegistry.FindSchema("OBND", "WEAP", 12);

        Assert.NotNull(schema);
        Assert.Equal(6, schema.Fields.Length); // 6 x uint16
    }

    [Fact]
    public void FindSchema_RNAM_INFO_ReturnsStringSchema()
    {
        // RNAM in INFO is a string
        var schema = SubrecordSchemaRegistry.FindSchema("RNAM", "INFO", 20);

        Assert.NotNull(schema);
        Assert.True(schema.IsString);
    }

    [Fact]
    public void FindSchema_RNAM_NPC_ReturnsFormIdSchema()
    {
        // RNAM in NPC_ is a FormID
        var schema = SubrecordSchemaRegistry.FindSchema("RNAM", "NPC_", 4);

        Assert.NotNull(schema);
        Assert.False(schema.IsString);
    }

    [Fact]
    public void FindSchema_MNAM_FACT_ReturnsStringSchema()
    {
        var schema = SubrecordSchemaRegistry.FindSchema("MNAM", "FACT", 10);

        Assert.NotNull(schema);
        Assert.True(schema.IsString);
    }

    [Fact]
    public void FindSchema_MODT_ReturnsUInt64ArraySchema()
    {
        var schema = SubrecordSchemaRegistry.FindSchema("MODT", "WEAP", 16);

        Assert.NotNull(schema);
        Assert.Single(schema.Fields);
        Assert.Equal(SubrecordFieldType.UInt64Array, schema.Fields[0].Type);
    }

    [Fact]
    public void FindSchema_DataLengthConstraint_MatchesCorrectSchema()
    {
        // SCHR with 4 bytes should be FormID
        var schema4 = SubrecordSchemaRegistry.FindSchema("SCHR", "QUST", 4);
        Assert.NotNull(schema4);
        Assert.Equal(4, schema4.DataLength);

        // SCHR with 20 bytes should be full header
        var schema20 = SubrecordSchemaRegistry.FindSchema("SCHR", "SCPT", 20);
        Assert.NotNull(schema20);
        Assert.Equal(20, schema20.DataLength);
    }

    #endregion

    #region Schema Processor Tests

    [Fact]
    public void TryConvert_StringSubrecord_NoModification()
    {
        var data = "TestString\0"u8.ToArray();
        var original = data.ToArray();

        var handled = SubrecordSchemaProcessor.TryConvert("EDID", "NPC_", data);

        Assert.True(handled);
        Assert.Equal(original, data);
    }

    [Fact]
    public void TryConvert_NAME_SwapsFormId()
    {
        var data = new byte[] { 0x00, 0x01, 0x23, 0x45 };

        var handled = SubrecordSchemaProcessor.TryConvert("NAME", "REFR", data);

        Assert.True(handled);
        Assert.Equal(new byte[] { 0x45, 0x23, 0x01, 0x00 }, data);
    }

    [Fact]
    public void TryConvert_OBND_SwapsAllInt16s()
    {
        var data = new byte[]
        {
            0xFF, 0xF6, // -10
            0xFF, 0xEC, // -20
            0xFF, 0xE2, // -30
            0x00, 0x0A, // +10
            0x00, 0x14, // +20
            0x00, 0x1E  // +30
        };

        var handled = SubrecordSchemaProcessor.TryConvert("OBND", "WEAP", data);

        Assert.True(handled);
        Assert.Equal(-10, BitConverter.ToInt16(data, 0));
        Assert.Equal(-20, BitConverter.ToInt16(data, 2));
        Assert.Equal(-30, BitConverter.ToInt16(data, 4));
        Assert.Equal(10, BitConverter.ToInt16(data, 6));
        Assert.Equal(20, BitConverter.ToInt16(data, 8));
        Assert.Equal(30, BitConverter.ToInt16(data, 10));
    }

    [Fact]
    public void TryConvert_MODT_SwapsAll8ByteHashes()
    {
        var data = new byte[]
        {
            0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
            0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18
        };

        var handled = SubrecordSchemaProcessor.TryConvert("MODT", "WEAP", data);

        Assert.True(handled);
        Assert.Equal(new byte[] { 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01 }, data[..8]);
        Assert.Equal(new byte[] { 0x18, 0x17, 0x16, 0x15, 0x14, 0x13, 0x12, 0x11 }, data[8..16]);
    }

    [Fact]
    public void TryConvert_CNTO_SwapsFormIdAndCount()
    {
        var data = new byte[]
        {
            0x00, 0x01, 0x23, 0x45, // FormID (BE: 0x00012345)
            0x00, 0x00, 0x00, 0x05  // Count (BE: 5)
        };

        var handled = SubrecordSchemaProcessor.TryConvert("CNTO", "CONT", data);

        Assert.True(handled);
        // After swap to LE, BitConverter reads the correct values
        Assert.Equal(0x00012345u, BitConverter.ToUInt32(data, 0));
        Assert.Equal(5u, BitConverter.ToUInt32(data, 4));
    }

    [Fact]
    public void TryConvert_Unknown_ReturnsFalse()
    {
        var data = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        var handled = SubrecordSchemaProcessor.TryConvert("ZZZZ", "XXXX", data);

        Assert.False(handled);
    }

    [Fact]
    public void TryConvert_CustomHandler_InvokesDelegate()
    {
        var data = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var handlerCalled = false;
        string? calledHandler = null;
        string? calledRecordType = null;

        bool CustomHandler(string handler, string recordType, byte[] d)
        {
            handlerCalled = true;
            calledHandler = handler;
            calledRecordType = recordType;
            return true;
        }

        // DATA requires custom handler
        var handled = SubrecordSchemaProcessor.TryConvert("DATA", "NPC_", data, CustomHandler);

        Assert.True(handled);
        Assert.True(handlerCalled);
        Assert.Equal("ConvertDataSubrecord", calledHandler);
        Assert.Equal("NPC_", calledRecordType);
    }

    #endregion

    #region IsStringSubrecord Tests

    [Theory]
    [InlineData("EDID", "NPC_", true)]
    [InlineData("FULL", "WEAP", true)]
    [InlineData("ICON", "ALCH", true)]
    [InlineData("MODL", "ARMO", true)]
    [InlineData("SCTX", "SCPT", true)]
    [InlineData("RNAM", "INFO", true)]
    [InlineData("RNAM", "CHAL", true)]
    [InlineData("MNAM", "FACT", true)]
    [InlineData("FNAM", "FACT", true)]
    [InlineData("NAME", "REFR", false)]
    [InlineData("RNAM", "NPC_", false)]
    [InlineData("MNAM", "NPC_", false)]
    public void IsStringSubrecord_ReturnsCorrectValue(string signature, string recordType, bool expected)
    {
        var result = SubrecordSchemaProcessor.IsStringSubrecord(signature, recordType);
        Assert.Equal(expected, result);
    }

    #endregion
}
