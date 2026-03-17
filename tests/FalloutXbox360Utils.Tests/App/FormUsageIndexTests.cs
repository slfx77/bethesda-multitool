using FalloutXbox360Utils;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Xunit;

namespace FalloutXbox360Utils.Tests.App;

public class FormUsageIndexTests
{
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
                                         u.Context.StartsWith("AI package UseMarkerPackage:", StringComparison.Ordinal));
        Assert.Contains(markerUses, u => u.SourceKind == "Dialogue" && u.Context == "Result script 1");

        var questScriptUses = usageIndex.GetUsages(questScriptFormId);
        Assert.Contains(questScriptUses, u => u.SourceKind == "Quest" && u.Context == "Attached script");
    }
}
