using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.BinaryTestWriter;
using static FalloutXbox360Utils.Tests.Helpers.EsmTestRecordBuilder;

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

    #region 1. False Positive Detection

    public static TheoryData<byte[], string> FalsePositivePatterns
    {
        get
        {
            var data = new TheoryData<byte[], string>();

            // VGT_ (0x56, 0x47, 0x54, 0x5F) is a known GPU debug pattern
            var vgt = new byte[72];
            WriteSig(vgt, 0, "VGT_");
            WriteUInt32LE(vgt, 4, 20);
            WriteUInt32LE(vgt, 8, 0);
            WriteUInt32LE(vgt, 12, 0x00010001);
            data.Add(vgt, "VGT_ GPU debug pattern");

            // SPI_ is another known GPU debug pattern
            var spi = new byte[72];
            WriteSig(spi, 0, "SPI_");
            WriteUInt32LE(spi, 4, 20);
            WriteUInt32LE(spi, 8, 0);
            WriteUInt32LE(spi, 12, 0x00010001);
            data.Add(spi, "SPI_ GPU debug pattern");

            // All-ASCII FormID (0x54455354 = "TEST") triggers IsFormIdAllPrintableAscii
            var ascii = new byte[72];
            WriteMainRecordHeader(ascii, 0, "WEAP", 20, 0, 0x54455354);
            data.Add(ascii, "All-ASCII FormID");

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(FalsePositivePatterns))]
    public void ScanForRecords_FalsePositivePattern_IsRejected(byte[] buf, string description)
    {
        var result = EsmRecordScanner.ScanForRecords(buf);

        _output.WriteLine($"Pattern: {description}, MainRecords detected: {result.MainRecords.Count}");
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

    public static TheoryData<uint, uint, string> InvalidHeaderParameters => new()
    {
        { 20_000_000, 0x00010001u, "Oversized dataSize (>10M)" },
        { 0, 0x00010001u, "Zero dataSize" },
        { 20, 0x00000000u, "Zero FormId" },
        { 20, 0xFFFFFFFFu, "Max FormId (0xFFFFFFFF)" },
    };

    [Theory]
    [MemberData(nameof(InvalidHeaderParameters))]
    public void ScanForRecords_InvalidHeader_IsRejected(uint dataSize, uint formId, string description)
    {
        var buf = new byte[72];
        WriteMainRecordHeader(buf, 0, "WEAP", dataSize, 0, formId);

        var result = EsmRecordScanner.ScanForRecords(buf);

        _output.WriteLine($"Rejection reason: {description}, MainRecords detected: {result.MainRecords.Count}");
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
        EsmAssert.AllCollectionsEmpty(result);
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

        var totalSize = 24 + ds1 + 24 + ds2 + 24 + ds3 + 24; // +24 padding
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

    #region 7. Big-Endian Fast-Reject Regression (NPC_, TES4)

    /// <summary>
    ///     Regression test: big-endian NPC_ records start with '_' (0x5F) when byte-swapped.
    ///     The scanner's fast-reject must NOT skip positions starting with '_', otherwise
    ///     NPC_ records in Xbox 360 memory dumps are silently lost.
    /// </summary>
    [Fact]
    public void ScanMemoryMapped_BigEndianNpc_IsDetectedByFastReject()
    {
        var edid = Encoding.ASCII.GetBytes("TestNpc\0");
        var buf = BuildMinimalRecordBE("NPC_", 0x00010001, "EDID", edid);

        var result = ScanViaMemoryMappedFile(buf);

        _output.WriteLine($"MainRecords: {result.MainRecords.Count}, BE: {result.BigEndianRecords}");
        Assert.Single(result.MainRecords);
        Assert.Equal("NPC_", result.MainRecords[0].RecordType);
        Assert.True(result.MainRecords[0].IsBigEndian);
    }

    /// <summary>
    ///     Regression test: big-endian TES4 records start with '4' (0x34) when byte-swapped.
    ///     The scanner's fast-reject must allow digits so TES4 positions are not skipped.
    ///     TES4 is not in RuntimeRecordTypes so it won't be detected as a main record,
    ///     but the fast-reject must still allow the position to reach the dispatch lookup.
    /// </summary>
    [Fact]
    public void FastReject_BigEndianTes4_DigitFirstByteAccepted()
    {
        // BE signature for "TES4" on disk: bytes [0x34='4', 0x53='S', 0x45='E', 0x54='T']
        // The first byte '4' (0x34) must pass the fast-reject filter.
        byte b = (byte)'4';
        bool passesFilter = !(b is < (byte)'0' or (> (byte)'9' and < (byte)'A') or (> (byte)'Z' and not (byte)'_'));
        Assert.True(passesFilter, "Digit '4' must pass the fast-reject filter");
    }

    /// <summary>
    ///     Verify that all record types in RuntimeRecordTypes have their big-endian first byte
    ///     accepted by the fast-reject filter. This prevents future regressions if new record
    ///     types with non-A-Z characters are added.
    /// </summary>
    [Fact]
    public void FastReject_AllRuntimeRecordTypes_BigEndianFirstByteAccepted()
    {
        var rejected = new List<string>();

        foreach (var type in RecordScannerDispatch.RuntimeRecordTypes)
        {
            // Big-endian signature is reversed: "NPC_" -> "_CPN", first byte = '_'
            byte firstByteBE = (byte)type[3];

            bool passesFilter = !(firstByteBE is < (byte)'0'
                or (> (byte)'9' and < (byte)'A')
                or (> (byte)'Z' and not (byte)'_'));

            if (!passesFilter)
            {
                rejected.Add($"{type} (BE first byte: 0x{firstByteBE:X2} '{(char)firstByteBE}')");
            }
        }

        _output.WriteLine($"Checked {RecordScannerDispatch.RuntimeRecordTypes.Length} record types");
        foreach (var r in rejected)
        {
            _output.WriteLine($"  REJECTED: {r}");
        }

        Assert.Empty(rejected);
    }

    /// <summary>
    ///     Helper: writes data to a temp file and scans via ScanForRecordsMemoryMapped,
    ///     exercising the production fast-reject + unified dispatch code path.
    /// </summary>
    private static EsmRecordScanResult ScanViaMemoryMappedFile(byte[] data)
    {
        var tmpFile = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            File.WriteAllBytes(tmpFile, data);
            using var mmf = MemoryMappedFile.CreateFromFile(
                tmpFile, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, data.Length, MemoryMappedFileAccess.Read);
            return EsmRecordScanner.ScanForRecordsMemoryMapped(accessor, data.Length);
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    #endregion
}