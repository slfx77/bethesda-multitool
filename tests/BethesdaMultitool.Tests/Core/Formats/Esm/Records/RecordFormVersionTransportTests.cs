using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Coverage;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Records;

public sealed class RecordFormVersionTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlindScanner_Parses_FormVersion_At_HeaderOffset20(bool bigEndian)
    {
        var bytes = bigEndian
            ? EsmTestRecordBuilder.BuildMinimalRecordBE("WEAP", 0x1234, "EDID", "Test\0"u8.ToArray())
            : EsmTestRecordBuilder.BuildMinimalRecordLE("WEAP", 0x1234, "EDID", "Test\0"u8.ToArray());
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(20), 0x0123);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 0x0123);
        }

        var record = Assert.Single(EsmRecordScanner.ScanForRecords(bytes).MainRecords);

        Assert.Equal((ushort)0x0123, record.FormVersion);
    }

    [Fact]
    public void ParsedAndDescriptorPipelines_Preserve_Modern_FormVersion()
    {
        var record = EsmTestFileBuilder.BuildRecord("WEAP", 0x1234, 0,
            ("EDID", "Test\0"u8.ToArray()));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), 97);
        var file = new EsmTestFileBuilder().AddTopLevelGrup("WEAP", record).Build();

        var (parsed, _) = EsmParser.EnumerateRecordsWithGrups(file);
        var parsedRecord = Assert.Single(parsed, item => item.Header.Signature == "WEAP");
        Assert.Equal((ushort)97, parsedRecord.Header.FormVersion);

        var converted = EsmDataExtractor.ConvertToScanResult(parsed, false);
        Assert.Equal((ushort)97,
            Assert.Single(converted.MainRecords, item => item.RecordType == "WEAP").FormVersion);

        var descriptor = EsmDescriptorScanner.Scan(file).ScanResult;
        Assert.Equal((ushort)97,
            Assert.Single(descriptor.MainRecords, item => item.RecordType == "WEAP").FormVersion);
    }

    [Fact]
    public void Oblivion_Header_Reports_Absent_FormVersion_Not_KnownZero()
    {
        var headerBytes = new byte[20];
        Encoding.ASCII.GetBytes("WEAP", headerBytes);

        var header = EsmParser.ParseRecordHeader(headerBytes, format: PluginFormat.Oblivion);

        Assert.NotNull(header);
        Assert.Null(header.FormVersion);
    }

    [Fact]
    public void Modern_Header_Preserves_KnownZero_And_DoesNotRead_Offset22()
    {
        var headerBytes = new byte[24];
        Encoding.ASCII.GetBytes("WEAP", headerBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(headerBytes.AsSpan(20), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(headerBytes.AsSpan(22), 0xBEEF);

        var header = EsmParser.ParseRecordHeader(headerBytes, format: PluginFormat.Fnv);

        Assert.NotNull(header);
        Assert.Equal((ushort)0, header.FormVersion);
        Assert.Equal((ushort)0xBEEF, header.Version);
    }

    [Fact]
    public void BlindScanner_Uses_OblivionTwentyByte_MainRecordHeaders_ButLeavesVersionAbsent()
    {
        // A minimal 20-byte TES4 header makes PluginFormat.Detect select Oblivion framing.
        // The following WEAP begins at byte 30; a hard-coded 24-byte guard/offset either misses it or
        // reads its first subrecord four bytes late.
        var bytes = new byte[30 + 20 + 11];
        Encoding.ASCII.GetBytes("TES4", bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 10);
        Encoding.ASCII.GetBytes("HEDR", bytes.AsSpan(20));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(24), 4);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(26), 1.0f);

        const int recordOffset = 30;
        Encoding.ASCII.GetBytes("WEAP", bytes.AsSpan(recordOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(recordOffset + 4), 11);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(recordOffset + 12), 0x1234);
        Encoding.ASCII.GetBytes("EDID", bytes.AsSpan(recordOffset + 20));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(recordOffset + 24), 5);
        Encoding.ASCII.GetBytes("Test\0", bytes.AsSpan(recordOffset + 26));

        var record = Assert.Single(
            EsmRecordScanner.ScanForRecords(bytes).MainRecords,
            item => item.RecordType == "WEAP");

        Assert.Equal(recordOffset, record.Offset);
        Assert.Equal(20, record.HeaderSize);
        Assert.Null(record.FormVersion);
    }
}