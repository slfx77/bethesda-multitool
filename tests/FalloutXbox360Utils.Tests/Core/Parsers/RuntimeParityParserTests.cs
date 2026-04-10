using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;
using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.BinaryTestWriter;
using static FalloutXbox360Utils.Tests.Helpers.EsmTestRecordBuilder;

namespace FalloutXbox360Utils.Tests.Core.Parsers;

public sealed class RuntimeParityParserTests : IDisposable
{
    private const int DataSize = 24 * 1024;
    private const uint HeapBaseVa = 0x40000000;

    private const byte ExtraOwnershipType = 0x21;
    private const byte ExtraPersistentCellType = 0x0C;
    private const byte ExtraStartingPositionType = 0x0F;
    private const byte ExtraPackageStartLocationType = 0x18;
    private const byte ExtraLeveledCreatureType = 0x2E;
    private const byte ExtraLockType = 0x2A;
    private const byte ExtraTeleportType = 0x2B;
    private const byte ExtraMapMarkerType = 0x2C;
    private const byte ExtraMerchantContainerType = 0x3C;
    private const byte ExtraEnableParentType = 0x37;
    private const byte ExtraRadiusType = 0x5C;
    private const byte ExtraStartingWorldOrCellType = 0x49;
    private const byte ExtraEncounterZoneType = 0x74;
    private const byte ExtraLinkedRefType = 0x51;
    private const byte ExtraLinkedRefChildrenType = 0x52;
    private MemoryMappedViewAccessor? _accessor;

    private MemoryMappedFile? _mmf;
    private string? _tempFilePath;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _accessor?.Dispose();
        _mmf?.Dispose();

        if (_tempFilePath != null && File.Exists(_tempFilePath))
        {
            try
            {
                File.Delete(_tempFilePath);
            }
            catch
            {
                // Best-effort temp cleanup only.
            }
        }
    }

    [Fact]
    public void ParseFormLists_RuntimeOverlayPreservesEsmEditorIdAndFillsEntries()
    {
        var data = new byte[DataSize];
        const uint formListFormId = 0x00100010;
        const int runtimeStructOffset = 4096;
        const int listNodeOffset = 4608;
        const int itemAOffset = 5120;
        const int itemBOffset = 5376;

        var esmRecord = BuildRecordBytes(formListFormId, "FLST", false,
            ("EDID", NullTermString("FormListFromEsm")));
        Array.Copy(esmRecord, 0, data, 0, esmRecord.Length);

        WriteTesFormHeader(data, runtimeStructOffset, 0x82010000, 0x55, formListFormId);
        WriteUInt32BE(data, runtimeStructOffset + 40, FileOffsetToVa(itemAOffset));
        WriteUInt32BE(data, runtimeStructOffset + 44, FileOffsetToVa(listNodeOffset));
        WriteUInt32BE(data, listNodeOffset, FileOffsetToVa(itemBOffset));
        WriteTesFormHeader(data, itemAOffset, 0x82010000, 0x28, 0x00F00001);
        WriteTesFormHeader(data, itemBOffset, 0x82010000, 0x29, 0x00F00002);

        var scanResult = MakeScanResult(
            [new DetectedMainRecord("FLST", (uint)(esmRecord.Length - 24), 0, formListFormId, 0, false)],
            runtimeEditorIds:
            [
                new RuntimeEditorIdEntry
                {
                    EditorId = "FormListFromRuntime",
                    FormId = formListFormId,
                    FormType = 0x55,
                    TesFormOffset = runtimeStructOffset
                }
            ]);

        var parser = CreateParser(scanResult, data);
        var result = parser.ParseFormLists();

        var formList = Assert.Single(result);
        Assert.Equal("FormListFromEsm", formList.EditorId);
        Assert.Equal([0x00F00001u, 0x00F00002u], formList.FormIds);
    }

    [Fact]
    public void ParseLeveledLists_RuntimeOverlayPreservesEsmIdentityAndFillsEntries()
    {
        var data = new byte[DataSize];
        const uint leveledListFormId = 0x00110010;
        const int runtimeStructOffset = 4096;
        const int leveledObjectOffset = 4608;
        const int entryFormOffset = 5120;
        const int globalOffset = 5632;

        var esmRecord = BuildRecordBytes(leveledListFormId, "LVLI", false,
            ("EDID", NullTermString("LvlItemsFromEsm")));
        Array.Copy(esmRecord, 0, data, 0, esmRecord.Length);

        WriteTesFormHeader(data, runtimeStructOffset, 0x82010000, 0x34, leveledListFormId);
        WriteUInt32BE(data, runtimeStructOffset + 68, FileOffsetToVa(leveledObjectOffset));
        WriteUInt32BE(data, runtimeStructOffset + 72, 0);
        data[runtimeStructOffset + 76] = 15;
        data[runtimeStructOffset + 77] = 0x04;
        WriteTesFormHeader(data, entryFormOffset, 0x82010000, 0x28, 0x00F10001);
        WriteTesFormHeader(data, globalOffset, 0x82010000, 0x06, 0x00F10002);
        WriteUInt32BE(data, runtimeStructOffset + 80, FileOffsetToVa(globalOffset));
        WriteUInt32BE(data, leveledObjectOffset, FileOffsetToVa(entryFormOffset));
        WriteUInt16BE(data, leveledObjectOffset + 4, 3);
        WriteUInt16BE(data, leveledObjectOffset + 6, 24);

        var scanResult = MakeScanResult(
            [new DetectedMainRecord("LVLI", (uint)(esmRecord.Length - 24), 0, leveledListFormId, 0, false)],
            runtimeEditorIds:
            [
                new RuntimeEditorIdEntry
                {
                    EditorId = "LvlItemsFromRuntime",
                    FormId = leveledListFormId,
                    FormType = 0x34,
                    TesFormOffset = runtimeStructOffset
                }
            ]);

        var parser = CreateParser(scanResult, data);
        var result = parser.ParseLeveledLists();

        var leveledList = Assert.Single(result);
        Assert.Equal("LvlItemsFromEsm", leveledList.EditorId);
        Assert.Equal("LVLI", leveledList.ListType);
        Assert.Equal((byte)15, leveledList.ChanceNone);
        Assert.Equal((byte)0x04, leveledList.Flags);
        Assert.Equal(0x00F10002u, leveledList.GlobalFormId);
        var entry = Assert.Single(leveledList.Entries);
        Assert.Equal((ushort)24, entry.Level);
        Assert.Equal(0x00F10001u, entry.FormId);
        Assert.Equal((ushort)3, entry.Count);
    }

    [Fact]
    public void ParseActivators_RuntimeOverlayPreservesEsmNameAndFillsRuntimeFields()
    {
        var data = new byte[DataSize];
        const uint activatorFormId = 0x00120010;
        const int runtimeStructOffset = 4096;
        const int nameOffset = 4608;
        const int modelOffset = 4864;
        const int scriptOffset = 5376;
        const int soundOffset = 5632;

        var esmRecord = BuildRecordBytes(activatorFormId, "ACTI", false,
            ("EDID", NullTermString("ActiFromEsm")),
            ("FULL", NullTermString("Switch From ESM")));
        Array.Copy(esmRecord, 0, data, 0, esmRecord.Length);

        WriteTesFormHeader(data, runtimeStructOffset, 0x82010000, 0x15, activatorFormId);
        WriteBounds(data, runtimeStructOffset + 52, -2, -2, 0, 2, 2, 8);
        WriteBSStringT(data, runtimeStructOffset + 68, FileOffsetToVa(nameOffset), "Switch From Runtime", nameOffset);
        WriteBSStringT(data, runtimeStructOffset + 80, FileOffsetToVa(modelOffset), "meshes\\clutter\\switch01.nif",
            modelOffset);
        WriteTesFormHeader(data, scriptOffset, 0x82010000, 0x11, 0x00F20001);
        WriteTesFormHeader(data, soundOffset, 0x82010000, 0x0D, 0x00F20002);
        WriteUInt32BE(data, runtimeStructOffset + 112, FileOffsetToVa(scriptOffset));
        WriteUInt32BE(data, runtimeStructOffset + 136, FileOffsetToVa(soundOffset));

        var scanResult = MakeScanResult(
            [new DetectedMainRecord("ACTI", (uint)(esmRecord.Length - 24), 0, activatorFormId, 0, false)],
            runtimeEditorIds:
            [
                new RuntimeEditorIdEntry
                {
                    EditorId = "ActiFromRuntime",
                    FormId = activatorFormId,
                    FormType = 0x15,
                    TesFormOffset = runtimeStructOffset
                }
            ]);

        var parser = CreateParser(scanResult, data);
        var result = parser.ParseActivators();

        var activator = Assert.Single(result);
        Assert.Equal("ActiFromEsm", activator.EditorId);
        Assert.Equal("Switch From ESM", activator.FullName);
        Assert.Equal("meshes\\clutter\\switch01.nif", activator.ModelPath);
        Assert.Equal(0x00F20001u, activator.Script);
        Assert.Equal(0x00F20002u, activator.ActivationSoundFormId);
        Assert.NotNull(activator.Bounds);
    }

    [Fact]
    public void ParseDialogTopics_RuntimeOverlayPreservesEsmIdentityAndFillsTopicMetadata()
    {
        var data = new byte[DataSize];
        const uint topicFormId = 0x00123010;
        const uint siblingTopicFormId = 0x00123011;
        const int siblingRecordOffset = 256;
        const int runtimeStructOffset = 4096;
        const int runtimeSiblingOffset = 4352;
        const int runtimeNameOffset = 4608;
        const int runtimePromptOffset = 4864;
        const int runtimeSiblingNameOffset = 5120;
        const int runtimeSiblingPromptOffset = 5376;

        var topicRecordBytes = BuildRecordBytes(topicFormId, "DIAL", false,
            ("EDID", NullTermString("TopicFromEsm")),
            ("FULL", NullTermString("Topic Name From ESM")),
            ("DATA", new byte[] { 0x01, 0x02 }));
        Array.Copy(topicRecordBytes, 0, data, 0, topicRecordBytes.Length);

        var siblingRecordBytes = BuildRecordBytes(siblingTopicFormId, "DIAL", false,
            ("EDID", NullTermString("SiblingTopicFromEsm")),
            ("FULL", NullTermString("Sibling Topic From ESM")),
            ("DATA", new byte[] { 0x00, 0x00 }));
        Array.Copy(siblingRecordBytes, 0, data, siblingRecordOffset, siblingRecordBytes.Length);

        WriteTesFormHeader(data, runtimeStructOffset, 0x82010000, 0x45, topicFormId);
        WriteBSStringT(
            data,
            runtimeStructOffset + 44,
            FileOffsetToVa(runtimeNameOffset),
            "Topic Name From Runtime",
            runtimeNameOffset);
        data[runtimeStructOffset + 52] = 1;
        data[runtimeStructOffset + 53] = 0x02;
        WriteFloatBE(data, runtimeStructOffset + 56, 35.0f);
        WriteBSStringT(
            data,
            runtimeStructOffset + 68,
            FileOffsetToVa(runtimePromptOffset),
            "Runtime Dummy Prompt",
            runtimePromptOffset);
        WriteInt32BE(data, runtimeStructOffset + 76, 17);
        WriteUInt32BE(data, runtimeStructOffset + 84, 6);

        WriteTesFormHeader(data, runtimeSiblingOffset, 0x82010000, 0x45, siblingTopicFormId);
        WriteBSStringT(
            data,
            runtimeSiblingOffset + 44,
            FileOffsetToVa(runtimeSiblingNameOffset),
            "Sibling Topic Runtime",
            runtimeSiblingNameOffset);
        data[runtimeSiblingOffset + 52] = 0;
        data[runtimeSiblingOffset + 53] = 0;
        WriteFloatBE(data, runtimeSiblingOffset + 56, 5.0f);
        WriteBSStringT(
            data,
            runtimeSiblingOffset + 68,
            FileOffsetToVa(runtimeSiblingPromptOffset),
            "Sibling Prompt",
            runtimeSiblingPromptOffset);
        WriteInt32BE(data, runtimeSiblingOffset + 76, 0);
        WriteUInt32BE(data, runtimeSiblingOffset + 84, 1);

        var scanResult = MakeScanResult(
            [
                new DetectedMainRecord("DIAL", (uint)(topicRecordBytes.Length - 24), 0, topicFormId, 0, false),
                new DetectedMainRecord("DIAL", (uint)(siblingRecordBytes.Length - 24), siblingRecordOffset,
                    siblingTopicFormId, 0, false)
            ],
            runtimeEditorIds:
            [
                new RuntimeEditorIdEntry
                {
                    EditorId = "TopicFromRuntime",
                    FormId = topicFormId,
                    FormType = 0x45,
                    TesFormOffset = runtimeStructOffset,
                    DisplayName = "Topic Name From Runtime"
                },
                new RuntimeEditorIdEntry
                {
                    EditorId = "SiblingTopicFromRuntime",
                    FormId = siblingTopicFormId,
                    FormType = 0x45,
                    TesFormOffset = runtimeSiblingOffset,
                    DisplayName = "Sibling Topic Runtime"
                }
            ]);

        var parser = CreateParser(scanResult, data);
        var topics = parser.ParseDialogTopics();
        var topic = Assert.Single(topics, record => record.FormId == topicFormId);

        Assert.Equal("TopicFromEsm", topic.EditorId);
        Assert.Equal("Topic Name From ESM", topic.FullName);
        Assert.Equal((byte)1, topic.TopicType);
        Assert.Equal((byte)0x02, topic.Flags);
        Assert.Equal(6, topic.ResponseCount);
        Assert.Equal(35.0f, topic.Priority);
        Assert.Equal(17, topic.JournalIndex);
        Assert.Equal("Runtime Dummy Prompt", topic.DummyPrompt);
    }

    [Fact]
    public void ParseAll_RuntimeDialogueTopicLinksFillTopicQuestAndDerivedResponseCount()
    {
        var data = new byte[DataSize];
        const uint topicFormId = 0x00123020;
        const uint siblingTopicFormId = 0x00123021;
        const uint questFormId = 0x00123030;
        const uint infoAFormId = 0x00123040;
        const uint infoBFormId = 0x00123041;
        const int siblingRecordOffset = 256;
        const int runtimeStructOffset = 4096;
        const int runtimeSiblingOffset = 4352;
        const int runtimeNameOffset = 4608;
        const int runtimeSiblingNameOffset = 4864;
        const int questInfoOffset = 5120;
        const int infoArrayOffset = 5376;
        const int questOffset = 5632;
        const int infoAOffset = 5888;
        const int infoBOffset = 6144;

        var topicRecordBytes = BuildRecordBytes(topicFormId, "DIAL", false,
            ("EDID", NullTermString("TopicNeedsQuest")));
        Array.Copy(topicRecordBytes, 0, data, 0, topicRecordBytes.Length);

        var siblingRecordBytes = BuildRecordBytes(siblingTopicFormId, "DIAL", false,
            ("EDID", NullTermString("TopicSibling")));
        Array.Copy(siblingRecordBytes, 0, data, siblingRecordOffset, siblingRecordBytes.Length);

        WriteTesFormHeader(data, runtimeStructOffset, 0x82010000, 0x45, topicFormId);
        WriteBSStringT(
            data,
            runtimeStructOffset + 44,
            FileOffsetToVa(runtimeNameOffset),
            "Topic Needs Quest",
            runtimeNameOffset);
        data[runtimeStructOffset + 52] = 0;
        data[runtimeStructOffset + 53] = 0;
        WriteFloatBE(data, runtimeStructOffset + 56, 50.0f);
        WriteUInt32BE(data, runtimeStructOffset + 60, FileOffsetToVa(questInfoOffset));
        WriteUInt32BE(data, runtimeStructOffset + 64, 0);
        WriteInt32BE(data, runtimeStructOffset + 76, 0);
        WriteUInt32BE(data, runtimeStructOffset + 84, 0);

        WriteTesFormHeader(data, runtimeSiblingOffset, 0x82010000, 0x45, siblingTopicFormId);
        WriteBSStringT(
            data,
            runtimeSiblingOffset + 44,
            FileOffsetToVa(runtimeSiblingNameOffset),
            "Sibling Topic",
            runtimeSiblingNameOffset);
        data[runtimeSiblingOffset + 52] = 0;
        data[runtimeSiblingOffset + 53] = 0;
        WriteFloatBE(data, runtimeSiblingOffset + 56, 10.0f);
        WriteInt32BE(data, runtimeSiblingOffset + 76, 0);
        WriteUInt32BE(data, runtimeSiblingOffset + 84, 1);

        WriteUInt32BE(data, questInfoOffset, FileOffsetToVa(questOffset));
        WriteUInt32BE(data, questInfoOffset + 8, FileOffsetToVa(infoArrayOffset));
        WriteUInt32BE(data, questInfoOffset + 16, 2);
        WriteUInt32BE(data, infoArrayOffset, FileOffsetToVa(infoAOffset));
        WriteUInt32BE(data, infoArrayOffset + 4, FileOffsetToVa(infoBOffset));

        WriteTesFormHeader(data, questOffset, 0x82010000, 0x47, questFormId);
        WriteTesFormHeader(data, infoAOffset, 0x82010000, 0x46, infoAFormId);
        WriteTesFormHeader(data, infoBOffset, 0x82010000, 0x46, infoBFormId);

        var scanResult = MakeScanResult(
            [
                new DetectedMainRecord("DIAL", (uint)(topicRecordBytes.Length - 24), 0, topicFormId, 0, false),
                new DetectedMainRecord("DIAL", (uint)(siblingRecordBytes.Length - 24), siblingRecordOffset,
                    siblingTopicFormId, 0, false)
            ],
            runtimeEditorIds:
            [
                new RuntimeEditorIdEntry
                {
                    EditorId = "TopicNeedsQuestRuntime",
                    FormId = topicFormId,
                    FormType = 0x45,
                    TesFormOffset = runtimeStructOffset
                },
                new RuntimeEditorIdEntry
                {
                    EditorId = "TopicSiblingRuntime",
                    FormId = siblingTopicFormId,
                    FormType = 0x45,
                    TesFormOffset = runtimeSiblingOffset
                }
            ]);

        var parser = CreateParser(scanResult, data);
        var result = parser.ParseAll();
        var topic = Assert.Single(result.DialogTopics, record => record.FormId == topicFormId);

        Assert.Equal(questFormId, topic.QuestFormId);
        Assert.Equal(2, topic.ResponseCount);
    }

    [Fact]
    public void ParseAll_RuntimeDialogueConversationDataFillsLinkAndFollowUpLists()
    {
        var data = new byte[DataSize];
        const uint runtimeFilledInfoFormId = 0x00123050;
        const uint esmPreferredInfoFormId = 0x00123051;
        const int esmPreferredRecordOffset = 256;
        const int runtimeFilledInfoOffset = 4096;
        const int runtimeFilledConversationOffset = 4352;
        const int runtimeFilledLinkFromNodeOffset = 4416;
        const int runtimeFilledLinkToNodeOffset = 4448;
        const int runtimeFilledFollowUpNodeOffset = 4480;
        const int esmPreferredInfoOffset = 4608;
        const int esmPreferredConversationOffset = 4864;
        const int esmPreferredFollowUpNodeOffset = 4928;
        const int runtimeTopicAOffset = 5376;
        const int runtimeTopicBOffset = 5632;
        const int runtimeTopicCOffset = 5888;
        const int runtimeTopicDOffset = 6144;
        const int runtimeTopicEOffset = 6400;
        const int runtimeTopicFOffset = 6656;
        const int runtimeFollowUpAOffset = 6912;
        const int runtimeFollowUpBOffset = 7168;
        const int runtimeFollowUpCOffset = 7424;

        var runtimeFilledInfoRecordBytes = BuildRecordBytes(runtimeFilledInfoFormId, "INFO", false,
            ("EDID", NullTermString("InfoRuntimeFilled")));
        Array.Copy(runtimeFilledInfoRecordBytes, 0, data, 0, runtimeFilledInfoRecordBytes.Length);

        var esmPreferredInfoRecordBytes = BuildRecordBytes(esmPreferredInfoFormId, "INFO", false,
            ("EDID", NullTermString("InfoEsmPreferred")),
            ("TCLF", BitConverter.GetBytes(0x00123100u)),
            ("TCLT", BitConverter.GetBytes(0x00123101u)));
        Array.Copy(esmPreferredInfoRecordBytes, 0, data, esmPreferredRecordOffset, esmPreferredInfoRecordBytes.Length);

        WriteTesFormHeader(data, runtimeFilledInfoOffset, 0x82010000, 0x46, runtimeFilledInfoFormId);
        WriteUInt16BE(data, runtimeFilledInfoOffset + 48, 4);
        WriteUInt32BE(data, runtimeFilledInfoOffset + 72, FileOffsetToVa(runtimeFilledConversationOffset));
        WriteSimpleListNode(
            data,
            runtimeFilledConversationOffset + 0,
            FileOffsetToVa(runtimeTopicAOffset),
            FileOffsetToVa(runtimeFilledLinkFromNodeOffset));
        WriteSimpleListNode(data, runtimeFilledLinkFromNodeOffset, FileOffsetToVa(runtimeTopicBOffset), 0);
        WriteSimpleListNode(
            data,
            runtimeFilledConversationOffset + 8,
            FileOffsetToVa(runtimeTopicCOffset),
            FileOffsetToVa(runtimeFilledLinkToNodeOffset));
        WriteSimpleListNode(data, runtimeFilledLinkToNodeOffset, FileOffsetToVa(runtimeTopicDOffset), 0);
        WriteSimpleListNode(
            data,
            runtimeFilledConversationOffset + 16,
            FileOffsetToVa(runtimeFollowUpAOffset),
            FileOffsetToVa(runtimeFilledFollowUpNodeOffset));
        WriteSimpleListNode(data, runtimeFilledFollowUpNodeOffset, FileOffsetToVa(runtimeFollowUpBOffset), 0);

        WriteTesFormHeader(data, esmPreferredInfoOffset, 0x82010000, 0x46, esmPreferredInfoFormId);
        WriteUInt16BE(data, esmPreferredInfoOffset + 48, 5);
        WriteUInt32BE(data, esmPreferredInfoOffset + 72, FileOffsetToVa(esmPreferredConversationOffset));
        WriteSimpleListNode(data, esmPreferredConversationOffset + 0, FileOffsetToVa(runtimeTopicEOffset), 0);
        WriteSimpleListNode(data, esmPreferredConversationOffset + 8, FileOffsetToVa(runtimeTopicFOffset), 0);
        WriteSimpleListNode(
            data,
            esmPreferredConversationOffset + 16,
            FileOffsetToVa(runtimeFollowUpCOffset),
            FileOffsetToVa(esmPreferredFollowUpNodeOffset));
        WriteSimpleListNode(data, esmPreferredFollowUpNodeOffset, FileOffsetToVa(runtimeFollowUpBOffset), 0);

        WriteTesFormHeader(data, runtimeTopicAOffset, 0x82010000, 0x45, 0x00123060);
        WriteTesFormHeader(data, runtimeTopicBOffset, 0x82010000, 0x45, 0x00123061);
        WriteTesFormHeader(data, runtimeTopicCOffset, 0x82010000, 0x45, 0x00123062);
        WriteTesFormHeader(data, runtimeTopicDOffset, 0x82010000, 0x45, 0x00123063);
        WriteTesFormHeader(data, runtimeTopicEOffset, 0x82010000, 0x45, 0x00123064);
        WriteTesFormHeader(data, runtimeTopicFOffset, 0x82010000, 0x45, 0x00123065);
        WriteTesFormHeader(data, runtimeFollowUpAOffset, 0x82010000, 0x46, 0x00123070);
        WriteTesFormHeader(data, runtimeFollowUpBOffset, 0x82010000, 0x46, 0x00123071);
        WriteTesFormHeader(data, runtimeFollowUpCOffset, 0x82010000, 0x46, 0x00123072);

        var scanResult = MakeScanResult(
            [
                new DetectedMainRecord("INFO", (uint)(runtimeFilledInfoRecordBytes.Length - 24), 0,
                    runtimeFilledInfoFormId, 0, false),
                new DetectedMainRecord("INFO", (uint)(esmPreferredInfoRecordBytes.Length - 24),
                    esmPreferredRecordOffset, esmPreferredInfoFormId, 0, false)
            ],
            runtimeEditorIds:
            [
                new RuntimeEditorIdEntry
                {
                    EditorId = "InfoRuntimeFilled",
                    FormId = runtimeFilledInfoFormId,
                    FormType = 0x46,
                    TesFormOffset = runtimeFilledInfoOffset,
                    DialogueLine = "Runtime prompt"
                },
                new RuntimeEditorIdEntry
                {
                    EditorId = "InfoEsmPreferredRuntime",
                    FormId = esmPreferredInfoFormId,
                    FormType = 0x46,
                    TesFormOffset = esmPreferredInfoOffset,
                    DialogueLine = "Runtime prompt"
                }
            ]);

        var parser = CreateParser(scanResult, data);
        var result = parser.ParseAll();

        var runtimeFilled = Assert.Single(result.Dialogues, record => record.FormId == runtimeFilledInfoFormId);
        Assert.Equal([0x00123062u, 0x00123063u], runtimeFilled.LinkToTopics);
        Assert.Equal([0x00123060u, 0x00123061u], runtimeFilled.LinkFromTopics);
        Assert.Equal([0x00123070u, 0x00123071u], runtimeFilled.FollowUpInfos);

        var esmPreferred = Assert.Single(result.Dialogues, record => record.FormId == esmPreferredInfoFormId);
        Assert.NotEmpty(esmPreferred.LinkToTopics);
        Assert.NotEmpty(esmPreferred.LinkFromTopics);
        Assert.Equal([0x00123072u, 0x00123071u], esmPreferred.FollowUpInfos);
    }

    [Fact]
    public void ParseQuests_RuntimeOverlayMergesObjectivesEsmFirst()
    {
        var data = new byte[DataSize];
        const uint questFormId = 0x00124010;
        const int runtimeStructOffset = 4096;
        const int objectiveAOffset = 4608;
        const int objectiveNodeAOffset = 4688;
        const int objectiveBOffset = 4736;
        const int objectiveNodeBOffset = 4816;
        const int objectiveCOffset = 4864;
        const int objectiveATextOffset = 6144;
        const int objectiveBTextOffset = 6400;
        const int objectiveCTextOffset = 6656;
        const int runtimeFullNameOffset = 6912;
        const int runtimeScriptOffset = 7168;

        var questRecordBytes = BuildRecordBytes(questFormId, "QUST", false,
            ("EDID", NullTermString("QuestFromEsm")),
            ("FULL", NullTermString("Quest Name From ESM")),
            ("QOBJ", BitConverter.GetBytes(10)),
            ("NNAM", NullTermString("Objective 10 From ESM")),
            ("QOBJ", BitConverter.GetBytes(20)));
        Array.Copy(questRecordBytes, 0, data, 0, questRecordBytes.Length);

        WriteTesFormHeader(data, runtimeStructOffset, 0x82010000, 0x47, questFormId);
        WriteUInt32BE(data, runtimeStructOffset + 44, FileOffsetToVa(runtimeScriptOffset));
        WriteBSStringT(
            data,
            runtimeStructOffset + 68,
            FileOffsetToVa(runtimeFullNameOffset),
            "Quest Name From Runtime",
            runtimeFullNameOffset);
        data[runtimeStructOffset + 76] = 0x14;
        data[runtimeStructOffset + 77] = 5;
        WriteFloatBE(data, runtimeStructOffset + 80, 2.5f);
        WriteUInt32BE(data, runtimeStructOffset + 92, FileOffsetToVa(objectiveAOffset));
        WriteUInt32BE(data, runtimeStructOffset + 96, FileOffsetToVa(objectiveNodeAOffset));

        WriteTesFormHeader(data, runtimeScriptOffset, 0x82010000, 0x11, 0x00F24001);
        WriteQuestObjective(
            data,
            objectiveAOffset,
            10,
            "Objective 10 From Runtime",
            objectiveATextOffset,
            FileOffsetToVa(runtimeStructOffset),
            true,
            1);
        WriteSimpleListNode(data, objectiveNodeAOffset, FileOffsetToVa(objectiveBOffset),
            FileOffsetToVa(objectiveNodeBOffset));
        WriteQuestObjective(
            data,
            objectiveBOffset,
            20,
            "Objective 20 From Runtime",
            objectiveBTextOffset,
            FileOffsetToVa(runtimeStructOffset),
            true,
            1);
        WriteSimpleListNode(data, objectiveNodeBOffset, FileOffsetToVa(objectiveCOffset), 0);
        WriteQuestObjective(
            data,
            objectiveCOffset,
            30,
            "Objective 30 Runtime Only",
            objectiveCTextOffset,
            FileOffsetToVa(runtimeStructOffset),
            true,
            2);

        var scanResult = MakeScanResult(
            [new DetectedMainRecord("QUST", (uint)(questRecordBytes.Length - 24), 0, questFormId, 0, false)],
            runtimeEditorIds:
            [
                new RuntimeEditorIdEntry
                {
                    EditorId = "QuestFromRuntime",
                    FormId = questFormId,
                    FormType = 0x47,
                    TesFormOffset = runtimeStructOffset,
                    DisplayName = "Quest Name From Runtime"
                }
            ]);

        var parser = CreateParser(scanResult, data);
        var quest = Assert.Single(parser.ParseQuests());

        Assert.Equal("QuestFromEsm", quest.EditorId);
        Assert.Equal("Quest Name From ESM", quest.FullName);
        Assert.Equal((byte)0x14, quest.Flags);
        Assert.Equal((byte)5, quest.Priority);
        Assert.Equal(2.5f, quest.QuestDelay);
        Assert.Equal(0x00F24001u, quest.Script);
        Assert.Collection(quest.Objectives,
            objective =>
            {
                Assert.Equal(10, objective.Index);
                Assert.Equal("Objective 10 From ESM", objective.DisplayText);
            },
            objective =>
            {
                Assert.Equal(20, objective.Index);
                Assert.Equal("Objective 20 From Runtime", objective.DisplayText);
            },
            objective =>
            {
                Assert.Equal(30, objective.Index);
                Assert.Equal("Objective 30 Runtime Only", objective.DisplayText);
            });
    }

    [Fact]
    public void ParseQuests_RuntimeOverlayMergesStagesEsmFirst()
    {
        var data = new byte[DataSize];
        const uint questFormId = 0x00124020;
        const int runtimeStructOffset = 4096;
        const int stageAOffset = 4608;
        const int stageNodeAOffset = 4688;
        const int stageBOffset = 4736;
        const int stageNodeBOffset = 4816;
        const int stageCOffset = 4864;
        const int stageAItemOffset = 5120;
        const int stageBItemOffset = 5376;
        const int stageCItemOffset = 5632;

        var questRecordBytes = BuildRecordBytes(questFormId, "QUST", false,
            ("EDID", NullTermString("QuestStageFromEsm")),
            ("FULL", NullTermString("Quest Stages From ESM")),
            ("INDX", BitConverter.GetBytes((short)10)),
            ("CNAM", NullTermString("Stage 10 From ESM")),
            ("INDX", BitConverter.GetBytes((short)20)),
            ("CNAM", NullTermString("Stage 20 From ESM")),
            ("QSDT", new byte[] { 0x10 }));
        Array.Copy(questRecordBytes, 0, data, 0, questRecordBytes.Length);

        WriteTesFormHeader(data, runtimeStructOffset, 0x82010000, 0x47, questFormId);
        WriteUInt32BE(data, runtimeStructOffset + 84, FileOffsetToVa(stageAOffset));
        WriteUInt32BE(data, runtimeStructOffset + 88, FileOffsetToVa(stageNodeAOffset));

        WriteQuestStage(data, stageAOffset, 10, FileOffsetToVa(stageAItemOffset));
        WriteSimpleListNode(data, stageNodeAOffset, FileOffsetToVa(stageBOffset), FileOffsetToVa(stageNodeBOffset));
        WriteQuestStage(data, stageBOffset, 20, FileOffsetToVa(stageBItemOffset));
        WriteSimpleListNode(data, stageNodeBOffset, FileOffsetToVa(stageCOffset), 0);
        WriteQuestStage(data, stageCOffset, 30, FileOffsetToVa(stageCItemOffset));

        WriteQuestStageItem(data, stageAItemOffset, 0x04, FileOffsetToVa(runtimeStructOffset), true);
        WriteQuestStageItem(data, stageBItemOffset, 0x08, FileOffsetToVa(runtimeStructOffset), false);
        WriteQuestStageItem(data, stageCItemOffset, 0x02, FileOffsetToVa(runtimeStructOffset), false);

        var scanResult = MakeScanResult(
            [new DetectedMainRecord("QUST", (uint)(questRecordBytes.Length - 24), 0, questFormId, 0, false)],
            runtimeEditorIds:
            [
                new RuntimeEditorIdEntry
                {
                    EditorId = "QuestStageFromRuntime",
                    FormId = questFormId,
                    FormType = 0x47,
                    TesFormOffset = runtimeStructOffset
                }
            ]);

        var parser = CreateParser(scanResult, data);
        var quest = Assert.Single(parser.ParseQuests());

        Assert.Collection(quest.Stages,
            stage =>
            {
                Assert.Equal(10, stage.Index);
                Assert.Equal("Stage 10 From ESM", stage.LogEntry);
                Assert.Equal((byte)0x04, stage.Flags);
            },
            stage =>
            {
                Assert.Equal(20, stage.Index);
                Assert.Equal("Stage 20 From ESM", stage.LogEntry);
                Assert.Equal((byte)0x10, stage.Flags);
            },
            stage =>
            {
                Assert.Equal(30, stage.Index);
                Assert.Null(stage.LogEntry);
                Assert.Equal((byte)0x02, stage.Flags);
            });
    }

    [Fact]
    public void ParseAll_RuntimeWorldAndCellOverlayFillsMapAndGridData()
    {
        var data = new byte[DataSize];
        const uint worldspaceFormId = 0x00130010;
        const uint cellFormId = 0x00130020;
        const int cellRecordOffset = 256;
        const int worldRecordOffset = 512;
        const int runtimeWorldOffset = 4096;
        const int runtimeCellMapOffset = 4608;
        const int runtimeBucketArrayOffset = 4640;
        const int runtimeBucketItemOffset = 4672;
        const int runtimeCellOffset = 5120;
        const int worldNameOffset = 6144;
        const int cellNameOffset = 6400;

        var cellRecordBytes = BuildRecordBytes(cellFormId, "CELL", false,
            ("EDID", NullTermString("CellFromEsm")));
        Array.Copy(cellRecordBytes, 0, data, cellRecordOffset, cellRecordBytes.Length);

        var worldRecordBytes = BuildRecordBytes(worldspaceFormId, "WRLD", false,
            ("EDID", NullTermString("WorldFromEsm")),
            ("FULL", NullTermString("World Name From ESM")));
        Array.Copy(worldRecordBytes, 0, data, worldRecordOffset, worldRecordBytes.Length);

        WriteTesFormHeader(data, runtimeWorldOffset, 0x82010000, 0x41, worldspaceFormId);
        WriteBSStringT(data, runtimeWorldOffset + 44, FileOffsetToVa(worldNameOffset), "World Name From Runtime",
            worldNameOffset);
        WriteUInt32BE(data, runtimeWorldOffset + 64, FileOffsetToVa(runtimeCellMapOffset));
        WriteUInt32BE(data, runtimeWorldOffset + 68, FileOffsetToVa(runtimeCellOffset));
        WriteInt32BE(data, runtimeWorldOffset + 144, 768);
        WriteInt32BE(data, runtimeWorldOffset + 148, 512);
        WriteUInt16BE(data, runtimeWorldOffset + 152, unchecked((ushort)-4));
        WriteUInt16BE(data, runtimeWorldOffset + 154, unchecked((ushort)-4));
        WriteUInt16BE(data, runtimeWorldOffset + 156, unchecked(8));
        WriteUInt16BE(data, runtimeWorldOffset + 158, unchecked(8));

        WriteUInt32BE(data, runtimeCellMapOffset + 4, 1);
        WriteUInt32BE(data, runtimeCellMapOffset + 8, FileOffsetToVa(runtimeBucketArrayOffset));
        WriteUInt32BE(data, runtimeCellMapOffset + 12, 1);
        WriteUInt32BE(data, runtimeBucketArrayOffset, FileOffsetToVa(runtimeBucketItemOffset));
        WriteUInt32BE(data, runtimeBucketItemOffset, 0);
        WriteUInt32BE(data, runtimeBucketItemOffset + 4, PackCellMapKey(5, -2));
        WriteUInt32BE(data, runtimeBucketItemOffset + 8, FileOffsetToVa(runtimeCellOffset));

        WriteTesFormHeader(data, runtimeCellOffset, 0x82010000, 0x39, cellFormId);
        WriteBSStringT(data, runtimeCellOffset + 44, FileOffsetToVa(cellNameOffset), "Cell Name From Runtime",
            cellNameOffset);
        data[runtimeCellOffset + 52] = 0x02;
        WriteFloatBE(data, runtimeCellOffset + 96, 96f);
        WriteUInt32BE(data, runtimeCellOffset + 160, FileOffsetToVa(runtimeWorldOffset));

        var scanResult = MakeScanResult(
            [
                new DetectedMainRecord("CELL", (uint)(cellRecordBytes.Length - 24), 0, cellFormId, cellRecordOffset,
                    false),
                new DetectedMainRecord("WRLD", (uint)(worldRecordBytes.Length - 24), 0, worldspaceFormId,
                    worldRecordOffset, false)
            ],
            runtimeEditorIds:
            [
                new RuntimeEditorIdEntry
                {
                    EditorId = "WorldFromRuntime",
                    FormId = worldspaceFormId,
                    FormType = 0x41,
                    TesFormOffset = runtimeWorldOffset,
                    DisplayName = "World Name From Runtime"
                },
                new RuntimeEditorIdEntry
                {
                    EditorId = "CellFromRuntime",
                    FormId = cellFormId,
                    FormType = 0x39,
                    TesFormOffset = runtimeCellOffset,
                    DisplayName = "Cell Name From Runtime"
                }
            ]);

        var parser = CreateParser(scanResult, data);
        var result = parser.ParseAll();

        var worldspace = Assert.Single(result.Worldspaces);
        Assert.Equal("WorldFromEsm", worldspace.EditorId);
        Assert.Equal("World Name From ESM", worldspace.FullName);
        Assert.Equal(768, worldspace.MapUsableWidth);
        Assert.Equal(512, worldspace.MapUsableHeight);
        Assert.Equal((short)-4, worldspace.MapNWCellX);

        var cell = Assert.Single(result.Cells, c => c.FormId == cellFormId);
        Assert.Equal("CellFromEsm", cell.EditorId);
        Assert.Equal("Cell Name From Runtime", cell.FullName);
        Assert.Equal(5, cell.GridX);
        Assert.Equal(-2, cell.GridY);
        Assert.Equal(worldspaceFormId, cell.WorldspaceFormId);
        Assert.Equal(96f, cell.WaterHeight);
        Assert.False(cell.IsVirtual);
    }

    [Fact]
    public void ParseWorldspaces_RuntimeCellMapFallbackCarriesRuntimeMetadata()
    {
        const uint worldspaceFormId = 0x00130030;
        var context = new RecordParserContext(
            new EsmRecordScanResult(),
            new Dictionary<uint, string> { [worldspaceFormId] = "RuntimeCellMapWorld" });
        context.RuntimeWorldspaceCellMaps = new Dictionary<uint, RuntimeWorldspaceData>
        {
            [worldspaceFormId] = new()
            {
                FormId = worldspaceFormId,
                EditorId = "RuntimeCellMapWorld",
                FullName = "Runtime Cell Map World",
                ParentWorldFormId = 0x00130031,
                ClimateFormId = 0x00130032,
                WaterFormId = 0x00130033,
                DefaultLandHeight = 96f,
                DefaultWaterHeight = 128f,
                MapUsableWidth = 768,
                MapUsableHeight = 512,
                MapNWCellX = -4,
                MapNWCellY = 8,
                MapSECellX = 12,
                MapSECellY = -6,
                BoundsMinX = -16384f,
                BoundsMinY = -24576f,
                BoundsMaxX = 53248f,
                BoundsMaxY = 36864f,
                EncounterZoneFormId = 0x00130034,
                Offset = 4096
            }
        };

        var handler = new WorldRecordHandler(context);
        var worldspaces = handler.ParseWorldspaces();

        var worldspace = Assert.Single(worldspaces);
        Assert.Equal("RuntimeCellMapWorld", worldspace.EditorId);
        Assert.Equal("Runtime Cell Map World", worldspace.FullName);
        Assert.Equal(0x00130031u, worldspace.ParentWorldspaceFormId);
        Assert.Equal(0x00130032u, worldspace.ClimateFormId);
        Assert.Equal(0x00130033u, worldspace.WaterFormId);
        Assert.Equal(96f, worldspace.DefaultLandHeight);
        Assert.Equal(128f, worldspace.DefaultWaterHeight);
        Assert.Equal(768, worldspace.MapUsableWidth);
        Assert.Equal((short)-4, worldspace.MapNWCellX);
        Assert.Equal(-16384f, worldspace.BoundsMinX);
        Assert.Equal(0x00130034u, worldspace.EncounterZoneFormId);
        Assert.Equal(4096, worldspace.Offset);
    }

    [Fact]
    public void EnsureWorldspacesForCells_RuntimeCellMapMetadataEnrichesRecoveredWorldspace()
    {
        const uint worldspaceFormId = 0x00130040;
        const uint cellFormId = 0x00130041;
        const uint encounterZoneFormId = 0x00130045;
        var context = new RecordParserContext(
            new EsmRecordScanResult(),
            new Dictionary<uint, string> { [worldspaceFormId] = "RecoveredWorldspace" });
        context.RuntimeWorldspaceCellMaps = new Dictionary<uint, RuntimeWorldspaceData>
        {
            [worldspaceFormId] = new()
            {
                FormId = worldspaceFormId,
                EditorId = "RecoveredWorldspace",
                FullName = "Recovered Worldspace",
                ParentWorldFormId = 0x00130042,
                ClimateFormId = 0x00130043,
                WaterFormId = 0x00130044,
                DefaultLandHeight = 32f,
                DefaultWaterHeight = 64f,
                Offset = 8192
            }
        };

        var cells = new List<CellRecord>
        {
            new()
            {
                FormId = cellFormId,
                WorldspaceFormId = worldspaceFormId,
                GridX = 3,
                GridY = -2,
                EncounterZoneFormId = encounterZoneFormId
            }
        };
        var worldspaces = new List<WorldspaceRecord>();

        WorldRecordHandler.EnsureWorldspacesForCells(cells, worldspaces, context);

        var worldspace = Assert.Single(worldspaces);
        Assert.Equal("RecoveredWorldspace", worldspace.EditorId);
        Assert.Equal("Recovered Worldspace", worldspace.FullName);
        Assert.Equal(0x00130042u, worldspace.ParentWorldspaceFormId);
        Assert.Equal(0x00130043u, worldspace.ClimateFormId);
        Assert.Equal(0x00130044u, worldspace.WaterFormId);
        Assert.Equal(32f, worldspace.DefaultLandHeight);
        Assert.Equal(64f, worldspace.DefaultWaterHeight);
        Assert.Equal((short)3, worldspace.MapNWCellX);
        Assert.Equal((short)-2, worldspace.MapNWCellY);
        Assert.Equal((short)3, worldspace.MapSECellX);
        Assert.Equal((short)-2, worldspace.MapSECellY);
        Assert.Equal(3 * 4096f, worldspace.BoundsMinX);
        Assert.Equal(-2 * 4096f, worldspace.BoundsMinY);
        Assert.Equal(4 * 4096f, worldspace.BoundsMaxX);
        Assert.Equal(-1 * 4096f, worldspace.BoundsMaxY);
        Assert.Equal(encounterZoneFormId, worldspace.EncounterZoneFormId);
        Assert.Equal(8192, worldspace.Offset);
    }

    [Fact]
    public void ParseAll_RuntimeCellMembershipOverridesProximityFallback()
    {
        var data = new byte[DataSize];
        const uint worldspaceFormId = 0x00135010;
        const uint cellFormId = 0x00135020;
        const uint runtimeCellRefFormId = 0x00135030;
        const uint proximityRefFormId = 0x00135040;
        const uint runtimeBaseFormId = 0x00135A01;
        const uint proximityBaseFormId = 0x00135A02;
        const int cellRecordOffset = 256;
        const int worldRecordOffset = 512;
        const int runtimeWorldOffset = 4096;
        const int runtimeCellMapOffset = 4608;
        const int runtimeBucketArrayOffset = 4640;
        const int runtimeBucketItemOffset = 4672;
        const int runtimeCellOffset = 5120;
        const int runtimeCellRefOffset = 5376;

        var cellRecordBytes = BuildRecordBytes(cellFormId, "CELL", false,
            ("EDID", NullTermString("CellFromEsm")));
        Array.Copy(cellRecordBytes, 0, data, cellRecordOffset, cellRecordBytes.Length);

        var worldRecordBytes = BuildRecordBytes(worldspaceFormId, "WRLD", false,
            ("EDID", NullTermString("WorldFromEsm")));
        Array.Copy(worldRecordBytes, 0, data, worldRecordOffset, worldRecordBytes.Length);

        WriteTesFormHeader(data, runtimeWorldOffset, 0x82010000, 0x41, worldspaceFormId);
        WriteUInt32BE(data, runtimeWorldOffset + 64, FileOffsetToVa(runtimeCellMapOffset));
        WriteUInt32BE(data, runtimeWorldOffset + 68, FileOffsetToVa(runtimeCellOffset));

        WriteUInt32BE(data, runtimeCellMapOffset + 4, 1);
        WriteUInt32BE(data, runtimeCellMapOffset + 8, FileOffsetToVa(runtimeBucketArrayOffset));
        WriteUInt32BE(data, runtimeCellMapOffset + 12, 1);
        WriteUInt32BE(data, runtimeBucketArrayOffset, FileOffsetToVa(runtimeBucketItemOffset));
        WriteUInt32BE(data, runtimeBucketItemOffset, 0);
        WriteUInt32BE(data, runtimeBucketItemOffset + 4, PackCellMapKey(7, -1));
        WriteUInt32BE(data, runtimeBucketItemOffset + 8, FileOffsetToVa(runtimeCellOffset));

        WriteTesFormHeader(data, runtimeCellOffset, 0x82010000, 0x39, cellFormId);
        WriteUInt32BE(data, runtimeCellOffset + 140, FileOffsetToVa(runtimeCellRefOffset));
        WriteUInt32BE(data, runtimeCellOffset + 144, 0);
        WriteUInt32BE(data, runtimeCellOffset + 160, FileOffsetToVa(runtimeWorldOffset));
        WriteTesFormHeader(data, runtimeCellRefOffset, 0x82010000, 0x3A, runtimeCellRefFormId);

        var scanResult = new EsmRecordScanResult
        {
            MainRecords =
            [
                new DetectedMainRecord("CELL", (uint)(cellRecordBytes.Length - 24), 0, cellFormId, cellRecordOffset,
                    false),
                new DetectedMainRecord("WRLD", (uint)(worldRecordBytes.Length - 24), 0, worldspaceFormId,
                    worldRecordOffset, false)
            ],
            RefrRecords =
            [
                new ExtractedRefrRecord
                {
                    Header = new DetectedMainRecord("REFR", 0, 0, runtimeCellRefFormId, 1024, false),
                    BaseFormId = runtimeBaseFormId,
                    Position = new PositionSubrecord(100f, 200f, 300f, 0f, 0f, 0f, 1024, false),
                    Scale = 1.0f
                },
                new ExtractedRefrRecord
                {
                    Header = new DetectedMainRecord("REFR", 0, 0, proximityRefFormId, 1200, false),
                    BaseFormId = proximityBaseFormId,
                    Position = new PositionSubrecord(9000f, 9100f, 100f, 0f, 0f, 0f, 1200, false),
                    Scale = 1.0f
                }
            ],
            RuntimeEditorIds =
            [
                new RuntimeEditorIdEntry
                {
                    EditorId = "WorldFromRuntime",
                    FormId = worldspaceFormId,
                    FormType = 0x41,
                    TesFormOffset = runtimeWorldOffset
                },
                new RuntimeEditorIdEntry
                {
                    EditorId = "CellFromRuntime",
                    FormId = cellFormId,
                    FormType = 0x39,
                    TesFormOffset = runtimeCellOffset
                }
            ]
        };

        var parser = CreateParser(scanResult, data);
        var result = parser.ParseAll();

        var cell = Assert.Single(result.Cells, item => item.FormId == cellFormId);
        var placedRef = Assert.Single(cell.PlacedObjects);

        Assert.Equal(runtimeCellRefFormId, placedRef.FormId);
        Assert.Equal("RuntimeCellList", placedRef.AssignmentSource);
        Assert.DoesNotContain(cell.PlacedObjects, item => item.FormId == proximityRefFormId);
        Assert.Equal(7, cell.GridX);
        Assert.Equal(-1, cell.GridY);
        Assert.Equal(worldspaceFormId, cell.WorldspaceFormId);
    }

    [Fact]
    public void ParseAll_RuntimeParentCellSignalCreatesRealCellStubAndAvoidsVirtualFallback()
    {
        var data = new byte[DataSize];
        const uint worldspaceFormId = 0x00135110;
        const uint cellFormId = 0x00135120;
        const uint refrFormId = 0x00135130;
        const uint baseFormId = 0x00135140;
        const int worldRecordOffset = 256;
        const int runtimeWorldOffset = 4096;
        const int runtimeRefrOffset = 4608;
        const int runtimeBaseObjectOffset = 5120;
        const int runtimeParentCellOffset = 5376;

        // MNAM: usableWidth(4) + usableHeight(4) + NWCellX(2) + NWCellY(2) + SECellX(2) + SECellY(2) = 16 bytes
        var mnamData = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(mnamData.AsSpan(0), 768);
        BinaryPrimitives.WriteInt32LittleEndian(mnamData.AsSpan(4), 512);
        BinaryPrimitives.WriteInt16LittleEndian(mnamData.AsSpan(8), -4);
        BinaryPrimitives.WriteInt16LittleEndian(mnamData.AsSpan(10), -4);
        BinaryPrimitives.WriteInt16LittleEndian(mnamData.AsSpan(12), 8);
        BinaryPrimitives.WriteInt16LittleEndian(mnamData.AsSpan(14), 8);
        var worldRecordBytes = BuildRecordBytes(worldspaceFormId, "WRLD", false,
            ("EDID", NullTermString("RuntimeParentStubWorld")),
            ("MNAM", mnamData));
        Array.Copy(worldRecordBytes, 0, data, worldRecordOffset, worldRecordBytes.Length);

        WriteTesFormHeader(data, runtimeWorldOffset, 0x82010000, 0x41, worldspaceFormId);
        WriteInt32BE(data, runtimeWorldOffset + 144, 768);
        WriteInt32BE(data, runtimeWorldOffset + 148, 512);
        WriteUInt16BE(data, runtimeWorldOffset + 152, unchecked((ushort)-4));
        WriteUInt16BE(data, runtimeWorldOffset + 154, unchecked((ushort)-4));
        WriteUInt16BE(data, runtimeWorldOffset + 156, unchecked(8));
        WriteUInt16BE(data, runtimeWorldOffset + 158, unchecked(8));

        WriteTesFormHeader(data, runtimeRefrOffset, 0x82010000, 0x3A, refrFormId);
        WriteUInt32BE(data, runtimeRefrOffset + 48, FileOffsetToVa(runtimeBaseObjectOffset));
        WriteFloatBE(data, runtimeRefrOffset + 64, 8704f);
        WriteFloatBE(data, runtimeRefrOffset + 68, -3840f);
        WriteFloatBE(data, runtimeRefrOffset + 72, 32f);
        WriteFloatBE(data, runtimeRefrOffset + 76, 1.0f);
        WriteUInt32BE(data, runtimeRefrOffset + 80, FileOffsetToVa(runtimeParentCellOffset));

        WriteTesFormHeader(data, runtimeBaseObjectOffset, 0x82010000, 0x15, baseFormId);
        WriteTesFormHeader(data, runtimeParentCellOffset, 0x82010000, 0x39, cellFormId);
        data[runtimeParentCellOffset + 52] = 0x00;

        var scanResult = new EsmRecordScanResult
        {
            MainRecords =
            [
                new DetectedMainRecord("WRLD", (uint)(worldRecordBytes.Length - 24), 0, worldspaceFormId,
                    worldRecordOffset, false)
            ],
            RefrRecords =
            [
                new ExtractedRefrRecord
                {
                    Header = new DetectedMainRecord("REFR", 0, 0, refrFormId, 1024, false),
                    BaseFormId = baseFormId,
                    Position = new PositionSubrecord(8704f, -3840f, 32f, 0f, 0f, 0f, 1024, false),
                    Scale = 1.0f
                }
            ],
            RuntimeEditorIds =
            [
                new RuntimeEditorIdEntry
                {
                    EditorId = "RuntimeParentStubWorld",
                    FormId = worldspaceFormId,
                    FormType = 0x41,
                    TesFormOffset = runtimeWorldOffset
                }
            ],
            RuntimeRefrFormEntries =
            [
                new RuntimeEditorIdEntry
                {
                    EditorId = "RuntimeParentStubRef",
                    FormId = refrFormId,
                    FormType = 0x3A,
                    TesFormOffset = runtimeRefrOffset
                }
            ]
        };

        var parser = CreateParser(scanResult, data);
        var result = parser.ParseAll();

        var cell = Assert.Single(result.Cells, item => item.FormId == cellFormId);
        var placedRef = Assert.Single(cell.PlacedObjects);
        var worldspace = Assert.Single(result.Worldspaces);

        Assert.False(cell.IsVirtual);
        Assert.Equal(2, cell.GridX);
        Assert.Equal(-1, cell.GridY);
        Assert.Equal(worldspaceFormId, cell.WorldspaceFormId);
        Assert.Equal(refrFormId, placedRef.FormId);
        Assert.Equal("ParentCell", placedRef.AssignmentSource);
        Assert.Contains(worldspace.Cells, item => item.FormId == cellFormId);
    }

    [Fact]
    public void ParseAll_RuntimeCellWorldspaceSignalCreatesWorldspaceStubAndLinksCell()
    {
        var data = new byte[DataSize];
        const uint worldspaceFormId = 0x00135150;
        const uint cellFormId = 0x00135160;
        const int runtimeCellOffset = 4096;
        const int runtimeWorldOffset = 4608;
        const int cellNameOffset = 5120;

        WriteTesFormHeader(data, runtimeCellOffset, 0x82010000, 0x39, cellFormId);
        WriteBSStringT(data, runtimeCellOffset + 44, FileOffsetToVa(cellNameOffset), "Runtime Cell Only",
            cellNameOffset);
        data[runtimeCellOffset + 52] = 0x00;
        WriteUInt32BE(data, runtimeCellOffset + 160, FileOffsetToVa(runtimeWorldOffset));

        WriteTesFormHeader(data, runtimeWorldOffset, 0x82010000, 0x41, worldspaceFormId);

        var scanResult = MakeScanResult(
            [],
            runtimeEditorIds:
            [
                new RuntimeEditorIdEntry
                {
                    EditorId = "RuntimeCellOnly",
                    FormId = cellFormId,
                    FormType = 0x39,
                    TesFormOffset = runtimeCellOffset,
                    DisplayName = "Runtime Cell Only"
                }
            ]);

        var parser = CreateParser(scanResult, data);
        var result = parser.ParseAll();

        var cell = Assert.Single(result.Cells);
        var worldspace = Assert.Single(result.Worldspaces);

        Assert.Equal(cellFormId, cell.FormId);
        Assert.Equal(worldspaceFormId, cell.WorldspaceFormId);
        Assert.Equal(worldspaceFormId, worldspace.FormId);
        Assert.Contains(worldspace.Cells, item => item.FormId == cellFormId);
    }

    [Fact]
    public void ParseAll_PartialWorldspaceIsEnrichedFromLinkedCellCoverage()
    {
        const uint worldspaceFormId = 0x00135170;
        const uint cellFormId = 0x00135171;

        var xclc = new byte[12];
        Buffer.BlockCopy(BitConverter.GetBytes(3), 0, xclc, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(-2), 0, xclc, 4, 4);

        var worldRecordBytes = BuildRecordBytes(
            worldspaceFormId,
            "WRLD",
            false,
            ("EDID", NullTermString("PartialWorld")));
        var cellRecordBytes = BuildRecordBytes(
            cellFormId,
            "CELL",
            false,
            ("EDID", NullTermString("LinkedCell")),
            ("XCLC", xclc));

        var data = new byte[worldRecordBytes.Length + cellRecordBytes.Length];
        Buffer.BlockCopy(worldRecordBytes, 0, data, 0, worldRecordBytes.Length);
        Buffer.BlockCopy(cellRecordBytes, 0, data, worldRecordBytes.Length, cellRecordBytes.Length);

        var scanResult = new EsmRecordScanResult
        {
            MainRecords =
            [
                new DetectedMainRecord("WRLD", (uint)(worldRecordBytes.Length - 24), 0, worldspaceFormId, 0, false),
                new DetectedMainRecord("CELL", (uint)(cellRecordBytes.Length - 24), 0, cellFormId,
                    worldRecordBytes.Length, false)
            ],
            CellToWorldspaceMap =
            {
                [cellFormId] = worldspaceFormId
            }
        };

        var parser = CreateParser(scanResult, data);
        var result = parser.ParseAll();

        var worldspace = Assert.Single(result.Worldspaces);
        Assert.Equal((short)3, worldspace.MapNWCellX);
        Assert.Equal((short)-2, worldspace.MapNWCellY);
        Assert.Equal((short)3, worldspace.MapSECellX);
        Assert.Equal((short)-2, worldspace.MapSECellY);
        Assert.Equal(3 * 4096f, worldspace.BoundsMinX);
        Assert.Equal(-2 * 4096f, worldspace.BoundsMinY);
        Assert.Equal(4 * 4096f, worldspace.BoundsMaxX);
        Assert.Equal(-1 * 4096f, worldspace.BoundsMaxY);
        Assert.Contains(worldspace.Cells, item => item.FormId == cellFormId);
    }

    [Fact]
    public void ParseAll_PartialWorldspaceDerivesEncounterZoneFromConsistentLinkedCells()
    {
        const uint worldspaceFormId = 0x00135180;
        const uint cellFormIdA = 0x00135181;
        const uint cellFormIdB = 0x00135182;
        const uint encounterZoneFormId = 0x00135190;

        var encounterZoneData = BitConverter.GetBytes(encounterZoneFormId);
        var worldRecordBytes = BuildRecordBytes(
            worldspaceFormId,
            "WRLD",
            false,
            ("EDID", NullTermString("PartialWorldWithEncounterZone")));
        var cellRecordBytesA = BuildRecordBytes(
            cellFormIdA,
            "CELL",
            false,
            ("EDID", NullTermString("LinkedCellA")),
            ("XEZN", encounterZoneData));
        var cellRecordBytesB = BuildRecordBytes(
            cellFormIdB,
            "CELL",
            false,
            ("EDID", NullTermString("LinkedCellB")),
            ("XEZN", encounterZoneData));

        var data = new byte[worldRecordBytes.Length + cellRecordBytesA.Length + cellRecordBytesB.Length];
        Buffer.BlockCopy(worldRecordBytes, 0, data, 0, worldRecordBytes.Length);
        Buffer.BlockCopy(cellRecordBytesA, 0, data, worldRecordBytes.Length, cellRecordBytesA.Length);
        Buffer.BlockCopy(cellRecordBytesB, 0, data, worldRecordBytes.Length + cellRecordBytesA.Length,
            cellRecordBytesB.Length);

        var scanResult = new EsmRecordScanResult
        {
            MainRecords =
            [
                new DetectedMainRecord("WRLD", (uint)(worldRecordBytes.Length - 24), 0, worldspaceFormId, 0, false),
                new DetectedMainRecord("CELL", (uint)(cellRecordBytesA.Length - 24), 0, cellFormIdA,
                    worldRecordBytes.Length, false),
                new DetectedMainRecord("CELL", (uint)(cellRecordBytesB.Length - 24), 0, cellFormIdB,
                    worldRecordBytes.Length + cellRecordBytesA.Length, false)
            ],
            CellToWorldspaceMap =
            {
                [cellFormIdA] = worldspaceFormId,
                [cellFormIdB] = worldspaceFormId
            }
        };

        var parser = CreateParser(scanResult, data);
        var result = parser.ParseAll();

        var worldspace = Assert.Single(result.Worldspaces);
        Assert.Equal(encounterZoneFormId, worldspace.EncounterZoneFormId);
    }

    [Fact]
    public void ParseAll_PartialWorldspacePreservesEsmEncounterZoneOverLinkedCellDerivedValue()
    {
        const uint worldspaceFormId = 0x00135186;
        const uint cellFormIdA = 0x00135187;
        const uint cellFormIdB = 0x00135188;
        const uint esmEncounterZoneFormId = 0x00135193;
        const uint derivedEncounterZoneFormId = 0x00135194;

        var worldRecordBytes = BuildRecordBytes(
            worldspaceFormId,
            "WRLD",
            false,
            ("EDID", NullTermString("WorldWithAuthoritativeEncounterZone")),
            ("XEZN", BitConverter.GetBytes(esmEncounterZoneFormId)));
        var cellRecordBytesA = BuildRecordBytes(
            cellFormIdA,
            "CELL",
            false,
            ("EDID", NullTermString("LinkedCellEsmA")),
            ("XEZN", BitConverter.GetBytes(derivedEncounterZoneFormId)));
        var cellRecordBytesB = BuildRecordBytes(
            cellFormIdB,
            "CELL",
            false,
            ("EDID", NullTermString("LinkedCellEsmB")),
            ("XEZN", BitConverter.GetBytes(derivedEncounterZoneFormId)));

        var data = new byte[worldRecordBytes.Length + cellRecordBytesA.Length + cellRecordBytesB.Length];
        Buffer.BlockCopy(worldRecordBytes, 0, data, 0, worldRecordBytes.Length);
        Buffer.BlockCopy(cellRecordBytesA, 0, data, worldRecordBytes.Length, cellRecordBytesA.Length);
        Buffer.BlockCopy(cellRecordBytesB, 0, data, worldRecordBytes.Length + cellRecordBytesA.Length,
            cellRecordBytesB.Length);

        var scanResult = new EsmRecordScanResult
        {
            MainRecords =
            [
                new DetectedMainRecord("WRLD", (uint)(worldRecordBytes.Length - 24), 0, worldspaceFormId, 0, false),
                new DetectedMainRecord("CELL", (uint)(cellRecordBytesA.Length - 24), 0, cellFormIdA,
                    worldRecordBytes.Length, false),
                new DetectedMainRecord("CELL", (uint)(cellRecordBytesB.Length - 24), 0, cellFormIdB,
                    worldRecordBytes.Length + cellRecordBytesA.Length, false)
            ],
            CellToWorldspaceMap =
            {
                [cellFormIdA] = worldspaceFormId,
                [cellFormIdB] = worldspaceFormId
            }
        };

        var parser = CreateParser(scanResult, data);
        var result = parser.ParseAll();

        var worldspace = Assert.Single(result.Worldspaces);
        Assert.Equal(esmEncounterZoneFormId, worldspace.EncounterZoneFormId);
    }

    [Fact]
    public void ParseAll_PartialWorldspaceDoesNotGuessEncounterZoneWhenLinkedCellsDisagree()
    {
        const uint worldspaceFormId = 0x00135183;
        const uint cellFormIdA = 0x00135184;
        const uint cellFormIdB = 0x00135185;
        const uint encounterZoneFormIdA = 0x00135191;
        const uint encounterZoneFormIdB = 0x00135192;

        var worldRecordBytes = BuildRecordBytes(
            worldspaceFormId,
            "WRLD",
            false,
            ("EDID", NullTermString("PartialWorldWithConflictingEncounterZones")));
        var cellRecordBytesA = BuildRecordBytes(
            cellFormIdA,
            "CELL",
            false,
            ("EDID", NullTermString("LinkedCellConflictA")),
            ("XEZN", BitConverter.GetBytes(encounterZoneFormIdA)));
        var cellRecordBytesB = BuildRecordBytes(
            cellFormIdB,
            "CELL",
            false,
            ("EDID", NullTermString("LinkedCellConflictB")),
            ("XEZN", BitConverter.GetBytes(encounterZoneFormIdB)));

        var data = new byte[worldRecordBytes.Length + cellRecordBytesA.Length + cellRecordBytesB.Length];
        Buffer.BlockCopy(worldRecordBytes, 0, data, 0, worldRecordBytes.Length);
        Buffer.BlockCopy(cellRecordBytesA, 0, data, worldRecordBytes.Length, cellRecordBytesA.Length);
        Buffer.BlockCopy(cellRecordBytesB, 0, data, worldRecordBytes.Length + cellRecordBytesA.Length,
            cellRecordBytesB.Length);

        var scanResult = new EsmRecordScanResult
        {
            MainRecords =
            [
                new DetectedMainRecord("WRLD", (uint)(worldRecordBytes.Length - 24), 0, worldspaceFormId, 0, false),
                new DetectedMainRecord("CELL", (uint)(cellRecordBytesA.Length - 24), 0, cellFormIdA,
                    worldRecordBytes.Length, false),
                new DetectedMainRecord("CELL", (uint)(cellRecordBytesB.Length - 24), 0, cellFormIdB,
                    worldRecordBytes.Length + cellRecordBytesA.Length, false)
            ],
            CellToWorldspaceMap =
            {
                [cellFormIdA] = worldspaceFormId,
                [cellFormIdB] = worldspaceFormId
            }
        };

        var parser = CreateParser(scanResult, data);
        var result = parser.ParseAll();

        var worldspace = Assert.Single(result.Worldspaces);
        Assert.Null(worldspace.EncounterZoneFormId);
    }

    [Fact]
    public void ParseAll_RuntimePlacedReferenceOverlayPreservesEsmFieldsAndFillsPlacementLinks()
    {
        var data = new byte[DataSize];
        const uint cellFormId = 0x00140010;
        const uint refrFormId = 0x00140020;
        const uint esmBaseFormId = 0x0014AA01;
        const uint esmOwnerFormId = 0x0014AA02;
        const uint esmEncounterZoneFormId = 0x0014AA05;
        const uint esmLockKeyFormId = 0x0014AA03;
        const uint esmLinkedRefKeywordFormId = 0x0014AA04;
        const int cellRecordOffset = 0;
        const int runtimeRefrOffset = 4096;
        const int extraOwnershipOffset = 4608;
        const int extraLockOffset = 4640;
        const int extraEncounterZoneOffset = 4672;
        const int extraEnableParentOffset = 4704;
        const int extraLinkedRefOffset = 4736;
        const int extraTeleportOffset = 4768;
        const int extraMapMarkerOffset = 4800;
        const int runtimeBaseObjectOffset = 5120;
        const int runtimeParentCellOffset = 5376;
        const int runtimeOwnerOffset = 5632;
        const int runtimeLockDataOffset = 5760;
        const int runtimeLockKeyOffset = 5888;
        const int runtimeEnableParentOffset = 6016;
        const int runtimeLinkedRefOffset = 6272;
        const int runtimeDestinationDoorOffset = 6528;
        const int runtimeTeleportDataOffset = 6784;
        const int runtimeMapDataOffset = 7040;
        const int runtimeMarkerNameOffset = 7296;
        const int runtimeEncounterZoneOffset = 7552;

        var cellRecordBytes = BuildRecordBytes(cellFormId, "CELL", false,
            ("EDID", NullTermString("CellFromEsm")));
        Array.Copy(cellRecordBytes, 0, data, cellRecordOffset, cellRecordBytes.Length);

        WriteTesFormHeader(data, runtimeRefrOffset, 0x82010000, 0x3A, refrFormId);
        WriteUInt32BE(data, runtimeRefrOffset + 48, FileOffsetToVa(runtimeBaseObjectOffset));
        WriteFloatBE(data, runtimeRefrOffset + 64, 111f);
        WriteFloatBE(data, runtimeRefrOffset + 68, 222f);
        WriteFloatBE(data, runtimeRefrOffset + 72, 333f);
        WriteFloatBE(data, runtimeRefrOffset + 76, 1.5f);
        WriteUInt32BE(data, runtimeRefrOffset + 80, FileOffsetToVa(runtimeParentCellOffset));
        WriteUInt32BE(data, runtimeRefrOffset + 88, FileOffsetToVa(extraOwnershipOffset));

        WriteTesFormHeader(data, runtimeBaseObjectOffset, 0x82010000, 0x15, 0x0014BB01);
        WriteTesFormHeader(data, runtimeParentCellOffset, 0x82010000, 0x39, cellFormId);
        WriteTesFormHeader(data, runtimeOwnerOffset, 0x82010000, 0x2A, 0x0014BB02);
        WriteTesFormHeader(data, runtimeLockKeyOffset, 0x82010000, 0x2D, 0x0014BB06);
        WriteTesFormHeader(data, runtimeEnableParentOffset, 0x82010000, 0x3A, 0x0014BB03);
        WriteTesFormHeader(data, runtimeLinkedRefOffset, 0x82010000, 0x3A, 0x0014BB04);
        WriteTesFormHeader(data, runtimeDestinationDoorOffset, 0x82010000, 0x3A, 0x0014BB05);
        WriteTesFormHeader(data, runtimeEncounterZoneOffset, 0x82010000, 0x61, 0x0014BB07);

        WriteExtraPointerNode(data, extraOwnershipOffset, ExtraOwnershipType, FileOffsetToVa(extraLockOffset),
            FileOffsetToVa(runtimeOwnerOffset));
        WriteExtraPointerNode(data, extraLockOffset, ExtraLockType, FileOffsetToVa(extraEncounterZoneOffset),
            FileOffsetToVa(runtimeLockDataOffset));
        WriteExtraPointerNode(data, extraEncounterZoneOffset, ExtraEncounterZoneType,
            FileOffsetToVa(extraEnableParentOffset), FileOffsetToVa(runtimeEncounterZoneOffset));
        WriteExtraEnableParentNode(data, extraEnableParentOffset, FileOffsetToVa(extraLinkedRefOffset),
            FileOffsetToVa(runtimeEnableParentOffset), 0x02);
        WriteExtraPointerNode(data, extraLinkedRefOffset, ExtraLinkedRefType, FileOffsetToVa(extraTeleportOffset),
            FileOffsetToVa(runtimeLinkedRefOffset));
        WriteExtraPointerNode(data, extraTeleportOffset, ExtraTeleportType, FileOffsetToVa(extraMapMarkerOffset),
            FileOffsetToVa(runtimeTeleportDataOffset));
        WriteExtraPointerNode(data, extraMapMarkerOffset, ExtraMapMarkerType, 0, FileOffsetToVa(runtimeMapDataOffset));

        WriteRefrLockData(data, runtimeLockDataOffset, 25, FileOffsetToVa(runtimeLockKeyOffset), 0x03, 4, 2);
        WriteDoorTeleportData(data, runtimeTeleportDataOffset, FileOffsetToVa(runtimeDestinationDoorOffset));
        WriteMapMarkerData(data, runtimeMapDataOffset, FileOffsetToVa(runtimeMarkerNameOffset), "Runtime Marker",
            runtimeMarkerNameOffset, 9);

        var scanResult = new EsmRecordScanResult
        {
            MainRecords =
            [
                new DetectedMainRecord("CELL", (uint)(cellRecordBytes.Length - 24), 0, cellFormId, cellRecordOffset,
                    false)
            ],
            RefrRecords =
            [
                new ExtractedRefrRecord
                {
                    Header = new DetectedMainRecord("REFR", 0, 0, refrFormId, 8192, false),
                    BaseFormId = esmBaseFormId,
                    Position = new PositionSubrecord(10f, 20f, 30f, 0f, 0f, 0f, 8192, false),
                    Scale = 1.0f,
                    OwnerFormId = esmOwnerFormId,
                    EncounterZoneFormId = esmEncounterZoneFormId,
                    LockLevel = 5,
                    LockKeyFormId = esmLockKeyFormId,
                    LinkedRefKeywordFormId = esmLinkedRefKeywordFormId
                }
            ],
            CellToRefrMap =
            {
                [cellFormId] = [refrFormId]
            },
            RuntimeRefrFormEntries =
            [
                new RuntimeEditorIdEntry
                {
                    EditorId = "PlacedRefRuntime",
                    FormId = refrFormId,
                    FormType = 0x3A,
                    TesFormOffset = runtimeRefrOffset
                }
            ]
        };

        var parser = CreateParser(scanResult, data);
        var result = parser.ParseAll();

        var cell = Assert.Single(result.Cells);
        var placedRef = Assert.Single(cell.PlacedObjects);

        Assert.Equal(esmBaseFormId, placedRef.BaseFormId);
        Assert.Equal(10f, placedRef.X);
        Assert.Equal(20f, placedRef.Y);
        Assert.Equal(30f, placedRef.Z);
        Assert.Equal(esmOwnerFormId, placedRef.OwnerFormId);
        Assert.Equal(esmEncounterZoneFormId, placedRef.EncounterZoneFormId);
        Assert.Equal((byte)5, placedRef.LockLevel);
        Assert.Equal(esmLockKeyFormId, placedRef.LockKeyFormId);
        Assert.Equal((byte)0x03, placedRef.LockFlags);
        Assert.Equal(4u, placedRef.LockNumTries);
        Assert.Equal(2u, placedRef.LockTimesUnlocked);
        Assert.Equal(0x0014BB03u, placedRef.EnableParentFormId);
        Assert.Equal((byte)0x02, placedRef.EnableParentFlags);
        Assert.Equal(esmLinkedRefKeywordFormId, placedRef.LinkedRefKeywordFormId);
        Assert.Equal(0x0014BB04u, placedRef.LinkedRefFormId);
        Assert.Equal(0x0014BB05u, placedRef.DestinationDoorFormId);
        Assert.True(placedRef.IsMapMarker);
        Assert.Equal(MapMarkerType.Office, placedRef.MarkerType);
        Assert.Equal("Runtime Marker", placedRef.MarkerName);
    }

    [Fact]
    public void ParseAll_RuntimePersistentCellFallbackAndChildLinksFlowIntoPlacedReference()
    {
        var data = new byte[DataSize];
        const uint cellFormId = 0x00145010;
        const uint refrFormId = 0x00145020;
        const uint esmBaseFormId = 0x00145A01;
        const int cellRecordOffset = 0;
        const int refrRecordOffset = 600_000;
        const int runtimeRefrOffset = 4096;
        const int extraPersistentCellOffset = 4608;
        const int extraEncounterZoneOffset = 4640;
        const int extraLinkedRefChildrenOffset = 4672;
        const int linkedRefChildNodeOffset = 4704;
        const int runtimeBaseObjectOffset = 5120;
        const int runtimePersistentCellOffset = 5376;
        const int runtimeChildAOffset = 5632;
        const int runtimeChildBOffset = 5888;
        const int runtimeEncounterZoneOffset = 6144;

        var cellRecordBytes = BuildRecordBytes(cellFormId, "CELL", false,
            ("EDID", NullTermString("PersistentCellFromEsm")));
        Array.Copy(cellRecordBytes, 0, data, cellRecordOffset, cellRecordBytes.Length);

        WriteTesFormHeader(data, runtimeRefrOffset, 0x82010000, 0x3A, refrFormId);
        WriteUInt32BE(data, runtimeRefrOffset + 48, FileOffsetToVa(runtimeBaseObjectOffset));
        WriteFloatBE(data, runtimeRefrOffset + 64, 1536f);
        WriteFloatBE(data, runtimeRefrOffset + 68, 2048f);
        WriteFloatBE(data, runtimeRefrOffset + 72, 64f);
        WriteFloatBE(data, runtimeRefrOffset + 76, 1.0f);
        WriteUInt32BE(data, runtimeRefrOffset + 88, FileOffsetToVa(extraPersistentCellOffset));

        WriteTesFormHeader(data, runtimeBaseObjectOffset, 0x82010000, 0x15, 0x00145B01);
        WriteTesFormHeader(data, runtimePersistentCellOffset, 0x82010000, 0x39, cellFormId);
        WriteTesFormHeader(data, runtimeChildAOffset, 0x82010000, 0x3A, 0x00145B02);
        WriteTesFormHeader(data, runtimeChildBOffset, 0x82010000, 0x3B, 0x00145B03);
        WriteTesFormHeader(data, runtimeEncounterZoneOffset, 0x82010000, 0x61, 0x00145B04);

        WriteExtraPointerNode(data, extraPersistentCellOffset, ExtraPersistentCellType,
            FileOffsetToVa(extraEncounterZoneOffset), FileOffsetToVa(runtimePersistentCellOffset));
        WriteExtraPointerNode(data, extraEncounterZoneOffset, ExtraEncounterZoneType,
            FileOffsetToVa(extraLinkedRefChildrenOffset), FileOffsetToVa(runtimeEncounterZoneOffset));
        WriteExtraLinkedRefChildrenNode(
            data,
            extraLinkedRefChildrenOffset,
            0,
            FileOffsetToVa(runtimeChildAOffset),
            FileOffsetToVa(linkedRefChildNodeOffset));
        WriteSimpleListNode(data, linkedRefChildNodeOffset, FileOffsetToVa(runtimeChildBOffset), 0);

        var scanResult = new EsmRecordScanResult
        {
            MainRecords =
            [
                new DetectedMainRecord("CELL", (uint)(cellRecordBytes.Length - 24), 0, cellFormId, cellRecordOffset,
                    false)
            ],
            RefrRecords =
            [
                new ExtractedRefrRecord
                {
                    Header = new DetectedMainRecord("REFR", 0, 0, refrFormId, refrRecordOffset, false),
                    BaseFormId = esmBaseFormId,
                    Position = new PositionSubrecord(1536f, 2048f, 64f, 0f, 0f, 0f, refrRecordOffset, false),
                    Scale = 1.0f
                }
            ],
            RuntimeRefrFormEntries =
            [
                new RuntimeEditorIdEntry
                {
                    EditorId = "PersistentRefRuntime",
                    FormId = refrFormId,
                    FormType = 0x3A,
                    TesFormOffset = runtimeRefrOffset
                }
            ]
        };

        var parser = CreateParser(scanResult, data);
        var result = parser.ParseAll();

        var cell = Assert.Single(result.Cells, item => item.FormId == cellFormId);
        var placedRef = Assert.Single(cell.PlacedObjects);

        Assert.Equal(refrFormId, placedRef.FormId);
        Assert.Equal(cellFormId, placedRef.PersistentCellFormId);
        Assert.Equal(0x00145B04u, placedRef.EncounterZoneFormId);
        Assert.Equal([0x00145B02u, 0x00145B03u], placedRef.LinkedRefChildrenFormIds);
        Assert.Equal("ParentCell", placedRef.AssignmentSource);
    }

    [Fact]
    public void ParseAll_RuntimeActorStartExtrasFlowIntoPlacedReference()
    {
        var data = new byte[DataSize];
        const uint cellFormId = 0x00145C00;
        const uint refrFormId = 0x00145C01;
        const uint esmBaseFormId = 0x00145C02;
        const int cellRecordOffset = 0;
        const int runtimeRefrOffset = 4096;
        const int runtimeBaseObjectOffset = 5376;
        const int runtimeParentCellOffset = 5632;
        const int runtimeStartCellOffset = 5888;
        const int runtimePackageCellOffset = 6144;
        const int extraStartingPositionOffset = 4608;
        const int extraPackageStartLocationOffset = 4672;
        const int extraStartingWorldOrCellOffset = 4736;

        var cellRecordBytes = BuildRecordBytes(
            cellFormId,
            "CELL",
            false,
            ("EDID", NullTermString("RuntimeStartCell")));
        Buffer.BlockCopy(cellRecordBytes, 0, data, cellRecordOffset, cellRecordBytes.Length);

        WriteTesFormHeader(data, runtimeRefrOffset, 0x82010000, 0x3B, refrFormId);
        WriteUInt32BE(data, runtimeRefrOffset + 48, FileOffsetToVa(runtimeBaseObjectOffset));
        WriteFloatBE(data, runtimeRefrOffset + 76, 1.0f);
        WriteUInt32BE(data, runtimeRefrOffset + 80, FileOffsetToVa(runtimeParentCellOffset));
        WriteUInt32BE(data, runtimeRefrOffset + 88, FileOffsetToVa(extraStartingPositionOffset));

        WriteTesFormHeader(data, runtimeBaseObjectOffset, 0x82010000, 0x2A, esmBaseFormId);
        WriteTesFormHeader(data, runtimeParentCellOffset, 0x82010000, 0x39, cellFormId);
        WriteTesFormHeader(data, runtimeStartCellOffset, 0x82010000, 0x39, 0x00145C03);
        WriteTesFormHeader(data, runtimePackageCellOffset, 0x82010000, 0x39, 0x00145C04);

        WriteExtraStartingPositionNode(
            data,
            extraStartingPositionOffset,
            FileOffsetToVa(extraPackageStartLocationOffset),
            3200f,
            4100f,
            80f,
            0.10f,
            0.20f,
            0.30f);
        WriteExtraPackageStartLocationNode(
            data,
            extraPackageStartLocationOffset,
            FileOffsetToVa(extraStartingWorldOrCellOffset),
            FileOffsetToVa(runtimePackageCellOffset),
            3300f,
            4200f,
            88f,
            0.45f);
        WriteExtraPointerNode(
            data,
            extraStartingWorldOrCellOffset,
            ExtraStartingWorldOrCellType,
            0,
            FileOffsetToVa(runtimeStartCellOffset));

        var scanResult = new EsmRecordScanResult
        {
            MainRecords =
            [
                new DetectedMainRecord("CELL", (uint)(cellRecordBytes.Length - 24), 0, cellFormId, cellRecordOffset,
                    false)
            ],
            RefrRecords =
            [
                new ExtractedRefrRecord
                {
                    Header = new DetectedMainRecord("ACHR", 0, 0, refrFormId, 0x2000, false),
                    BaseFormId = esmBaseFormId,
                    Scale = 1.0f
                }
            ],
            RuntimeRefrFormEntries =
            [
                new RuntimeEditorIdEntry
                {
                    EditorId = "RuntimeActorStart",
                    FormId = refrFormId,
                    FormType = 0x3B,
                    TesFormOffset = runtimeRefrOffset
                }
            ]
        };

        var parser = CreateParser(scanResult, data);
        var result = parser.ParseAll();

        var cell = Assert.Single(result.Cells, item => item.FormId == cellFormId);
        var placedRef = Assert.Single(cell.PlacedObjects);

        Assert.NotNull(placedRef.StartingPosition);
        Assert.Equal(3200f, placedRef.StartingPosition!.X);
        Assert.Equal(4100f, placedRef.StartingPosition.Y);
        Assert.Equal(80f, placedRef.StartingPosition.Z);
        Assert.Equal(0.30f, placedRef.StartingPosition.RotZ);
        Assert.Equal(0x00145C03u, placedRef.StartingWorldOrCellFormId);
        Assert.NotNull(placedRef.PackageStartLocation);
        Assert.Equal(0x00145C04u, placedRef.PackageStartLocation!.LocationFormId);
        Assert.Equal(3300f, placedRef.PackageStartLocation.X);
        Assert.Equal(4200f, placedRef.PackageStartLocation.Y);
        Assert.Equal(88f, placedRef.PackageStartLocation.Z);
        Assert.Equal(0.45f, placedRef.PackageStartLocation.RotZ);
    }

    [Fact]
    public void ParseAll_RuntimeRadiusFlowsIntoPlacedReference()
    {
        var data = new byte[DataSize];
        const uint cellFormId = 0x00145CA0;
        const uint refrFormId = 0x00145CA1;
        const uint esmBaseFormId = 0x00145CA2;
        const int cellRecordOffset = 0;
        const int runtimeRefrOffset = 4096;
        const int extraRadiusOffset = 4608;
        const int runtimeBaseObjectOffset = 5376;
        const int runtimeParentCellOffset = 5632;

        var cellRecordBytes = BuildRecordBytes(
            cellFormId,
            "CELL",
            false,
            ("EDID", NullTermString("RuntimeRadiusCell")));
        Buffer.BlockCopy(cellRecordBytes, 0, data, cellRecordOffset, cellRecordBytes.Length);

        WriteTesFormHeader(data, runtimeRefrOffset, 0x82010000, 0x3A, refrFormId);
        WriteUInt32BE(data, runtimeRefrOffset + 48, FileOffsetToVa(runtimeBaseObjectOffset));
        WriteFloatBE(data, runtimeRefrOffset + 64, 2048f);
        WriteFloatBE(data, runtimeRefrOffset + 68, 1024f);
        WriteFloatBE(data, runtimeRefrOffset + 72, 64f);
        WriteFloatBE(data, runtimeRefrOffset + 76, 1.0f);
        WriteUInt32BE(data, runtimeRefrOffset + 80, FileOffsetToVa(runtimeParentCellOffset));
        WriteUInt32BE(data, runtimeRefrOffset + 88, FileOffsetToVa(extraRadiusOffset));

        WriteTesFormHeader(data, runtimeBaseObjectOffset, 0x82010000, 0x15, esmBaseFormId);
        WriteTesFormHeader(data, runtimeParentCellOffset, 0x82010000, 0x39, cellFormId);
        WriteExtraRadiusNode(data, extraRadiusOffset, 0, 288f);

        var scanResult = new EsmRecordScanResult
        {
            MainRecords =
            [
                new DetectedMainRecord("CELL", (uint)(cellRecordBytes.Length - 24), 0, cellFormId, cellRecordOffset,
                    false)
            ],
            RefrRecords =
            [
                new ExtractedRefrRecord
                {
                    Header = new DetectedMainRecord("REFR", 0, 0, refrFormId, 0x2600, false),
                    BaseFormId = esmBaseFormId,
                    Scale = 1.0f
                }
            ],
            RuntimeRefrFormEntries =
            [
                new RuntimeEditorIdEntry
                {
                    EditorId = "RuntimeRadiusRef",
                    FormId = refrFormId,
                    FormType = 0x3A,
                    TesFormOffset = runtimeRefrOffset
                }
            ]
        };

        var parser = CreateParser(scanResult, data);
        var result = parser.ParseAll();

        var cell = Assert.Single(result.Cells, item => item.FormId == cellFormId);
        var placedRef = Assert.Single(cell.PlacedObjects);
        Assert.Equal(288f, placedRef.Radius);
    }

    [Fact]
    public void ParseAll_EsmRadiusWinsOverRuntimeRadius()
    {
        var data = new byte[DataSize];
        const uint cellFormId = 0x00145CB0;
        const uint refrFormId = 0x00145CB1;
        const uint esmBaseFormId = 0x00145CB2;
        const int cellRecordOffset = 0;
        const int runtimeRefrOffset = 4096;
        const int extraRadiusOffset = 4608;
        const int runtimeBaseObjectOffset = 5376;
        const int runtimeParentCellOffset = 5632;

        var cellRecordBytes = BuildRecordBytes(
            cellFormId,
            "CELL",
            false,
            ("EDID", NullTermString("RuntimeRadiusMergeCell")));
        Buffer.BlockCopy(cellRecordBytes, 0, data, cellRecordOffset, cellRecordBytes.Length);

        WriteTesFormHeader(data, runtimeRefrOffset, 0x82010000, 0x3A, refrFormId);
        WriteUInt32BE(data, runtimeRefrOffset + 48, FileOffsetToVa(runtimeBaseObjectOffset));
        WriteFloatBE(data, runtimeRefrOffset + 64, 2304f);
        WriteFloatBE(data, runtimeRefrOffset + 68, 1280f);
        WriteFloatBE(data, runtimeRefrOffset + 72, 72f);
        WriteFloatBE(data, runtimeRefrOffset + 76, 1.0f);
        WriteUInt32BE(data, runtimeRefrOffset + 80, FileOffsetToVa(runtimeParentCellOffset));
        WriteUInt32BE(data, runtimeRefrOffset + 88, FileOffsetToVa(extraRadiusOffset));

        WriteTesFormHeader(data, runtimeBaseObjectOffset, 0x82010000, 0x15, esmBaseFormId);
        WriteTesFormHeader(data, runtimeParentCellOffset, 0x82010000, 0x39, cellFormId);
        WriteExtraRadiusNode(data, extraRadiusOffset, 0, 320f);

        var scanResult = new EsmRecordScanResult
        {
            MainRecords =
            [
                new DetectedMainRecord("CELL", (uint)(cellRecordBytes.Length - 24), 0, cellFormId, cellRecordOffset,
                    false)
            ],
            RefrRecords =
            [
                new ExtractedRefrRecord
                {
                    Header = new DetectedMainRecord("REFR", 0, 0, refrFormId, 0x2800, false),
                    BaseFormId = esmBaseFormId,
                    Radius = 144f,
                    Scale = 1.0f
                }
            ],
            RuntimeRefrFormEntries =
            [
                new RuntimeEditorIdEntry
                {
                    EditorId = "RuntimeRadiusMergeRef",
                    FormId = refrFormId,
                    FormType = 0x3A,
                    TesFormOffset = runtimeRefrOffset
                }
            ]
        };

        var parser = CreateParser(scanResult, data);
        var result = parser.ParseAll();

        var cell = Assert.Single(result.Cells, item => item.FormId == cellFormId);
        var placedRef = Assert.Single(cell.PlacedObjects);
        Assert.Equal(144f, placedRef.Radius);
    }

    [Fact]
    public void ParseAll_RuntimeMerchantContainerAndLeveledCreatureExtrasFlowIntoPlacedReference()
    {
        var data = new byte[DataSize];
        const uint cellFormId = 0x00145D00;
        const uint refrFormId = 0x00145D01;
        const uint esmBaseFormId = 0x00145D02;
        const int cellRecordOffset = 0;
        const int runtimeRefrOffset = 4096;
        const int runtimeBaseObjectOffset = 5376;
        const int runtimeParentCellOffset = 5632;
        const int runtimeMerchantContainerOffset = 5888;
        const int runtimeOriginalBaseOffset = 6144;
        const int runtimeTemplateOffset = 6400;
        const int extraMerchantContainerOffset = 4608;
        const int extraLeveledCreatureOffset = 4640;

        var cellRecordBytes = BuildRecordBytes(
            cellFormId,
            "CELL",
            false,
            ("EDID", NullTermString("RuntimeMerchantCell")));
        Buffer.BlockCopy(cellRecordBytes, 0, data, cellRecordOffset, cellRecordBytes.Length);

        WriteTesFormHeader(data, runtimeRefrOffset, 0x82010000, 0x3C, refrFormId);
        WriteUInt32BE(data, runtimeRefrOffset + 48, FileOffsetToVa(runtimeBaseObjectOffset));
        WriteFloatBE(data, runtimeRefrOffset + 76, 1.0f);
        WriteUInt32BE(data, runtimeRefrOffset + 80, FileOffsetToVa(runtimeParentCellOffset));
        WriteUInt32BE(data, runtimeRefrOffset + 88, FileOffsetToVa(extraMerchantContainerOffset));

        WriteTesFormHeader(data, runtimeBaseObjectOffset, 0x82010000, 0x2B, esmBaseFormId);
        WriteTesFormHeader(data, runtimeParentCellOffset, 0x82010000, 0x39, cellFormId);
        WriteTesFormHeader(data, runtimeMerchantContainerOffset, 0x82010000, 0x3A, 0x00145D03);
        WriteTesFormHeader(data, runtimeOriginalBaseOffset, 0x82010000, 0x2B, 0x00145D04);
        WriteTesFormHeader(data, runtimeTemplateOffset, 0x82010000, 0x2C, 0x00145D05);

        WriteExtraPointerNode(
            data,
            extraMerchantContainerOffset,
            ExtraMerchantContainerType,
            FileOffsetToVa(extraLeveledCreatureOffset),
            FileOffsetToVa(runtimeMerchantContainerOffset));
        WriteExtraLeveledCreatureNode(
            data,
            extraLeveledCreatureOffset,
            0,
            FileOffsetToVa(runtimeOriginalBaseOffset),
            FileOffsetToVa(runtimeTemplateOffset));

        var scanResult = new EsmRecordScanResult
        {
            MainRecords =
            [
                new DetectedMainRecord("CELL", (uint)(cellRecordBytes.Length - 24), 0, cellFormId, cellRecordOffset,
                    false)
            ],
            RefrRecords =
            [
                new ExtractedRefrRecord
                {
                    Header = new DetectedMainRecord("ACRE", 0, 0, refrFormId, 0x2400, false),
                    BaseFormId = esmBaseFormId,
                    Scale = 1.0f
                }
            ],
            RuntimeRefrFormEntries =
            [
                new RuntimeEditorIdEntry
                {
                    EditorId = "RuntimeMerchantCreature",
                    FormId = refrFormId,
                    FormType = 0x3C,
                    TesFormOffset = runtimeRefrOffset
                }
            ]
        };

        var parser = CreateParser(scanResult, data);
        var result = parser.ParseAll();

        var cell = Assert.Single(result.Cells, item => item.FormId == cellFormId);
        var placedRef = Assert.Single(cell.PlacedObjects);

        Assert.Equal(0x00145D03u, placedRef.MerchantContainerFormId);
        Assert.Equal(0x00145D04u, placedRef.LeveledCreatureOriginalBaseFormId);
        Assert.Equal(0x00145D05u, placedRef.LeveledCreatureTemplateFormId);
    }

    private RecordParser CreateParser(EsmRecordScanResult scanResult, byte[] data)
    {
        _tempFilePath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllBytes(_tempFilePath, data);

        _mmf = MemoryMappedFile.CreateFromFile(_tempFilePath, FileMode.Open, null, data.Length,
            MemoryMappedFileAccess.Read);
        _accessor = _mmf.CreateViewAccessor(0, data.Length, MemoryMappedFileAccess.Read);

        var minidumpInfo = new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            NumberOfStreams = 1,
            MemoryRegions =
            [
                new MinidumpMemoryRegion
                {
                    VirtualAddress = Xbox360MemoryUtils.VaToLong(HeapBaseVa),
                    Size = data.Length,
                    FileOffset = 0
                }
            ]
        };

        return new RecordParser(scanResult, accessor: _accessor, fileSize: data.Length, minidumpInfo: minidumpInfo);
    }

    private static void WriteTesFormHeader(byte[] data, int fileOffset, uint vtable, byte formType, uint formId)
    {
        WriteUInt32BE(data, fileOffset, vtable);
        data[fileOffset + 4] = formType;
        WriteUInt32BE(data, fileOffset + 12, formId);
    }

    private static void WriteBSStringT(byte[] data, int bstFileOffset, uint stringVa, string text,
        int stringDataFileOffset)
    {
        WriteUInt32BE(data, bstFileOffset, stringVa);
        WriteUInt16BE(data, bstFileOffset + 4, (ushort)text.Length);
        Encoding.ASCII.GetBytes(text, data.AsSpan(stringDataFileOffset, text.Length));
    }

    private static void WriteBounds(
        byte[] data,
        int offset,
        short x1,
        short y1,
        short z1,
        short x2,
        short y2,
        short z2)
    {
        WriteUInt16BE(data, offset, unchecked((ushort)x1));
        WriteUInt16BE(data, offset + 2, unchecked((ushort)y1));
        WriteUInt16BE(data, offset + 4, unchecked((ushort)z1));
        WriteUInt16BE(data, offset + 6, unchecked((ushort)x2));
        WriteUInt16BE(data, offset + 8, unchecked((ushort)y2));
        WriteUInt16BE(data, offset + 10, unchecked((ushort)z2));
    }

    private static uint FileOffsetToVa(int fileOffset)
    {
        return HeapBaseVa + (uint)fileOffset;
    }

    private static void WriteExtraPointerNode(byte[] data, int nodeOffset, byte extraType, uint nextVa, uint payloadVa)
    {
        data[nodeOffset + 4] = extraType;
        WriteUInt32BE(data, nodeOffset + 8, nextVa);
        WriteUInt32BE(data, nodeOffset + 12, payloadVa);
    }

    private static void WriteExtraEnableParentNode(
        byte[] data,
        int nodeOffset,
        uint nextVa,
        uint parentVa,
        byte flags)
    {
        WriteExtraPointerNode(data, nodeOffset, ExtraEnableParentType, nextVa, parentVa);
        data[nodeOffset + 16] = flags;
    }

    private static void WriteExtraLinkedRefChildrenNode(
        byte[] data,
        int nodeOffset,
        uint nextVa,
        uint childVa,
        uint childNextVa)
    {
        data[nodeOffset + 4] = ExtraLinkedRefChildrenType;
        WriteUInt32BE(data, nodeOffset + 8, nextVa);
        WriteUInt32BE(data, nodeOffset + 12, childVa);
        WriteUInt32BE(data, nodeOffset + 16, childNextVa);
    }

    private static void WriteExtraStartingPositionNode(
        byte[] data,
        int nodeOffset,
        uint nextVa,
        float x,
        float y,
        float z,
        float rotX,
        float rotY,
        float rotZ)
    {
        data[nodeOffset + 4] = ExtraStartingPositionType;
        WriteUInt32BE(data, nodeOffset + 8, nextVa);
        WriteFloatBE(data, nodeOffset + 12, x);
        WriteFloatBE(data, nodeOffset + 16, y);
        WriteFloatBE(data, nodeOffset + 20, z);
        WriteFloatBE(data, nodeOffset + 24, rotX);
        WriteFloatBE(data, nodeOffset + 28, rotY);
        WriteFloatBE(data, nodeOffset + 32, rotZ);
    }

    private static void WriteExtraPackageStartLocationNode(
        byte[] data,
        int nodeOffset,
        uint nextVa,
        uint locationVa,
        float x,
        float y,
        float z,
        float rotZ)
    {
        data[nodeOffset + 4] = ExtraPackageStartLocationType;
        WriteUInt32BE(data, nodeOffset + 8, nextVa);
        WriteUInt32BE(data, nodeOffset + 12, locationVa);
        WriteFloatBE(data, nodeOffset + 16, x);
        WriteFloatBE(data, nodeOffset + 20, y);
        WriteFloatBE(data, nodeOffset + 24, z);
        WriteFloatBE(data, nodeOffset + 28, rotZ);
    }

    private static void WriteExtraRadiusNode(byte[] data, int nodeOffset, uint nextVa, float radius)
    {
        data[nodeOffset + 4] = ExtraRadiusType;
        WriteUInt32BE(data, nodeOffset + 8, nextVa);
        WriteFloatBE(data, nodeOffset + 12, radius);
    }

    private static void WriteExtraLeveledCreatureNode(
        byte[] data,
        int nodeOffset,
        uint nextVa,
        uint originalBaseVa,
        uint templateVa)
    {
        data[nodeOffset + 4] = ExtraLeveledCreatureType;
        WriteUInt32BE(data, nodeOffset + 8, nextVa);
        WriteUInt32BE(data, nodeOffset + 12, originalBaseVa);
        WriteUInt32BE(data, nodeOffset + 16, templateVa);
    }

    private static void WriteSimpleListNode(byte[] data, int nodeOffset, uint itemVa, uint nextVa)
    {
        WriteUInt32BE(data, nodeOffset, itemVa);
        WriteUInt32BE(data, nodeOffset + 4, nextVa);
    }

    private static void WriteQuestObjective(
        byte[] data,
        int objectiveOffset,
        int index,
        string displayText,
        int displayTextOffset,
        uint ownerQuestVa,
        bool initialized,
        uint state)
    {
        WriteUInt32BE(data, objectiveOffset + 4, unchecked((uint)index));
        WriteBSStringT(data, objectiveOffset + 8, FileOffsetToVa(displayTextOffset), displayText, displayTextOffset);
        WriteUInt32BE(data, objectiveOffset + 16, ownerQuestVa);
        data[objectiveOffset + 28] = initialized ? (byte)1 : (byte)0;
        WriteUInt32BE(data, objectiveOffset + 32, state);
    }

    private static void WriteQuestStage(byte[] data, int stageOffset, byte index, uint firstItemVa, uint nextNodeVa = 0)
    {
        data[stageOffset] = index;
        WriteUInt32BE(data, stageOffset + 4, firstItemVa);
        WriteUInt32BE(data, stageOffset + 8, nextNodeVa);
    }

    private static void WriteQuestStageItem(
        byte[] data,
        int stageItemOffset,
        byte flags,
        uint ownerQuestVa,
        bool hasLogEntry)
    {
        data[stageItemOffset] = flags;
        data[stageItemOffset + 117] = hasLogEntry ? (byte)1 : (byte)0;
        WriteUInt32BE(data, stageItemOffset + 124, ownerQuestVa);
    }

    private static void WriteDoorTeleportData(byte[] data, int teleportDataOffset, uint linkedDoorVa)
    {
        WriteUInt32BE(data, teleportDataOffset, linkedDoorVa);
    }

    private static void WriteRefrLockData(
        byte[] data,
        int lockDataOffset,
        byte level,
        uint keyVa,
        byte flags,
        uint numTries,
        uint timesUnlocked)
    {
        data[lockDataOffset] = level;
        WriteUInt32BE(data, lockDataOffset + 4, keyVa);
        data[lockDataOffset + 8] = flags;
        WriteUInt32BE(data, lockDataOffset + 12, numTries);
        WriteUInt32BE(data, lockDataOffset + 16, timesUnlocked);
    }

    private static void WriteMapMarkerData(
        byte[] data,
        int mapDataOffset,
        uint markerNameVa,
        string markerName,
        int markerNameOffset,
        ushort markerType)
    {
        WriteBSStringT(data, mapDataOffset + 4, markerNameVa, markerName, markerNameOffset);
        WriteUInt16BE(data, mapDataOffset + 14, markerType);
    }

    private static uint PackCellMapKey(int gridX, int gridY)
    {
        return unchecked((uint)((gridX << 16) | (ushort)gridY));
    }
}