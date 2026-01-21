using EsmAnalyzer.Conversion;
using Xunit;
using static EsmAnalyzer.Conversion.EsmEndianHelpers;

namespace Xbox360MemoryCarver.Tests.Esm;

/// <summary>
///     Tests for EsmEndianHelpers byte swapping operations.
///     These are the foundational operations that all ESM conversion relies on.
/// </summary>
public sealed class EsmEndianHelpersTests
{
    #region Swap2Bytes Tests

    [Fact]
    public void Swap2Bytes_SwapsBytes()
    {
        // Arrange: 0x0102 in big-endian
        var data = new byte[] { 0x01, 0x02 };

        // Act
        Swap2Bytes(data, 0);

        // Assert: Should be 0x0201 (little-endian)
        Assert.Equal(0x02, data[0]);
        Assert.Equal(0x01, data[1]);
    }

    [Fact]
    public void Swap2Bytes_AtOffset_OnlySwapsTargetBytes()
    {
        // Arrange: Padding + 0x0102 + Padding
        var data = new byte[] { 0xFF, 0xFF, 0x01, 0x02, 0xFF, 0xFF };

        // Act
        Swap2Bytes(data, 2);

        // Assert: Only bytes at offset 2-3 swapped
        Assert.Equal(0xFF, data[0]);
        Assert.Equal(0xFF, data[1]);
        Assert.Equal(0x02, data[2]);
        Assert.Equal(0x01, data[3]);
        Assert.Equal(0xFF, data[4]);
        Assert.Equal(0xFF, data[5]);
    }

    [Fact]
    public void Swap2Bytes_Idempotent_DoubleSwapRestoresOriginal()
    {
        var original = new byte[] { 0x01, 0x02 };
        var data = new byte[] { 0x01, 0x02 };

        Swap2Bytes(data, 0);
        Swap2Bytes(data, 0);

        Assert.Equal(original, data);
    }

    #endregion

    #region Swap4Bytes Tests

    [Fact]
    public void Swap4Bytes_SwapsBytes()
    {
        // Arrange: 0x01020304 in big-endian
        var data = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        // Act
        Swap4Bytes(data, 0);

        // Assert: Should be 0x04030201 (little-endian)
        Assert.Equal(0x04, data[0]);
        Assert.Equal(0x03, data[1]);
        Assert.Equal(0x02, data[2]);
        Assert.Equal(0x01, data[3]);
    }

    [Fact]
    public void Swap4Bytes_AtOffset_OnlySwapsTargetBytes()
    {
        // Arrange
        var data = new byte[] { 0xFF, 0xFF, 0x01, 0x02, 0x03, 0x04, 0xFF, 0xFF };

        // Act
        Swap4Bytes(data, 2);

        // Assert
        Assert.Equal(0xFF, data[0]);
        Assert.Equal(0xFF, data[1]);
        Assert.Equal(0x04, data[2]);
        Assert.Equal(0x03, data[3]);
        Assert.Equal(0x02, data[4]);
        Assert.Equal(0x01, data[5]);
        Assert.Equal(0xFF, data[6]);
        Assert.Equal(0xFF, data[7]);
    }

    [Theory]
    [InlineData(0x00000000u)]
    [InlineData(0x12345678u)]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0x00010203u)]
    public void Swap4Bytes_ConvertsKnownValues(uint bigEndianValue)
    {
        // Arrange: Big-endian bytes
        var data = BitConverter.GetBytes(bigEndianValue);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(data);

        // Act
        Swap4Bytes(data, 0);

        // Assert: Now little-endian, should read as the original value
        Assert.Equal(bigEndianValue, BitConverter.ToUInt32(data, 0));
    }

    #endregion

    #region Swap8Bytes Tests

    [Fact]
    public void Swap8Bytes_SwapsBytes()
    {
        // Arrange: 8-byte value in big-endian
        var data = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };

        // Act
        Swap8Bytes(data, 0);

        // Assert: Reversed order
        Assert.Equal(new byte[] { 0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01 }, data);
    }

    #endregion

    #region IsValidSubrecordSignature Tests

    [Theory]
    [InlineData("EDID", true)]
    [InlineData("NAME", true)]
    [InlineData("DATA", true)]
    [InlineData("OBND", true)]
    [InlineData("", false)]
    [InlineData("ED", false)]
    [InlineData("ABCDE", false)]
    [InlineData("abc", false)]
    public void IsValidSubrecordSignature_ValidatesCorrectly(string signature, bool expected)
    {
        var result = IsValidSubrecordSignature(signature);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsValidSubrecordSignature_AcceptsIadSpecialCases()
    {
        // IAD signatures can have binary prefix bytes
        Assert.True(IsValidSubrecordSignature("\x00IAD"));
        Assert.True(IsValidSubrecordSignature("\x40IAD"));
        Assert.True(IsValidSubrecordSignature("\x01IAD"));
    }

    #endregion

    #region IsStringSubrecord Tests

    [Theory]
    [InlineData("EDID", "NPC_", true)]
    [InlineData("EDID", "WEAP", true)]
    [InlineData("FULL", "NPC_", true)]
    [InlineData("DESC", "BOOK", true)]
    [InlineData("ICON", "ALCH", true)]
    [InlineData("MODL", "WEAP", true)]
    [InlineData("SCTX", "SCPT", true)]
    public void IsStringSubrecord_ReturnsTrue_ForKnownStrings(string signature, string recordType, bool expected)
    {
        var result = IsStringSubrecord(signature, recordType);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("NAME", "NPC_")]
    [InlineData("DATA", "NPC_")]
    [InlineData("OBND", "WEAP")]
    [InlineData("ACBS", "NPC_")]
    public void IsStringSubrecord_ReturnsFalse_ForBinarySubrecords(string signature, string recordType)
    {
        var result = IsStringSubrecord(signature, recordType);
        Assert.False(result);
    }

    #endregion
}
