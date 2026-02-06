using System.IO.Compression;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Parsers;

/// <summary>
///     Tests for EsmParser — ESM file structure parsing including endianness, headers, and subrecords.
/// </summary>
public class EsmParserTests
{
    #region Helpers

    /// <summary>
    ///     Write a little-endian 4-byte subrecord signature.
    /// </summary>
    private static void WriteSig(byte[] buf, int offset, string sig)
    {
        buf[offset] = (byte)sig[0];
        buf[offset + 1] = (byte)sig[1];
        buf[offset + 2] = (byte)sig[2];
        buf[offset + 3] = (byte)sig[3];
    }

    /// <summary>
    ///     Write a big-endian 4-byte subrecord signature (reversed).
    /// </summary>
    private static void WriteSigBE(byte[] buf, int offset, string sig)
    {
        buf[offset] = (byte)sig[3];
        buf[offset + 1] = (byte)sig[2];
        buf[offset + 2] = (byte)sig[1];
        buf[offset + 3] = (byte)sig[0];
    }

    private static void WriteUInt32LE(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
        buf[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteUInt32BE(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)((value >> 24) & 0xFF);
        buf[offset + 1] = (byte)((value >> 16) & 0xFF);
        buf[offset + 2] = (byte)((value >> 8) & 0xFF);
        buf[offset + 3] = (byte)(value & 0xFF);
    }

    private static void WriteUInt16LE(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static void WriteUInt16BE(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)((value >> 8) & 0xFF);
        buf[offset + 1] = (byte)(value & 0xFF);
    }

    /// <summary>
    ///     Build a minimal LE TES4 record header (24 bytes) + HEDR subrecord.
    /// </summary>
    private static byte[] BuildLeTes4Header(float version = 1.34f, uint nextObjId = 0x001000)
    {
        // TES4 main record header (24 bytes)
        var headerSize = 24;
        var hedrSize = 12; // float + uint + uint
        var totalData = 6 + hedrSize; // HEDR subrecord header (6) + data
        var buf = new byte[headerSize + totalData];

        // Signature "TES4"
        WriteSig(buf, 0, "TES4");
        WriteUInt32LE(buf, 4, (uint)totalData); // DataSize
        // Flags, FormId, VC1, VC2 all zero

        // HEDR subrecord
        var off = headerSize;
        WriteSig(buf, off, "HEDR");
        WriteUInt16LE(buf, off + 4, (ushort)hedrSize);
        // Version float
        var versionBits = BitConverter.SingleToUInt32Bits(version);
        WriteUInt32LE(buf, off + 6, versionBits);
        // Records count (4 bytes)
        WriteUInt32LE(buf, off + 10, 0);
        // Next object ID
        WriteUInt32LE(buf, off + 14, nextObjId);

        return buf;
    }

    #endregion

    #region IsBigEndian

    [Fact]
    public void IsBigEndian_Tes4_ReturnsFalse()
    {
        byte[] data = [(byte)'T', (byte)'E', (byte)'S', (byte)'4'];
        Assert.False(EsmParser.IsBigEndian(data));
    }

    [Fact]
    public void IsBigEndian_4Set_ReturnsTrue()
    {
        byte[] data = [(byte)'4', (byte)'S', (byte)'E', (byte)'T'];
        Assert.True(EsmParser.IsBigEndian(data));
    }

    [Fact]
    public void IsBigEndian_TooShort_ReturnsFalse()
    {
        byte[] data = [(byte)'T', (byte)'E'];
        Assert.False(EsmParser.IsBigEndian(data));
    }

    [Fact]
    public void IsBigEndian_Empty_ReturnsFalse()
    {
        Assert.False(EsmParser.IsBigEndian([]));
    }

    [Fact]
    public void IsBigEndian_RandomBytes_ReturnsFalse()
    {
        byte[] data = [0xDE, 0xAD, 0xBE, 0xEF];
        Assert.False(EsmParser.IsBigEndian(data));
    }

    #endregion

    #region ParseRecordHeader

    [Fact]
    public void ParseRecordHeader_ValidLeWeap_ReturnsHeader()
    {
        var buf = new byte[24];
        WriteSig(buf, 0, "WEAP");
        WriteUInt32LE(buf, 4, 100); // DataSize
        WriteUInt32LE(buf, 8, 0); // Flags
        WriteUInt32LE(buf, 12, 0x00012345); // FormId
        WriteUInt32LE(buf, 16, 1); // VC1
        WriteUInt32LE(buf, 20, 2); // VC2

        var header = EsmParser.ParseRecordHeader(buf);

        Assert.NotNull(header);
        Assert.Equal("WEAP", header!.Signature);
        Assert.Equal(100u, header.DataSize);
        Assert.Equal(0x00012345u, header.FormId);
    }

    [Fact]
    public void ParseRecordHeader_ValidBeWeap_ReturnsHeader()
    {
        var buf = new byte[24];
        WriteSigBE(buf, 0, "WEAP");
        WriteUInt32BE(buf, 4, 100);
        WriteUInt32BE(buf, 8, 0);
        WriteUInt32BE(buf, 12, 0x00012345);
        WriteUInt32BE(buf, 16, 1);
        WriteUInt32BE(buf, 20, 2);

        var header = EsmParser.ParseRecordHeader(buf, bigEndian: true);

        Assert.NotNull(header);
        Assert.Equal("WEAP", header!.Signature);
        Assert.Equal(100u, header.DataSize);
        Assert.Equal(0x00012345u, header.FormId);
    }

    [Fact]
    public void ParseRecordHeader_TooShort_ReturnsNull()
    {
        var buf = new byte[20]; // Less than 24
        Assert.Null(EsmParser.ParseRecordHeader(buf));
    }

    [Fact]
    public void ParseRecordHeader_InvalidSignature_ReturnsNull()
    {
        var buf = new byte[24];
        buf[0] = 0x01; // Non-ASCII
        buf[1] = 0x02;
        buf[2] = 0x03;
        buf[3] = 0x04;

        Assert.Null(EsmParser.ParseRecordHeader(buf));
    }

    [Fact]
    public void ParseRecordHeader_CompressedFlag_Detected()
    {
        var buf = new byte[24];
        WriteSig(buf, 0, "REFR");
        WriteUInt32LE(buf, 4, 50);
        WriteUInt32LE(buf, 8, 0x00040000); // Compressed flag
        WriteUInt32LE(buf, 12, 0x00001234);

        var header = EsmParser.ParseRecordHeader(buf);

        Assert.NotNull(header);
        Assert.True(header!.IsCompressed);
    }

    [Fact]
    public void ParseRecordHeader_NPC_Underscore_Valid()
    {
        var buf = new byte[24];
        WriteSig(buf, 0, "NPC_");
        WriteUInt32LE(buf, 4, 200);
        WriteUInt32LE(buf, 12, 0x00005678);

        var header = EsmParser.ParseRecordHeader(buf);

        Assert.NotNull(header);
        Assert.Equal("NPC_", header!.Signature);
    }

    #endregion

    #region ParseGroupHeader

    [Fact]
    public void ParseGroupHeader_ValidLeGrup_ReturnsHeader()
    {
        var buf = new byte[24];
        WriteSig(buf, 0, "GRUP");
        WriteUInt32LE(buf, 4, 100); // GroupSize
        WriteSig(buf, 8, "WEAP"); // Label (record type for type 0)
        WriteUInt32LE(buf, 12, 0); // GroupType = 0 (top level)
        WriteUInt32LE(buf, 16, 0); // Stamp

        var header = EsmParser.ParseGroupHeader(buf);

        Assert.NotNull(header);
        Assert.Equal(100u, header!.GroupSize);
        Assert.Equal(0, header.GroupType);
        Assert.Equal("WEAP", header.LabelAsSignature);
    }

    [Fact]
    public void ParseGroupHeader_NotGrup_ReturnsNull()
    {
        var buf = new byte[24];
        WriteSig(buf, 0, "WEAP"); // Not GRUP
        Assert.Null(EsmParser.ParseGroupHeader(buf));
    }

    [Fact]
    public void ParseGroupHeader_TooShort_ReturnsNull()
    {
        var buf = new byte[20];
        Assert.Null(EsmParser.ParseGroupHeader(buf));
    }

    #endregion

    #region ParseSubrecords

    [Fact]
    public void ParseSubrecords_SingleEdid_ReturnsSingleSubrecord()
    {
        // EDID subrecord: sig(4) + len(2) + data
        var edidText = "TestItem\0";
        var edidBytes = Encoding.ASCII.GetBytes(edidText);
        var buf = new byte[6 + edidBytes.Length];
        WriteSig(buf, 0, "EDID");
        WriteUInt16LE(buf, 4, (ushort)edidBytes.Length);
        Array.Copy(edidBytes, 0, buf, 6, edidBytes.Length);

        var subs = EsmParser.ParseSubrecords(buf);

        Assert.Single(subs);
        Assert.Equal("EDID", subs[0].Signature);
        Assert.Equal("TestItem", subs[0].DataAsString);
    }

    [Fact]
    public void ParseSubrecords_MultipleSubrecords_ReturnsAll()
    {
        // EDID + DATA chain
        var edid = "Item\0"u8.ToArray();
        var data = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        var buf = new byte[6 + edid.Length + 6 + data.Length];
        var off = 0;

        // EDID
        WriteSig(buf, off, "EDID");
        WriteUInt16LE(buf, off + 4, (ushort)edid.Length);
        Array.Copy(edid, 0, buf, off + 6, edid.Length);
        off += 6 + edid.Length;

        // DATA
        WriteSig(buf, off, "DATA");
        WriteUInt16LE(buf, off + 4, (ushort)data.Length);
        Array.Copy(data, 0, buf, off + 6, data.Length);

        var subs = EsmParser.ParseSubrecords(buf);

        Assert.Equal(2, subs.Count);
        Assert.Equal("EDID", subs[0].Signature);
        Assert.Equal("DATA", subs[1].Signature);
        Assert.Equal(4, subs[1].Data.Length);
    }

    [Fact]
    public void ParseSubrecords_BeSubrecords_ReturnsCorrectly()
    {
        var edid = "Test\0"u8.ToArray();
        var buf = new byte[6 + edid.Length];
        WriteSigBE(buf, 0, "EDID");
        WriteUInt16BE(buf, 4, (ushort)edid.Length);
        Array.Copy(edid, 0, buf, 6, edid.Length);

        var subs = EsmParser.ParseSubrecords(buf, bigEndian: true);

        Assert.Single(subs);
        Assert.Equal("EDID", subs[0].Signature);
    }

    [Fact]
    public void ParseSubrecords_XxxxExtendedSize_HandlesCorrectly()
    {
        // XXXX marker: sig(4) + len=4(2) + extSize(4)
        // Then: next subrecord with extended size
        var bigData = new byte[300];
        for (var i = 0; i < bigData.Length; i++) bigData[i] = (byte)(i % 256);

        var buf = new byte[10 + 6 + bigData.Length]; // XXXX(10) + next subrecord header(6) + data
        var off = 0;

        // XXXX marker
        WriteSig(buf, off, "XXXX");
        WriteUInt16LE(buf, off + 4, 4); // len = 4
        WriteUInt32LE(buf, off + 6, (uint)bigData.Length); // extended size
        off += 10;

        // Next subrecord (DATA with extended size)
        WriteSig(buf, off, "DATA");
        WriteUInt16LE(buf, off + 4, 0); // This length is overridden by XXXX
        off += 6;

        Array.Copy(bigData, 0, buf, off, bigData.Length);

        var subs = EsmParser.ParseSubrecords(buf);

        Assert.Single(subs);
        Assert.Equal("DATA", subs[0].Signature);
        Assert.Equal(300, subs[0].Data.Length);
    }

    [Fact]
    public void ParseSubrecords_Empty_ReturnsEmpty()
    {
        var subs = EsmParser.ParseSubrecords([]);
        Assert.Empty(subs);
    }

    [Fact]
    public void ParseSubrecords_InvalidSignature_StopsEarly()
    {
        var buf = new byte[12];
        // First subrecord with non-ASCII signature
        buf[0] = 0x01;
        buf[1] = 0x02;
        buf[2] = 0x03;
        buf[3] = 0x04;

        var subs = EsmParser.ParseSubrecords(buf);
        Assert.Empty(subs);
    }

    [Fact]
    public void ParseSubrecords_TruncatedData_StopsGracefully()
    {
        var buf = new byte[8];
        WriteSig(buf, 0, "EDID");
        WriteUInt16LE(buf, 4, 100); // Claims 100 bytes but only 2 available

        var subs = EsmParser.ParseSubrecords(buf);
        Assert.Empty(subs);
    }

    #endregion

    #region ParseFileHeader

    [Fact]
    public void ParseFileHeader_ValidLe_ReturnsHeader()
    {
        var buf = BuildLeTes4Header(version: 1.34f, nextObjId: 0x001000);
        var header = EsmParser.ParseFileHeader(buf);

        Assert.NotNull(header);
        Assert.False(header!.IsBigEndian);
        Assert.Equal(1.34f, header.Version, 0.01f);
        Assert.Equal(0x001000u, header.NextObjectId);
    }

    [Fact]
    public void ParseFileHeader_WithAuthor_ReturnsAuthor()
    {
        // Build TES4 with HEDR + CNAM
        var author = "TestAuthor\0"u8.ToArray();
        var hedrSize = 12;
        var totalData = (6 + hedrSize) + (6 + author.Length);
        var buf = new byte[24 + totalData];

        WriteSig(buf, 0, "TES4");
        WriteUInt32LE(buf, 4, (uint)totalData);

        // HEDR
        var off = 24;
        WriteSig(buf, off, "HEDR");
        WriteUInt16LE(buf, off + 4, (ushort)hedrSize);
        WriteUInt32LE(buf, off + 6, BitConverter.SingleToUInt32Bits(1.34f));
        off += 6 + hedrSize;

        // CNAM (author)
        WriteSig(buf, off, "CNAM");
        WriteUInt16LE(buf, off + 4, (ushort)author.Length);
        Array.Copy(author, 0, buf, off + 6, author.Length);

        var header = EsmParser.ParseFileHeader(buf);

        Assert.NotNull(header);
        Assert.Equal("TestAuthor", header!.Author);
    }

    [Fact]
    public void ParseFileHeader_TooShort_ReturnsNull()
    {
        Assert.Null(EsmParser.ParseFileHeader(new byte[10]));
    }

    [Fact]
    public void ParseFileHeader_NotTes4_ReturnsNull()
    {
        var buf = new byte[24];
        WriteSig(buf, 0, "WEAP"); // Not TES4
        Assert.Null(EsmParser.ParseFileHeader(buf));
    }

    #endregion

    #region EnumerateRecords

    [Fact]
    public void EnumerateRecords_Tes4PlusGrupPlusRecord_ReturnsRecord()
    {
        // Build: TES4 header + GRUP(WEAP) containing one WEAP record
        var hedrSize = 12;
        var tes4DataSize = 6 + hedrSize;
        var tes4TotalSize = 24 + tes4DataSize;

        // WEAP record: header(24) + EDID subrecord
        var edid = "TestWeap\0"u8.ToArray();
        var weapDataSize = 6 + edid.Length;
        var weapTotalSize = 24 + weapDataSize;

        // GRUP header(24) + WEAP record
        var grupSize = 24 + weapTotalSize;

        var buf = new byte[tes4TotalSize + grupSize];

        // TES4 header
        WriteSig(buf, 0, "TES4");
        WriteUInt32LE(buf, 4, (uint)tes4DataSize);
        var off = 24;
        WriteSig(buf, off, "HEDR");
        WriteUInt16LE(buf, off + 4, (ushort)hedrSize);
        WriteUInt32LE(buf, off + 6, BitConverter.SingleToUInt32Bits(1.34f));
        off += 6 + hedrSize;

        // GRUP header
        WriteSig(buf, off, "GRUP");
        WriteUInt32LE(buf, off + 4, (uint)grupSize);
        WriteSig(buf, off + 8, "WEAP"); // Label
        WriteUInt32LE(buf, off + 12, 0); // GroupType = 0
        off += 24;

        // WEAP record
        WriteSig(buf, off, "WEAP");
        WriteUInt32LE(buf, off + 4, (uint)weapDataSize);
        WriteUInt32LE(buf, off + 12, 0x00012345); // FormId
        off += 24;

        // EDID subrecord
        WriteSig(buf, off, "EDID");
        WriteUInt16LE(buf, off + 4, (ushort)edid.Length);
        Array.Copy(edid, 0, buf, off + 6, edid.Length);

        var records = EsmParser.EnumerateRecords(buf);

        Assert.Single(records);
        Assert.Equal("WEAP", records[0].Header.Signature);
        Assert.Equal(0x00012345u, records[0].Header.FormId);
        Assert.Equal("TestWeap", records[0].EditorId);
    }

    [Fact]
    public void EnumerateRecords_InvalidTes4_ReturnsEmpty()
    {
        var buf = new byte[48];
        WriteSig(buf, 0, "WEAP"); // Not TES4
        Assert.Empty(EsmParser.EnumerateRecords(buf));
    }

    #endregion

    #region ScanRecords

    [Fact]
    public void ScanRecords_ValidFile_ReturnsTes4AndRecords()
    {
        // Similar to EnumerateRecords but lighter
        var hedrSize = 12;
        var tes4DataSize = 6 + hedrSize;

        // WEAP record with minimal data
        var weapDataSize = 4;
        var grupSize = 24 + 24 + weapDataSize;

        var buf = new byte[24 + tes4DataSize + grupSize];

        // TES4
        WriteSig(buf, 0, "TES4");
        WriteUInt32LE(buf, 4, (uint)tes4DataSize);
        var off = 24;
        WriteSig(buf, off, "HEDR");
        WriteUInt16LE(buf, off + 4, (ushort)hedrSize);
        off += 6 + hedrSize;

        // GRUP
        WriteSig(buf, off, "GRUP");
        WriteUInt32LE(buf, off + 4, (uint)grupSize);
        WriteSig(buf, off + 8, "WEAP");
        off += 24;

        // WEAP
        WriteSig(buf, off, "WEAP");
        WriteUInt32LE(buf, off + 4, (uint)weapDataSize);
        WriteUInt32LE(buf, off + 12, 0x00001234);

        var records = EsmParser.ScanRecords(buf);

        Assert.True(records.Count >= 2); // TES4 + at least one record
        Assert.Equal("TES4", records[0].Signature);
    }

    [Fact]
    public void ScanRecords_InvalidFile_ReturnsEmpty()
    {
        Assert.Empty(EsmParser.ScanRecords(new byte[10]));
    }

    #endregion

    #region GetRecordTypeCounts

    [Fact]
    public void GetRecordTypeCounts_CountsCorrectly()
    {
        var hedrSize = 12;
        var tes4DataSize = 6 + hedrSize;
        var recordDataSize = 4;
        var recordTotalSize = 24 + recordDataSize;
        var grupSize = 24 + recordTotalSize * 2; // Two WEAP records

        var buf = new byte[24 + tes4DataSize + grupSize];

        // TES4
        WriteSig(buf, 0, "TES4");
        WriteUInt32LE(buf, 4, (uint)tes4DataSize);
        var off = 24;
        WriteSig(buf, off, "HEDR");
        WriteUInt16LE(buf, off + 4, (ushort)hedrSize);
        off += 6 + hedrSize;

        // GRUP
        WriteSig(buf, off, "GRUP");
        WriteUInt32LE(buf, off + 4, (uint)grupSize);
        WriteSig(buf, off + 8, "WEAP");
        off += 24;

        // First WEAP
        WriteSig(buf, off, "WEAP");
        WriteUInt32LE(buf, off + 4, (uint)recordDataSize);
        WriteUInt32LE(buf, off + 12, 0x00001111);
        off += recordTotalSize;

        // Second WEAP
        WriteSig(buf, off, "WEAP");
        WriteUInt32LE(buf, off + 4, (uint)recordDataSize);
        WriteUInt32LE(buf, off + 12, 0x00002222);

        var counts = EsmParser.GetRecordTypeCounts(buf);

        Assert.True(counts.ContainsKey("TES4"));
        Assert.Equal(1, counts["TES4"]);
        Assert.True(counts.ContainsKey("WEAP"));
        Assert.Equal(2, counts["WEAP"]);
    }

    #endregion

    #region DecompressRecordData

    [Fact]
    public void DecompressRecordData_TooShort_ReturnsNull()
    {
        Assert.Null(EsmParser.DecompressRecordData(new byte[3], false));
    }

    [Fact]
    public void DecompressRecordData_ValidCompressed_Decompresses()
    {
        // Create compressed data: [4 bytes: decompressed size] [zlib data]
        var original = Encoding.ASCII.GetBytes("Hello World! This is test data for compression.");

        using var ms = new MemoryStream();
        // Write decompressed size (LE)
        ms.Write(BitConverter.GetBytes((uint)original.Length));
        // Write zlib compressed data
        using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(original);
        }

        var compressed = ms.ToArray();
        var result = EsmParser.DecompressRecordData(compressed, false);

        Assert.NotNull(result);
        Assert.Equal(original, result);
    }

    [Fact]
    public void DecompressRecordData_InvalidZlib_ReturnsNull()
    {
        byte[] data =
        [
            0x10, 0x00, 0x00, 0x00, // Decompressed size = 16
            0xDE, 0xAD, 0xBE, 0xEF, 0xFF // Invalid zlib data
        ];
        Assert.Null(EsmParser.DecompressRecordData(data, false));
    }

    [Fact]
    public void DecompressRecordData_HugeSize_ReturnsNull()
    {
        byte[] data =
        [
            0xFF, 0xFF, 0xFF, 0x7F, // > 16MB decompressed size
            0x00
        ];
        Assert.Null(EsmParser.DecompressRecordData(data, false));
    }

    #endregion

    #region Constants

    [Fact]
    public void MainRecordHeaderSize_Is24()
    {
        Assert.Equal(24, EsmParser.MainRecordHeaderSize);
    }

    [Fact]
    public void SubrecordHeaderSize_Is6()
    {
        Assert.Equal(6, EsmParser.SubrecordHeaderSize);
    }

    [Fact]
    public void CompressedFlag_IsCorrect()
    {
        Assert.Equal(0x00040000u, EsmParser.CompressedFlag);
    }

    #endregion
}
