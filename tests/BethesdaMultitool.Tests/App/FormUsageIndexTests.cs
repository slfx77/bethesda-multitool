using BethesdaMultitool.Core.EsmView;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public class FormUsageIndexTests
{
    [Fact]
    public void Build_IndexesCtdaReferenceSlotOnlyWhenGamePolicyMakesItSemantic()
    {
        const uint semanticReference = 0x00001234;
        const uint ignoredStorage = 0x00005678;
        var records = new RecordCollection
        {
            Game = BethesdaGame.FalloutNewVegas,
            Dialogues =
            [
                new DialogueRecord
                {
                    FormId = 0x100,
                    Conditions =
                    [
                        new DialogueCondition { RunOn = 2, Reference = semanticReference },
                        new DialogueCondition { RunOn = 5, Reference = ignoredStorage },
                        new DialogueCondition { FunctionIndex = 0x006A, RunOn = 2, Reference = ignoredStorage }
                    ]
                }
            ]
        };

        var usageIndex = FormUsageIndex.Build(records);

        Assert.Equal(1, usageIndex.GetUseCount(semanticReference));
        Assert.Equal(0, usageIndex.GetUseCount(ignoredStorage));
    }

    [Fact]
    public void Build_IndexesScriptsListsPackagesAndAttachedScripts()
    {
        const uint itemFormId = 0x00001000;
        const uint markerRefFormId = 0x00005000;
        const uint questScriptFormId = 0x00006000;
        const uint packageFormId = 0x00003001;
        const uint npcFormId = 0x00002002;

        var records = new RecordCollection
        {
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = 0x00002000,
                    EditorId = "TestScript",
                    ReferencedObjects = [itemFormId]
                }
            ],
            Containers =
            [
                new ContainerRecord
                {
                    FormId = 0x00002001,
                    EditorId = "TestContainer",
                    Contents = [new InventoryItem(itemFormId, 2)]
                }
            ],
            Npcs =
            [
                new NpcRecord
                {
                    FormId = npcFormId,
                    EditorId = "TestNpc",
                    Inventory = [new InventoryItem(itemFormId, 1)],
                    Packages = [packageFormId]
                }
            ],
            Dialogues =
            [
                new DialogueRecord
                {
                    FormId = 0x00002003,
                    EditorId = "TestDialogue",
                    ResultScripts =
                    [
                        new DialogueResultScript
                        {
                            SourceText = "StartCombat Player",
                            ReferencedObjects = [markerRefFormId]
                        }
                    ]
                }
            ],
            LeveledLists =
            [
                new LeveledListRecord
                {
                    FormId = 0x00002004,
                    EditorId = "TestLeveledList",
                    Entries = [new LeveledEntry(1, itemFormId, 1)]
                }
            ],
            FormLists =
            [
                new FormListRecord
                {
                    FormId = 0x00002005,
                    EditorId = "TestFormList",
                    FormIds = [itemFormId]
                }
            ],
            Quests =
            [
                new QuestRecord
                {
                    FormId = 0x00002006,
                    EditorId = "TestQuest",
                    Script = questScriptFormId
                }
            ],
            Packages =
            [
                new PackageRecord
                {
                    FormId = packageFormId,
                    EditorId = "UseMarkerPackage",
                    Location = new PackageLocation
                    {
                        Type = 0,
                        Union = markerRefFormId,
                        Radius = 128
                    }
                }
            ]
        };

        var usageIndex = FormUsageIndex.Build(records);

        Assert.Equal(5, usageIndex.GetUseCount(itemFormId));
        Assert.Equal(3, usageIndex.GetUseCount(markerRefFormId));
        Assert.Equal(1, usageIndex.GetUseCount(questScriptFormId));

        var markerUses = usageIndex.GetUsages(markerRefFormId);
        Assert.Contains(markerUses, u => u.SourceFormId == packageFormId && u.SourceKind == "Package");
        Assert.Contains(markerUses, u => u.SourceFormId == npcFormId &&
                                         u.Context.StartsWith("AI package UseMarkerPackage:",
                                             StringComparison.Ordinal));
        Assert.Contains(markerUses, u => u.SourceKind == "Dialogue" && u.Context == "Result script 1");

        var questScriptUses = usageIndex.GetUsages(questScriptFormId);
        Assert.Contains(questScriptUses, u => u.SourceKind == "Quest" && u.Context == "Attached script");
    }

    [Fact]
    public void Build_DoesNotIndexCisPlaceholderAsFormId()
    {
        const uint placeholder = 0x00123456;
        var records = new RecordCollection
        {
            Game = BethesdaGame.Fallout4,
            Dialogues =
            [
                new DialogueRecord
                {
                    FormId = 0x100,
                    Conditions =
                    [
                        new DialogueCondition
                        {
                            FunctionIndex = 0x001, // GetDistance normally takes a FormID.
                            Parameter1 = placeholder,
                            Parameter1String = string.Empty
                        }
                    ]
                }
            ]
        };

        var usageIndex = FormUsageIndex.Build(records);

        Assert.Equal(0, usageIndex.GetUseCount(placeholder));
    }

    [Theory]
    [InlineData(BethesdaGame.Fallout3)]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    public void Build_DoesNotIndexActorValueEnumAsFormId(BethesdaGame game)
    {
        const uint actorValueIndex = 5;
        var records = new RecordCollection
        {
            Game = game,
            Dialogues =
            [
                new DialogueRecord
                {
                    FormId = 0x100,
                    Conditions =
                    [
                        new DialogueCondition
                        {
                            FunctionIndex = 0x00E,
                            Parameter1 = actorValueIndex
                        }
                    ]
                }
            ]
        };

        var usageIndex = FormUsageIndex.Build(records);

        Assert.Equal(0, usageIndex.GetUseCount(actorValueIndex));
    }

    [Fact]
    public void Build_IndexesUseGlobalComparisonButNotNumericComparisonBits()
    {
        const uint globalFormId = 0x00123456;
        var records = new RecordCollection
        {
            Game = BethesdaGame.Fallout4,
            Dialogues =
            [
                new DialogueRecord
                {
                    FormId = 0x100,
                    Conditions =
                    [
                        new DialogueCondition
                        {
                            Type = 0x04,
                            ComparisonValue = BitConverter.UInt32BitsToSingle(globalFormId)
                        },
                        new DialogueCondition
                        {
                            Type = 0,
                            ComparisonValue = BitConverter.UInt32BitsToSingle(0x00111111)
                        }
                    ]
                }
            ]
        };

        var usageIndex = FormUsageIndex.Build(records);

        var use = Assert.Single(usageIndex.GetUsages(globalFormId));
        Assert.Equal("Condition global comparison", use.Context);
        Assert.Equal(0, usageIndex.GetUseCount(0x00111111));
    }
}