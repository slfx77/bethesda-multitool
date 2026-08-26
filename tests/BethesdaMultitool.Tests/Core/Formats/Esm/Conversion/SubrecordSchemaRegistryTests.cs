using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Conversion;

/// <summary>
///     Tests for SubrecordSchemaRegistry — schema lookup, string detection, and signature management.
/// </summary>
public class SubrecordSchemaRegistryTests
{
    #region GetReversedSignature

    [Theory]
    [InlineData("EDID", "DIDE")]
    [InlineData("TES4", "4SET")]
    [InlineData("ABCD", "DCBA")]
    [InlineData("NPC_", "_CPN")]
    public void GetReversedSignature_ReturnsTheFourCharactersInReverseOrder(string input, string expected)
    {
        Assert.Equal(expected, SubrecordSchemaRegistry.GetReversedSignature(input));
    }

    #endregion

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

    [Fact]
    public void GetSchema_PerkEntryConditionFields_MatchFnvLayout()
    {
        var perkData = SubrecordSchemaRegistry.GetSchema("DATA", "PERK", 5);
        Assert.NotNull(perkData);
        Assert.Equal(["Trait", "MinLevel", "Ranks", "Playable", "Hidden"],
            perkData.Fields.Select(field => field.Name));

        var data = SubrecordSchemaRegistry.GetSchema("DATA", "PERK", 3);
        Assert.NotNull(data);
        Assert.Equal(["EntryPoint", "Function", "PerkConditionTabCount"],
            data.Fields.Select(field => field.Name));

        var prkc = SubrecordSchemaRegistry.GetSchema("PRKC", "PERK", 1);
        Assert.NotNull(prkc);
        var runOn = Assert.Single(prkc.Fields);
        Assert.Equal("RunOn", runOn.Name);
        Assert.Equal(SubrecordFieldType.Int8, runOn.Type);
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

    [Theory]
    [InlineData("DNAM", 244)]
    [InlineData("BNAM", 8)]
    [InlineData("VNAM", 16)]
    [InlineData("TNAM", 16)]
    [InlineData("NAM3", 16)]
    [InlineData("RNAM", 16)]
    [InlineData("SNAM", 16)]
    [InlineData("UNAM", 16)]
    [InlineData("NAM1", 16)]
    [InlineData("NAM2", 16)]
    [InlineData("WNAM", 16)]
    [InlineData("XNAM", 16)]
    [InlineData("YNAM", 16)]
    [InlineData("NAM4", 16)]
    [InlineData("AIAD", 8)] // Keyed *IAD subrecord (first char is key, followed by "IAD")
    [InlineData("QQQQ", 12)] // Unknown IMAD subrecords default to FloatArray
    public void GetSchema_ImadFloatArraySubrecord_ReturnsFloatArray(string signature, int size)
    {
        var schema = SubrecordSchemaRegistry.GetSchema(signature, "IMAD", size);
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

    [Theory]
    [InlineData("CAMS", 36, 9)] // truncated CAMS: no TargetPctBetweenActors
    [InlineData("IPDS", 36, 9)] // truncated IPDS: 9 material FormIDs
    [InlineData("IPDS", 40, 10)]
    [InlineData("IPDS", 44, 11)]
    public void GetSchema_TruncatedCamsIpdsData_ReturnsTypedSchemaNotFloatArrayFallback(
        string recordType, int dataLength, int expectedFieldCount)
    {
        // Truncated CAMS/IPDS DATA lengths must resolve to their registered prefix schema, NOT the
        // generic DATA->FloatArray fallback (which reads only the first element and mistypes the rest).
        var schema = SubrecordSchemaRegistry.GetSchema("DATA", recordType, dataLength);
        Assert.NotNull(schema);
        Assert.NotSame(SubrecordSchema.FloatArray, schema);
        Assert.Equal(expectedFieldCount, schema!.Fields.Length);
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
    // Signatures that carry a string in every record type.
    [InlineData("EDID", "WEAP")]
    [InlineData("FULL", "NPC_")]
    [InlineData("MODL", "ARMO")]
    [InlineData("DESC", "BOOK")]
    [InlineData("ICON", "MISC")]
    [InlineData("MICO", "WEAP")]
    [InlineData("TX00", "LTEX")]
    [InlineData("TX07", "LTEX")]
    // Signatures that are a string only in a specific record type — the same four characters
    // carry binary data elsewhere, so the record type is what decides.
    [InlineData("CNAM", "TES4")] // plugin author
    [InlineData("SNAM", "TES4")] // plugin description
    [InlineData("MAST", "TES4")] // master file name
    [InlineData("RNAM", "INFO")] // prompt / result string
    [InlineData("NAM1", "INFO")] // response text
    [InlineData("TNAM", "NOTE")] // note text
    [InlineData("DNAM", "WTHR")] // cloud texture path
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

    /// <summary>
    ///     <c>ExpectedSize</c> encodes how a schema validates payload length: a positive value is
    ///     an exact byte count, 0 means "any length" (variable-length payloads), and -1 means
    ///     "any whole multiple of the element width" (repeating arrays).
    /// </summary>
    public static TheoryData<string, SubrecordSchema, int> SchemaExpectedSizes => new()
    {
        { "String", SubrecordSchema.String, 0 },
        { "Empty", SubrecordSchema.Empty, 0 },
        { "ByteArray", SubrecordSchema.ByteArray, 0 },
        { "FormIdArray", SubrecordSchema.FormIdArray, -1 },
        { "FloatArray", SubrecordSchema.FloatArray, -1 },
        { "Simple4Byte", SubrecordSchema.Simple4Byte(), 4 },
        { "Simple2Byte", SubrecordSchema.Simple2Byte(), 2 }
    };

    [Theory]
    [MemberData(nameof(SchemaExpectedSizes))]
    public void SubrecordSchema_ExposesTheExpectedSizeForItsKind(
        string kind, SubrecordSchema schema, int expectedSize)
    {
        _ = kind; // Names the case in the test display name.

        Assert.Equal(expectedSize, schema.ExpectedSize);
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