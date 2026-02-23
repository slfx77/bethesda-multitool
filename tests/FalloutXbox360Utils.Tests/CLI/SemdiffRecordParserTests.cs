using System.Text;
using FalloutXbox360Utils.CLI;
using Xunit;

namespace FalloutXbox360Utils.Tests.CLI;

/// <summary>
///     Tests for <see cref="SemdiffRecordParser" /> covering record parsing
///     from byte arrays and field comparison logic.
/// </summary>
public class SemdiffRecordParserTests
{
    #region Helpers

    /// <summary>Write a 4-char ASCII signature in little-endian byte order.</summary>
    private static void WriteSigLE(byte[] buf, int offset, string sig)
    {
        buf[offset] = (byte)sig[0];
        buf[offset + 1] = (byte)sig[1];
        buf[offset + 2] = (byte)sig[2];
        buf[offset + 3] = (byte)sig[3];
    }

    /// <summary>Write a 4-char ASCII signature in big-endian (reversed) byte order.</summary>
    private static void WriteSigBE(byte[] buf, int offset, string sig)
    {
        buf[offset] = (byte)sig[3];
        buf[offset + 1] = (byte)sig[2];
        buf[offset + 2] = (byte)sig[1];
        buf[offset + 3] = (byte)sig[0];
    }

    /// <summary>Write a uint32 in little-endian.</summary>
    private static void WriteUInt32LE(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
        buf[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    /// <summary>Write a uint32 in big-endian.</summary>
    private static void WriteUInt32BE(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)((value >> 24) & 0xFF);
        buf[offset + 1] = (byte)((value >> 16) & 0xFF);
        buf[offset + 2] = (byte)((value >> 8) & 0xFF);
        buf[offset + 3] = (byte)(value & 0xFF);
    }

    /// <summary>Write a uint16 in little-endian.</summary>
    private static void WriteUInt16LE(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    /// <summary>Write a uint16 in big-endian.</summary>
    private static void WriteUInt16BE(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)((value >> 8) & 0xFF);
        buf[offset + 1] = (byte)(value & 0xFF);
    }

    /// <summary>
    ///     Build a minimal little-endian ESM record with one subrecord.
    ///     Record header: [Sig:4][DataSize:4][Flags:4][FormId:4][VC1:4][VC2:4] = 24 bytes
    ///     Subrecord: [Sig:4][Size:2][Data:N]
    /// </summary>
    private static byte[] BuildMinimalRecordLE(string recSig, uint formId, string subSig, byte[] subData)
    {
        var subrecordSize = 4 + 2 + subData.Length; // sig + size + data
        var dataSize = (uint)subrecordSize;
        var totalSize = 24 + (int)dataSize;
        var buf = new byte[totalSize];

        // Record header
        WriteSigLE(buf, 0, recSig);
        WriteUInt32LE(buf, 4, dataSize);
        WriteUInt32LE(buf, 8, 0);        // flags
        WriteUInt32LE(buf, 12, formId);
        WriteUInt32LE(buf, 16, 0);       // VC1
        WriteUInt32LE(buf, 20, 0);       // VC2

        // Subrecord
        WriteSigLE(buf, 24, subSig);
        WriteUInt16LE(buf, 28, (ushort)subData.Length);
        Array.Copy(subData, 0, buf, 30, subData.Length);

        return buf;
    }

    /// <summary>
    ///     Build a minimal big-endian ESM record with one subrecord.
    /// </summary>
    private static byte[] BuildMinimalRecordBE(string recSig, uint formId, string subSig, byte[] subData)
    {
        var subrecordSize = 4 + 2 + subData.Length;
        var dataSize = (uint)subrecordSize;
        var totalSize = 24 + (int)dataSize;
        var buf = new byte[totalSize];

        // Record header (sig bytes reversed for big-endian)
        WriteSigBE(buf, 0, recSig);
        WriteUInt32BE(buf, 4, dataSize);
        WriteUInt32BE(buf, 8, 0);
        WriteUInt32BE(buf, 12, formId);
        WriteUInt32BE(buf, 16, 0);
        WriteUInt32BE(buf, 20, 0);

        // Subrecord (sig bytes reversed for big-endian)
        WriteSigBE(buf, 24, subSig);
        WriteUInt16BE(buf, 28, (ushort)subData.Length);
        Array.Copy(subData, 0, buf, 30, subData.Length);

        return buf;
    }

    /// <summary>
    ///     Build a little-endian record with multiple subrecords.
    /// </summary>
    private static byte[] BuildRecordWithSubrecordsLE(string recSig, uint formId,
        params (string sig, byte[] data)[] subrecords)
    {
        var totalSubSize = 0;
        foreach (var (_, data) in subrecords)
        {
            totalSubSize += 4 + 2 + data.Length;
        }

        var buf = new byte[24 + totalSubSize];

        WriteSigLE(buf, 0, recSig);
        WriteUInt32LE(buf, 4, (uint)totalSubSize);
        WriteUInt32LE(buf, 8, 0);
        WriteUInt32LE(buf, 12, formId);
        WriteUInt32LE(buf, 16, 0);
        WriteUInt32LE(buf, 20, 0);

        var offset = 24;
        foreach (var (sig, data) in subrecords)
        {
            WriteSigLE(buf, offset, sig);
            WriteUInt16LE(buf, offset + 4, (ushort)data.Length);
            Array.Copy(data, 0, buf, offset + 6, data.Length);
            offset += 6 + data.Length;
        }

        return buf;
    }

    #endregion

    #region ParseRecordsWithSubrecords — Little-Endian

    [Fact]
    public void ParseRecords_SingleRecord_LE_ParsesCorrectly()
    {
        // Build an ALCH record with an EDID subrecord containing "Stimpak\0"
        var edidData = Encoding.ASCII.GetBytes("Stimpak\0");
        var data = BuildMinimalRecordLE("ALCH", 0x00015169, "EDID", edidData);

        var records = SemdiffRecordParser.ParseRecordsWithSubrecords(data, bigEndian: false, null, null);

        Assert.Single(records);
        Assert.Equal("ALCH", records[0].Type);
        Assert.Equal(0x00015169u, records[0].FormId);
        Assert.Single(records[0].Subrecords);
        Assert.Equal("EDID", records[0].Subrecords[0].Signature);
        Assert.Equal(edidData, records[0].Subrecords[0].Data);
    }

    [Fact]
    public void ParseRecords_MultipleSubrecords_LE_ParsesAll()
    {
        var edidData = Encoding.ASCII.GetBytes("TestWeap\0");
        var fullData = Encoding.ASCII.GetBytes("Test Weapon\0");
        var data = BuildRecordWithSubrecordsLE("WEAP", 0x000F1234,
            ("EDID", edidData),
            ("FULL", fullData));

        var records = SemdiffRecordParser.ParseRecordsWithSubrecords(data, bigEndian: false, null, null);

        Assert.Single(records);
        Assert.Equal(2, records[0].Subrecords.Count);
        Assert.Equal("EDID", records[0].Subrecords[0].Signature);
        Assert.Equal("FULL", records[0].Subrecords[1].Signature);
        Assert.Equal(fullData, records[0].Subrecords[1].Data);
    }

    [Fact]
    public void ParseRecords_TypeFilter_FiltersCorrectly()
    {
        // Build two records: one ALCH, one WEAP
        var alchData = BuildMinimalRecordLE("ALCH", 0x00010001, "EDID", [0x41, 0x00]);
        var weapData = BuildMinimalRecordLE("WEAP", 0x00020002, "EDID", [0x42, 0x00]);
        var combined = new byte[alchData.Length + weapData.Length];
        Array.Copy(alchData, 0, combined, 0, alchData.Length);
        Array.Copy(weapData, 0, combined, alchData.Length, weapData.Length);

        // Filter to WEAP only
        var records = SemdiffRecordParser.ParseRecordsWithSubrecords(combined, bigEndian: false, "WEAP", null);

        Assert.Single(records);
        Assert.Equal("WEAP", records[0].Type);
        Assert.Equal(0x00020002u, records[0].FormId);
    }

    [Fact]
    public void ParseRecords_FormIdFilter_FiltersCorrectly()
    {
        var rec1 = BuildMinimalRecordLE("ALCH", 0x00010001, "EDID", [0x41, 0x00]);
        var rec2 = BuildMinimalRecordLE("ALCH", 0x00020002, "EDID", [0x42, 0x00]);
        var combined = new byte[rec1.Length + rec2.Length];
        Array.Copy(rec1, 0, combined, 0, rec1.Length);
        Array.Copy(rec2, 0, combined, rec1.Length, rec2.Length);

        var records = SemdiffRecordParser.ParseRecordsWithSubrecords(combined, bigEndian: false, null, 0x00020002);

        Assert.Single(records);
        Assert.Equal(0x00020002u, records[0].FormId);
    }

    [Fact]
    public void ParseRecords_GrupRecord_IsSkipped()
    {
        // Build a GRUP "record" (24 bytes) followed by a real ALCH record
        var alchData = BuildMinimalRecordLE("ALCH", 0x00010001, "EDID", [0x41, 0x00]);
        var combined = new byte[24 + alchData.Length];

        // Write GRUP header (the parser skips it by advancing 24 bytes)
        WriteSigLE(combined, 0, "GRUP");
        WriteUInt32LE(combined, 4, 24); // Group size = header only
        // rest of GRUP header is zeros

        Array.Copy(alchData, 0, combined, 24, alchData.Length);

        var records = SemdiffRecordParser.ParseRecordsWithSubrecords(combined, bigEndian: false, null, null);

        Assert.Single(records);
        Assert.Equal("ALCH", records[0].Type);
    }

    [Fact]
    public void ParseRecords_EmptyData_ReturnsEmpty()
    {
        var records = SemdiffRecordParser.ParseRecordsWithSubrecords([], bigEndian: false, null, null);
        Assert.Empty(records);
    }

    [Fact]
    public void ParseRecords_DataTooShortForHeader_ReturnsEmpty()
    {
        // Less than 24 bytes means no record can be parsed
        var records = SemdiffRecordParser.ParseRecordsWithSubrecords(new byte[20], bigEndian: false, null, null);
        Assert.Empty(records);
    }

    #endregion

    #region ParseRecordsWithSubrecords — Big-Endian

    [Fact]
    public void ParseRecords_SingleRecord_BE_ParsesCorrectly()
    {
        var edidData = Encoding.ASCII.GetBytes("TestNPC\0");
        var data = BuildMinimalRecordBE("NPC_", 0x0017B37C, "EDID", edidData);

        var records = SemdiffRecordParser.ParseRecordsWithSubrecords(data, bigEndian: true, null, null);

        Assert.Single(records);
        Assert.Equal("NPC_", records[0].Type);
        Assert.Equal(0x0017B37Cu, records[0].FormId);
        Assert.Single(records[0].Subrecords);
        Assert.Equal("EDID", records[0].Subrecords[0].Signature);
    }

    #endregion

    #region CompareRecordFields — Identical Records

    [Fact]
    public void CompareRecordFields_IdenticalRecords_NoDiffs()
    {
        var subData = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var subrecords = new List<SemdiffTypes.ParsedSubrecord>
        {
            new("EDID", Encoding.ASCII.GetBytes("TestItem\0"), 24),
            new("DATA", subData, 40)
        };

        var recA = new SemdiffTypes.ParsedRecord("ALCH", 0x00010001, 0, 0, subrecords);

        // Create identical subrecords (separate byte arrays with same contents)
        var subrecordsB = new List<SemdiffTypes.ParsedSubrecord>
        {
            new("EDID", Encoding.ASCII.GetBytes("TestItem\0"), 24),
            new("DATA", (byte[])subData.Clone(), 40)
        };
        var recB = new SemdiffTypes.ParsedRecord("ALCH", 0x00010001, 0, 0, subrecordsB);

        var diffs = SemdiffRecordParser.CompareRecordFields(recA, recB, bigEndianA: false, bigEndianB: false);

        Assert.Empty(diffs);
    }

    #endregion

    #region CompareRecordFields — Different Data

    [Fact]
    public void CompareRecordFields_DifferentSubrecordData_DetectsDiff()
    {
        var subA = new List<SemdiffTypes.ParsedSubrecord>
        {
            new("DATA", new byte[] { 0x01, 0x02, 0x03, 0x04 }, 24)
        };
        var subB = new List<SemdiffTypes.ParsedSubrecord>
        {
            new("DATA", new byte[] { 0x01, 0x02, 0x03, 0xFF }, 24)
        };

        var recA = new SemdiffTypes.ParsedRecord("ALCH", 0x00010001, 0, 0, subA);
        var recB = new SemdiffTypes.ParsedRecord("ALCH", 0x00010001, 0, 0, subB);

        var diffs = SemdiffRecordParser.CompareRecordFields(recA, recB, bigEndianA: false, bigEndianB: false);

        Assert.Single(diffs);
        Assert.Equal("DATA", diffs[0].Signature);
        Assert.NotNull(diffs[0].DataA);
        Assert.NotNull(diffs[0].DataB);
    }

    #endregion

    #region CompareRecordFields — Missing Subrecords

    [Fact]
    public void CompareRecordFields_SubrecordOnlyInA_DetectsOnlyInA()
    {
        var subA = new List<SemdiffTypes.ParsedSubrecord>
        {
            new("EDID", Encoding.ASCII.GetBytes("Test\0"), 24),
            new("PNAM", new byte[] { 0x01 }, 36)
        };
        var subB = new List<SemdiffTypes.ParsedSubrecord>
        {
            new("EDID", Encoding.ASCII.GetBytes("Test\0"), 24)
        };

        var recA = new SemdiffTypes.ParsedRecord("INFO", 0x00010001, 0, 0, subA);
        var recB = new SemdiffTypes.ParsedRecord("INFO", 0x00010001, 0, 0, subB);

        var diffs = SemdiffRecordParser.CompareRecordFields(recA, recB, bigEndianA: false, bigEndianB: false);

        Assert.Single(diffs);
        Assert.Equal("PNAM", diffs[0].Signature);
        Assert.NotNull(diffs[0].DataA);
        Assert.Null(diffs[0].DataB);
        Assert.Contains("Only in A", diffs[0].Message);
    }

    [Fact]
    public void CompareRecordFields_SubrecordOnlyInB_DetectsOnlyInB()
    {
        var subA = new List<SemdiffTypes.ParsedSubrecord>
        {
            new("EDID", Encoding.ASCII.GetBytes("Test\0"), 24)
        };
        var subB = new List<SemdiffTypes.ParsedSubrecord>
        {
            new("EDID", Encoding.ASCII.GetBytes("Test\0"), 24),
            new("XSCL", new byte[] { 0x00, 0x00, 0x80, 0x3F }, 36) // float 1.0
        };

        var recA = new SemdiffTypes.ParsedRecord("REFR", 0x00010001, 0, 0, subA);
        var recB = new SemdiffTypes.ParsedRecord("REFR", 0x00010001, 0, 0, subB);

        var diffs = SemdiffRecordParser.CompareRecordFields(recA, recB, bigEndianA: false, bigEndianB: false);

        Assert.Single(diffs);
        Assert.Equal("XSCL", diffs[0].Signature);
        Assert.Null(diffs[0].DataA);
        Assert.NotNull(diffs[0].DataB);
        Assert.Contains("Only in B", diffs[0].Message);
    }

    #endregion

    #region CompareRecordFields — Multiple Instances of Same Subrecord

    [Fact]
    public void CompareRecordFields_DuplicateSubrecords_ComparesInOrder()
    {
        // Record with two CTDA subrecords (common for conditions)
        var subA = new List<SemdiffTypes.ParsedSubrecord>
        {
            new("CTDA", new byte[] { 0x01, 0x02 }, 24),
            new("CTDA", new byte[] { 0x03, 0x04 }, 32)
        };
        var subB = new List<SemdiffTypes.ParsedSubrecord>
        {
            new("CTDA", new byte[] { 0x01, 0x02 }, 24),
            new("CTDA", new byte[] { 0xFF, 0xFF }, 32) // Second CTDA differs
        };

        var recA = new SemdiffTypes.ParsedRecord("INFO", 0x00010001, 0, 0, subA);
        var recB = new SemdiffTypes.ParsedRecord("INFO", 0x00010001, 0, 0, subB);

        var diffs = SemdiffRecordParser.CompareRecordFields(recA, recB, bigEndianA: false, bigEndianB: false);

        // Only the second CTDA should differ
        Assert.Single(diffs);
        Assert.Equal("CTDA", diffs[0].Signature);
        Assert.Equal(new byte[] { 0x03, 0x04 }, diffs[0].DataA);
        Assert.Equal(new byte[] { 0xFF, 0xFF }, diffs[0].DataB);
    }

    [Fact]
    public void CompareRecordFields_UnevenDuplicateCounts_DetectsExtraInstances()
    {
        // A has 3 CTDA, B has 2 — the third in A should be "Only in A (index 2)"
        var subA = new List<SemdiffTypes.ParsedSubrecord>
        {
            new("CTDA", new byte[] { 0x01 }, 24),
            new("CTDA", new byte[] { 0x02 }, 30),
            new("CTDA", new byte[] { 0x03 }, 36)
        };
        var subB = new List<SemdiffTypes.ParsedSubrecord>
        {
            new("CTDA", new byte[] { 0x01 }, 24),
            new("CTDA", new byte[] { 0x02 }, 30)
        };

        var recA = new SemdiffTypes.ParsedRecord("INFO", 0x00010001, 0, 0, subA);
        var recB = new SemdiffTypes.ParsedRecord("INFO", 0x00010001, 0, 0, subB);

        var diffs = SemdiffRecordParser.CompareRecordFields(recA, recB, bigEndianA: false, bigEndianB: false);

        Assert.Single(diffs);
        Assert.Contains("Only in A", diffs[0].Message);
        Assert.Contains("index 2", diffs[0].Message);
    }

    #endregion

    #region End-to-End: Parse + Compare

    [Fact]
    public void ParseAndCompare_IdenticalBytes_NoDiffs()
    {
        var edidData = Encoding.ASCII.GetBytes("Stimpak\0");
        var dataPayload = new byte[] { 0x0A, 0x00, 0x00, 0x00 };
        var recordBytes = BuildRecordWithSubrecordsLE("ALCH", 0x00015169,
            ("EDID", edidData),
            ("DATA", dataPayload));

        var recordsA = SemdiffRecordParser.ParseRecordsWithSubrecords(recordBytes, false, null, null);
        var recordsB = SemdiffRecordParser.ParseRecordsWithSubrecords(
            (byte[])recordBytes.Clone(), false, null, null);

        Assert.Single(recordsA);
        Assert.Single(recordsB);

        var diffs = SemdiffRecordParser.CompareRecordFields(recordsA[0], recordsB[0], false, false);
        Assert.Empty(diffs);
    }

    [Fact]
    public void ParseAndCompare_DifferentBytes_DetectsDiff()
    {
        // Two ALCH records with different DATA payloads
        var edidData = Encoding.ASCII.GetBytes("Stimpak\0");
        var recordA = BuildRecordWithSubrecordsLE("ALCH", 0x00015169,
            ("EDID", edidData),
            ("DATA", new byte[] { 0x0A, 0x00, 0x00, 0x00 }));
        var recordB = BuildRecordWithSubrecordsLE("ALCH", 0x00015169,
            ("EDID", edidData),
            ("DATA", new byte[] { 0x14, 0x00, 0x00, 0x00 }));

        var parsedA = SemdiffRecordParser.ParseRecordsWithSubrecords(recordA, false, null, null);
        var parsedB = SemdiffRecordParser.ParseRecordsWithSubrecords(recordB, false, null, null);

        var diffs = SemdiffRecordParser.CompareRecordFields(parsedA[0], parsedB[0], false, false);

        Assert.Single(diffs);
        Assert.Equal("DATA", diffs[0].Signature);
    }

    #endregion
}
