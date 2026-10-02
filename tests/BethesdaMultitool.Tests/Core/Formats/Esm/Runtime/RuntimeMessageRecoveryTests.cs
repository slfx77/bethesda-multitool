using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Export.Csv;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.RuntimeBuffer;
using Microsoft.VisualBasic.FileIO;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

public sealed class RuntimeMessageRecoveryTests
{
    private const uint BaseVa = 0x40000000;
    private const string Label = "Repair, \"Radio\"";

    [Theory]
    [InlineData((byte)0x62)]
    [InlineData((byte)0x63)]
    public void MessageUsesTextAtZeroAndConditionsAtEight_WithCanonicalType(byte rawType)
    {
        var (data, entry) = Fixture(rawType);
        var context = Context(data);
        var message = Assert.IsType<MessageRecord>(new RuntimeMessageReader(context).ReadRuntimeMessage(entry));

        Assert.Equal(new[] { Label, "" }, message.Buttons);
        var condition = Assert.Single(message.GetButtonConditions(0));
        Assert.Equal((ushort)36, condition.FunctionIndex);
        Assert.Equal(1000u, condition.Parameter1);
        Assert.Equal(1f, condition.ComparisonValue);
        Assert.Empty(message.GetButtonConditions(1));
        Assert.Null(message.Description);
        Assert.Equal(0x12345678u, message.RuntimeEvidence!.DescriptionFileOffset);
        Assert.Contains("file-offset-only", message.RuntimeEvidence.DescriptionStatus);
        Assert.Equal("complete", message.RuntimeEvidence.ButtonListStatus);
        Assert.Equal(new[] { "captured", "empty" }, message.RuntimeEvidence.Buttons.Select(b => b.TextStatus));

        var claim = Assert.Single(RuntimeNestedStringClaimExtractor.ExtractClaims([entry], context));
        Assert.Equal(0x400L, claim.StringFileOffset);
        Assert.Equal(BaseVa + 0x180L, claim.ReferrerVa);
        Assert.Equal(0x180L, claim.ReferrerFileOffset);
        Assert.Contains("+0", claim.Validation);
    }

    [Theory]
    [InlineData("item")]
    [InlineData("text")]
    [InlineData("next")]
    [InlineData("cycle")]
    [InlineData("condition")]
    public void MissingCaptureAndCyclesRetainButtonPositionsAndDiagnostics(string failure)
    {
        var (data, entry) = Fixture(0x62);
        const uint absent = BaseVa + 0x10000;
        if (failure == "item") Write32(data, 0x80, absent);
        if (failure == "text") Write32(data, 0x180, absent);
        if (failure == "condition") Write32(data, 0x188, absent);
        if (failure == "next") Write32(data, 0x84, absent);
        if (failure == "cycle") Write32(data, 0x204, BaseVa + 0x200);
        var message = Assert.IsType<MessageRecord>(new RuntimeMessageReader(Context(data)).ReadRuntimeMessage(entry));
        var evidence = message.RuntimeEvidence!;
        if (failure == "item")
        {
            Assert.Equal(new[] { "", "" }, message.Buttons);
            Assert.Equal("item-not-captured", evidence.Buttons[0].TextStatus);
            Assert.Equal("empty", evidence.Buttons[1].TextStatus);
        }
        else if (failure == "condition")
        {
            Assert.Equal(Label, message.Buttons[0]);
            Assert.Empty(message.GetButtonConditions(0));
            Assert.Equal("partial: condition unavailable or invalid", evidence.Buttons[0].ConditionStatus);
        }
        else if (failure == "text")
        {
            Assert.Equal(2, message.Buttons.Count);
            Assert.NotEqual("empty", evidence.Buttons[0].TextStatus);
            Assert.NotEqual("captured", evidence.Buttons[0].TextStatus);
            Assert.Single(message.GetButtonConditions(0));
        }
        else
        {
            Assert.Equal(failure == "next" ? 1 : 2, message.Buttons.Count);
            Assert.Equal(failure == "next" ? "partial: node-not-captured" : "partial: cycle", evidence.ButtonListStatus);
        }
    }

    [Fact]
    public void MessageCsvPreservesQuotedTextAndExposesCaptureStatusAndButtonIndex()
    {
        var (data, entry) = Fixture(0x62);
        var message = new RuntimeMessageReader(Context(data)).ReadRuntimeMessage(entry)!;
        var csv = CsvSupplementalWriter.GenerateMessagesCsv([message], FormIdResolver.Empty);
        using var parser = new TextFieldParser(new StringReader(csv))
        {
            TextFieldType = FieldType.Delimited, Delimiters = [","], HasFieldsEnclosedInQuotes = true
        };
        var header = parser.ReadFields()!;
        var rows = new List<Dictionary<string, string>>();
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields()!;
            Assert.Equal(header.Length, fields.Length);
            rows.Add(header.Zip(fields).ToDictionary(p => p.First, p => p.Second));
        }
        Assert.Equal(3, rows.Count);
        Assert.Equal("MESG", rows[0]["RowType"]);
        Assert.Equal("0x12345678", rows[0]["RuntimeDescriptionFileOffset"]);
        Assert.Equal("BUTTON", rows[1]["RowType"]);
        Assert.Equal("0", rows[1]["ButtonIndex"]);
        Assert.Equal(Label, rows[1]["ButtonText"]);
        Assert.Equal("captured", rows[1]["ButtonTextStatus"]);
        Assert.NotEmpty(rows[1]["ButtonConditions"]);
        Assert.Equal("1", rows[2]["ButtonIndex"]);
        Assert.Equal("empty", rows[2]["ButtonTextStatus"]);
        Assert.Contains(RuntimeMessageEvidenceFormatter.Format(message.RuntimeEvidence), line => line.Contains("file-offset-only"));
    }

    private static (byte[] Data, RuntimeEditorIdEntry Entry) Fixture(byte rawType)
    {
        var data = new byte[0x600];
        // BGSMessage +64 inline list; MESSAGEBOX_BUTTON Text+0, TESCondition+8.
        data[0x44] = rawType;
        Write32(data, 0x4C, 0xAF687);
        Write32(data, 0x78, 0x12345678); // TESDescription.lFileOffset, deliberately not a VA.
        Write32(data, 0x80, BaseVa + 0x180);
        Write32(data, 0x84, BaseVa + 0x200);
        Write32(data, 0x180, BaseVa + 0x400);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x184), (ushort)Label.Length);
        Encoding.ASCII.GetBytes(Label).CopyTo(data, 0x400);
        Write32(data, 0x188, BaseVa + 0x240);
        Write32(data, 0x200, BaseVa + 0x1C0); // Second button has an explicitly empty label.
        Write32(data, 0x244, 0x3F800000); // 1.0f comparison
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x248), 36); // MenuMode(integer)
        Write32(data, 0x24C, 1000);
        return (data, new RuntimeEditorIdEntry
        {
            EditorId = "RadioMessage", FormId = 0xAF687, FormType = 0x62,
            OriginalFormType = rawType == 0x62 ? null : rawType,
            TesFormOffset = 0x40, TesFormPointer = BaseVa + 0x40
        });
    }

    private static RuntimeMemoryContext Context(byte[] data) => new(new ByteArrayMemoryAccessor(data), data.Length,
        new MinidumpInfo { IsValid = true, MemoryRegions =
            [new MinidumpMemoryRegion { VirtualAddress = BaseVa, FileOffset = 0, Size = data.Length }] });

    private static void Write32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);
}
