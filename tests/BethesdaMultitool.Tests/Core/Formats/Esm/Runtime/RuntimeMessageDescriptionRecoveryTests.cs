using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

public sealed class RuntimeMessageDescriptionRecoveryTests
{
    private const long BaseVa = 0x40000000;
    private const uint Offset = 0x100;
    private const uint FormId = 0xAF687;

    [Theory]
    [InlineData("valid", "Recovered")]
    [InlineData("signature", "Signature mismatch")]
    [InlineData("form", "FormID mismatch")]
    [InlineData("compressed", "Compressed payload not inspected")]
    [InlineData("large", "Exceeds 64 KiB inspection limit")]
    [InlineData("bounds", "Invalid subrecord bounds")]
    [InlineData("missing", "No DESC subrecord")]
    [InlineData("unterminated", "Unterminated DESC")]
    [InlineData("page-gap", "Incomplete mapped payload")]
    public void RecoveryRequiresMatchingRecordAndCompleteDescription(string scenario, string expected)
    {
        var bytes = Fixture("A radio, repaired.");
        if (scenario == "signature") bytes[Offset] = (byte)'I';
        if (scenario == "form") Write32(bytes, (int)Offset + 12, 1);
        if (scenario == "compressed") Write32(bytes, (int)Offset + 8, 0x40000);
        if (scenario == "large") Write32(bytes, (int)Offset + 4, 65537);
        if (scenario == "bounds") BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan((int)Offset + 28), 1000);
        if (scenario == "missing") Encoding.ASCII.GetBytes("TXTI").CopyTo(bytes, Offset + 24);
        if (scenario == "unterminated") bytes[Offset + 30 + "A radio, repaired.".Length] = (byte)'x';
        var context = Context(bytes, scenario == "page-gap" ? Offset + 26 : bytes.Length);
        var result = RuntimeMessageDescriptionRecovery.Recover(context, Message(), [Segment()]);
        var mapping = Assert.Single(result.RuntimeEvidence!.DescriptionMappings);
        Assert.Equal(expected, mapping.Status);
        Assert.Equal(BaseVa + Offset, mapping.RecordVirtualAddress);
        Assert.Equal((long)Offset, mapping.RecordDumpOffset);
        if (scenario == "valid")
        {
            Assert.Equal("Recovered", result.RuntimeEvidence.DescriptionStatus);
            Assert.Equal("A radio, repaired.", result.Description);
            Assert.Equal(MessageFieldSource.RuntimeMappedRecord, result.DescriptionSource);
            Assert.Equal(BaseVa + Offset + 30, mapping.DescriptionVirtualAddress);
            Assert.Equal((long)Offset + 30, mapping.DescriptionDumpOffset);
        }
        else
        {
            Assert.Equal("Unavailable", result.RuntimeEvidence.DescriptionStatus);
            Assert.Null(result.Description);
            Assert.Equal(MessageFieldSource.Unavailable, result.DescriptionSource);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultipleMappedRecordsRetainPathsAndNeverChooseConflictingText(bool disagree)
    {
        var first = Fixture("First");
        var second = Fixture(disagree ? "Other" : "First");
        var bytes = first.Concat(second).ToArray();
        var result = RuntimeMessageDescriptionRecovery.Recover(Context(bytes, bytes.Length), Message(),
            [Segment(), Segment() with { BaseVirtualAddress = BaseVa + first.Length }]);
        Assert.Equal(2, result.RuntimeEvidence!.DescriptionMappings.Count);
        Assert.All(result.RuntimeEvidence.DescriptionMappings, mapping => Assert.Equal("Recovered", mapping.Status));
        Assert.Equal(disagree ? "Ambiguous mappings" : "Recovered", result.RuntimeEvidence.DescriptionStatus);
        Assert.Equal(disagree ? null : "First", result.Description);
    }

    [Theory]
    [InlineData("Mapped", "Recovered")]
    [InlineData("Parsed", "Conflicts with parsed description")]
    public void ExistingParsedTextSurvivesAConflictingMappedCandidate(string parsed, string expected)
    {
        var bytes = Fixture("Mapped");
        var result = RuntimeMessageDescriptionRecovery.Recover(Context(bytes, bytes.Length),
            Message() with { Description = parsed }, [Segment()]);
        Assert.Equal(parsed, result.Description);
        Assert.Equal(MessageFieldSource.StoredRecord, result.DescriptionSource);
        Assert.Equal(expected, result.RuntimeEvidence!.DescriptionStatus);
        Assert.Equal("Mapped", Assert.Single(result.RuntimeEvidence.DescriptionMappings).Text);
    }

    [Theory]
    [InlineData("uncalibrated", "Uncalibrated mapping")]
    [InlineData("outside", "Outside calibrated segments")]
    [InlineData("zero", "No source-file offset")]
    public void MissingMappingIsDistinctFromMissingDescription(string scenario, string expected)
    {
        var bytes = Fixture("Text");
        var message = Message();
        if (scenario == "zero") message = message with
            { RuntimeEvidence = message.RuntimeEvidence! with { DescriptionFileOffset = 0 } };
        DialogueTesFileMappingSegment[] segments = scenario == "uncalibrated" ? [] :
            [Segment() with { MinTesFileOffset = scenario == "outside" ? Offset + 1 : Offset }];
        var result = RuntimeMessageDescriptionRecovery.Recover(Context(bytes, bytes.Length), message, segments);
        Assert.Equal(expected, result.RuntimeEvidence!.DescriptionStatus);
        Assert.Empty(result.RuntimeEvidence.DescriptionMappings);
        Assert.Null(result.Description);
    }

    private static MessageRecord Message() => new()
    {
        FormId = FormId, RuntimeEvidence = new(Offset, "file-offset-only", "complete", [])
    };

    private static DialogueTesFileMappingSegment Segment() => new()
    {
        BaseVirtualAddress = BaseVa, MinTesFileOffset = Offset, MaxTesFileOffset = Offset,
        MatchCount = 2, ExampleFormId = 0x100
    };

    private static RecordParserContext Context(byte[] bytes, long capturedSize) => new(MakeScanResult(), null,
        new ByteArrayMemoryAccessor(bytes), bytes.Length, new MinidumpInfo
        {
            IsValid = true, MemoryRegions = [new() { VirtualAddress = BaseVa, FileOffset = 0, Size = capturedSize }]
        });

    private static byte[] Fixture(string text)
    {
        var bytes = new byte[0x400];
        Encoding.ASCII.GetBytes("MESG").CopyTo(bytes, Offset);
        var data = Encoding.ASCII.GetBytes(text + "\0");
        Write32(bytes, (int)Offset + 4, (uint)(6 + data.Length));
        Write32(bytes, (int)Offset + 12, FormId);
        Encoding.ASCII.GetBytes("CSED").CopyTo(bytes, Offset + 24);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan((int)Offset + 28), (ushort)data.Length);
        data.CopyTo(bytes, Offset + 30);
        return bytes;
    }

    private static void Write32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
}
