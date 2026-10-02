using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Utils;
using System.Buffers.Binary;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class RecordPayloadBoundsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyRecordAtEndOfInputHasAnEmptyPayload(bool bigEndian)
    {
        var context = CreateContext();
        var result = context.ReadRecordData(new DetectedMainRecord("REFR", 0, 0x20, 0x0100459B, 8, bigEndian), []);

        Assert.NotNull(result);
        Assert.Equal(0, result.Value.Size);
        Assert.Empty(result.Value.Data);
        Assert.Empty(context.RecordReadDiagnostics);
    }

    [Theory]
    [InlineData(1u, 8L, 32L, 0L)]
    [InlineData(uint.MaxValue, 0L, 24L, 8L)]
    [InlineData(4u, long.MaxValue, null, 0L)]
    [InlineData(4u, -1L, null, 0L)]
    public void InvalidSpanReportsTheRecordAndAvailableBytes(uint size, long offset, long? dataOffset, long available)
    {
        var context = CreateContext();
        var result = context.ReadRecordData(new DetectedMainRecord("REFR", size, 0, 0x1234, offset, true), new byte[32]);

        Assert.Null(result);
        var diagnostic = Assert.Single(context.RecordReadDiagnostics);
        Assert.Equal("REFR", diagnostic.Signature);
        Assert.Equal(0x1234u, diagnostic.FormId);
        Assert.Equal(offset, diagnostic.RecordOffset);
        Assert.Equal(dataOffset, diagnostic.DataOffset);
        Assert.Equal(size, diagnostic.DeclaredLength);
        Assert.Equal(available, diagnostic.AvailableLength);
        Assert.True(diagnostic.IsBigEndian);
        Assert.Equal("record payload", diagnostic.Stage);
    }

    private static RecordParserContext CreateContext() => new(new EsmRecordScanResult(), null,
        new ByteArrayMemoryAccessor(new byte[32]), 32, null);

    [Theory]
    [InlineData(false, 6, 4u)]
    [InlineData(true, 6, 4u)]
    [InlineData(false, 16, uint.MaxValue)]
    [InlineData(true, 16, uint.MaxValue)]
    [InlineData(false, 20, 4u)]
    [InlineData(true, 20, 4u)]
    public void ExtendedSubrecordBoundsPreserveOnlyCompletePayloads(bool bigEndian, int length, uint declared)
    {
        var data = new byte[length];
        "XXXX"u8.CopyTo(data);
        if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), 4);
        else BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 4);
        if (length >= 16)
        {
            if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(6), declared);
            else BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(6), declared);
            (bigEndian ? "ATAD"u8 : "DATA"u8).CopyTo(data.AsSpan(10));
        }
        var diagnostics = new List<SubrecordReadDiagnostic>();
        var records = EsmParser.ParseSubrecords(data, bigEndian, diagnostics.Add);
        var framed = EsmSubrecordUtils.IterateSubrecords(data, data.Length, bigEndian).ToArray();
        if (length == 20)
        {
            Assert.Equal(4, Assert.Single(records).Data.Length);
            Assert.Equal(4, Assert.Single(framed).DataLength);
            Assert.Null(EsmSubrecordUtils.FindBoundsIssue(data, bigEndian));
            Assert.Empty(diagnostics);
        }
        else
        {
            Assert.Empty(records);
            Assert.Empty(framed);
            Assert.NotNull(EsmSubrecordUtils.FindBoundsIssue(data, bigEndian));
            var issue = Assert.Single(diagnostics);
            Assert.Equal(bigEndian, issue.IsBigEndian);
            Assert.Equal(0, issue.AvailableLength);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TruncatedSubrecordReportsPhysicalAndPayloadOffsetsWithoutThrowing(bool bigEndian)
    {
        var data = new byte[32];
        (bigEndian ? "ATAD"u8 : "DATA"u8).CopyTo(data.AsSpan(24));
        if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(28), 4);
        else BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), 4);
        var context = new RecordParserContext(new EsmRecordScanResult(), null,
            new ByteArrayMemoryAccessor(data), data.Length, null);
        var descriptor = new DetectedMainRecord("REFR", 8, 0, 0x1234, 0, bigEndian);
        Assert.NotNull(context.ReadRecordData(descriptor, new byte[8]));
        Assert.NotNull(context.ReadRecordData(descriptor, new byte[8]));
        var issue = Assert.Single(context.RecordReadDiagnostics);
        Assert.Equal("DATA", issue.SubrecordSignature);
        Assert.Equal(6, issue.PayloadOffset);
        Assert.Equal(30, issue.DataOffset);
        Assert.Equal(4u, issue.DeclaredLength);
        Assert.Equal(2, issue.AvailableLength);
        Assert.Equal(bigEndian, issue.IsBigEndian);
    }
}
