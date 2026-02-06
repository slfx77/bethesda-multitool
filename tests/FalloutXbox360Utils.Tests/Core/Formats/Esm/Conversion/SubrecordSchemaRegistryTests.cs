using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Conversion;

/// <summary>
///     Tests for SubrecordSchemaRegistry — schema lookup, string detection, and signature management.
/// </summary>
public class SubrecordSchemaRegistryTests
{
    #region GetSchema Lookup Priority

    [Fact]
    public void GetSchema_ExactMatch_ReturnsSchema()
    {
        // HEDR is a well-known subrecord with specific schema
        var schema = SubrecordSchemaRegistry.GetSchema("HEDR", "TES4", 12);
        Assert.NotNull(schema);
    }

    [Fact]
    public void GetSchema_SignatureAndRecordType_ReturnsSchema()
    {
        // DNAM in WEAP has a record-type-specific schema
        var schema = SubrecordSchemaRegistry.GetSchema("DNAM", "WEAP", 204);
        Assert.NotNull(schema);
    }

    [Fact]
    public void GetSchema_SignatureOnly_ReturnsDefault()
    {
        // ANAM has a default schema (any record type)
        var schema = SubrecordSchemaRegistry.GetSchema("ANAM", "UNKN", 4);
        // Should find some schema for ANAM
        Assert.NotNull(schema);
    }

    [Fact]
    public void GetSchema_UnknownSignature_ReturnsNull()
    {
        var schema = SubrecordSchemaRegistry.GetSchema("ZZZZ", "UNKN", 4);
        Assert.Null(schema);
    }

    #endregion

    #region IMAD Special Handling

    [Fact]
    public void GetSchema_ImadEdid_ReturnsStringSchema()
    {
        var schema = SubrecordSchemaRegistry.GetSchema("EDID", "IMAD", 10);
        Assert.NotNull(schema);
        Assert.Same(SubrecordSchema.String, schema);
    }

    [Fact]
    public void GetSchema_ImadDnam_ReturnsFloatArray()
    {
        var schema = SubrecordSchemaRegistry.GetSchema("DNAM", "IMAD", 244);
        Assert.NotNull(schema);
        Assert.Same(SubrecordSchema.FloatArray, schema);
    }

    [Fact]
    public void GetSchema_ImadBnam_ReturnsFloatArray()
    {
        var schema = SubrecordSchemaRegistry.GetSchema("BNAM", "IMAD", 8);
        Assert.NotNull(schema);
        Assert.Same(SubrecordSchema.FloatArray, schema);
    }

    [Theory]
    [InlineData("VNAM")]
    [InlineData("TNAM")]
    [InlineData("NAM3")]
    [InlineData("RNAM")]
    [InlineData("SNAM")]
    [InlineData("UNAM")]
    [InlineData("NAM1")]
    [InlineData("NAM2")]
    [InlineData("WNAM")]
    [InlineData("XNAM")]
    [InlineData("YNAM")]
    [InlineData("NAM4")]
    public void GetSchema_ImadKnownFloatArraySubrecord_ReturnsFloatArray(string signature)
    {
        var schema = SubrecordSchemaRegistry.GetSchema(signature, "IMAD", 16);
        Assert.NotNull(schema);
        Assert.Same(SubrecordSchema.FloatArray, schema);
    }

    [Fact]
    public void GetSchema_ImadIadSubrecord_ReturnsFloatArray()
    {
        // Keyed *IAD subrecords (first char is key, followed by "IAD")
        var schema = SubrecordSchemaRegistry.GetSchema("AIAD", "IMAD", 8);
        Assert.NotNull(schema);
        Assert.Same(SubrecordSchema.FloatArray, schema);
    }

    [Fact]
    public void GetSchema_ImadUnknown_ReturnsFloatArray()
    {
        // Unknown IMAD subrecords default to FloatArray
        var schema = SubrecordSchemaRegistry.GetSchema("QQQQ", "IMAD", 12);
        Assert.NotNull(schema);
        Assert.Same(SubrecordSchema.FloatArray, schema);
    }

    #endregion

    #region DATA Fallback Logic

    [Fact]
    public void GetSchema_DataSmall_ReturnsByteArray()
    {
        // DATA <= 2 bytes -> ByteArray (fallback)
        var schema = SubrecordSchemaRegistry.GetSchema("DATA", "ZZZZ", 1);
        Assert.NotNull(schema);
        Assert.Same(SubrecordSchema.ByteArray, schema);
    }

    [Fact]
    public void GetSchema_DataSmall2Bytes_ReturnsByteArray()
    {
        var schema = SubrecordSchemaRegistry.GetSchema("DATA", "ZZZZ", 2);
        Assert.NotNull(schema);
        Assert.Same(SubrecordSchema.ByteArray, schema);
    }

    [Fact]
    public void GetSchema_DataMediumDiv4_ReturnsFloatArray()
    {
        // DATA 3-64 bytes, divisible by 4 -> FloatArray (fallback)
        var schema = SubrecordSchemaRegistry.GetSchema("DATA", "ZZZZ", 8);
        Assert.NotNull(schema);
        Assert.Same(SubrecordSchema.FloatArray, schema);
    }

    [Fact]
    public void GetSchema_DataLargeIrregular_ReturnsByteArray()
    {
        // DATA > 64 bytes or not divisible by 4 -> ByteArray (fallback)
        var schema = SubrecordSchemaRegistry.GetSchema("DATA", "ZZZZ", 100);
        Assert.NotNull(schema);
        Assert.Same(SubrecordSchema.ByteArray, schema);
    }

    [Fact]
    public void GetSchema_DataNotDiv4_ReturnsByteArray()
    {
        // 7 bytes is not divisible by 4 and > 2
        var schema = SubrecordSchemaRegistry.GetSchema("DATA", "ZZZZ", 7);
        Assert.NotNull(schema);
        Assert.Same(SubrecordSchema.ByteArray, schema);
    }

    #endregion

    #region WTHR *IAD Subrecords

    [Fact]
    public void GetSchema_WthrIadSubrecord_ReturnsFloatArray()
    {
        // WTHR keyed *IAD subrecords (e.g., \x00IAD, @IAD, AIAD) are float arrays
        var schema = SubrecordSchemaRegistry.GetSchema("AIAD", "WTHR", 8);
        Assert.NotNull(schema);
        Assert.Same(SubrecordSchema.FloatArray, schema);
    }

    [Fact]
    public void GetSchema_WthrNonIadSubrecord_UsesNormalLookup()
    {
        // WTHR EDID should use string schema, not IAD handling
        Assert.True(SubrecordSchemaRegistry.IsStringSubrecord("EDID", "WTHR"));
    }

    #endregion

    #region IsStringSubrecord

    [Theory]
    [InlineData("EDID", "WEAP")]
    [InlineData("FULL", "NPC_")]
    [InlineData("MODL", "ARMO")]
    [InlineData("DESC", "BOOK")]
    [InlineData("ICON", "MISC")]
    [InlineData("MICO", "WEAP")]
    [InlineData("TX00", "LTEX")]
    [InlineData("TX07", "LTEX")]
    public void IsStringSubrecord_KnownStrings_ReturnsTrue(string signature, string recordType)
    {
        Assert.True(SubrecordSchemaRegistry.IsStringSubrecord(signature, recordType));
    }

    [Fact]
    public void IsStringSubrecord_DataSubrecord_ReturnsFalse()
    {
        // DATA is never a string subrecord
        Assert.False(SubrecordSchemaRegistry.IsStringSubrecord("DATA", "WEAP"));
    }

    [Fact]
    public void IsStringSubrecord_Tes4Cnam_ReturnsTrue()
    {
        // CNAM in TES4 is the author string
        Assert.True(SubrecordSchemaRegistry.IsStringSubrecord("CNAM", "TES4"));
    }

    [Fact]
    public void IsStringSubrecord_Tes4Snam_ReturnsTrue()
    {
        Assert.True(SubrecordSchemaRegistry.IsStringSubrecord("SNAM", "TES4"));
    }

    [Fact]
    public void IsStringSubrecord_Tes4Mast_ReturnsTrue()
    {
        Assert.True(SubrecordSchemaRegistry.IsStringSubrecord("MAST", "TES4"));
    }

    [Fact]
    public void IsStringSubrecord_InfoRnam_ReturnsTrue()
    {
        // INFO RNAM is a prompt/result string
        Assert.True(SubrecordSchemaRegistry.IsStringSubrecord("RNAM", "INFO"));
    }

    [Fact]
    public void IsStringSubrecord_InfoNam1_ReturnsTrue()
    {
        // INFO NAM1 is Response Text
        Assert.True(SubrecordSchemaRegistry.IsStringSubrecord("NAM1", "INFO"));
    }

    [Fact]
    public void IsStringSubrecord_NoteTnam_ReturnsTrue()
    {
        // NOTE TNAM is text
        Assert.True(SubrecordSchemaRegistry.IsStringSubrecord("TNAM", "NOTE"));
    }

    [Fact]
    public void IsStringSubrecord_WthrDnam_ReturnsTrue()
    {
        // WTHR DNAM is cloud texture string
        Assert.True(SubrecordSchemaRegistry.IsStringSubrecord("DNAM", "WTHR"));
    }

    #endregion

    #region GetAllSignatures

    [Fact]
    public void GetAllSignatures_ContainsCommonSignatures()
    {
        var sigs = SubrecordSchemaRegistry.GetAllSignatures();
        Assert.Contains("EDID", sigs);
        Assert.Contains("FULL", sigs);
        Assert.Contains("MODL", sigs);
        Assert.Contains("DATA", sigs);
        Assert.Contains("DNAM", sigs);
    }

    [Fact]
    public void GetAllSignatures_ReturnsNonEmptySet()
    {
        var sigs = SubrecordSchemaRegistry.GetAllSignatures();
        Assert.True(sigs.Count > 50); // Should have many signatures
    }

    #endregion

    #region GetReversedSignature

    [Theory]
    [InlineData("EDID", "DIDE")]
    [InlineData("TES4", "4SET")]
    [InlineData("ABCD", "DCBA")]
    [InlineData("NPC_", "_CPN")]
    public void GetReversedSignature_ReversesCorrectly(string input, string expected)
    {
        Assert.Equal(expected, SubrecordSchemaRegistry.GetReversedSignature(input));
    }

    #endregion

    #region Fallback Logging

    [Fact]
    public void FallbackLogging_WhenDisabled_DoesNotRecord()
    {
        SubrecordSchemaRegistry.EnableFallbackLogging = false;
        SubrecordSchemaRegistry.ClearFallbackLog();
        SubrecordSchemaRegistry.RecordFallback("TEST", "DATA", 4, "Test");
        Assert.False(SubrecordSchemaRegistry.HasFallbackUsage);
    }

    [Fact]
    public void FallbackLogging_WhenEnabled_RecordsFallback()
    {
        SubrecordSchemaRegistry.EnableFallbackLogging = true;
        SubrecordSchemaRegistry.ClearFallbackLog();
        SubrecordSchemaRegistry.RecordFallback("TEST", "DATA", 4, "TestFallback");
        Assert.True(SubrecordSchemaRegistry.HasFallbackUsage);
        var usage = SubrecordSchemaRegistry.GetFallbackUsage().ToList();
        // Check our specific entry exists (other tests may also record fallbacks via static state)
        var testEntry = usage.First(u => u.FallbackType == "TestFallback");
        Assert.Equal("TEST", testEntry.RecordType);
        Assert.Equal(1, testEntry.Count);

        // Cleanup
        SubrecordSchemaRegistry.ClearFallbackLog();
        SubrecordSchemaRegistry.EnableFallbackLogging = false;
    }

    [Fact]
    public void ClearFallbackLog_ClearsAllRecords()
    {
        SubrecordSchemaRegistry.EnableFallbackLogging = true;
        SubrecordSchemaRegistry.RecordFallback("A", "B", 4, "C");
        SubrecordSchemaRegistry.ClearFallbackLog();
        Assert.False(SubrecordSchemaRegistry.HasFallbackUsage);
        SubrecordSchemaRegistry.EnableFallbackLogging = false;
    }

    #endregion

    #region Schema Properties

    [Fact]
    public void SubrecordSchema_String_HasExpectedSize0()
    {
        Assert.Equal(0, SubrecordSchema.String.ExpectedSize);
    }

    [Fact]
    public void SubrecordSchema_Empty_HasExpectedSize0()
    {
        Assert.Equal(0, SubrecordSchema.Empty.ExpectedSize);
    }

    [Fact]
    public void SubrecordSchema_ByteArray_HasExpectedSize0()
    {
        Assert.Equal(0, SubrecordSchema.ByteArray.ExpectedSize);
    }

    [Fact]
    public void SubrecordSchema_FormIdArray_HasExpectedSizeNegative1()
    {
        Assert.Equal(-1, SubrecordSchema.FormIdArray.ExpectedSize);
    }

    [Fact]
    public void SubrecordSchema_FloatArray_HasExpectedSizeNegative1()
    {
        Assert.Equal(-1, SubrecordSchema.FloatArray.ExpectedSize);
    }

    [Fact]
    public void SubrecordSchema_Simple4Byte_HasExpectedSize4()
    {
        var schema = SubrecordSchema.Simple4Byte();
        Assert.Equal(4, schema.ExpectedSize);
    }

    [Fact]
    public void SubrecordSchema_Simple2Byte_HasExpectedSize2()
    {
        var schema = SubrecordSchema.Simple2Byte();
        Assert.Equal(2, schema.ExpectedSize);
    }

    #endregion

    #region SubrecordField EffectiveSize

    [Theory]
    [InlineData(SubrecordFieldType.UInt8, 1)]
    [InlineData(SubrecordFieldType.Int8, 1)]
    [InlineData(SubrecordFieldType.UInt16, 2)]
    [InlineData(SubrecordFieldType.Int16, 2)]
    [InlineData(SubrecordFieldType.UInt32, 4)]
    [InlineData(SubrecordFieldType.Int32, 4)]
    [InlineData(SubrecordFieldType.FormId, 4)]
    [InlineData(SubrecordFieldType.Float, 4)]
    [InlineData(SubrecordFieldType.UInt64, 8)]
    [InlineData(SubrecordFieldType.Int64, 8)]
    [InlineData(SubrecordFieldType.Double, 8)]
    [InlineData(SubrecordFieldType.Vec3, 12)]
    [InlineData(SubrecordFieldType.Quaternion, 16)]
    [InlineData(SubrecordFieldType.ColorRgba, 4)]
    [InlineData(SubrecordFieldType.PosRot, 24)]
    [InlineData(SubrecordFieldType.UInt32WordSwapped, 4)]
    public void SubrecordField_EffectiveSize_MatchesExpected(SubrecordFieldType type, int expectedSize)
    {
        var field = new SubrecordField("Test", type);
        Assert.Equal(expectedSize, field.EffectiveSize);
    }

    [Fact]
    public void SubrecordField_CustomSize_OverridesDefault()
    {
        var field = SubrecordField.Bytes("Data", 16);
        Assert.Equal(16, field.EffectiveSize);
    }

    #endregion
}
