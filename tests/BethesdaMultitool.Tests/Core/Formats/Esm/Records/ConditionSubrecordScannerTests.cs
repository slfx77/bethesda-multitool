using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Coverage;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Minidump;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Records;

public sealed class ConditionSubrecordScannerTests
{
    public static TheoryData<int, bool> SupportedWidthsAndEndianness => new()
    {
        { 20, false },
        { 24, false },
        { 28, false },
        { 32, false },
        { 20, true },
        { 24, true },
        { 28, true },
        { 32, true }
    };

    [Theory]
    [MemberData(nameof(SupportedWidthsAndEndianness))]
    public void ByteScanner_MatchesStructuredDecoder_AndPreservesOnlyPhysicalTailFields(
        int bodyLength,
        bool bigEndian)
    {
        var bytes = BuildCtda(bodyLength, bigEndian);

        var blind = Assert.Single(EsmRecordScanner.ScanForRecords(bytes).Conditions);
        var structured = EsmDataExtractor.ExtractCondition(bytes.AsSpan(6, bodyLength).ToArray(), 0, bigEndian);

        Assert.Equal(structured, blind);
        Assert.Equal((byte)5, blind.Operator);
        Assert.Equal(0x3FA00000u, blind.ComparisonRawBits);
        Assert.Equal(1.25f, blind.ComparisonValue);
        Assert.Equal((ushort)0x0294, blind.FunctionIndex);
        Assert.Equal(0x11223344u, blind.Param1);
        Assert.Equal(0x55667788u, blind.Param2);
        Assert.Equal(bodyLength >= 24 ? 7u : null, blind.RunOn);
        Assert.Equal(bodyLength >= 28 ? 0x12345678u : null, blind.ReferenceStorage);
        Assert.Equal(bodyLength >= 32 ? -42 : null, blind.Parameter3);
    }

    [Theory]
    [MemberData(nameof(SupportedWidthsAndEndianness))]
    public void MemoryMappedScanner_MatchesByteScanner_ForCanonicalAndReversedCtda(
        int bodyLength,
        bool bigEndian)
    {
        var bytes = BuildCtda(bodyLength, bigEndian);

        var byteCondition = Assert.Single(EsmRecordScanner.ScanForRecords(bytes).Conditions);
        var mappedCondition = Assert.Single(ScanViaMemoryMappedFile(bytes).Conditions);

        Assert.Equal(byteCondition, mappedCondition);
    }

    [Theory]
    [InlineData(5000, false)]
    [InlineData(5000, true)]
    [InlineData(12004, false)]
    [InlineData(12004, true)]
    public void BlindScanners_AcceptExactFallout76HighConditionIndices(int functionIndex, bool bigEndian)
    {
        var bytes = BuildCtda(32, bigEndian, functionIndex: (ushort)functionIndex);

        var byteCondition = Assert.Single(EsmRecordScanner.ScanForRecords(bytes).Conditions);
        var mappedCondition = Assert.Single(ScanViaMemoryMappedFile(bytes).Conditions);

        Assert.Equal((ushort)functionIndex, byteCondition.FunctionIndex);
        Assert.Equal(byteCondition, mappedCondition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlindScanners_RejectUnmappedHighConditionIndex(bool bigEndian)
    {
        var bytes = BuildCtda(32, bigEndian, functionIndex: 12005);

        Assert.Empty(EsmRecordScanner.ScanForRecords(bytes).Conditions);
        Assert.Empty(ScanViaMemoryMappedFile(bytes).Conditions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlindScanners_UseGlobal_PreserveRawFormIdBitsWithoutFloatPlausibilityRejection(bool bigEndian)
    {
        const uint rawGlobalBits = 0x7FC01234;
        var bytes = BuildCtda(32, bigEndian, 0xA4, rawGlobalBits);

        var byteCondition = Assert.Single(EsmRecordScanner.ScanForRecords(bytes).Conditions);
        var mappedCondition = Assert.Single(ScanViaMemoryMappedFile(bytes).Conditions);

        Assert.Equal(byteCondition, mappedCondition);
        Assert.True(byteCondition.UsesGlobalComparison);
        Assert.Equal(rawGlobalBits, byteCondition.ComparisonRawBits);
        Assert.Equal(rawGlobalBits, byteCondition.ComparisonGlobalFormId);
        Assert.Null(byteCondition.NumericComparisonValue);
        Assert.True(float.IsNaN(byteCondition.ComparisonValue));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlindScanners_ClaimedThirtyTwoByteBodyTruncatedByOneByte_IsRejected(bool bigEndian)
    {
        var complete = BuildCtda(32, bigEndian);
        var truncated = complete[..^1];

        Assert.Empty(EsmRecordScanner.ScanForRecords(truncated).Conditions);
        Assert.Empty(ScanViaMemoryMappedFile(truncated).Conditions);
    }

    [Theory]
    [InlineData(21)]
    [InlineData(25)]
    [InlineData(29)]
    [InlineData(31)]
    [InlineData(33)]
    public void UnsupportedBodyWidth_IsNotSilentlyDecoded(int bodyLength)
    {
        var bytes = BuildCtda(bodyLength, false);

        Assert.Empty(EsmRecordScanner.ScanForRecords(bytes).Conditions);
        Assert.Throws<ArgumentException>(() =>
            EsmDataExtractor.ExtractCondition(bytes.AsSpan(6, bodyLength).ToArray(), 0, false));
    }

    [Fact]
    public void SemanticDumper_RendersUseGlobalAndPresentTailStorageWithoutInventingNumericComparison()
    {
        const uint rawGlobalBits = 0x7FC01234;
        var bytes = BuildCtda(32, false, 0xA4, rawGlobalBits);
        var condition = Assert.Single(EsmRecordScanner.ScanForRecords(bytes).Conditions);
        var result = new AnalysisResult
        {
            FilePath = "condition.dmp",
            EsmRecords = new EsmRecordScanResult { Conditions = [condition] }
        };

        var dump = MinidumpSemanticDumper.GenerateSemanticDump(result);

        Assert.Contains("<= GLOB[0x7FC01234]", dump, StringComparison.Ordinal);
        Assert.Contains("RunOn=7", dump, StringComparison.Ordinal);
        Assert.Contains("ReferenceStorage=0x12345678", dump, StringComparison.Ordinal);
        Assert.Contains("Parameter3=-42", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", dump, StringComparison.Ordinal);
    }

    private static byte[] BuildCtda(
        int bodyLength,
        bool bigEndian,
        byte type = 0xA0,
        uint comparisonRawBits = 0x3FA00000,
        ushort functionIndex = 0x0294)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bodyLength, 20);

        var bytes = new byte[6 + bodyLength];
        Encoding.ASCII.GetBytes(bigEndian ? "ADTC" : "CTDA").CopyTo(bytes, 0);
        WriteUInt16(bytes.AsSpan(4, 2), (ushort)bodyLength, bigEndian);

        var body = bytes.AsSpan(6, bodyLength);
        body[0] = type;
        body[1] = 0xEE; // Padding must never be mistaken for the operator.
        body[2] = 0xDD;
        body[3] = 0xCC;
        WriteUInt32(body[4..8], comparisonRawBits, bigEndian);
        WriteUInt16(body[8..10], functionIndex, bigEndian);
        body[10] = 0xBB;
        body[11] = 0xAA;
        WriteUInt32(body[12..16], 0x11223344, bigEndian);
        WriteUInt32(body[16..20], 0x55667788, bigEndian);

        if (bodyLength >= 24)
        {
            WriteUInt32(body[20..24], 7, bigEndian);
        }

        if (bodyLength >= 28)
        {
            WriteUInt32(body[24..28], 0x12345678, bigEndian);
        }

        if (bodyLength >= 32)
        {
            WriteInt32(body[28..32], -42, bigEndian);
        }

        return bytes;
    }

    private static void WriteUInt16(Span<byte> destination, ushort value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(destination, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(destination, value);
        }
    }

    private static void WriteUInt32(Span<byte> destination, uint value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
        }
    }

    private static void WriteInt32(Span<byte> destination, int value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteInt32BigEndian(destination, value);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(destination, value);
        }
    }

    private static EsmRecordScanResult ScanViaMemoryMappedFile(byte[] data)
    {
        using var mmf = MemoryMappedFile.CreateNew(null, data.Length);
        using var accessor = mmf.CreateViewAccessor(0, data.Length);
        accessor.WriteArray(0, data, 0, data.Length);
        return EsmRecordScanner.ScanForRecordsMemoryMapped(accessor, data.Length);
    }
}