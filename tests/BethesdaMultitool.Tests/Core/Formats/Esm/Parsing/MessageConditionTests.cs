using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.CLI.Commands.Analysis;
using BethesdaMultitool.CLI.Shared;
using BethesdaMultitool.CLI.Show;
using BethesdaMultitool.Core.Formats.Esm.Export.Csv;
using BethesdaMultitool.Core.Formats.Esm.Export.Geck;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Presentation;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Semantic;
using Microsoft.VisualBasic.FileIO;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class MessageConditionTests
{
    private const uint MessageId = 0x00134506;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParsedButtons_KeepEmptySlotsConditionOrderAndStringOwnership(bool bigEndian)
    {
        var message = Parse(bigEndian,
            ("CTDA", Condition(bigEndian, 72, 0x01000055)),
            ("ITXT", NullTermString("Leave It Alone")),
            ("ITXT", NullTermString(string.Empty)),
            ("CTDA", Condition(bigEndian, 72, 0x01000066)),
            ("CIS1", NullTermString("[literal]")),
            ("ITXT", NullTermString("Repair Turret")),
            ("CIS2", NullTermString("stale string")),
            ("CTDA", Condition(bigEndian, 14, 39, 45, 0x61)),
            ("CTDA", new byte[27]),
            ("CIS1", NullTermString("must not attach")),
            ("CTDA", Condition(bigEndian, 72, 0x01000077)));

        Assert.Equal(["Leave It Alone", "", "Repair Turret"], message.Buttons);
        Assert.Equal(0x01000055u, Assert.Single(message.UnassignedConditions).Parameter1);
        Assert.Empty(message.GetButtonConditions(0));
        var emptyButtonCondition = Assert.Single(message.GetButtonConditions(1));
        Assert.Equal("[literal]", emptyButtonCondition.Parameter1String);
        Assert.Null(emptyButtonCondition.Parameter2String);
        var repairConditions = message.GetButtonConditions(2);
        Assert.Equal(2, repairConditions.Count);
        Assert.Equal((ushort)14, repairConditions[0].FunctionIndex);
        Assert.Equal(39u, repairConditions[0].Parameter1);
        Assert.Equal(45f, repairConditions[0].ComparisonValue);
        Assert.True(repairConditions[0].IsOr);
        Assert.Null(repairConditions[0].Parameter1String);
        Assert.Equal(0x01000077u, repairConditions[1].Parameter1);

        // Ambiguous ownership must not silently become an unconditional button in an emitted plugin.
        var rejected = MesgEncoder.EncodeNew(message);
        Assert.Empty(rejected.Subrecords);
        Assert.Contains(rejected.Warnings, warning => warning.Contains("no known button owner", StringComparison.Ordinal));

        var encoded = MesgEncoder.EncodeNew(message with { UnassignedConditions = [] });
        Assert.Equal(["EDID", "ITXT", "ITXT", "CTDA", "CIS1", "ITXT", "CTDA", "CTDA"],
            encoded.Subrecords.Select(sub => sub.Signature));
        var encodedRepair = encoded.Subrecords[6].Bytes;
        Assert.Equal(0x61, encodedRepair[0]);
        Assert.Equal(45f, BinaryPrimitives.ReadSingleLittleEndian(encodedRepair.AsSpan(4)));
        Assert.Equal(39u, BinaryPrimitives.ReadUInt32LittleEndian(encodedRepair.AsSpan(12)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShowAndReport_AssociateCompleteConditionsWithTheirButton(bool fullText)
    {
        var message = Parse(false,
            ("CTDA", Condition(false, 72, 0x01000088)),
            ("ITXT", NullTermString("Leave It Alone")),
            ("ITXT", NullTermString("Repair [Turret]")),
            // Exact retail repair check, plus a synthetic OR alternative to exercise grouping.
            ("CTDA", Condition(false, 14, 39, 45, 0x61)),
            ("CTDA", Condition(false, 72, 0x01000077, 1, runOn: 1)));
        var records = new RecordCollection { Game = BethesdaGame.FalloutNewVegas, Messages = [message] };
        var resolver = new FormIdResolver(new Dictionary<uint, string>
        {
            [0x01000077] = "[Alternative]"
        }, []);
        var rendered = false;
        var output = CliHelpers.CaptureSpectreOutput(console =>
        {
            console.Profile.Width = 800;
            rendered = ShowCommand.TryRender(records, resolver, MessageId, null,
                new ShowRenderContext(console, FullText: fullText));
        });
        Assert.True(rendered);
        var report = GeckTextContentWriter.GenerateMessagesReport([message], resolver,
            ConditionDisplayContext.From(records, resolver));

        foreach (var text in new[] { output, report })
        {
            Assert.Contains("Unassigned conditions (before first button; ownership unknown)", text);
            var first = text.IndexOf("[1] Leave It Alone", StringComparison.Ordinal);
            var second = text.IndexOf("[2] Repair [Turret]", StringComparison.Ordinal);
            Assert.True(first >= 0 && second > first);
            Assert.Contains("Conditions (0):", text[first..second]);
            Assert.DoesNotContain("GetActorValue", text[first..second]);
            Assert.Contains("GetActorValue(Repair) >= 45 [Run On: Subject] OR", text[second..]);
            Assert.Contains("GetIsID([Alternative] [0x01000077]) == 1 [Run On: Target]", text[second..]);
            Assert.Contains("Grouping: 1 OR 2 (GECK convention:", text[second..]);
            Assert.DoesNotContain("assumed", text);
        }
    }

    [Fact]
    public void LoadOrderRebase_MapsNestedMessageFormOperandsWithoutChangingScalarsOrSource()
    {
        var source = new RecordCollection
        {
            Game = BethesdaGame.FalloutNewVegas,
            Messages = [new MessageRecord
            {
                FormId = 0x01001000,
                Buttons = ["Repair"],
                ButtonConditions = [[
                    new() { FunctionIndex = 14, Parameter1 = 39, ComparisonValue = 45 },
                    new() { FunctionIndex = 72, Parameter1 = 0x01000077 }
                ]],
                UnassignedConditions = [new() { FunctionIndex = 72, Parameter1 = 0x01000088 }],
                RuntimeButtons = new()
                {
                    ObjectFileOffset = 0x01000099,
                    Buttons = ["Runtime repair"],
                    ButtonConditions = [[new() { FunctionIndex = 72, Parameter1 = 0x01000066 }]]
                }
            }]
        };
        var rebased = Assert.Single(RecordCollectionFormIdRebaser.Rebase(source,
            id => (id & 0x00FFFFFF) | 0x02000000).Messages);
        Assert.Equal(39u, rebased.ButtonConditions[0][0].Parameter1);
        Assert.Equal(45f, rebased.ButtonConditions[0][0].ComparisonValue);
        Assert.Equal(0x02000077u, rebased.ButtonConditions[0][1].Parameter1);
        Assert.Equal(0x02000088u, rebased.UnassignedConditions[0].Parameter1);
        Assert.Equal(0x01000077u, source.Messages[0].ButtonConditions[0][1].Parameter1);
        Assert.Equal(0x02000066u, rebased.RuntimeButtons!.ButtonConditions[0][0].Parameter1);
        Assert.Equal(0x01000099, rebased.RuntimeButtons.ObjectFileOffset);
        Assert.Equal(0x01000066u, source.Messages[0].RuntimeButtons!.ButtonConditions[0][0].Parameter1);
    }

    [Theory]
    [InlineData("different-count")]
    [InlineData("different-text")]
    [InlineData("reordered")]
    [InlineData("different-condition")]
    [InlineData("matching")]
    [InlineData("empty-stored")]
    public void RuntimeMerge_ExportsCompleteSeparatelySourcedButtonRepresentations(string scenario)
    {
        var stored = new MessageRecord
        {
            FormId = MessageId, EditorId = "FilesMenu", Offset = 0x100,
            Description = "Stored description", Buttons = ["Leave", "Pacer"],
            ButtonConditions = [[], [new() { FunctionIndex = 72, Parameter1 = 0x100 }]]
        };
        var runtime = RuntimeMessage(0x900);
        if (scenario == "different-count") stored = stored with { Buttons = ["Leave"], ButtonConditions = [[]] };
        if (scenario == "different-text") stored = stored with { Buttons = ["Exit", "Patient"] };
        if (scenario == "reordered") stored = stored with { Buttons = ["Pacer", "Leave"] };
        if (scenario == "different-condition") stored = stored with { ButtonConditions = [] };
        if (scenario == "empty-stored") stored = stored with { Buttons = [], ButtonConditions = [] };
        if (scenario == "matching") stored = stored with { Offset = 0 };
        var merged = MessageRuntimeMerger.Merge(stored, runtime);
        var usesRuntime = scenario == "empty-stored";
        Assert.Equal(stored.Buttons.Count, merged.StoredButtonCount);
        Assert.Equal("Stored description", merged.Description);
        Assert.Equal(MessageFieldSource.StoredRecord, merged.DescriptionSource);
        Assert.Equal(usesRuntime ? runtime.Buttons : stored.Buttons, merged.Buttons);
        Assert.Same(usesRuntime ? runtime.ButtonConditions : stored.ButtonConditions, merged.ButtonConditions);
        Assert.Same(runtime.RuntimeButtons, merged.RuntimeButtons);
        Assert.Equal(stored.Offset, merged.Offset);
        Assert.False(merged.IsBigEndian);

        var rows = ReadMessageCsv(merged);
        Assert.Equal("stored-record", rows[0]["DescriptionSource"]);
        Assert.Equal(stored.Offset.ToString(), rows[0]["Offset"]);
        Assert.Equal("2304", rows[0]["RuntimeObjectOffset"]);
        Assert.Equal(stored.Buttons.Count.ToString(), rows[0]["StoredButtonCount"]);
        var selected = rows.Where(row => row["RowType"] == "BUTTON").ToList();
        Assert.Equal(merged.Buttons.Count, selected.Count);
        Assert.All(selected, row =>
        {
            Assert.Equal(usesRuntime ? "runtime-object" : "stored-record", row["ButtonSource"]);
            Assert.Equal(usesRuntime ? "2304" : stored.Offset.ToString(), row["ButtonSourceOffset"]);
            Assert.Equal(usesRuntime ? "captured" : "", row["ButtonTextStatus"]);
            Assert.Equal(usesRuntime ? "complete" : "", row["ButtonConditionStatus"]);
            Assert.Equal(usesRuntime, row["ButtonItemVA"].Length != 0);
        });
        var alternatives = rows.Where(row => row["RowType"] == "RUNTIME_BUTTON").ToList();
        Assert.Equal(usesRuntime ? 0 : 2, alternatives.Count);
        if (!usesRuntime)
        {
            Assert.Equal(["Leave", "Pacer"], alternatives.Select(row => row["ButtonText"]));
            Assert.All(alternatives, row => Assert.Equal("captured", row["ButtonTextStatus"]));
            Assert.Equal("0x51000020", alternatives[1]["ButtonItemVA"]);
            Assert.NotEmpty(alternatives[1]["ButtonConditions"]);
        }
        if (scenario == "different-condition") Assert.All(selected, row => Assert.Contains("Conditions (0)", row["ButtonConditions"]));

        var records = new RecordCollection { Game = BethesdaGame.FalloutNewVegas, Messages = [merged] };
        Assert.True(RecordDetailPresenter.TryBuildForRecord(merged, records, FormIdResolver.Empty, out var detail));
        Assert.NotNull(detail);
        var selectedFields = detail.Sections.Where(section => section.Title.StartsWith("Selected button [", StringComparison.Ordinal))
            .SelectMany(section => section.Entries).ToList();
        Assert.Contains(selectedFields, entry => entry.Label == "Source" && entry.Value == (usesRuntime ? "runtime-object" : "stored-record"));
        if (!usesRuntime) Assert.DoesNotContain(selectedFields, entry => entry.Label == "Item VA" && !string.IsNullOrEmpty(entry.Value));
        Assert.Equal(!usesRuntime, detail.Sections.Any(section => section.Title.StartsWith("Runtime alternative @ 0x900", StringComparison.Ordinal)));
        var output = CliHelpers.CaptureSpectreOutput(console =>
        {
            console.Profile.Width = 800;
            Assert.True(ShowCommand.TryRender(records, FormIdResolver.Empty, MessageId, null,
                new ShowRenderContext(console, FullText: true)));
        });
        var report = GeckTextContentWriter.GenerateMessagesReport([merged], FormIdResolver.Empty,
            ConditionDisplayContext.From(records, FormIdResolver.Empty));
        foreach (var text in new[] { output, report })
        {
            Assert.Contains("Description source: stored-record", text);
            Assert.Contains("Buttons source: " + (usesRuntime ? "runtime-object" : "stored-record"), text);
            Assert.Contains("Stored button count: " + stored.Buttons.Count, text);
            Assert.Contains("Stored description", text);
            Assert.Equal(!usesRuntime, text.Contains("Runtime alternative (2 buttons)", StringComparison.Ordinal));
            Assert.Equal(1, text.Split("Runtime button list: complete", StringSplitOptions.None).Length - 1);
        }
    }

    [Fact]
    public void RepeatedRuntimeOccurrences_DoNotReplaceSelectedButtonEvidence()
    {
        var first = RuntimeMessage(0x900);
        var second = RuntimeMessage(0xA00) with { Buttons = ["Other"] };
        second = second with { RuntimeButtons = second.RuntimeButtons! with { Buttons = second.Buttons } };
        var merged = MessageRuntimeMerger.Merge(first, second);
        Assert.Same(first.Buttons, merged.Buttons);
        Assert.Same(first.RuntimeEvidence, merged.RuntimeEvidence);
        Assert.Same(first.RuntimeButtons, merged.RuntimeButtons);
        Assert.Same(second.RuntimeButtons, Assert.Single(merged.AdditionalRuntimeButtons));
        Assert.Single(MessageRuntimeMerger.Merge(merged, second).AdditionalRuntimeButtons);
        var rows = ReadMessageCsv(merged);
        Assert.Equal(2, rows.Count(row => row["RowType"] == "BUTTON"));
        var alternative = Assert.Single(rows.Where(row => row["RowType"] == "RUNTIME_BUTTON"));
        Assert.Equal("Other", alternative["ButtonText"]);
        Assert.Equal("2560", alternative["RuntimeObjectOffset"]);
    }

    private static MessageRecord RuntimeMessage(long offset)
    {
        List<string> buttons = ["Leave", "Pacer"];
        List<List<DialogueCondition>> conditions = [[], [new() { FunctionIndex = 72, Parameter1 = 0x100 }]];
        var evidence = new RuntimeMessageEvidence(0x1234, "Outside calibrated segments", "complete",
        [
            new(0, 0x51000010, 0x110, 0x51001000, 0x1000, "captured", "complete"),
            new(1, 0x51000020, 0x120, 0x51002000, 0x2000, "captured", "complete")
        ]);
        return new MessageRecord
        {
            FormId = MessageId, Offset = offset, IsBigEndian = true, Buttons = buttons, ButtonConditions = conditions,
            ButtonSource = MessageFieldSource.RuntimeObject, DescriptionSource = MessageFieldSource.Unavailable,
            RuntimeEvidence = evidence,
            RuntimeButtons = new() { ObjectFileOffset = offset, Buttons = buttons, ButtonConditions = conditions, Evidence = evidence }
        };
    }

    private static List<Dictionary<string, string>> ReadMessageCsv(MessageRecord message)
    {
        using var parser = new TextFieldParser(new StringReader(CsvSupplementalWriter.GenerateMessagesCsv([message], FormIdResolver.Empty)))
        { TextFieldType = FieldType.Delimited, Delimiters = [","], HasFieldsEnclosedInQuotes = true };
        var header = parser.ReadFields()!;
        var rows = new List<Dictionary<string, string>>();
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields()!;
            Assert.Equal(header.Length, fields.Length);
            rows.Add(header.Zip(fields).ToDictionary(pair => pair.First, pair => pair.Second));
        }
        return rows;
    }

    private static MessageRecord Parse(bool bigEndian, params (string Sig, byte[] Data)[] subrecords)
    {
        var bytes = BuildRecordBytes(MessageId, "MESG", bigEndian,
            [("EDID", NullTermString("vHDTurretMessageNCR01")), .. subrecords]);
        using var mmf = MemoryMappedFile.CreateNew(null, bytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, bytes.Length);
        accessor.WriteArray(0, bytes, 0, bytes.Length);
        var scan = MakeScanResult([new DetectedMainRecord("MESG", (uint)(bytes.Length - 24), 0,
            MessageId, 0, bigEndian)]);
        scan.Game = BethesdaGame.FalloutNewVegas;
        var parser = new RecordParser(scan, accessor: accessor, fileSize: bytes.Length);
        return Assert.Single(parser.ParseMessages());
    }

    private static byte[] Condition(bool bigEndian, ushort function, uint parameter,
        float comparison = 1, byte type = 0, uint runOn = 0)
    {
        var data = new byte[28];
        data[0] = type;
        if (bigEndian)
        {
            BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(4), comparison);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8), function);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), parameter);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), runOn);
        }
        else
        {
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), comparison);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), function);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), parameter);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), runOn);
        }

        return data;
    }
}
