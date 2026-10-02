using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Export.Geck;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Presentation;
using BethesdaMultitool.Core.Formats.Esm.RecordModel;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Schema;
using BethesdaMultitool.Core.Games;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Presentation;

/// <summary>
///     <c>show</c> on an INFO record. Before this, an INFO had no typed builder and fell through to the generic
///     identity panel: no conditions, no responses beyond the first, no result scripts and no DATA flags (the
///     FO3/FNV parser never read DATA, so every plugin INFO reported flags 0).
///     <para>
///         The records are synthetic INFO bytes run through the real parser, so the DATA read, the result-script
///         slot map and the presenter are exercised together. Expected text is written out literally; flag and
///         enum names are xEdit's, as the generated FalloutNvSchema/Fallout3Schema carry them.
///     </para>
/// </summary>
public sealed class InfoRecordDetailTests
{
    [Fact]
    public void DialogTopic_DoesNotSilentlyOmitResponsesBeyondTwenty()
    {
        var records = new RecordCollection
        {
            Game = BethesdaGame.FalloutNewVegas,
            DialogTopics = [new() { FormId = LinkedTopicFormId }],
            Dialogues = Enumerable.Range(1, 25).Select(i => new DialogueRecord
            {
                FormId = (uint)i, TopicFormId = LinkedTopicFormId
            }).ToList()
        };
        Assert.True(RecordDetailPresenter.TryBuildForLookup(records, Resolver, LinkedTopicFormId, null, out var built));
        var model = Assert.IsType<RecordDetailModel>(built);
        var section = Assert.Single(model.Sections, value => value.Title == "INFO Records");
        Assert.Equal(25, section.Entries.SelectMany(entry => entry.Items ?? []).Count());
    }

    [Theory]
    [InlineData(BethesdaGame.Oblivion)]
    [InlineData(BethesdaGame.Skyrim)]
    [InlineData(BethesdaGame.Fallout4)]
    [InlineData(BethesdaGame.Fallout76)]
    [InlineData(BethesdaGame.Morrowind)]
    public void OtherGames_InfoFallsThroughToSchemaRenderer(BethesdaGame game)
    {
        var records = new RecordCollection { Game = game, Dialogues = [new() { FormId = InfoFormId }] };
        Assert.False(RecordDetailPresenter.TryBuildForLookup(records, Resolver, InfoFormId, null, out _));
    }

    private const uint InfoFormId = 0x0010E001;
    private const uint QuestFormId = 0x0010AAAA;
    private const uint SpeakerFormId = 0x0010BBBB;
    private const uint LinkedTopicFormId = 0x0010CCCC;
    private const uint AddedTopicFormId = 0x0010DDDD;
    private const uint PlayerRef = 0x00000014;

    private const string EndScriptSource = "set iCount to 1\r\nplayer.additem Caps001 10";

    private const string NoCompiledCodeEmptySchr =
        "no compiled code (the SCHR header declares 0 compiled bytes; the block has no SCDA, SCTX, locals or references)";

    private const string DecompiledLabel =
        "Reconstruction (SCDA)";

    private static readonly FormIdResolver Resolver = new(
        new Dictionary<uint, string>
        {
            [QuestFormId] = "SyntheticQuest",
            [SpeakerFormId] = "SyntheticSpeaker",
            [LinkedTopicFormId] = "SyntheticTopicB"
        },
        []);

    [Fact]
    public void TryBuildForLookup_Info_EmitsConditionsResponsesResultScriptsAndDataFlags()
    {
        var info = Assert.Single(ParseInfo(FullInfoRecord()));
        var records = FnvRecords(info);

        Assert.True(RecordDetailPresenter.TryBuildForLookup(records, Resolver, InfoFormId, null, out var built));
        var model = Assert.IsType<RecordDetailModel>(built);

        Assert.Equal("INFO", model.RecordSignature);
        Assert.Equal("SyntheticInfo", model.EditorId);
        Assert.Equal("Little-Endian (PC)", Value(model, "Identity", "Container Byte Order"));

        Assert.Equal("SyntheticQuest [0x0010AAAA]", Value(model, "Relationships", "Quest"));
        Assert.Equal("SyntheticSpeaker [0x0010BBBB]", Value(model, "Relationships", "Speaker"));

        Assert.Equal("Conversation (1)", Value(model, "Data", "Type"));
        Assert.Equal("Target (0)", Value(model, "Data", "Next Speaker"));
        Assert.Equal("0x05 (Goodbye, Say Once)", Value(model, "Data", "Flags 1"));
        Assert.Equal("0x31 (Say Once a Day, Low Intelligence, High Intelligence)", Value(model, "Data", "Flags 2"));

        var conditions = ListItems(model, "Conditions", "Conditions");
        Assert.Equal(["1", "2"], conditions.Select(item => item.Label).ToArray());
        Assert.Equal("GetIsID(SyntheticSpeaker [0x0010BBBB]) == 1 [Run On: Subject] AND", conditions[0].Value);
        Assert.Equal(
            "GetQuestVariable(SyntheticQuest [0x0010AAAA], iTimesAsked [var 11]) >= 1 [Run On: Subject]",
            conditions[1].Value);

        Assert.Equal("1", Value(model, "Responses", "Response 1 Number"));
        Assert.Equal("Happy (+50)", Value(model, "Responses", "Response 1 Emotion"));
        Assert.Equal("First line, said in full.", Value(model, "Responses", "Response 1 Text"));
        Assert.Equal("2", Value(model, "Responses", "Response 2 Number"));
        Assert.Equal("Neutral (0)", Value(model, "Responses", "Response 2 Emotion"));
        Assert.Equal("Second line.", Value(model, "Responses", "Response 2 Text"));

        Assert.Equal(
            ["SyntheticTopicB [0x0010CCCC]"],
            ListItems(model, "Links", "Link To Topics (TCLT)").Select(item => item.Label).ToArray());
        Assert.Equal(
            ["0x0010DDDD (no EditorID)"],
            ListItems(model, "Links", "Add Topics (NAME)").Select(item => item.Label).ToArray());

        Assert.Equal(NoCompiledCodeEmptySchr, Value(model, "Result Scripts", "Result Script (Begin)"));
        Assert.Equal("4 bytes of compiled code (SCDA)", Value(model, "Result Scripts", "Result Script (End)"));
        Assert.Equal("plugin-record", Value(model, "Result Scripts", "Result Script (End) Provenance"));
        Assert.Equal(EndScriptSource, Value(model, "Result Scripts",
            "Result Script (End): Source (SCTX)"));
        Assert.False(string.IsNullOrWhiteSpace(Value(model, "Result Scripts",
            $"Result Script (End): {DecompiledLabel}")));
        Assert.Equal("4 bytes", Value(model, "Result Scripts", "Result Script (End) Compiled Size"));
        Assert.StartsWith("Little-Endian", Value(model, "Result Scripts", "Result Script (End) Bytecode Order"),
            StringComparison.Ordinal);

        var references = ListItems(model, "Result Scripts", "Result Script (End) References");
        Assert.Equal(["1", "2"], references.Select(item => item.Label).ToArray());
        Assert.Equal(["PlayerRef (0x00000014)", "local #1 (iCount)"], references.Select(item => item.Value).ToArray());
        Assert.Equal(
            ["ref iCount (scrv-local-reference; conflicts-with-integer-storage; storage type byte 1)"],
            ListItems(model, "Result Scripts", "Result Script (End) Variables").Select(item => item.Value).ToArray());
    }

    [Fact]
    public void ParsedData_IsPresentationOnly_AndNeverFeedsTheConversionFlags()
    {
        var info = Assert.Single(ParseInfo(FullInfoRecord()));

        var data = Assert.IsType<InfoSerializedData>(info.SerializedInfoData);
        Assert.Equal(4, data.DataLength);
        Assert.Equal<byte>(1, data.InfoType);
        Assert.Equal<byte?>(0, data.NextSpeaker);
        Assert.Equal<byte?>(0x05, data.Flags1);
        Assert.Equal<byte?>(0x31, data.Flags2);

        // InfoFlags/InfoFlagsExt drive dmp to-esm (goodbye detection, the INFO encoder); DATA must not reach them.
        Assert.Equal<byte>(0, info.InfoFlags);
        Assert.Equal<byte>(0, info.InfoFlagsExt);
    }

    [Fact]
    public void EmptyResultScriptBlocks_SayNoCompiledCode()
    {
        // The retail shape of INFO 0x0015E9CC: SCHR > NEXT > SCHR > ANAM, both headers declaring nothing.
        var recordBytes = BuildRecordBytes(InfoFormId, "INFO", false,
            ("EDID", NullTermString("SyntheticInfo")),
            ("DATA", new byte[] { 0x00, 0x00, 0x00, 0x00 }),
            ("QSTI", FormId(QuestFormId)),
            ("CTDA", Ctda(0x00, 1f, 0x0048, SpeakerFormId, 0)),
            ("SCHR", new byte[20]),
            ("NEXT", Array.Empty<byte>()),
            ("SCHR", new byte[20]),
            ("ANAM", FormId(SpeakerFormId)));
        var info = Assert.Single(ParseInfo(recordBytes));

        Assert.True(info.HasResultScript);
        Assert.Empty(info.ResultScripts);
        Assert.Equal(2, info.ResultScriptBlocks.Count);
        Assert.All(info.ResultScriptBlocks, block => Assert.Null(block.ResultScriptIndex));

        Assert.True(RecordDetailPresenter.TryBuildForLookup(FnvRecords(info), Resolver, InfoFormId, null, out var built));
        var model = Assert.IsType<RecordDetailModel>(built);

        var section = Section(model, "Result Scripts");
        Assert.Equal(
            ["Result Script (Begin)", "Result Script (End)"],
            section.Entries.Select(entry => entry.Label).ToArray());
        Assert.All(section.Entries, entry => Assert.Equal(NoCompiledCodeEmptySchr, entry.Value));
    }

    [Fact]
    public void DecompiledSubstituteIsNeverLabelledOriginal()
    {
        // A dump-recovered block whose SCTX slot holds text BethesdaMultitool decompiled from SCDA.
        var info = new DialogueRecord
        {
            FormId = InfoFormId,
            EditorId = "DumpInfo",
            ResultScripts =
            [
                new DialogueResultScript
                {
                    SourceText = "; reconstructed\r\nset iCount to 1",
                    SourceTextOrigin = ScriptSourceTextOrigin.DecompiledFromBytecode,
                    IsDmpDerived = true,
                    DecompiledText = "set iCount to 1",
                    CompiledData = [0x1D, 0x00, 0x00, 0x00]
                }
            ]
        };

        Assert.True(RecordDetailPresenter.TryBuildForLookup(
            FnvRecords(info), Resolver, InfoFormId, null, out var built, isMemoryDumpInput: true));
        var section = Section(Assert.IsType<RecordDetailModel>(built), "Result Scripts");

        Assert.Equal("decompiled-from-bytecode", Value(section, "Result Script Provenance"));
        Assert.Equal("; reconstructed\r\nset iCount to 1", Value(section,
            "Result Script: Reconstruction (SCDA)"));
        Assert.Single(section.Entries, entry => entry.Kind == RecordDetailEntryKind.CodeBlock);
        Assert.DoesNotContain(section.Entries, entry =>
            entry.Label.Contains("authored", StringComparison.OrdinalIgnoreCase) ||
            entry.Label.Contains("Source (SCTX", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ScriptSourceTextOrigin.None, false,
        "Source (SCTX)", "plugin-record")]
    [InlineData(ScriptSourceTextOrigin.None, true,
        "Recovered source (unattributed)",
        "unattributed-same-dump (correspondence: not-recorded)")]
    [InlineData(ScriptSourceTextOrigin.DmpFragment, true,
        "Recovered source (dump fragment)", "dmp-fragment (correspondence: not-recorded)")]
    public void ResultScriptSource_IsLabelledByItsProvenance(
        ScriptSourceTextOrigin origin,
        bool isMemoryDumpInput,
        string expectedHeading,
        string expectedProvenance)
    {
        var info = new DialogueRecord
        {
            FormId = InfoFormId,
            ResultScripts = [new DialogueResultScript { SourceText = "set iCount to 1", SourceTextOrigin = origin }]
        };

        Assert.True(RecordDetailPresenter.TryBuildForLookup(
            FnvRecords(info), Resolver, InfoFormId, null, out var built, isMemoryDumpInput));
        var section = Section(Assert.IsType<RecordDetailModel>(built), "Result Scripts");

        Assert.Equal("set iCount to 1", Value(section, $"Result Script: {expectedHeading}"));
        Assert.Equal(expectedProvenance, Value(section, "Result Script Provenance"));
        Assert.Equal("no compiled code (the block holds source text but no SCDA)", Value(section, "Result Script"));
    }

    [Theory]
    [InlineData(false, "none (no SCHR in this record)")]
    [InlineData(true,
        "none recovered: not present in this capture; absence from a partial memory dump is not evidence of absence from the build")]
    public void NoResultScripts_DumpAbsenceIsNotClaimedAsBuildAbsence(bool isMemoryDumpInput, string expected)
    {
        var info = new DialogueRecord { FormId = InfoFormId };

        Assert.True(RecordDetailPresenter.TryBuildForLookup(
            FnvRecords(info), Resolver, InfoFormId, null, out var built, isMemoryDumpInput));

        Assert.Equal(expected, Value(Assert.IsType<RecordDetailModel>(built), "Result Scripts", "Result Scripts"));
    }

    [Theory]
    [InlineData(BethesdaGame.FalloutNewVegas, "0x31 (Say Once a Day, Low Intelligence, High Intelligence)")]
    [InlineData(BethesdaGame.Fallout3, "0x31 (Say Once a Day, bit 4, bit 5)")]
    public void Flags2_AreNamedFromTheGamesOwnSchema(BethesdaGame game, string expected)
    {
        var info = new DialogueRecord
        {
            FormId = InfoFormId,
            SerializedInfoData = InfoSerializedData.TryRead([0x00, 0x00, 0x00, 0x31])
        };
        var records = new RecordCollection { Game = game, Dialogues = [info] };

        Assert.True(RecordDetailPresenter.TryBuildForLookup(records, Resolver, InfoFormId, null, out var built));

        Assert.Equal(expected, Value(Assert.IsType<RecordDetailModel>(built), "Data", "Flags 2"));
    }

    [Theory]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    [InlineData(BethesdaGame.Fallout3)]
    public void InfoDataLayout_IsTheGeneratedSchemasFourByteStruct(BethesdaGame game)
    {
        // InfoSerializedData reads fixed offsets; this pins them to the xEdit-generated DATA struct.
        var info = Assert.IsType<RecordDef>(EsmSchemas.IndexForGame(game)?["INFO"]);
        var data = Assert.Single(info.Members.OfType<StructDef>(), member => member.Signature == "DATA");

        Assert.NotNull(data.Members);
        var fields = data.Members.Select(member => Assert.IsType<FieldDef>(member)).ToArray();
        Assert.Equal(new string?[] { "Type", "Next Speaker", "Flags 1", "Flags 2" }, fields.Select(field => field.Name).ToArray());
        Assert.All(fields, field => Assert.Equal(PrimType.U8, field.Type));
        Assert.Equal(0, InfoSerializedData.TypeOffset);
        Assert.Equal(1, InfoSerializedData.NextSpeakerOffset);
        Assert.Equal(2, InfoSerializedData.Flags1Offset);
        Assert.Equal(3, InfoSerializedData.Flags2Offset);
    }

    [Fact]
    public void SerializedData_ReadsTheSameFromABigEndianContainer()
    {
        var recordBytes = BuildRecordBytes(InfoFormId, "INFO", true,
            ("EDID", NullTermString("SyntheticInfoX360")),
            ("DATA", new byte[] { 0x02, 0x01, 0x80, 0x02 }));

        var info = Assert.Single(ParseInfo(recordBytes, true));

        var data = Assert.IsType<InfoSerializedData>(info.SerializedInfoData);
        Assert.Equal<byte>(2, data.InfoType);
        Assert.Equal<byte?>(1, data.NextSpeaker);
        Assert.Equal<byte?>(0x80, data.Flags1);
        Assert.Equal<byte?>(0x02, data.Flags2);
        Assert.True(info.IsBigEndian);
    }

    [Fact]
    public void SplitInfoMerge_KeepsTheDataOfTheHalfThatCarriedIt()
    {
        var baseHalf = new DialogueRecord
        {
            FormId = InfoFormId,
            SerializedInfoData = InfoSerializedData.TryRead([0x01, 0x02, 0x01, 0x00])
        };
        var responseHalf = new DialogueRecord
        {
            FormId = InfoFormId,
            Responses = [new DialogueResponse { Text = "Hello there", ResponseNumber = 1 }]
        };

        var merged = Assert.Single(DialogueConditionParser.MergeSplitInfoRecords([responseHalf, baseHalf]));

        Assert.Equal("Hello there", Assert.Single(merged.Responses).Text);
        var data = Assert.IsType<InfoSerializedData>(merged.SerializedInfoData);
        Assert.Equal<byte>(0x01, data.InfoType);
        Assert.Equal<byte?>(0x02, data.NextSpeaker);
        Assert.Equal<byte?>(0x01, data.Flags1);
        Assert.Equal<byte>(0, merged.InfoFlags);
    }

    [Fact]
    public void ShortData_KeepsTheFieldsItHas_AndEmptyDataIsAbsent()
    {
        var shortData = Assert.IsType<InfoSerializedData>(InfoSerializedData.TryRead([0x06, 0x02]));

        Assert.Equal<byte>(6, shortData.InfoType);
        Assert.Equal<byte?>(2, shortData.NextSpeaker);
        Assert.Null(shortData.Flags1);
        Assert.Null(shortData.Flags2);
        Assert.True(shortData.IsTruncated);
        Assert.Null(InfoSerializedData.TryRead([]));
    }

    [Fact]
    public void ScriptReference_ScrvEntriesPrintTheLocalByNumberAndName()
    {
        List<ScriptVariableInfo> locals = [new(1, "iCount", 1), new(2, null, 0)];

        Assert.Equal("local #1 (iCount)", GeckScriptWriter.FormatScriptReference(0x80000001, locals, Resolver));
        Assert.Equal("local #2 (unnamed)", GeckScriptWriter.FormatScriptReference(0x80000002, locals, Resolver));
        Assert.Equal(
            "local #7 (not declared in this script's SLSD/SCVR table)",
            GeckScriptWriter.FormatScriptReference(0x80000007, locals, Resolver));
        Assert.Equal("PlayerRef (0x00000014)", GeckScriptWriter.FormatScriptReference(PlayerRef, locals, Resolver));
        Assert.Equal(
            "SyntheticQuest (0x0010AAAA)",
            GeckScriptWriter.FormatScriptReference(QuestFormId, locals, Resolver));
        Assert.Equal("SyntheticQuest (0x0010AAAA)", GeckScriptWriter.FormatScriptReference(QuestFormId, Resolver));
    }

    private static byte[] FullInfoRecord()
    {
        var endHeader = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(endHeader.AsSpan(4), 2); // SCRO + SCRV entries
        BinaryPrimitives.WriteUInt32LittleEndian(endHeader.AsSpan(8), 4); // SCDA bytes
        BinaryPrimitives.WriteUInt32LittleEndian(endHeader.AsSpan(12), 1); // locals

        return BuildRecordBytes(InfoFormId, "INFO", false,
            ("EDID", NullTermString("SyntheticInfo")),
            ("DATA", new byte[] { 0x01, 0x00, 0x05, 0x31 }),
            ("QSTI", FormId(QuestFormId)),
            ("TRDT", Trdt(5, 50, 1)),
            ("NAM1", NullTermString("First line, said in full.")),
            ("TRDT", Trdt(0, 0, 2)),
            ("NAM1", NullTermString("Second line.")),
            ("CTDA", Ctda(0x00, 1f, 0x0048, SpeakerFormId, 0)),
            ("CTDA", Ctda(0x60, 1f, 0x004F, QuestFormId, 11)),
            ("TCLT", FormId(LinkedTopicFormId)),
            ("NAME", FormId(AddedTopicFormId)),
            ("SCHR", new byte[20]),
            ("NEXT", Array.Empty<byte>()),
            ("SCHR", endHeader),
            ("SCDA", new byte[] { 0x1D, 0x00, 0x00, 0x00 }),
            ("SCTX", NullTermString(EndScriptSource)),
            ("SLSD", ScriptLocal(1, 1)),
            ("SCVR", NullTermString("iCount")),
            ("SCRO", FormId(PlayerRef)),
            ("SCRV", FormId(1)),
            ("ANAM", FormId(SpeakerFormId)));
    }

    private static List<DialogueRecord> ParseInfo(byte[] recordBytes, bool bigEndian = false)
    {
        var mainRecord = new DetectedMainRecord("INFO", (uint)(recordBytes.Length - 24), 0, InfoFormId, 0, bigEndian);
        var scanResult = MakeScanResult([mainRecord]);

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);

        var parser = new RecordParser(scanResult, new Dictionary<uint, string>(), accessor, recordBytes.Length);
        return parser.ParseDialogue();
    }

    private static RecordCollection FnvRecords(DialogueRecord info)
    {
        return new RecordCollection
        {
            Game = BethesdaGame.FalloutNewVegas,
            Dialogues = [info],
            Quests =
            [
                new QuestRecord
                {
                    FormId = QuestFormId,
                    EditorId = "SyntheticQuest",
                    Variables = [new ScriptVariableInfo(11, "iTimesAsked", 1)]
                }
            ]
        };
    }

    private static byte[] Ctda(byte type, float comparison, ushort function, uint parameter1, uint parameter2)
    {
        var data = new byte[28];
        data[0] = type;
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), comparison);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), function);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), parameter1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), parameter2);
        return data; // Run On (offset 20) and Reference (offset 24) stay 0: Subject, none.
    }

    private static byte[] Trdt(uint emotionType, int emotionValue, byte responseNumber)
    {
        var data = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0), emotionType);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), emotionValue);
        data[12] = responseNumber;
        return data;
    }

    private static byte[] ScriptLocal(uint index, byte type)
    {
        var data = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(data, index);
        data[16] = type;
        return data;
    }

    private static byte[] FormId(uint formId)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, formId);
        return data;
    }

    private static RecordDetailSection Section(RecordDetailModel model, string title)
    {
        return Assert.Single(model.Sections, section => section.Title == title);
    }

    private static string? Value(RecordDetailModel model, string section, string label)
    {
        return Value(Section(model, section), label);
    }

    private static string? Value(RecordDetailSection section, string label)
    {
        return Assert.Single(section.Entries, entry => entry.Label == label).Value;
    }

    private static IReadOnlyList<RecordDetailListItem> ListItems(RecordDetailModel model, string section, string label)
    {
        var entry = Assert.Single(Section(model, section).Entries, candidate => candidate.Label == label);
        Assert.Equal(RecordDetailEntryKind.List, entry.Kind);
        return Assert.IsAssignableFrom<IReadOnlyList<RecordDetailListItem>>(entry.Items);
    }
}
