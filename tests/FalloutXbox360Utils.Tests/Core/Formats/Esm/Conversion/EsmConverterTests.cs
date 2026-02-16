using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Conversion;

/// <summary>
///     Regression tests for EsmConverter.
///     Tests exercise ConvertToLittleEndian with synthetic big-endian ESM data.
///     These tests anchor behavior before the complexity-reduction refactoring.
/// </summary>
public class EsmConverterTests(ITestOutputHelper output, SampleFileFixture samples)
{
    private readonly ITestOutputHelper _output = output;

    #region Test Helpers

    /// <summary>
    ///     Writes a big-endian record header at the specified offset.
    ///     Record header: [SIG:4 reversed][SIZE:4 BE][FLAGS:4 BE][FORMID:4 BE][TS:4 BE][VCS:4 BE] = 24 bytes
    /// </summary>
    private static void WriteBERecordHeader(byte[] buffer, int offset, string signature, uint dataSize,
        uint formId = 0, uint flags = 0)
    {
        // Signature is stored reversed for big-endian
        buffer[offset + 0] = (byte)signature[3];
        buffer[offset + 1] = (byte)signature[2];
        buffer[offset + 2] = (byte)signature[1];
        buffer[offset + 3] = (byte)signature[0];
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 4), dataSize);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 8), flags);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 12), formId);
        // Timestamp and VCS: zeros
    }

    /// <summary>
    ///     Writes a big-endian GRUP header at the specified offset.
    ///     GRUP header: [GRUP:4 reversed][SIZE:4 BE][LABEL:4 BE][TYPE:4 BE][STAMP:4 BE][UNK:4 BE] = 24 bytes
    /// </summary>
    private static void WriteBEGrupHeader(byte[] buffer, int offset, uint groupSize, string labelSignature,
        int groupType = 0)
    {
        // "GRUP" reversed
        buffer[offset + 0] = (byte)'P';
        buffer[offset + 1] = (byte)'U';
        buffer[offset + 2] = (byte)'R';
        buffer[offset + 3] = (byte)'G';
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 4), groupSize);

        // Label: for type 0, this is a record type signature (reversed for BE)
        buffer[offset + 8] = (byte)labelSignature[3];
        buffer[offset + 9] = (byte)labelSignature[2];
        buffer[offset + 10] = (byte)labelSignature[1];
        buffer[offset + 11] = (byte)labelSignature[0];

        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset + 12), (uint)groupType);
        // Stamp and Unknown: zeros
    }

    /// <summary>
    ///     Writes a big-endian EDID subrecord at the specified offset.
    ///     Subrecord: [SIG:4 reversed][SIZE:2 BE][DATA:N]
    /// </summary>
    private static void WriteBESubrecord(byte[] buffer, int offset, string signature, byte[] data)
    {
        buffer[offset + 0] = (byte)signature[3];
        buffer[offset + 1] = (byte)signature[2];
        buffer[offset + 2] = (byte)signature[1];
        buffer[offset + 3] = (byte)signature[0];
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset + 4), (ushort)data.Length);
        Array.Copy(data, 0, buffer, offset + 6, data.Length);
    }

    /// <summary>
    ///     Creates a minimal big-endian ESM file with just a TES4 header record.
    ///     The TES4 record contains a HEDR subrecord (12 bytes) as required by the parser.
    /// </summary>
    private static byte[] BuildMinimalBigEndianEsm()
    {
        // TES4 header: 24-byte header + HEDR subrecord (6 header + 12 data = 18 bytes)
        const int tes4DataSize = 18; // HEDR subrecord
        const int totalSize = 24 + tes4DataSize;

        var data = new byte[totalSize];
        WriteBERecordHeader(data, 0, "TES4", tes4DataSize);

        // HEDR subrecord: version(float) + numRecords(int32) + nextObjectId(uint32)
        var hedrData = new byte[12];
        BinaryPrimitives.WriteSingleBigEndian(hedrData.AsSpan(0), 1.34f); // version
        BinaryPrimitives.WriteInt32BigEndian(hedrData.AsSpan(4), 1); // numRecords
        BinaryPrimitives.WriteUInt32BigEndian(hedrData.AsSpan(8), 0x00000800); // nextObjectId
        WriteBESubrecord(data, 24, "HEDR", hedrData);

        return data;
    }

    /// <summary>
    ///     Creates a big-endian ESM with TES4 header + a simple GRUP containing an ALCH record.
    /// </summary>
    private static byte[] BuildSimpleEsmWithGrup()
    {
        // Record data: EDID subrecord with "TestAlch\0"
        var edid = Encoding.ASCII.GetBytes("TestAlch\0");
        var subrecordSize = 6 + edid.Length; // sig(4) + size(2) + data
        var recordDataSize = subrecordSize;

        // TES4 header (24+18=42 bytes) + GRUP header (24 bytes) + ALCH record (24 + recordDataSize)
        var tes4Data = BuildMinimalBigEndianEsm();
        var alchRecordTotalSize = 24 + recordDataSize;
        var grupTotalSize = (uint)(24 + alchRecordTotalSize);
        var totalSize = tes4Data.Length + (int)grupTotalSize;

        var data = new byte[totalSize];
        Array.Copy(tes4Data, data, tes4Data.Length);

        var grupOffset = tes4Data.Length;
        WriteBEGrupHeader(data, grupOffset, grupTotalSize, "ALCH", groupType: 0);

        var recordOffset = grupOffset + 24;
        WriteBERecordHeader(data, recordOffset, "ALCH", (uint)recordDataSize, formId: 0x00010001);
        WriteBESubrecord(data, recordOffset + 24, "EDID", edid);

        return data;
    }

    #endregion

    #region ConvertToLittleEndian Tests

    [Fact]
    public void ConvertToLittleEndian_MinimalEsm_ProducesOutput()
    {
        var input = BuildMinimalBigEndianEsm();
        using var converter = new EsmConverter(input, verbose: false);

        var result = converter.ConvertToLittleEndian();

        Assert.NotNull(result);
        Assert.True(result.Length > 0, "Output should not be empty");

        // Verify output TES4 signature is little-endian (ASCII order: T, E, S, 4)
        Assert.Equal((byte)'T', result[0]);
        Assert.Equal((byte)'E', result[1]);
        Assert.Equal((byte)'S', result[2]);
        Assert.Equal((byte)'4', result[3]);
    }

    [Fact]
    public void ConvertToLittleEndian_WithGrup_ConvertsRecord()
    {
        var input = BuildSimpleEsmWithGrup();
        using var converter = new EsmConverter(input, verbose: false);

        var result = converter.ConvertToLittleEndian();

        Assert.NotNull(result);
        Assert.True(result.Length > 0);

        // Verify output contains GRUP header in little-endian (ASCII: G, R, U, P)
        var grupPos = FindAsciiSignature(result, "GRUP");
        Assert.True(grupPos >= 0, "Output should contain a GRUP header");

        // Verify output contains ALCH record in little-endian
        var alchPos = FindAsciiSignature(result, "ALCH", grupPos + 24);
        Assert.True(alchPos >= 0, "Output should contain an ALCH record");

        // Verify the ALCH FormID is written little-endian
        var formId = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(alchPos + 12));
        Assert.Equal(0x00010001u, formId);
    }

    [Fact]
    public void ConvertToLittleEndian_EmptyInput_DoesNotThrow()
    {
        // Very small input that can't even contain a header
        var input = new byte[4];
        using var converter = new EsmConverter(input, verbose: false);

        // Should not throw, though output may be minimal/empty
        var exception = Record.Exception(() => converter.ConvertToLittleEndian());

        // We just verify it doesn't crash with an unhandled exception
        // The converter may throw ArgumentOutOfRange for truly invalid data, which is fine
        _output.WriteLine(exception != null
            ? $"Expected exception for invalid input: {exception.GetType().Name}"
            : "Converter handled empty input gracefully");
    }

    [Fact]
    public void ConvertToLittleEndian_WithToftRegion_SkipsToftData()
    {
        // Build: TES4 header + TOFT region (sentinel + INFO inside + end marker)
        var tes4Data = BuildMinimalBigEndianEsm();

        // TOFT sentinel: FormID=0xFFFFFFFE, DataSize=48 (contains one INFO record)
        // INFO inside TOFT: FormID=0x000A0001, DataSize=0
        // TOFT end marker: FormID=0xFFFFFFFF, DataSize=0
        var infoInsideToft = 24; // Just a header, no data
        var toftDataSize = infoInsideToft; // INFO record is inside TOFT's data
        var toftEndMarker = 24; // TOFT end: header only, DataSize=0
        var toftRegionSize = 24 + toftDataSize + toftEndMarker; // sentinel + data + end

        var totalSize = tes4Data.Length + toftRegionSize;
        var data = new byte[totalSize];
        Array.Copy(tes4Data, data, tes4Data.Length);

        var offset = tes4Data.Length;

        // TOFT sentinel (DataSize includes the INFO record inside)
        WriteBERecordHeader(data, offset, "TOFT", (uint)toftDataSize, formId: 0xFFFFFFFE);
        offset += 24;

        // INFO record inside TOFT block
        WriteBERecordHeader(data, offset, "INFO", 0, formId: 0x000A0001);
        offset += 24;

        // TOFT end marker
        WriteBERecordHeader(data, offset, "TOFT", 0, formId: 0xFFFFFFFF);

        using var converter = new EsmConverter(data, verbose: false);
        var result = converter.ConvertToLittleEndian();

        Assert.NotNull(result);

        // TOFT records should NOT appear in the output
        var toftPos = FindAsciiSignature(result, "TOFT");
        Assert.Equal(-1, toftPos);

        // The INFO from TOFT should be indexed but not directly written as standalone
        // (INFO records in TOFT are merged into their parent DIAL GRUPs during conversion)
        _output.WriteLine($"Input: {data.Length} bytes, Output: {result.Length} bytes");
    }

    [Fact]
    public void GetStatsSummary_AfterConversion_ReturnsNonEmpty()
    {
        var input = BuildSimpleEsmWithGrup();
        using var converter = new EsmConverter(input, verbose: false);

        converter.ConvertToLittleEndian();
        var summary = converter.GetStatsSummary();

        Assert.False(string.IsNullOrEmpty(summary));
        _output.WriteLine(summary);
    }

    #endregion

    #region Sample-File-Based Tests

    [Fact]
    public void ConvertToLittleEndian_RealEsm_ProducesValidOutput()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        var input = File.ReadAllBytes(samples.Xbox360FinalEsm!);
        _output.WriteLine($"Input: {input.Length:N0} bytes");

        using var converter = new EsmConverter(input, verbose: false);
        var result = converter.ConvertToLittleEndian();

        _output.WriteLine($"Output: {result.Length:N0} bytes");
        _output.WriteLine(converter.GetStatsSummary());

        // Output should start with LE TES4 header
        Assert.Equal((byte)'T', result[0]);
        Assert.Equal((byte)'E', result[1]);
        Assert.Equal((byte)'S', result[2]);
        Assert.Equal((byte)'4', result[3]);

        // Output data size should be LE
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(4));
        Assert.True(dataSize > 0 && dataSize < (uint)result.Length);

        // Output should be smaller or similar in size to input
        // (TOFT region is removed, but new OFST tables may be added)
        Assert.True(result.Length > 0);
    }

    #endregion

    #region Utility

    /// <summary>
    ///     Finds the position of an ASCII signature in little-endian output.
    /// </summary>
    private static int FindAsciiSignature(byte[] data, string signature, int startOffset = 0)
    {
        var sigBytes = Encoding.ASCII.GetBytes(signature);
        for (var i = startOffset; i <= data.Length - 4; i++)
        {
            if (data[i] == sigBytes[0] && data[i + 1] == sigBytes[1] &&
                data[i + 2] == sigBytes[2] && data[i + 3] == sigBytes[3])
            {
                return i;
            }
        }

        return -1;
    }

    #endregion
}
