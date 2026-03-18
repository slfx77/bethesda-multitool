using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;
using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.BinaryTestWriter;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

public sealed class RuntimeParityStructReaderTests : IDisposable
{
    private const int DataSize = 16 * 1024;
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
    public void ReadRuntimeFormList_WithSimpleList_ReturnsOrderedFormIds()
    {
        var data = new byte[DataSize];
        const uint formListFormId = 0x00001000;
        const int structOffset = 0;
        const int nodeOffset = 512;
        const int itemAOffset = 2048;
        const int itemBOffset = 2304;

        WriteTesFormHeader(data, structOffset, 0x82010000, 0x55, formListFormId);
        WriteUInt32BE(data, structOffset + 40, FileOffsetToVa(itemAOffset));
        WriteUInt32BE(data, structOffset + 44, FileOffsetToVa(nodeOffset));
        WriteUInt32BE(data, nodeOffset, FileOffsetToVa(itemBOffset));

        WriteTesFormHeader(data, itemAOffset, 0x82010000, 0x28, 0x0000AAAA);
        WriteTesFormHeader(data, itemBOffset, 0x82010000, 0x29, 0x0000BBBB);

        var reader = CreateReader(data);
        var result = reader.ReadRuntimeFormList(MakeEntry("FormListTest", formListFormId, 0x55, structOffset));

        Assert.NotNull(result);
        Assert.Equal(formListFormId, result.FormId);
        Assert.Equal([0x0000AAAAu, 0x0000BBBBu], result.FormIds);
    }

    [Fact]
    public void ReadRuntimeLeveledList_WithRuntimeEntries_ReturnsSemanticEntries()
    {
        var data = new byte[DataSize];
        const uint leveledListFormId = 0x00002000;
        const int structOffset = 0;
        const int leveledObjectAOffset = 1024;
        const int levelNodeOffset = 1100;
        const int leveledObjectBOffset = 1120;
        const int entryAOffset = 2048;
        const int entryBOffset = 2304;
        const int globalOffset = 3072;

        WriteTesFormHeader(data, structOffset, 0x82010000, 0x34, leveledListFormId);
        WriteUInt32BE(data, structOffset + 68, FileOffsetToVa(leveledObjectAOffset));
        WriteUInt32BE(data, structOffset + 72, FileOffsetToVa(levelNodeOffset));
        data[structOffset + 76] = 25;
        data[structOffset + 77] = 0x05;
        WriteUInt32BE(data, structOffset + 80, FileOffsetToVa(globalOffset));

        WriteTesFormHeader(data, entryAOffset, 0x82010000, 0x28, 0x0000C001);
        WriteTesFormHeader(data, entryBOffset, 0x82010000, 0x29, 0x0000C002);
        WriteTesFormHeader(data, globalOffset, 0x82010000, 0x06, 0x0000A001);

        WriteUInt32BE(data, leveledObjectAOffset, FileOffsetToVa(entryAOffset));
        WriteUInt16BE(data, leveledObjectAOffset + 4, 2);
        WriteUInt16BE(data, leveledObjectAOffset + 6, 10);

        WriteUInt32BE(data, levelNodeOffset, FileOffsetToVa(leveledObjectBOffset));
        WriteUInt32BE(data, levelNodeOffset + 4, 0);
        WriteUInt32BE(data, leveledObjectBOffset, FileOffsetToVa(entryBOffset));
        WriteUInt16BE(data, leveledObjectBOffset + 4, 1);
        WriteUInt16BE(data, leveledObjectBOffset + 6, 20);

        var reader = CreateReader(data);
        var result =
            reader.ReadRuntimeLeveledList(MakeEntry("LeveledItemsTest", leveledListFormId, 0x34, structOffset));

        Assert.NotNull(result);
        Assert.Equal("LVLI", result.ListType);
        Assert.Equal((byte)25, result.ChanceNone);
        Assert.Equal((byte)0x05, result.Flags);
        Assert.Equal(0x0000A001u, result.GlobalFormId);
        Assert.Collection(result.Entries,
            entry =>
            {
                Assert.Equal((ushort)10, entry.Level);
                Assert.Equal(0x0000C001u, entry.FormId);
                Assert.Equal((ushort)2, entry.Count);
            },
            entry =>
            {
                Assert.Equal((ushort)20, entry.Level);
                Assert.Equal(0x0000C002u, entry.FormId);
                Assert.Equal((ushort)1, entry.Count);
            });
    }

    [Fact]
    public void ReadRuntimeDialogTopic_WithJournalAndResponseCount_ReturnsTopicMetadata()
    {
        var data = new byte[DataSize];
        const uint topicFormId = 0x00002400;
        const int structOffset = 0;
        const int fullNameOffset = 1024;
        const int promptOffset = 1280;

        WriteTesFormHeader(data, structOffset, 0x82010000, 0x45, topicFormId);
        WriteBSStringT(data, structOffset + 44, FileOffsetToVa(fullNameOffset), "Runtime Topic", fullNameOffset);
        data[structOffset + 52] = 5;
        data[structOffset + 53] = 0x03;
        WriteFloatBE(data, structOffset + 56, 25.0f);
        WriteBSStringT(data, structOffset + 68, FileOffsetToVa(promptOffset), "Ask about patrols", promptOffset);
        WriteInt32BE(data, structOffset + 76, 12);
        WriteUInt32BE(data, structOffset + 84, 4);

        var reader = CreateReader(data);
        var result = reader.ReadRuntimeDialogTopic(MakeEntry("RuntimeTopic", topicFormId, 0x45, structOffset));

        Assert.NotNull(result);
        Assert.Equal(topicFormId, result.FormId);
        Assert.Equal((byte)5, result.TopicType);
        Assert.Equal((byte)0x03, result.Flags);
        Assert.Equal(25.0f, result.Priority);
        Assert.Equal((uint)4, result.TopicCount);
        Assert.Equal(12, result.JournalIndex);
        Assert.Equal("Runtime Topic", result.FullName);
        Assert.Equal("Ask about patrols", result.DummyPrompt);
    }

    [Fact]
    public void ReadRuntimeDialogTopic_WithInvalidJournalAndResponseCount_DoesNotGuessValues()
    {
        var data = new byte[DataSize];
        const uint topicFormId = 0x00002410;
        const int structOffset = 0;
        const int fullNameOffset = 1408;
        const int promptOffset = 1664;

        WriteTesFormHeader(data, structOffset, 0x82010000, 0x45, topicFormId);
        WriteBSStringT(data, structOffset + 44, FileOffsetToVa(fullNameOffset), "Broken Topic", fullNameOffset);
        data[structOffset + 52] = 1;
        data[structOffset + 53] = 0x02;
        WriteFloatBE(data, structOffset + 56, 10.0f);
        WriteBSStringT(data, structOffset + 68, FileOffsetToVa(promptOffset), "Fallback Prompt", promptOffset);
        WriteInt32BE(data, structOffset + 76, -50000);
        WriteUInt32BE(data, structOffset + 84, 500000);

        var reader = CreateReader(data);
        var result = reader.ReadRuntimeDialogTopic(MakeEntry("BrokenTopic", topicFormId, 0x45, structOffset));

        Assert.NotNull(result);
        Assert.Equal(0u, result.TopicCount);
        Assert.Equal(0, result.JournalIndex);
    }

    [Fact]
    public void ReadRuntimeDialogueInfo_WithConversationData_ReturnsRuntimeLinkLists()
    {
        var data = new byte[DataSize];
        const uint infoFormId = 0x00002420;
        const int structOffset = 0;
        const int conversationOffset = 1024;
        const int linkFromNodeOffset = 1088;
        const int followUpNodeOffset = 1120;
        const int topicFromAOffset = 2048;
        const int topicFromBOffset = 2304;
        const int topicToOffset = 2560;
        const int followUpInfoAOffset = 2816;
        const int followUpInfoBOffset = 3072;

        WriteTesFormHeader(data, structOffset, 0x82010000, 0x46, infoFormId);
        WriteUInt16BE(data, structOffset + 48, 7);
        WriteUInt32BE(data, structOffset + 72, FileOffsetToVa(conversationOffset));

        WriteSimpleListNode(
            data,
            conversationOffset + 0,
            FileOffsetToVa(topicFromAOffset),
            FileOffsetToVa(linkFromNodeOffset));
        WriteSimpleListNode(data, linkFromNodeOffset, FileOffsetToVa(topicFromBOffset), 0);
        WriteSimpleListNode(data, conversationOffset + 8, FileOffsetToVa(topicToOffset), 0);
        WriteSimpleListNode(
            data,
            conversationOffset + 16,
            FileOffsetToVa(followUpInfoAOffset),
            FileOffsetToVa(followUpNodeOffset));
        WriteSimpleListNode(data, followUpNodeOffset, FileOffsetToVa(followUpInfoBOffset), 0);

        WriteTesFormHeader(data, topicFromAOffset, 0x82010000, 0x45, 0x00002421);
        WriteTesFormHeader(data, topicFromBOffset, 0x82010000, 0x45, 0x00002422);
        WriteTesFormHeader(data, topicToOffset, 0x82010000, 0x45, 0x00002423);
        WriteTesFormHeader(data, followUpInfoAOffset, 0x82010000, 0x46, 0x00002424);
        WriteTesFormHeader(data, followUpInfoBOffset, 0x82010000, 0x46, 0x00002425);

        var reader = CreateReader(data);
        var result = reader.ReadRuntimeDialogueInfo(new RuntimeEditorIdEntry
        {
            EditorId = "RuntimeInfoConversation",
            FormId = infoFormId,
            FormType = 0x46,
            TesFormOffset = structOffset,
            DialogueLine = "Runtime prompt"
        });

        Assert.NotNull(result);
        Assert.Equal((ushort)7, result.InfoIndex);
        Assert.Equal("Runtime prompt", result.PromptText);
        Assert.Equal([0x00002421u, 0x00002422u], result.LinkFromTopicFormIds);
        Assert.Equal([0x00002423u], result.LinkToTopicFormIds);
        Assert.Equal([0x00002424u, 0x00002425u], result.FollowUpInfoFormIds);
        Assert.Equal(structOffset, result.DumpOffset);
    }

    [Fact]
    public void ReadRuntimeDialogueInfo_WithInvalidConversationData_DoesNotGuessRuntimeLinkLists()
    {
        var data = new byte[DataSize];
        const uint infoFormId = 0x00002430;
        const int structOffset = 0;

        WriteTesFormHeader(data, structOffset, 0x82010000, 0x46, infoFormId);
        WriteUInt16BE(data, structOffset + 48, 3);
        WriteUInt32BE(data, structOffset + 72, 0x7FFF0000);

        var reader = CreateReader(data);
        var result = reader.ReadRuntimeDialogueInfo(new RuntimeEditorIdEntry
        {
            EditorId = "RuntimeInfoConversationInvalid",
            FormId = infoFormId,
            FormType = 0x46,
            TesFormOffset = structOffset,
            DialogueLine = "Runtime prompt"
        });

        Assert.NotNull(result);
        Assert.Empty(result.LinkFromTopicFormIds);
        Assert.Empty(result.LinkToTopicFormIds);
        Assert.Empty(result.FollowUpInfoFormIds);
    }

    [Fact]
    public void ReadRuntimeQuest_WithObjectiveList_ReturnsOrderedObjectives()
    {
        var data = new byte[DataSize];
        const uint questFormId = 0x00002500;
        const int structOffset = 0;
        const int objectiveAOffset = 1024;
        const int objectiveNodeOffset = 1100;
        const int objectiveBOffset = 1152;
        const int objectiveATextOffset = 4096;
        const int objectiveBTextOffset = 4352;
        const int fullNameOffset = 4608;
        const int scriptOffset = 4864;

        WriteTesFormHeader(data, structOffset, 0x82010000, 0x47, questFormId);
        WriteUInt32BE(data, structOffset + 44, FileOffsetToVa(scriptOffset));
        WriteBSStringT(data, structOffset + 68, FileOffsetToVa(fullNameOffset), "Quest Runtime Name", fullNameOffset);
        data[structOffset + 76] = 0x12;
        data[structOffset + 77] = 7;
        WriteFloatBE(data, structOffset + 80, 6.5f);
        WriteUInt32BE(data, structOffset + 92, FileOffsetToVa(objectiveAOffset));
        WriteUInt32BE(data, structOffset + 96, FileOffsetToVa(objectiveNodeOffset));

        WriteTesFormHeader(data, scriptOffset, 0x82010000, 0x11, 0x00002510);
        WriteQuestObjective(
            data,
            objectiveAOffset,
            10,
            "Find the component",
            objectiveATextOffset,
            FileOffsetToVa(structOffset),
            true,
            1);
        WriteSimpleListNode(data, objectiveNodeOffset, FileOffsetToVa(objectiveBOffset), 0);
        WriteQuestObjective(
            data,
            objectiveBOffset,
            20,
            "Return to the engineer",
            objectiveBTextOffset,
            FileOffsetToVa(structOffset),
            true,
            2);

        var reader = CreateReader(data);
        var result = reader.ReadRuntimeQuest(MakeEntry("QuestRuntime", questFormId, 0x47, structOffset));

        Assert.NotNull(result);
        Assert.Equal("QuestRuntime", result.EditorId);
        Assert.Equal("Quest Runtime Name", result.FullName);
        Assert.Equal((byte)0x12, result.Flags);
        Assert.Equal((byte)7, result.Priority);
        Assert.Equal(6.5f, result.QuestDelay);
        Assert.Equal(0x00002510u, result.Script);
        Assert.Collection(result.Objectives,
            objective =>
            {
                Assert.Equal(10, objective.Index);
                Assert.Equal("Find the component", objective.DisplayText);
            },
            objective =>
            {
                Assert.Equal(20, objective.Index);
                Assert.Equal("Return to the engineer", objective.DisplayText);
            });
    }

    [Fact]
    public void ReadRuntimeQuest_WithStageList_ReturnsOrderedStages()
    {
        var data = new byte[DataSize];
        const uint questFormId = 0x00002508;
        const int structOffset = 0;
        const int stageAOffset = 1024;
        const int stageNodeOffset = 1088;
        const int stageBOffset = 1152;
        const int stageAItemOffset = 1280;
        const int stageBItemOffset = 1536;

        WriteTesFormHeader(data, structOffset, 0x82010000, 0x47, questFormId);
        WriteUInt32BE(data, structOffset + 84, FileOffsetToVa(stageAOffset));
        WriteUInt32BE(data, structOffset + 88, FileOffsetToVa(stageNodeOffset));

        WriteQuestStage(data, stageAOffset, 10, FileOffsetToVa(stageAItemOffset));
        WriteSimpleListNode(data, stageNodeOffset, FileOffsetToVa(stageBOffset), 0);
        WriteQuestStage(data, stageBOffset, 20, FileOffsetToVa(stageBItemOffset));

        WriteQuestStageItem(data, stageAItemOffset, 0x04, FileOffsetToVa(structOffset), true);
        WriteQuestStageItem(data, stageBItemOffset, 0x08, FileOffsetToVa(structOffset), false);

        var reader = CreateReader(data);
        var result = reader.ReadRuntimeQuest(MakeEntry("QuestRuntimeStages", questFormId, 0x47, structOffset));

        Assert.NotNull(result);
        Assert.Collection(result.Stages,
            stage =>
            {
                Assert.Equal(10, stage.Index);
                Assert.Null(stage.LogEntry);
                Assert.Equal((byte)0x04, stage.Flags);
            },
            stage =>
            {
                Assert.Equal(20, stage.Index);
                Assert.Null(stage.LogEntry);
                Assert.Equal((byte)0x08, stage.Flags);
            });
    }

    [Fact]
    public void ReadRuntimeQuest_WithMissingStageItems_LeavesStageFlagsUnset()
    {
        var data = new byte[DataSize];
        const uint questFormId = 0x00002518;
        const int structOffset = 0;
        const int validStageOffset = 1024;
        const int invalidStageNodeOffset = 1088;
        const int invalidStageOffset = 1152;
        const int validStageItemOffset = 1280;

        WriteTesFormHeader(data, structOffset, 0x82010000, 0x47, questFormId);
        WriteUInt32BE(data, structOffset + 84, FileOffsetToVa(validStageOffset));
        WriteUInt32BE(data, structOffset + 88, FileOffsetToVa(invalidStageNodeOffset));

        WriteQuestStage(data, validStageOffset, 15, FileOffsetToVa(validStageItemOffset));
        WriteQuestStageItem(data, validStageItemOffset, 0x01, FileOffsetToVa(structOffset), false);

        WriteSimpleListNode(data, invalidStageNodeOffset, FileOffsetToVa(invalidStageOffset), 0);
        WriteQuestStage(data, invalidStageOffset, 25, 0);

        var reader = CreateReader(data);
        var result =
            reader.ReadRuntimeQuest(MakeEntry("QuestRuntimeMissingStageItems", questFormId, 0x47, structOffset));

        Assert.NotNull(result);
        Assert.Collection(result.Stages,
            stage =>
            {
                Assert.Equal(15, stage.Index);
                Assert.Equal((byte)0x01, stage.Flags);
            },
            stage =>
            {
                Assert.Equal(25, stage.Index);
                Assert.Equal((byte)0x00, stage.Flags);
            });
    }

    [Fact]
    public void ReadRuntimeQuest_WithInvalidObjectiveEntries_DoesNotGuessObjectives()
    {
        var data = new byte[DataSize];
        const uint questFormId = 0x00002520;
        const int structOffset = 0;
        const int validObjectiveOffset = 1024;
        const int invalidObjectiveNodeOffset = 1100;
        const int invalidObjectiveOffset = 1152;
        const int validTextOffset = 4096;
        const int invalidTextOffset = 4352;
        const int otherQuestOffset = 4864;

        WriteTesFormHeader(data, structOffset, 0x82010000, 0x47, questFormId);
        WriteUInt32BE(data, structOffset + 92, FileOffsetToVa(validObjectiveOffset));
        WriteUInt32BE(data, structOffset + 96, FileOffsetToVa(invalidObjectiveNodeOffset));

        WriteQuestObjective(
            data,
            validObjectiveOffset,
            15,
            "Objective from the correct quest",
            validTextOffset,
            FileOffsetToVa(structOffset),
            true,
            1);
        WriteSimpleListNode(data, invalidObjectiveNodeOffset, FileOffsetToVa(invalidObjectiveOffset), 0);
        WriteTesFormHeader(data, otherQuestOffset, 0x82010000, 0x47, 0x00002521);
        WriteQuestObjective(
            data,
            invalidObjectiveOffset,
            25,
            "Wrong owner objective",
            invalidTextOffset,
            FileOffsetToVa(otherQuestOffset),
            true,
            99);

        var reader = CreateReader(data);
        var result = reader.ReadRuntimeQuest(MakeEntry("QuestRuntimeInvalid", questFormId, 0x47, structOffset));

        Assert.NotNull(result);
        var objective = Assert.Single(result.Objectives);
        Assert.Equal(15, objective.Index);
        Assert.Equal("Objective from the correct quest", objective.DisplayText);
    }

    [Fact]
    public void ReadRuntimeActivator_WithExplicitPointers_ReturnsSemanticFields()
    {
        var data = new byte[DataSize];
        const uint activatorFormId = 0x00003000;
        const int structOffset = 0;
        const int nameOffset = 4096;
        const int modelOffset = 4608;
        const int scriptOffset = 5120;
        const int soundOffset = 5376;
        const int waterOffset = 5632;
        const int radioOffset = 5888;

        WriteTesFormHeader(data, structOffset, 0x82010000, 0x15, activatorFormId);
        WriteBounds(data, structOffset + 52, -8, -4, 0, 8, 4, 16);
        WriteBSStringT(data, structOffset + 68, FileOffsetToVa(nameOffset), "Campfire Switch", nameOffset);
        WriteBSStringT(data, structOffset + 80, FileOffsetToVa(modelOffset), "meshes\\clutter\\switch.nif",
            modelOffset);
        WriteTesFormHeader(data, scriptOffset, 0x82010000, 0x11, 0x0000D001);
        WriteTesFormHeader(data, soundOffset, 0x82010000, 0x0D, 0x0000D002);
        WriteTesFormHeader(data, waterOffset, 0x82010000, 0x4E, 0x0000D003);
        WriteTesFormHeader(data, radioOffset, 0x82010000, 0x15, 0x0000D004);
        WriteUInt32BE(data, structOffset + 112, FileOffsetToVa(scriptOffset));
        WriteUInt32BE(data, structOffset + 136, FileOffsetToVa(soundOffset));
        WriteUInt32BE(data, structOffset + 144, FileOffsetToVa(waterOffset));
        WriteUInt32BE(data, structOffset + 148, FileOffsetToVa(radioOffset));

        var reader = CreateReader(data);
        var result = reader.ReadRuntimeActivator(MakeEntry("CampfireSwitch", activatorFormId, 0x15, structOffset));

        Assert.NotNull(result);
        Assert.Equal("CampfireSwitch", result.EditorId);
        Assert.Equal("Campfire Switch", result.FullName);
        Assert.Equal("meshes\\clutter\\switch.nif", result.ModelPath);
        Assert.Equal(0x0000D001u, result.Script);
        Assert.Equal(0x0000D002u, result.ActivationSoundFormId);
        Assert.Equal(0x0000D003u, result.WaterTypeFormId);
        Assert.Equal(0x0000D004u, result.RadioStationFormId);
        Assert.NotNull(result.Bounds);
        Assert.Equal((short)-8, result.Bounds!.X1);
        Assert.Equal((short)16, result.Bounds.Z2);
    }

    [Fact]
    public void ReadRuntimeWorldspaceAndCell_WithCellMap_ReturnsTypedWorldData()
    {
        var data = new byte[DataSize];
        const uint worldspaceFormId = 0x00004000;
        const uint cellFormId = 0x00004010;
        const uint cellRefAFormId = 0x00004021;
        const uint cellRefBFormId = 0x00004022;
        const int worldOffset = 0;
        const int cellMapOffset = 512;
        const int bucketArrayOffset = 544;
        const int bucketItemOffset = 576;
        const int cellOffset = 1024;
        const int cellRefNodeOffset = 1280;
        const int cellRefAOffset = 1536;
        const int cellRefBOffset = 1792;
        const int worldNameOffset = 4096;
        const int cellNameOffset = 4352;
        const int climateOffset = 4608;
        const int waterOffset = 4864;
        const int encounterOffset = 5120;

        WriteTesFormHeader(data, worldOffset, 0x82010000, 0x41, worldspaceFormId);
        WriteBSStringT(data, worldOffset + 44, FileOffsetToVa(worldNameOffset), "Hidden Valley", worldNameOffset);
        WriteUInt32BE(data, worldOffset + 64, FileOffsetToVa(cellMapOffset));
        WriteUInt32BE(data, worldOffset + 68, FileOffsetToVa(cellOffset));
        WriteTesFormHeader(data, climateOffset, 0x82010000, 0x36, 0x0000E001);
        WriteTesFormHeader(data, waterOffset, 0x82010000, 0x4E, 0x0000E002);
        WriteTesFormHeader(data, encounterOffset, 0x82010000, 0x61, 0x0000E003);
        WriteUInt32BE(data, worldOffset + 80, FileOffsetToVa(climateOffset));
        WriteUInt32BE(data, worldOffset + 132, FileOffsetToVa(waterOffset));
        WriteUInt32BE(data, worldOffset + 216, FileOffsetToVa(encounterOffset));
        WriteInt32BE(data, worldOffset + 144, 512);
        WriteInt32BE(data, worldOffset + 148, 256);
        WriteUInt16BE(data, worldOffset + 152, unchecked((ushort)-12));
        WriteUInt16BE(data, worldOffset + 154, unchecked(8));
        WriteUInt16BE(data, worldOffset + 156, unchecked(20));
        WriteUInt16BE(data, worldOffset + 158, unchecked((ushort)-4));
        WriteFloatBE(data, worldOffset + 176, -49152f);
        WriteFloatBE(data, worldOffset + 180, -32768f);
        WriteFloatBE(data, worldOffset + 184, 81920f);
        WriteFloatBE(data, worldOffset + 188, 16384f);
        WriteFloatBE(data, worldOffset + 208, 32f);
        WriteFloatBE(data, worldOffset + 212, 64f);

        WriteUInt32BE(data, cellMapOffset + 4, 1);
        WriteUInt32BE(data, cellMapOffset + 8, FileOffsetToVa(bucketArrayOffset));
        WriteUInt32BE(data, cellMapOffset + 12, 1);
        WriteUInt32BE(data, bucketArrayOffset, FileOffsetToVa(bucketItemOffset));
        WriteUInt32BE(data, bucketItemOffset, 0);
        WriteUInt32BE(data, bucketItemOffset + 4, PackCellMapKey(12, -3));
        WriteUInt32BE(data, bucketItemOffset + 8, FileOffsetToVa(cellOffset));

        WriteTesFormHeader(data, cellOffset, 0x82010000, 0x39, cellFormId);
        WriteBSStringT(data, cellOffset + 44, FileOffsetToVa(cellNameOffset), "Hidden Valley Exterior", cellNameOffset);
        data[cellOffset + 52] = 0x02;
        WriteFloatBE(data, cellOffset + 96, 128f);
        WriteUInt32BE(data, cellOffset + 140, FileOffsetToVa(cellRefAOffset));
        WriteUInt32BE(data, cellOffset + 144, FileOffsetToVa(cellRefNodeOffset));
        WriteUInt32BE(data, cellOffset + 160, FileOffsetToVa(worldOffset));
        WriteUInt32BE(data, cellRefNodeOffset, FileOffsetToVa(cellRefBOffset));
        WriteTesFormHeader(data, cellRefAOffset, 0x82010000, 0x3A, cellRefAFormId);
        WriteTesFormHeader(data, cellRefBOffset, 0x82010000, 0x3B, cellRefBFormId);

        var reader = CreateReader(data);
        var worldEntry = MakeEntry("WildernessHV", worldspaceFormId, 0x41, worldOffset);

        var worldspace = reader.ReadRuntimeWorldspace(worldEntry);
        Assert.NotNull(worldspace);
        Assert.Equal("Hidden Valley", worldspace.FullName);
        Assert.Equal(512, worldspace.MapUsableWidth);
        Assert.Equal(256, worldspace.MapUsableHeight);
        Assert.Equal((short)-12, worldspace.MapNWCellX);
        Assert.Equal((short)-4, worldspace.MapSECellY);
        Assert.Equal(0x0000E001u, worldspace.ClimateFormId);
        Assert.Equal(0x0000E002u, worldspace.WaterFormId);
        Assert.Equal(0x0000E003u, worldspace.EncounterZoneFormId);

        var cellMap = reader.ReadAllWorldspaceCellMaps([worldEntry]);
        Assert.Equal("WildernessHV", cellMap[worldspaceFormId].EditorId);
        Assert.Equal("Hidden Valley", cellMap[worldspaceFormId].FullName);
        Assert.Equal(0x0000E001u, cellMap[worldspaceFormId].ClimateFormId);
        Assert.Equal(0x0000E002u, cellMap[worldspaceFormId].WaterFormId);
        Assert.Equal(32f, cellMap[worldspaceFormId].DefaultLandHeight);
        Assert.Equal(64f, cellMap[worldspaceFormId].DefaultWaterHeight);
        Assert.Equal(512, cellMap[worldspaceFormId].MapUsableWidth);
        Assert.Equal((short)-12, cellMap[worldspaceFormId].MapNWCellX);
        Assert.Equal(-49152f, cellMap[worldspaceFormId].BoundsMinX);
        Assert.Equal(0x0000E003u, cellMap[worldspaceFormId].EncounterZoneFormId);
        var cellEntry = Assert.Single(cellMap[worldspaceFormId].Cells);
        var cell = reader.ReadRuntimeCell(cellEntry, "CellHiddenValley");

        Assert.Equal([cellRefAFormId, cellRefBFormId], cellEntry.ReferenceFormIds);
        Assert.NotNull(cell);
        Assert.Equal("Hidden Valley Exterior", cell.FullName);
        Assert.Equal(12, cell.GridX);
        Assert.Equal(-3, cell.GridY);
        Assert.Equal(worldspaceFormId, cell.WorldspaceFormId);
        Assert.Equal((byte)0x02, cell.Flags);
        Assert.Equal(128f, cell.WaterHeight);
        Assert.True(cell.HasPersistentObjects);
    }

    [Fact]
    public void ReadRuntimeCell_FromMapStubWithoutPointer_DoesNotGuessUnsupportedFields()
    {
        var data = new byte[DataSize];
        var reader = CreateReader(data);

        var cell = reader.ReadRuntimeCell(new RuntimeCellMapEntry
        {
            CellFormId = 0x00005000,
            GridX = 3,
            GridY = -7,
            IsPersistent = true,
            WorldspaceFormId = 0x00005010
        }, "CellStub", "Stub Cell");

        Assert.NotNull(cell);
        Assert.Equal(3, cell.GridX);
        Assert.Equal(-7, cell.GridY);
        Assert.Equal(0x00005010u, cell.WorldspaceFormId);
        Assert.Null(cell.WaterHeight);
        Assert.Empty(cell.PlacedObjects);
    }

    [Fact]
    public void ReadRuntimeRefr_WithPlacementExtras_ReturnsSemanticFields()
    {
        var data = new byte[DataSize];
        const uint refrFormId = 0x00006000;
        const int structOffset = 0;
        const int extraOwnershipOffset = 512;
        const int extraLockOffset = 544;
        const int extraEnableParentOffset = 576;
        const int extraLinkedRefOffset = 608;
        const int extraTeleportOffset = 640;
        const int extraMapMarkerOffset = 672;
        const int baseObjectOffset = 2048;
        const int parentCellOffset = 2304;
        const int ownerOffset = 2560;
        const int lockDataOffset = 2688;
        const int lockKeyOffset = 2816;
        const int enableParentRefOffset = 2944;
        const int linkedRefOffset = 3200;
        const int destinationDoorOffset = 3456;
        const int mapDataOffset = 3712;
        const int markerNameOffset = 3968;
        const int teleportDataOffset = 4224;

        WriteTesFormHeader(data, structOffset, 0x82010000, 0x3A, refrFormId);
        WriteUInt32BE(data, structOffset + 48, FileOffsetToVa(baseObjectOffset));
        WriteFloatBE(data, structOffset + 52, 1.5f);
        WriteFloatBE(data, structOffset + 56, -0.25f);
        WriteFloatBE(data, structOffset + 60, 0.75f);
        WriteFloatBE(data, structOffset + 64, 1024f);
        WriteFloatBE(data, structOffset + 68, -2048f);
        WriteFloatBE(data, structOffset + 72, 512f);
        WriteFloatBE(data, structOffset + 76, 1.25f);
        WriteUInt32BE(data, structOffset + 80, FileOffsetToVa(parentCellOffset));
        WriteUInt32BE(data, structOffset + 88, FileOffsetToVa(extraOwnershipOffset));

        WriteTesFormHeader(data, baseObjectOffset, 0x82010000, 0x15, 0x00006010);
        WriteTesFormHeader(data, parentCellOffset, 0x82010000, 0x39, 0x00006011);
        data[parentCellOffset + 52] = 0x01;
        WriteTesFormHeader(data, ownerOffset, 0x82010000, 0x2A, 0x00006012);
        WriteTesFormHeader(data, lockKeyOffset, 0x82010000, 0x2D, 0x00006016);
        WriteTesFormHeader(data, enableParentRefOffset, 0x82010000, 0x3A, 0x00006013);
        WriteTesFormHeader(data, linkedRefOffset, 0x82010000, 0x3A, 0x00006014);
        WriteTesFormHeader(data, destinationDoorOffset, 0x82010000, 0x3A, 0x00006015);

        WriteExtraPointerNode(data, extraOwnershipOffset, ExtraOwnershipType, FileOffsetToVa(extraLockOffset),
            FileOffsetToVa(ownerOffset));
        WriteExtraPointerNode(data, extraLockOffset, ExtraLockType, FileOffsetToVa(extraEnableParentOffset),
            FileOffsetToVa(lockDataOffset));
        WriteExtraEnableParentNode(data, extraEnableParentOffset, FileOffsetToVa(extraLinkedRefOffset),
            FileOffsetToVa(enableParentRefOffset), 0x01);
        WriteExtraPointerNode(data, extraLinkedRefOffset, ExtraLinkedRefType, FileOffsetToVa(extraTeleportOffset),
            FileOffsetToVa(linkedRefOffset));
        WriteExtraPointerNode(data, extraTeleportOffset, ExtraTeleportType, FileOffsetToVa(extraMapMarkerOffset),
            FileOffsetToVa(teleportDataOffset));
        WriteExtraPointerNode(data, extraMapMarkerOffset, ExtraMapMarkerType, 0, FileOffsetToVa(mapDataOffset));

        WriteRefrLockData(data, lockDataOffset, 75, FileOffsetToVa(lockKeyOffset), 0x05, 2, 1);
        WriteDoorTeleportData(data, teleportDataOffset, FileOffsetToVa(destinationDoorOffset));
        WriteMapMarkerData(data, mapDataOffset, FileOffsetToVa(markerNameOffset), "Camp Marker", markerNameOffset, 7);

        var reader = CreateReader(data);
        var result = reader.ReadRuntimeRefr(MakeEntry("PlacedRefRuntime", refrFormId, 0x3A, structOffset));

        Assert.NotNull(result);
        Assert.Equal(0x00006010u, result.BaseFormId);
        Assert.Equal(0x00006011u, result.ParentCellFormId);
        Assert.True(result.ParentCellIsInterior == true);
        Assert.Equal(1.25f, result.Scale);
        Assert.Equal(1024f, result.Position!.X);
        Assert.Equal(-2048f, result.Position.Y);
        Assert.Equal(512f, result.Position.Z);
        Assert.Equal(0x00006012u, result.OwnerFormId);
        Assert.Equal((byte)75, result.LockLevel);
        Assert.Equal(0x00006016u, result.LockKeyFormId);
        Assert.Equal((byte)0x05, result.LockFlags);
        Assert.Equal(2u, result.LockNumTries);
        Assert.Equal(1u, result.LockTimesUnlocked);
        Assert.Equal(0x00006013u, result.EnableParentFormId);
        Assert.Equal((byte)0x01, result.EnableParentFlags);
        Assert.Equal(0x00006014u, result.LinkedRefFormId);
        Assert.Equal(0x00006015u, result.DestinationDoorFormId);
        Assert.True(result.IsMapMarker);
        Assert.Equal((ushort)7, result.MarkerType);
        Assert.Equal("Camp Marker", result.MarkerName);
    }

    [Fact]
    public void ReadRuntimeRefr_WithInvalidPlacementExtras_DoesNotGuessUnsupportedFields()
    {
        var data = new byte[DataSize];
        const uint refrFormId = 0x00006100;
        const int structOffset = 0;
        const int extraOwnershipOffset = 512;
        const int extraLockOffset = 544;
        const int extraEnableParentOffset = 576;
        const int extraLinkedRefOffset = 608;
        const int extraTeleportOffset = 640;
        const int baseObjectOffset = 2048;
        const int parentCellOffset = 2304;
        const int invalidTargetOffset = 2560;
        const int teleportDataOffset = 2816;

        WriteTesFormHeader(data, structOffset, 0x82010000, 0x3A, refrFormId);
        WriteUInt32BE(data, structOffset + 48, FileOffsetToVa(baseObjectOffset));
        WriteFloatBE(data, structOffset + 64, 128f);
        WriteFloatBE(data, structOffset + 68, 256f);
        WriteFloatBE(data, structOffset + 72, 32f);
        WriteFloatBE(data, structOffset + 76, 1.0f);
        WriteUInt32BE(data, structOffset + 80, FileOffsetToVa(parentCellOffset));
        WriteUInt32BE(data, structOffset + 88, FileOffsetToVa(extraOwnershipOffset));

        WriteTesFormHeader(data, baseObjectOffset, 0x82010000, 0x15, 0x00006110);
        WriteTesFormHeader(data, parentCellOffset, 0x82010000, 0x39, 0x00006111);
        WriteTesFormHeader(data, invalidTargetOffset, 0x82010000, 0x15, 0x00006112);

        WriteExtraPointerNode(data, extraOwnershipOffset, ExtraOwnershipType, FileOffsetToVa(extraLockOffset),
            0x7FFF0000);
        WriteExtraPointerNode(data, extraLockOffset, ExtraLockType, FileOffsetToVa(extraEnableParentOffset),
            0x7FFF0000);
        WriteExtraEnableParentNode(data, extraEnableParentOffset, FileOffsetToVa(extraLinkedRefOffset),
            FileOffsetToVa(invalidTargetOffset), 0x03);
        WriteExtraPointerNode(data, extraLinkedRefOffset, ExtraLinkedRefType, FileOffsetToVa(extraTeleportOffset),
            FileOffsetToVa(invalidTargetOffset));
        WriteExtraPointerNode(data, extraTeleportOffset, ExtraTeleportType, 0, FileOffsetToVa(teleportDataOffset));
        WriteDoorTeleportData(data, teleportDataOffset, FileOffsetToVa(invalidTargetOffset));

        var reader = CreateReader(data);
        var result = reader.ReadRuntimeRefr(MakeEntry("PlacedRefRuntimeInvalid", refrFormId, 0x3A, structOffset));

        Assert.NotNull(result);
        Assert.Null(result.OwnerFormId);
        Assert.Null(result.LockLevel);
        Assert.Null(result.LockKeyFormId);
        Assert.Null(result.LockFlags);
        Assert.Null(result.LockNumTries);
        Assert.Null(result.LockTimesUnlocked);
        Assert.Null(result.EnableParentFormId);
        Assert.Null(result.EnableParentFlags);
        Assert.Null(result.LinkedRefFormId);
        Assert.Null(result.DestinationDoorFormId);
        Assert.False(result.IsMapMarker);
        Assert.Null(result.MarkerType);
        Assert.Null(result.MarkerName);
    }

    [Fact]
    public void ReadRuntimeRefr_WithPersistentCellAndLinkedRefChildren_ReturnsSemanticFields()
    {
        var data = new byte[DataSize];
        const uint refrFormId = 0x00006180;
        const int structOffset = 0;
        const int extraPersistentCellOffset = 512;
        const int extraEncounterZoneOffset = 544;
        const int extraLinkedRefChildrenOffset = 576;
        const int linkedRefChildNodeOffset = 608;
        const int baseObjectOffset = 2048;
        const int parentCellOffset = 2304;
        const int persistentCellOffset = 2560;
        const int childAOffset = 2816;
        const int childBOffset = 3072;
        const int encounterZoneOffset = 3328;

        WriteTesFormHeader(data, structOffset, 0x82010000, 0x3A, refrFormId);
        WriteUInt32BE(data, structOffset + 48, FileOffsetToVa(baseObjectOffset));
        WriteFloatBE(data, structOffset + 64, 640f);
        WriteFloatBE(data, structOffset + 68, 768f);
        WriteFloatBE(data, structOffset + 72, 32f);
        WriteFloatBE(data, structOffset + 76, 1.0f);
        WriteUInt32BE(data, structOffset + 80, FileOffsetToVa(parentCellOffset));
        WriteUInt32BE(data, structOffset + 88, FileOffsetToVa(extraPersistentCellOffset));

        WriteTesFormHeader(data, baseObjectOffset, 0x82010000, 0x15, 0x00006190);
        WriteTesFormHeader(data, parentCellOffset, 0x82010000, 0x39, 0x00006191);
        WriteTesFormHeader(data, persistentCellOffset, 0x82010000, 0x39, 0x00006192);
        WriteTesFormHeader(data, childAOffset, 0x82010000, 0x3A, 0x00006193);
        WriteTesFormHeader(data, childBOffset, 0x82010000, 0x3B, 0x00006194);
        WriteTesFormHeader(data, encounterZoneOffset, 0x82010000, 0x61, 0x00006195);

        WriteExtraPointerNode(data, extraPersistentCellOffset, ExtraPersistentCellType,
            FileOffsetToVa(extraEncounterZoneOffset), FileOffsetToVa(persistentCellOffset));
        WriteExtraPointerNode(data, extraEncounterZoneOffset, ExtraEncounterZoneType,
            FileOffsetToVa(extraLinkedRefChildrenOffset), FileOffsetToVa(encounterZoneOffset));
        WriteExtraLinkedRefChildrenNode(
            data,
            extraLinkedRefChildrenOffset,
            0,
            FileOffsetToVa(childAOffset),
            FileOffsetToVa(linkedRefChildNodeOffset));
        WriteSimpleListNode(data, linkedRefChildNodeOffset, FileOffsetToVa(childBOffset), 0);

        var reader = CreateReader(data);
        var result = reader.ReadRuntimeRefr(MakeEntry("PlacedRefRuntimeChildren", refrFormId, 0x3A, structOffset));

        Assert.NotNull(result);
        Assert.Equal(0x00006192u, result.PersistentCellFormId);
        Assert.Equal(0x00006195u, result.EncounterZoneFormId);
        Assert.Equal([0x00006193u, 0x00006194u], result.LinkedRefChildrenFormIds);
    }

    [Fact]
    public void ReadRuntimeRefr_WithInvalidPersistentCellAndLinkedRefChildren_DoesNotGuessFields()
    {
        var data = new byte[DataSize];
        const uint refrFormId = 0x000061A0;
        const int structOffset = 0;
        const int extraPersistentCellOffset = 512;
        const int extraLinkedRefChildrenOffset = 544;
        const int linkedRefChildNodeOffset = 576;
        const int baseObjectOffset = 2048;
        const int invalidTargetOffset = 2304;

        WriteTesFormHeader(data, structOffset, 0x82010000, 0x3A, refrFormId);
        WriteUInt32BE(data, structOffset + 48, FileOffsetToVa(baseObjectOffset));
        WriteFloatBE(data, structOffset + 64, 128f);
        WriteFloatBE(data, structOffset + 68, 256f);
        WriteFloatBE(data, structOffset + 72, 64f);
        WriteFloatBE(data, structOffset + 76, 1.0f);
        WriteUInt32BE(data, structOffset + 88, FileOffsetToVa(extraPersistentCellOffset));

        WriteTesFormHeader(data, baseObjectOffset, 0x82010000, 0x15, 0x000061B0);
        WriteTesFormHeader(data, invalidTargetOffset, 0x82010000, 0x15, 0x000061B1);

        WriteExtraPointerNode(data, extraPersistentCellOffset, ExtraPersistentCellType,
            FileOffsetToVa(extraLinkedRefChildrenOffset), FileOffsetToVa(invalidTargetOffset));
        WriteExtraLinkedRefChildrenNode(
            data,
            extraLinkedRefChildrenOffset,
            0,
            FileOffsetToVa(invalidTargetOffset),
            FileOffsetToVa(linkedRefChildNodeOffset));
        WriteSimpleListNode(data, linkedRefChildNodeOffset, FileOffsetToVa(invalidTargetOffset), 0);

        var reader = CreateReader(data);
        var result =
            reader.ReadRuntimeRefr(MakeEntry("PlacedRefRuntimeChildrenInvalid", refrFormId, 0x3A, structOffset));

        Assert.NotNull(result);
        Assert.Null(result.PersistentCellFormId);
        Assert.Null(result.EncounterZoneFormId);
        Assert.Empty(result.LinkedRefChildrenFormIds);
    }

    [Fact]
    public void ReadRuntimeRefr_WithActorStartExtras_ReturnsRuntimeStartState()
    {
        var data = new byte[DataSize];
        const uint refrFormId = 0x000061C0;
        const int structOffset = 0;
        const int extraStartingPositionOffset = 512;
        const int extraPackageStartLocationOffset = 576;
        const int extraStartingWorldOrCellOffset = 608;
        const int baseObjectOffset = 2048;
        const int parentCellOffset = 2304;
        const int startCellOffset = 2560;
        const int packageLocationCellOffset = 2816;

        WriteTesFormHeader(data, structOffset, 0x82010000, 0x3B, refrFormId);
        WriteUInt32BE(data, structOffset + 48, FileOffsetToVa(baseObjectOffset));
        WriteFloatBE(data, structOffset + 64, 640f);
        WriteFloatBE(data, structOffset + 68, 768f);
        WriteFloatBE(data, structOffset + 72, 32f);
        WriteFloatBE(data, structOffset + 76, 1.0f);
        WriteUInt32BE(data, structOffset + 80, FileOffsetToVa(parentCellOffset));
        WriteUInt32BE(data, structOffset + 88, FileOffsetToVa(extraStartingPositionOffset));

        WriteTesFormHeader(data, baseObjectOffset, 0x82010000, 0x2A, 0x000061C1);
        WriteTesFormHeader(data, parentCellOffset, 0x82010000, 0x39, 0x000061C2);
        WriteTesFormHeader(data, startCellOffset, 0x82010000, 0x39, 0x000061C3);
        WriteTesFormHeader(data, packageLocationCellOffset, 0x82010000, 0x39, 0x000061C4);

        WriteExtraStartingPositionNode(
            data,
            extraStartingPositionOffset,
            FileOffsetToVa(extraPackageStartLocationOffset),
            1024f,
            2048f,
            96f,
            0.10f,
            0.20f,
            0.30f);
        WriteExtraPackageStartLocationNode(
            data,
            extraPackageStartLocationOffset,
            FileOffsetToVa(extraStartingWorldOrCellOffset),
            FileOffsetToVa(packageLocationCellOffset),
            1100f,
            2100f,
            120f,
            0.75f);
        WriteExtraPointerNode(
            data,
            extraStartingWorldOrCellOffset,
            ExtraStartingWorldOrCellType,
            0,
            FileOffsetToVa(startCellOffset));

        var reader = CreateReader(data);
        var result = reader.ReadRuntimeRefr(MakeEntry("PlacedRefRuntimeStartState", refrFormId, 0x3B, structOffset));

        Assert.NotNull(result);
        Assert.NotNull(result.StartingPosition);
        Assert.Equal(1024f, result.StartingPosition!.X);
        Assert.Equal(2048f, result.StartingPosition.Y);
        Assert.Equal(96f, result.StartingPosition.Z);
        Assert.Equal(0.10f, result.StartingPosition.RotX);
        Assert.Equal(0.20f, result.StartingPosition.RotY);
        Assert.Equal(0.30f, result.StartingPosition.RotZ);
        Assert.Equal(0x000061C3u, result.StartingWorldOrCellFormId);
        Assert.NotNull(result.PackageStartLocation);
        Assert.Equal(0x000061C4u, result.PackageStartLocation!.LocationFormId);
        Assert.Equal(1100f, result.PackageStartLocation.X);
        Assert.Equal(2100f, result.PackageStartLocation.Y);
        Assert.Equal(120f, result.PackageStartLocation.Z);
        Assert.Equal(0.75f, result.PackageStartLocation.RotZ);
    }

    [Fact]
    public void ReadRuntimeRefr_WithInvalidActorStartTargets_DoesNotGuessPackageOrWorldState()
    {
        var data = new byte[DataSize];
        const uint refrFormId = 0x000061D0;
        const int structOffset = 0;
        const int extraStartingPositionOffset = 512;
        const int extraPackageStartLocationOffset = 576;
        const int extraStartingWorldOrCellOffset = 608;
        const int baseObjectOffset = 2048;
        const uint invalidTargetVa = 0x7FFF0000;

        WriteTesFormHeader(data, structOffset, 0x82010000, 0x3B, refrFormId);
        WriteUInt32BE(data, structOffset + 48, FileOffsetToVa(baseObjectOffset));
        WriteUInt32BE(data, structOffset + 88, FileOffsetToVa(extraStartingPositionOffset));

        WriteTesFormHeader(data, baseObjectOffset, 0x82010000, 0x2A, 0x000061D1);
        WriteExtraStartingPositionNode(
            data,
            extraStartingPositionOffset,
            FileOffsetToVa(extraPackageStartLocationOffset),
            512f,
            768f,
            24f,
            0f,
            0f,
            0.5f);
        WriteExtraPackageStartLocationNode(
            data,
            extraPackageStartLocationOffset,
            FileOffsetToVa(extraStartingWorldOrCellOffset),
            invalidTargetVa,
            600f,
            700f,
            32f,
            1.0f);
        WriteExtraPointerNode(
            data,
            extraStartingWorldOrCellOffset,
            ExtraStartingWorldOrCellType,
            0,
            invalidTargetVa);

        var reader = CreateReader(data);
        var result =
            reader.ReadRuntimeRefr(MakeEntry("PlacedRefRuntimeStartStateInvalid", refrFormId, 0x3B, structOffset));

        Assert.NotNull(result);
        Assert.NotNull(result.StartingPosition);
        Assert.Null(result.PackageStartLocation);
        Assert.Null(result.StartingWorldOrCellFormId);
    }

    [Fact]
    public void ReadRuntimeRefr_WithRadiusExtra_ReturnsRadius()
    {
        var data = new byte[DataSize];
        const uint refrFormId = 0x000061D8;
        const int structOffset = 0;
        const int extraRadiusOffset = 512;
        const int baseObjectOffset = 2048;
        const int parentCellOffset = 2304;

        WriteTesFormHeader(data, structOffset, 0x82010000, 0x3A, refrFormId);
        WriteUInt32BE(data, structOffset + 48, FileOffsetToVa(baseObjectOffset));
        WriteFloatBE(data, structOffset + 64, 96f);
        WriteFloatBE(data, structOffset + 68, 144f);
        WriteFloatBE(data, structOffset + 72, 12f);
        WriteFloatBE(data, structOffset + 76, 1.0f);
        WriteUInt32BE(data, structOffset + 80, FileOffsetToVa(parentCellOffset));
        WriteUInt32BE(data, structOffset + 88, FileOffsetToVa(extraRadiusOffset));

        WriteTesFormHeader(data, baseObjectOffset, 0x82010000, 0x15, 0x000061D9);
        WriteTesFormHeader(data, parentCellOffset, 0x82010000, 0x39, 0x000061DA);
        WriteExtraRadiusNode(data, extraRadiusOffset, 0, 384f);

        var reader = CreateReader(data);
        var result = reader.ReadRuntimeRefr(MakeEntry("PlacedRefRuntimeRadius", refrFormId, 0x3A, structOffset));

        Assert.NotNull(result);
        Assert.Equal(384f, result.Radius);
    }

    [Fact]
    public void ReadRuntimeRefr_WithInvalidRadiusExtra_DoesNotGuessRadius()
    {
        var data = new byte[DataSize];
        const uint refrFormId = 0x000061DB;
        const int structOffset = 0;
        const int extraRadiusOffset = 512;
        const int baseObjectOffset = 2048;

        WriteTesFormHeader(data, structOffset, 0x82010000, 0x3A, refrFormId);
        WriteUInt32BE(data, structOffset + 48, FileOffsetToVa(baseObjectOffset));
        WriteUInt32BE(data, structOffset + 88, FileOffsetToVa(extraRadiusOffset));

        WriteTesFormHeader(data, baseObjectOffset, 0x82010000, 0x15, 0x000061DC);
        WriteExtraRadiusNode(data, extraRadiusOffset, 0, 600_001f);

        var reader = CreateReader(data);
        var result = reader.ReadRuntimeRefr(MakeEntry("PlacedRefRuntimeRadiusInvalid", refrFormId, 0x3A, structOffset));

        Assert.NotNull(result);
        Assert.Null(result.Radius);
    }

    [Fact]
    public void ReadRuntimeRefr_WithMerchantContainerAndLeveledCreatureExtras_ReturnsSemanticFields()
    {
        var data = new byte[DataSize];
        const uint refrFormId = 0x000061E0;
        const int structOffset = 0;
        const int extraMerchantContainerOffset = 512;
        const int extraLeveledCreatureOffset = 544;
        const int baseObjectOffset = 2048;
        const int merchantContainerOffset = 2304;
        const int originalBaseOffset = 2560;
        const int templateOffset = 2816;

        WriteTesFormHeader(data, structOffset, 0x82010000, 0x3B, refrFormId);
        WriteUInt32BE(data, structOffset + 48, FileOffsetToVa(baseObjectOffset));
        WriteUInt32BE(data, structOffset + 88, FileOffsetToVa(extraMerchantContainerOffset));

        WriteTesFormHeader(data, baseObjectOffset, 0x82010000, 0x2A, 0x000061E1);
        WriteTesFormHeader(data, merchantContainerOffset, 0x82010000, 0x3A, 0x000061E2);
        WriteTesFormHeader(data, originalBaseOffset, 0x82010000, 0x2B, 0x000061E3);
        WriteTesFormHeader(data, templateOffset, 0x82010000, 0x2D, 0x000061E4);

        WriteExtraPointerNode(
            data,
            extraMerchantContainerOffset,
            ExtraMerchantContainerType,
            FileOffsetToVa(extraLeveledCreatureOffset),
            FileOffsetToVa(merchantContainerOffset));
        WriteExtraLeveledCreatureNode(
            data,
            extraLeveledCreatureOffset,
            0,
            FileOffsetToVa(originalBaseOffset),
            FileOffsetToVa(templateOffset));

        var reader = CreateReader(data);
        var result = reader.ReadRuntimeRefr(MakeEntry("PlacedRefRuntimeMerchant", refrFormId, 0x3B, structOffset));

        Assert.NotNull(result);
        Assert.Equal(0x000061E2u, result.MerchantContainerFormId);
        Assert.Equal(0x000061E3u, result.LeveledCreatureOriginalBaseFormId);
        Assert.Equal(0x000061E4u, result.LeveledCreatureTemplateFormId);
    }

    [Fact]
    public void ReadRuntimeRefr_WithInvalidMerchantContainerAndLeveledCreatureTargets_DoesNotGuessFields()
    {
        var data = new byte[DataSize];
        const uint refrFormId = 0x000061F0;
        const int structOffset = 0;
        const int extraMerchantContainerOffset = 512;
        const int extraLeveledCreatureOffset = 544;
        const int baseObjectOffset = 2048;
        const int wrongMerchantOffset = 2304;
        const uint invalidTargetVa = 0x7FFF0000;

        WriteTesFormHeader(data, structOffset, 0x82010000, 0x3B, refrFormId);
        WriteUInt32BE(data, structOffset + 48, FileOffsetToVa(baseObjectOffset));
        WriteUInt32BE(data, structOffset + 88, FileOffsetToVa(extraMerchantContainerOffset));

        WriteTesFormHeader(data, baseObjectOffset, 0x82010000, 0x2A, 0x000061F1);
        WriteTesFormHeader(data, wrongMerchantOffset, 0x82010000, 0x15, 0x000061F2);

        WriteExtraPointerNode(
            data,
            extraMerchantContainerOffset,
            ExtraMerchantContainerType,
            FileOffsetToVa(extraLeveledCreatureOffset),
            FileOffsetToVa(wrongMerchantOffset));
        WriteExtraLeveledCreatureNode(
            data,
            extraLeveledCreatureOffset,
            0,
            invalidTargetVa,
            invalidTargetVa);

        var reader = CreateReader(data);
        var result =
            reader.ReadRuntimeRefr(MakeEntry("PlacedRefRuntimeMerchantInvalid", refrFormId, 0x3B, structOffset));

        Assert.NotNull(result);
        Assert.Null(result.MerchantContainerFormId);
        Assert.Null(result.LeveledCreatureOriginalBaseFormId);
        Assert.Null(result.LeveledCreatureTemplateFormId);
    }

    [Fact]
    public void BuildRuntimeRefrExtraDataCensus_WithKnownExtras_CountsTypesAndSemanticSignals()
    {
        var data = new byte[DataSize];
        const int refrAOffset = 0;
        const int refrBOffset = 256;
        const int baseObjectOffset = 2048;
        const int parentCellOffset = 2304;
        const int ownerOffset = 2560;
        const int lockDataOffset = 2688;
        const int keyOffset = 2816;
        const int extraOwnershipOffset = 3072;
        const int extraLockOffset = 3104;
        const int extraTeleportOffset = 3136;
        const int extraRadiusOffset = 3168;
        const int teleportDataOffset = 3392;
        const int destinationDoorOffset = 3648;

        WriteTesFormHeader(data, refrAOffset, 0x82010000, 0x3A, 0x00006200);
        WriteUInt32BE(data, refrAOffset + 48, FileOffsetToVa(baseObjectOffset));
        WriteFloatBE(data, refrAOffset + 64, 64f);
        WriteFloatBE(data, refrAOffset + 68, 96f);
        WriteFloatBE(data, refrAOffset + 72, 12f);
        WriteUInt32BE(data, refrAOffset + 80, FileOffsetToVa(parentCellOffset));
        WriteUInt32BE(data, refrAOffset + 88, FileOffsetToVa(extraOwnershipOffset));

        WriteTesFormHeader(data, refrBOffset, 0x82010000, 0x3A, 0x00006210);
        WriteUInt32BE(data, refrBOffset + 48, FileOffsetToVa(baseObjectOffset));
        WriteFloatBE(data, refrBOffset + 64, 128f);
        WriteFloatBE(data, refrBOffset + 68, 96f);
        WriteFloatBE(data, refrBOffset + 72, 24f);
        WriteUInt32BE(data, refrBOffset + 80, FileOffsetToVa(parentCellOffset));

        WriteTesFormHeader(data, baseObjectOffset, 0x82010000, 0x15, 0x00006220);
        WriteTesFormHeader(data, parentCellOffset, 0x82010000, 0x39, 0x00006221);
        WriteTesFormHeader(data, ownerOffset, 0x82010000, 0x2A, 0x00006222);
        WriteTesFormHeader(data, keyOffset, 0x82010000, 0x2D, 0x00006223);
        WriteTesFormHeader(data, destinationDoorOffset, 0x82010000, 0x3A, 0x00006224);

        WriteExtraPointerNode(data, extraOwnershipOffset, ExtraOwnershipType, FileOffsetToVa(extraLockOffset),
            FileOffsetToVa(ownerOffset));
        WriteExtraPointerNode(data, extraLockOffset, ExtraLockType, FileOffsetToVa(extraTeleportOffset),
            FileOffsetToVa(lockDataOffset));
        WriteExtraPointerNode(data, extraTeleportOffset, ExtraTeleportType, FileOffsetToVa(extraRadiusOffset),
            FileOffsetToVa(teleportDataOffset));
        WriteExtraRadiusNode(data, extraRadiusOffset, 0, 256f);
        WriteRefrLockData(data, lockDataOffset, 50, FileOffsetToVa(keyOffset), 0x01, 3, 1);
        WriteDoorTeleportData(data, teleportDataOffset, FileOffsetToVa(destinationDoorOffset));

        var reader = CreateReader(data);
        var census = reader.BuildRuntimeRefrExtraDataCensus(
            [
                MakeEntry("PlacedRefA", 0x00006200, 0x3A, refrAOffset),
                MakeEntry("PlacedRefB", 0x00006210, 0x3A, refrBOffset)
            ],
            8);

        Assert.Equal(2, census.SampleCount);
        Assert.Equal(2, census.ValidRefrCount);
        Assert.Equal(1, census.RefsWithExtraData);
        Assert.Equal(4, census.VisitedNodeCount);
        Assert.Equal(1, census.OwnershipCount);
        Assert.Equal(1, census.LockCount);
        Assert.Equal(1, census.TeleportCount);
        Assert.Equal(0, census.MapMarkerCount);
        Assert.Equal(0, census.EnableParentCount);
        Assert.Equal(0, census.LinkedRefCount);
        Assert.Equal(0, census.EncounterZoneCount);
        Assert.Equal(0, census.StartingPositionCount);
        Assert.Equal(0, census.StartingWorldOrCellCount);
        Assert.Equal(0, census.PackageStartLocationCount);
        Assert.Equal(0, census.MerchantContainerCount);
        Assert.Equal(0, census.LeveledCreatureCount);
        Assert.Equal(1, census.RadiusCount);
        Assert.Equal(1, census.TypeCounts[ExtraOwnershipType]);
        Assert.Equal(1, census.TypeCounts[ExtraLockType]);
        Assert.Equal(1, census.TypeCounts[ExtraTeleportType]);
        Assert.Equal(1, census.TypeCounts[ExtraRadiusType]);
    }

    private RuntimeStructReader CreateReader(byte[] data)
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

        return new RuntimeStructReader(_accessor, data.Length, minidumpInfo);
    }

    private static RuntimeEditorIdEntry MakeEntry(string editorId, uint formId, byte formType, long tesFormOffset)
    {
        return new RuntimeEditorIdEntry
        {
            EditorId = editorId,
            FormId = formId,
            FormType = formType,
            TesFormOffset = tesFormOffset
        };
    }

    private static uint FileOffsetToVa(int fileOffset)
    {
        return HeapBaseVa + (uint)fileOffset;
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

    private static void WriteExtraRadiusNode(byte[] data, int nodeOffset, uint nextVa, float radius)
    {
        data[nodeOffset + 4] = ExtraRadiusType;
        WriteUInt32BE(data, nodeOffset + 8, nextVa);
        WriteFloatBE(data, nodeOffset + 12, radius);
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