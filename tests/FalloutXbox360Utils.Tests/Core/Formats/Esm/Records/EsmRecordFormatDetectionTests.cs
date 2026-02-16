using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Records;

/// <summary>
///     Tests for EsmRecordFormat header validation, false positive detection, signature
///     validation, FormID correlation, texture signature matching, and result shape.
///     Complements the existing EsmParserTests (which cover ScanRecords/ParseRecordHeader)
///     by exercising the memory-dump scanner's detection and filtering logic.
/// </summary>
public class EsmRecordFormatDetectionTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    #region Helpers

    /// <summary>Write a 4-char ASCII signature in little-endian byte order.</summary>
    private static void WriteSig(byte[] buf, int offset, string sig)
    {
        buf[offset] = (byte)sig[0];
        buf[offset + 1] = (byte)sig[1];
        buf[offset + 2] = (byte)sig[2];
        buf[offset + 3] = (byte)sig[3];
    }

    /// <summary>Write a uint32 in little-endian.</summary>
    private static void WriteUInt32LE(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
        buf[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    /// <summary>Write a uint16 in little-endian.</summary>
    private static void WriteUInt16LE(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    /// <summary>
    ///     Build a synthetic main record header (24 bytes) at the specified offset in the buffer.
    ///     Layout: [SIG:4][DataSize:4][Flags:4][FormId:4][VC1:4][VC2:4]
    /// </summary>
    private static void WriteMainRecordHeader(
        byte[] buf, int offset, string sig, uint dataSize, uint flags, uint formId)
    {
        WriteSig(buf, offset, sig);
        WriteUInt32LE(buf, offset + 4, dataSize);
        WriteUInt32LE(buf, offset + 8, flags);
        WriteUInt32LE(buf, offset + 12, formId);
        WriteUInt32LE(buf, offset + 16, 0); // VC1
        WriteUInt32LE(buf, offset + 20, 0); // VC2
    }

    /// <summary>
    ///     Build a synthetic EDID subrecord at the specified offset.
    ///     Layout: [EDID:4][Length:2][NullTermString]
    /// </summary>
    private static int WriteEdidSubrecord(byte[] buf, int offset, string editorId)
    {
        WriteSig(buf, offset, "EDID");
        var text = Encoding.ASCII.GetBytes(editorId + "\0");
        WriteUInt16LE(buf, offset + 4, (ushort)text.Length);
        Array.Copy(text, 0, buf, offset + 6, text.Length);
        return 6 + text.Length;
    }

    /// <summary>
    ///     Build a synthetic FULL subrecord at the specified offset.
    ///     Layout: [FULL:4][Length:2][NullTermString]
    /// </summary>
    private static void WriteFullSubrecord(byte[] buf, int offset, string displayName)
    {
        WriteSig(buf, offset, "FULL");
        var text = Encoding.ASCII.GetBytes(displayName + "\0");
        WriteUInt16LE(buf, offset + 4, (ushort)text.Length);
        Array.Copy(text, 0, buf, offset + 6, text.Length);
    }

    /// <summary>
    ///     Build a complete valid record (header + EDID) in a fresh buffer, with enough
    ///     padding so ScanForRecords can scan without overrun (needs data.Length - 24 range).
    /// </summary>
    private static byte[] BuildRecordWithEdid(
        string sig, uint dataSize, uint flags, uint formId, string editorId)
    {
        var edidPayload = Encoding.ASCII.GetBytes(editorId + "\0");
        var edidSubrecordSize = 6 + edidPayload.Length;

        // Ensure dataSize covers the EDID subrecord
        var effectiveDataSize = Math.Max(dataSize, (uint)edidSubrecordSize);

        // Buffer: 24 (header) + effectiveDataSize + 24 (padding for scanner lookahead)
        var buf = new byte[24 + effectiveDataSize + 24];
        WriteMainRecordHeader(buf, 0, sig, effectiveDataSize, flags, formId);
        WriteEdidSubrecord(buf, 24, editorId);
        return buf;
    }

    #endregion

    #region 1. False Positive Detection

    [Fact]
    public void ScanForRecords_GpuDebugPattern_VGT_IsRejected()
    {
        // VGT_ (0x56, 0x47, 0x54, 0x5F) is a known GPU debug pattern.
        // Place it where a main record header would be, with plausible size/flags/formId.
        var buf = new byte[72];
        WriteSig(buf, 0, "VGT_");
        WriteUInt32LE(buf, 4, 20);          // dataSize
        WriteUInt32LE(buf, 8, 0);           // flags
        WriteUInt32LE(buf, 12, 0x00010001); // formId - would be valid if not rejected
        // Remaining bytes zero (VC fields + data)

        var result = EsmRecordScanner.ScanForRecords(buf);

        _output.WriteLine($"MainRecords detected: {result.MainRecords.Count}");
        Assert.Empty(result.MainRecords);
    }

    [Fact]
    public void ScanForRecords_AllAsciiFormId_IsRejected()
    {
        // Create a record header where the FormID is all printable ASCII: 0x54455354 = "TEST"
        // This triggers IsFormIdAllPrintableAscii and should be rejected.
        // Use "WEAP" as the signature since it is a known RuntimeRecordType.
        var buf = new byte[72];
        WriteMainRecordHeader(buf, 0, "WEAP", 20, 0, 0x54455354); // "TEST" in ASCII

        var result = EsmRecordScanner.ScanForRecords(buf);

        _output.WriteLine($"MainRecords detected: {result.MainRecords.Count}");
        Assert.Empty(result.MainRecords);
    }

    [Fact]
    public void ScanForRecords_SpiUnderscore_GpuPattern_IsRejected()
    {
        // SPI_ is another known false positive GPU pattern.
        var buf = new byte[72];
        WriteSig(buf, 0, "SPI_");
        WriteUInt32LE(buf, 4, 20);
        WriteUInt32LE(buf, 8, 0);
        WriteUInt32LE(buf, 12, 0x00010001);

        var result = EsmRecordScanner.ScanForRecords(buf);

        Assert.Empty(result.MainRecords);
    }

    #endregion

    #region 2. Record Header Validation

    [Fact]
    public void ScanForRecords_ValidMainRecordHeader_IsDetected()
    {
        // Valid NPC_ record with reasonable dataSize, zero flags, valid FormID, and EDID subrecord.
        var buf = BuildRecordWithEdid("NPC_", 30, 0, 0x00010001, "TestNpc");

        var result = EsmRecordScanner.ScanForRecords(buf);

        _output.WriteLine($"MainRecords: {result.MainRecords.Count}, EditorIds: {result.EditorIds.Count}");
        Assert.Single(result.MainRecords);
        Assert.Equal("NPC_", result.MainRecords[0].RecordType);
        Assert.Equal(0x00010001u, result.MainRecords[0].FormId);
        // The EDID should also be detected
        Assert.Single(result.EditorIds);
        Assert.Equal("TestNpc", result.EditorIds[0].Name);
    }

    [Fact]
    public void ScanForRecords_OversizedDataSize_IsRejected()
    {
        // dataSize > 10,000,000 should fail IsValidMainRecordHeader
        var buf = new byte[72];
        WriteMainRecordHeader(buf, 0, "WEAP", 20_000_000, 0, 0x00010001);

        var result = EsmRecordScanner.ScanForRecords(buf);

        Assert.Empty(result.MainRecords);
    }

    [Fact]
    public void ScanForRecords_ZeroDataSize_IsRejected()
    {
        // dataSize == 0 should fail validation
        var buf = new byte[72];
        WriteMainRecordHeader(buf, 0, "WEAP", 0, 0, 0x00010001);

        var result = EsmRecordScanner.ScanForRecords(buf);

        Assert.Empty(result.MainRecords);
    }

    [Fact]
    public void ScanForRecords_ZeroFormId_IsRejected()
    {
        var buf = new byte[72];
        WriteMainRecordHeader(buf, 0, "WEAP", 20, 0, 0x00000000);

        var result = EsmRecordScanner.ScanForRecords(buf);

        Assert.Empty(result.MainRecords);
    }

    [Fact]
    public void ScanForRecords_MaxFormId_IsRejected()
    {
        // FormID 0xFFFFFFFF should be rejected
        var buf = new byte[72];
        WriteMainRecordHeader(buf, 0, "WEAP", 20, 0, 0xFFFFFFFF);

        var result = EsmRecordScanner.ScanForRecords(buf);

        Assert.Empty(result.MainRecords);
    }

    [Fact]
    public void ScanForRecords_CompressedFlag_IsAccepted()
    {
        // A compressed record (flag 0x00040000) with upper bits set should still be valid
        var buf = BuildRecordWithEdid("WEAP", 30, 0x00040000, 0x00010001, "CompressedWeap");

        var result = EsmRecordScanner.ScanForRecords(buf);

        Assert.Single(result.MainRecords);
        Assert.True(result.MainRecords[0].IsCompressed);
    }

    #endregion

    #region 3. Signature Validation

    [Theory]
    [InlineData("ALCH")]
    [InlineData("NPC_")]
    [InlineData("WEAP")]
    [InlineData("CREA")]
    [InlineData("REFR")]
    [InlineData("QUST")]
    public void ScanForRecords_KnownRecordTypes_AreDetected(string recordType)
    {
        var buf = BuildRecordWithEdid(recordType, 30, 0, 0x00010001, "TestRecord");

        var result = EsmRecordScanner.ScanForRecords(buf);

        Assert.Single(result.MainRecords);
        Assert.Equal(recordType, result.MainRecords[0].RecordType);
    }

    [Fact]
    public void ScanForRecords_UnknownUppercaseType_NotInRuntimeSet_NotDetected()
    {
        // "ZZYX" is all uppercase but is NOT in RuntimeRecordTypes, so the scanner
        // won't match it against RuntimeRecordMagicLE/BE. It should not be detected
        // as a main record (the scanner only checks known runtime types).
        var buf = new byte[72];
        WriteMainRecordHeader(buf, 0, "ZZYX", 20, 0, 0x00010001);

        var result = EsmRecordScanner.ScanForRecords(buf);

        // Not in the RuntimeRecordTypes set, so it won't be scanned
        Assert.Empty(result.MainRecords);
    }

    [Fact]
    public void ScanForRecords_MixedCaseSignature_IsNotDetected()
    {
        // "NpC_" has mixed case - not a valid signature and not in RuntimeRecordMagicLE
        var buf = new byte[72];
        buf[0] = (byte)'N';
        buf[1] = (byte)'p';
        buf[2] = (byte)'C';
        buf[3] = (byte)'_';
        WriteUInt32LE(buf, 4, 20);
        WriteUInt32LE(buf, 8, 0);
        WriteUInt32LE(buf, 12, 0x00010001);

        var result = EsmRecordScanner.ScanForRecords(buf);

        Assert.Empty(result.MainRecords);
    }

    #endregion

    #region 4. FormID Correlation

    [Fact]
    public void CorrelateFormIdsToNames_WithValidRecordAndEdid_MapsFormIdToName()
    {
        // Build a buffer with a valid NPC_ record header followed by an EDID subrecord.
        // CorrelateFormIdsToNames scans for EDIDs, then walks backward from each EDID
        // to find the parent record header's FormID.
        var editorId = "TestNpcOne";
        uint formId = 0x00010001;
        var buf = BuildRecordWithEdid("NPC_", 30, 0, formId, editorId);

        var correlations = EsmFormIdCorrelator.CorrelateFormIdsToNames(buf);

        _output.WriteLine($"Correlations: {correlations.Count}");
        foreach (var kv in correlations)
        {
            _output.WriteLine($"  0x{kv.Key:X8} -> {kv.Value}");
        }

        Assert.True(correlations.ContainsKey(formId),
            $"Expected FormID 0x{formId:X8} to be correlated to '{editorId}'");
        Assert.Equal(editorId, correlations[formId]);
    }

    [Fact]
    public void CorrelateFormIdsToNames_MultipleRecords_MapsAll()
    {
        // Build two consecutive records, each with an EDID
        var edid1 = "WeaponLaserPistol";
        var edid2 = "ArmorLeather";
        uint formId1 = 0x00011001;
        uint formId2 = 0x00022002;

        var payload1 = Encoding.ASCII.GetBytes(edid1 + "\0");
        var subrecSize1 = 6 + payload1.Length;
        var dataSize1 = (uint)subrecSize1;

        var payload2 = Encoding.ASCII.GetBytes(edid2 + "\0");
        var subrecSize2 = 6 + payload2.Length;
        var dataSize2 = (uint)subrecSize2;

        // Total: record1 header + data + record2 header + data + padding
        var buf = new byte[24 + dataSize1 + 24 + dataSize2 + 24];

        // Record 1
        WriteMainRecordHeader(buf, 0, "WEAP", dataSize1, 0, formId1);
        WriteEdidSubrecord(buf, 24, edid1);

        // Record 2
        var rec2Start = 24 + (int)dataSize1;
        WriteMainRecordHeader(buf, rec2Start, "ARMO", dataSize2, 0, formId2);
        WriteEdidSubrecord(buf, rec2Start + 24, edid2);

        var correlations = EsmFormIdCorrelator.CorrelateFormIdsToNames(buf);

        _output.WriteLine($"Correlations: {correlations.Count}");
        Assert.True(correlations.ContainsKey(formId1));
        Assert.Equal(edid1, correlations[formId1]);
        Assert.True(correlations.ContainsKey(formId2));
        Assert.Equal(edid2, correlations[formId2]);
    }

    [Fact]
    public void CorrelateFormIdsToNames_NoEdids_ReturnsEmpty()
    {
        // Buffer with no EDID subrecords
        var buf = new byte[72];
        WriteMainRecordHeader(buf, 0, "WEAP", 20, 0, 0x00010001);

        var correlations = EsmFormIdCorrelator.CorrelateFormIdsToNames(buf);

        Assert.Empty(correlations);
    }

    #endregion

    #region 5. Texture Signature Matching

    [Fact]
    public void ScanForRecords_Tx00WithValidPath_IsDetected()
    {
        // TX00 subrecord with a valid texture path
        var texPath = "textures\\test\\diffuse.dds\0";
        var texBytes = Encoding.ASCII.GetBytes(texPath);
        var buf = new byte[6 + texBytes.Length + 24]; // subrecord + padding

        WriteSig(buf, 0, "TX00");
        WriteUInt16LE(buf, 4, (ushort)texBytes.Length);
        Array.Copy(texBytes, 0, buf, 6, texBytes.Length);

        var result = EsmRecordScanner.ScanForRecords(buf);

        _output.WriteLine($"TexturePaths: {result.TexturePaths.Count}");
        Assert.Single(result.TexturePaths);
        Assert.Equal("TX00", result.TexturePaths[0].SubrecordType);
        Assert.Contains("diffuse.dds", result.TexturePaths[0].Text);
    }

    [Theory]
    [InlineData("TX00")]
    [InlineData("TX01")]
    [InlineData("TX05")]
    [InlineData("TX07")]
    public void ScanForRecords_TextureSignatures_TX00_Through_TX07_Detected(string txSig)
    {
        var texPath = "textures\\weapons\\test.dds\0";
        var texBytes = Encoding.ASCII.GetBytes(texPath);
        var buf = new byte[6 + texBytes.Length + 24];

        WriteSig(buf, 0, txSig);
        WriteUInt16LE(buf, 4, (ushort)texBytes.Length);
        Array.Copy(texBytes, 0, buf, 6, texBytes.Length);

        var result = EsmRecordScanner.ScanForRecords(buf);

        Assert.Single(result.TexturePaths);
        Assert.Equal(txSig, result.TexturePaths[0].SubrecordType);
    }

    [Fact]
    public void ScanForRecords_Tx08_IsNotDetected()
    {
        // TX08 is out of the valid range TX00-TX07
        var texPath = "textures\\weapons\\test.dds\0";
        var texBytes = Encoding.ASCII.GetBytes(texPath);
        var buf = new byte[6 + texBytes.Length + 24];

        WriteSig(buf, 0, "TX08");
        WriteUInt16LE(buf, 4, (ushort)texBytes.Length);
        Array.Copy(texBytes, 0, buf, 6, texBytes.Length);

        var result = EsmRecordScanner.ScanForRecords(buf);

        // TX08 won't match MatchesTextureSignature, so it won't be in TexturePaths
        Assert.Empty(result.TexturePaths);
    }

    #endregion

    #region 6. EsmRecordScanResult Shape

    [Fact]
    public void ScanForRecords_EmptyData_ReturnsEmptyCollections()
    {
        // Minimum size: scanner needs at least 24 bytes of room
        var buf = new byte[24];

        var result = EsmRecordScanner.ScanForRecords(buf);

        Assert.NotNull(result);
        Assert.Empty(result.EditorIds);
        Assert.Empty(result.FullNames);
        Assert.Empty(result.Descriptions);
        Assert.Empty(result.GameSettings);
        Assert.Empty(result.ScriptSources);
        Assert.Empty(result.FormIdReferences);
        Assert.Empty(result.MainRecords);
        Assert.Empty(result.NameReferences);
        Assert.Empty(result.Positions);
        Assert.Empty(result.ActorBases);
        Assert.Empty(result.ResponseTexts);
        Assert.Empty(result.ResponseData);
        Assert.Empty(result.ModelPaths);
        Assert.Empty(result.IconPaths);
        Assert.Empty(result.TexturePaths);
        Assert.Empty(result.ScriptRefs);
        Assert.Empty(result.EffectRefs);
        Assert.Empty(result.SoundRefs);
        Assert.Empty(result.QuestRefs);
        Assert.Empty(result.Conditions);
        Assert.Empty(result.Heightmaps);
        Assert.Empty(result.CellGrids);
        Assert.Empty(result.AssetStrings);
        Assert.Empty(result.RuntimeEditorIds);
    }

    [Fact]
    public void ScanForRecords_WithMultipleSubrecordTypes_PopulatesCorrectLists()
    {
        // Build a buffer with a WEAP record containing EDID + FULL subrecords
        var editorId = "WeaponTestGun";
        var fullName = "Test Gun";
        var edidPayload = Encoding.ASCII.GetBytes(editorId + "\0");
        var fullPayload = Encoding.ASCII.GetBytes(fullName + "\0");

        var edidSize = 6 + edidPayload.Length;
        var fullSize = 6 + fullPayload.Length;
        var dataSize = (uint)(edidSize + fullSize);

        // Buffer: header(24) + data + padding(24)
        var buf = new byte[24 + dataSize + 24];

        WriteMainRecordHeader(buf, 0, "WEAP", dataSize, 0, 0x00010001);
        var off = 24;
        off += WriteEdidSubrecord(buf, off, editorId);
        WriteFullSubrecord(buf, off, fullName);

        var result = EsmRecordScanner.ScanForRecords(buf);

        _output.WriteLine($"MainRecords: {result.MainRecords.Count}");
        _output.WriteLine($"EditorIds: {result.EditorIds.Count}");
        _output.WriteLine($"FullNames: {result.FullNames.Count}");

        Assert.Single(result.MainRecords);
        Assert.Single(result.EditorIds);
        Assert.Single(result.FullNames);
        Assert.Equal(editorId, result.EditorIds[0].Name);
        Assert.Equal(fullName, result.FullNames[0].Text);
    }

    [Fact]
    public void ScanForRecords_MainRecordCounts_AggregatesCorrectly()
    {
        // Build two WEAP records and one ALCH record
        var edid1Bytes = Encoding.ASCII.GetBytes("Weapon1\0");
        var edid2Bytes = Encoding.ASCII.GetBytes("Weapon2\0");
        var edid3Bytes = Encoding.ASCII.GetBytes("AlchPotion\0");

        var ds1 = (uint)(6 + edid1Bytes.Length);
        var ds2 = (uint)(6 + edid2Bytes.Length);
        var ds3 = (uint)(6 + edid3Bytes.Length);

        var totalSize = (24 + ds1) + (24 + ds2) + (24 + ds3) + 24; // +24 padding
        var buf = new byte[totalSize];

        var off = 0;
        WriteMainRecordHeader(buf, off, "WEAP", ds1, 0, 0x00010001);
        WriteEdidSubrecord(buf, off + 24, "Weapon1");
        off += 24 + (int)ds1;

        WriteMainRecordHeader(buf, off, "WEAP", ds2, 0, 0x00010002);
        WriteEdidSubrecord(buf, off + 24, "Weapon2");
        off += 24 + (int)ds2;

        WriteMainRecordHeader(buf, off, "ALCH", ds3, 0, 0x00020001);
        WriteEdidSubrecord(buf, off + 24, "AlchPotion");

        var result = EsmRecordScanner.ScanForRecords(buf);

        _output.WriteLine($"Total MainRecords: {result.MainRecords.Count}");
        foreach (var kv in result.MainRecordCounts)
        {
            _output.WriteLine($"  {kv.Key}: {kv.Value}");
        }

        Assert.Equal(3, result.MainRecords.Count);
        Assert.Equal(2, result.MainRecordCounts["WEAP"]);
        Assert.Equal(1, result.MainRecordCounts["ALCH"]);
    }

    [Fact]
    public void ScanForRecords_EndiannessCounts_TrackCorrectly()
    {
        var buf = BuildRecordWithEdid("WEAP", 30, 0, 0x00010001, "TestWeapon");

        var result = EsmRecordScanner.ScanForRecords(buf);

        // ScanForRecords with byte[] always reads LE first
        Assert.Equal(1, result.LittleEndianRecords);
        Assert.Equal(0, result.BigEndianRecords);
    }

    #endregion
}
