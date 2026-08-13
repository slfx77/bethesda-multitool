using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public class QuestVariableConditionSanitizerTests
{
    private const uint QuestFormId = 0x00001000;
    private const uint ScriptFormId = 0x00002000;

    // Removed with retirement Stage E: the legacy Phase-3 loop's
    // ShouldDeferLegacyScriptModelToAugmentation gate deferred a legacy-encoded SCPT model
    // when an augmentation already owned its master target. The planner's PlanWriter owns
    // SCPT augmentation end to end (PlanWriter.BuildGrupForType emits the augmented master
    // before the generic model path), so there is no second encoder to defer to.

    [Fact]
    public void Apply_keeps_new_info_when_variable_name_and_id_match_retained_master_script()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(12, "HastingsBribed", 1)],
            [new ScriptVariableInfo(12, "HastingsBribed", 1)],
            NewDialogue(0x01000001, QuestFormId, 12));

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        var dialogue = Assert.Single(records.Collection.Dialogues);
        Assert.Equal(12u, Assert.Single(dialogue.Conditions).Parameter2);
        Assert.Equal(0, result.SuppressedInfoCount);
        Assert.Equal(0, result.RemappedConditionCount);
    }

    [Fact]
    public void Apply_remaps_new_info_by_unique_variable_name_and_type()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(27, "TroopersDead", 1)],
            [
                new ScriptVariableInfo(27, "DifferentRetailVariable", 1),
                new ScriptVariableInfo(31, "TroopersDead", 1)
            ],
            NewDialogue(0x01000002, QuestFormId, 27));

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        var condition = Assert.Single(Assert.Single(records.Collection.Dialogues).Conditions);
        Assert.Equal(31u, condition.Parameter2);
        Assert.Equal(1, result.RemappedConditionCount);
        Assert.Contains(result.Diagnostics, d => d.Code == "quest-variable.remapped");
        var mapping = Assert.Single(result.VariableRecoveryMappings);
        Assert.Equal(QuestFormId, mapping.SourceQuestFormId);
        Assert.Equal(QuestFormId, mapping.TargetQuestFormId);
        Assert.Equal(new ScriptVariableInfo(27, "TroopersDead", 1), mapping.SourceVariable);
        Assert.Equal(new ScriptVariableInfo(31, "TroopersDead", 1), mapping.TargetVariable);
    }

    [Fact]
    public void Apply_appends_fresh_variable_when_exact_variable_is_absent()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(27, "TroopersDead", 1)],
            [
                new ScriptVariableInfo(27, "DifferentRetailVariable", 1),
                new ScriptVariableInfo(31, "RetailMaximum", 1)
            ],
            NewDialogue(0x01000003, QuestFormId, 27));

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        var condition = Assert.Single(Assert.Single(records.Collection.Dialogues).Conditions);
        Assert.Equal(32u, condition.Parameter2);
        Assert.Equal(1, result.RemappedConditionCount);
        Assert.Equal(0, result.SuppressedInfoCount);
        var augmentation = Assert.Single(result.ScriptVariableAugmentations);
        Assert.Equal(ScriptFormId, augmentation.TargetScriptFormId);
        Assert.Equal(new ScriptVariableInfo(32, "TroopersDead", 1), augmentation.Variable);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "quest-variable.augmented-and-remapped"
                          && diagnostic.Metadata["condition-remapped-variable-index"] == "32");
    }

    [Fact]
    public void Apply_suppresses_entire_new_info_when_source_variable_metadata_is_missing()
    {
        var dialogue = NewDialogue(0x01000003, QuestFormId, 9) with
        {
            EditorId = "PrototypeInfo",
            TopicFormId = 0x00003000,
            PromptText = "Ask about the prototype",
            SpeakerFactionFormId = 0x00004000,
            Responses =
            [
                new DialogueResponse { ResponseNumber = 1, Text = "Prototype answer." }
            ],
            Conditions =
            [
                new DialogueCondition { FunctionIndex = 1, Parameter1 = 0 },
                QuestVariable(QuestFormId, 9)
            ]
        };
        var records = BuildRecords(
            [],
            [new ScriptVariableInfo(4, "RetailState", 1)],
            dialogue);

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Empty(records.Collection.Dialogues);
        Assert.Equal(1, result.SuppressedInfoCount);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.True(diagnostic.RecordSuppressed);
        Assert.Null(diagnostic.VariableName);
        Assert.Equal("PrototypeInfo", diagnostic.Metadata["record-editor-id"]);
        Assert.Equal("Ask about the prototype", diagnostic.Metadata["info-prompt"]);
        Assert.Equal("0x00003000", diagnostic.Metadata["info-topic-form-id"]);
        Assert.Null(diagnostic.Metadata["info-speaker-form-id"]);
        Assert.Equal("0x00004000", diagnostic.Metadata["info-speaker-faction-form-id"]);
        Assert.Equal("faction", diagnostic.Metadata["info-speaker-scope-kind"]);
        Assert.Equal("0x00004000", diagnostic.Metadata["info-speaker-scope-form-id"]);
        Assert.Equal("1", diagnostic.Metadata["info-response-count"]);
        Assert.Equal("1", diagnostic.Metadata["info-response-000-number"]);
        Assert.Equal("Prototype answer.", diagnostic.Metadata["info-response-000-text"]);
        Assert.Equal("0x00001000", diagnostic.Metadata["condition-target-form-id"]);
        Assert.Equal("9", diagnostic.Metadata["condition-variable-index"]);
        Assert.Null(diagnostic.Metadata["condition-variable-name"]);
        Assert.Equal("0x00002000", diagnostic.Metadata["condition-target-script-form-id"]);
        Assert.Empty(result.ScriptVariableAugmentations);
    }

    [Fact]
    public void Apply_preserves_shared_overlay_but_blocks_derived_info_when_condition_is_unsafe()
    {
        const uint sharedInfo = 0x00003001;
        var dialogue = NewDialogue(sharedInfo, QuestFormId, 9) with
        {
            PromptText = "Ask about the prototype",
            Responses = [new DialogueResponse { ResponseNumber = 2, Text = "Cut response." }]
        };
        var records = BuildRecords(
            [],
            [new ScriptVariableInfo(4, "RetailState", 1)],
            dialogue);
        records.MasterRecords[sharedInfo] = MasterRecord("INFO", sharedInfo);

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null,
            sanitizeMasterAnchoredInfoFormIds: new HashSet<uint> { sharedInfo });

        var retainedOverlaySource = Assert.Single(records.Collection.Dialogues);
        Assert.Equal(sharedInfo, retainedOverlaySource.FormId);
        Assert.True(retainedOverlaySource.SuppressPrototypeDerivedDialogue);
        Assert.Equal(0, result.SuppressedInfoCount);
        Assert.Equal(1, result.SuppressedPrototypeDerivedInfoCount);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("quest-variable.prototype-derived-info-suppressed", diagnostic.Code);
        Assert.False(diagnostic.RecordSuppressed);
        Assert.Equal("retail-overlay-retained", diagnostic.Metadata["owner-disposition"]);
        Assert.Equal("quest-variable.record-suppressed", diagnostic.Metadata["suppression-reason-code"]);
    }

    [Fact]
    public void Apply_remaps_shared_prototype_conditions_for_safe_derived_info_only()
    {
        const uint sharedInfo = 0x00003002;
        var dialogue = NewDialogue(sharedInfo, QuestFormId, 27) with
        {
            PromptText = "Ask about the prototype",
            Responses = [new DialogueResponse { ResponseNumber = 2, Text = "Cut response." }]
        };
        var records = BuildRecords(
            [new ScriptVariableInfo(27, "PrototypeState", 1)],
            [new ScriptVariableInfo(31, "PrototypeState", 1)],
            dialogue);
        records.MasterRecords[sharedInfo] = MasterRecord("INFO", sharedInfo);

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null,
            sanitizeMasterAnchoredInfoFormIds: new HashSet<uint> { sharedInfo });

        var retainedOverlaySource = Assert.Single(records.Collection.Dialogues);
        Assert.False(retainedOverlaySource.SuppressPrototypeDerivedDialogue);
        Assert.Equal(31u, Assert.Single(retainedOverlaySource.Conditions).Parameter2);
        Assert.Equal(0, result.SuppressedPrototypeDerivedInfoCount);
        Assert.Equal(1, result.RemappedConditionCount);
        Assert.Equal(31u, Assert.Single(result.VariableRecoveryMappings).TargetVariable.Index);
    }

    [Fact]
    public void Apply_augments_and_remaps_new_package_without_widening_it()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(25, "bUlyssesFired", 1)],
            [new ScriptVariableInfo(7, "ArcadeHired", 1)]);
        records.Collection.Packages.Add(new PackageRecord
        {
            FormId = 0x01000004,
            EditorId = "PrototypeFollowerPackage",
            Conditions =
            [
                new DialogueCondition { FunctionIndex = 1 },
                QuestVariable(QuestFormId, 25)
            ]
        });

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        var package = Assert.Single(records.Collection.Packages);
        Assert.Equal(8u, package.Conditions[1].Parameter2);
        Assert.Equal(0, result.SuppressedPackageCount);
        Assert.Equal(0, result.InvalidConditionCount);
        Assert.Equal(
            new ScriptVariableInfo(8, "bUlyssesFired", 1),
            Assert.Single(result.ScriptVariableAugmentations).Variable);
    }

    [Fact]
    public void Apply_does_not_require_scpt_for_info_when_dialogue_emission_is_skipped()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(25, "PrototypeDialogueState", 1)],
            [new ScriptVariableInfo(7, "RetailState", 1)],
            NewDialogue(0x01000030, QuestFormId, 25));

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null,
            false);

        Assert.Equal(25u, Assert.Single(Assert.Single(records.Collection.Dialogues).Conditions).Parameter2);
        Assert.Equal(0, result.RemappedConditionCount);
        Assert.Empty(result.ScriptVariableAugmentations);
        Assert.Empty(result.Diagnostics);

        PluginBuilder.EnsureScriptVariableAugmentationsCanBeEmitted(
            result.ScriptVariableAugmentations,
            new HashSet<string>(["DIAL", "SCPT"], StringComparer.Ordinal));
    }

    [Fact]
    public void Apply_does_not_require_scpt_for_package_when_package_emission_is_skipped()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(25, "PrototypePackageState", 1)],
            [new ScriptVariableInfo(7, "RetailState", 1)]);
        records.Collection.Packages.Add(NewPackage(0x01000031, QuestFormId, 25));

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null,
            sanitizePackageRecords: false);

        Assert.Equal(25u, Assert.Single(Assert.Single(records.Collection.Packages).Conditions).Parameter2);
        Assert.Equal(0, result.RemappedConditionCount);
        Assert.Empty(result.ScriptVariableAugmentations);
        Assert.Empty(result.Diagnostics);

        PluginBuilder.EnsureScriptVariableAugmentationsCanBeEmitted(
            result.ScriptVariableAugmentations,
            new HashSet<string>(["PACK", "SCPT"], StringComparer.Ordinal));
    }

    [Fact]
    public void Apply_excludes_skipped_info_from_enabled_package_augmentation_allocation()
    {
        var records = BuildRecords(
            [
                new ScriptVariableInfo(25, "AInfoOnlyState", 1),
                new ScriptVariableInfo(26, "ZPackageState", 1)
            ],
            [new ScriptVariableInfo(40, "RetailMaximum", 1)],
            NewDialogue(0x01000032, QuestFormId, 25));
        records.Collection.Packages.Add(NewPackage(0x01000033, QuestFormId, 26));

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null,
            false);

        Assert.Equal(25u, Assert.Single(Assert.Single(records.Collection.Dialogues).Conditions).Parameter2);
        Assert.Equal(41u, Assert.Single(Assert.Single(records.Collection.Packages).Conditions).Parameter2);
        Assert.Equal(
            new ScriptVariableInfo(41, "ZPackageState", 1),
            Assert.Single(result.ScriptVariableAugmentations).Variable);
    }

    [Fact]
    public void Apply_excludes_skipped_package_from_enabled_info_augmentation_allocation()
    {
        var records = BuildRecords(
            [
                new ScriptVariableInfo(25, "ZInfoState", 1),
                new ScriptVariableInfo(26, "APackageOnlyState", 1)
            ],
            [new ScriptVariableInfo(40, "RetailMaximum", 1)],
            NewDialogue(0x01000034, QuestFormId, 25));
        records.Collection.Packages.Add(NewPackage(0x01000035, QuestFormId, 26));

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null,
            sanitizePackageRecords: false);

        Assert.Equal(41u, Assert.Single(Assert.Single(records.Collection.Dialogues).Conditions).Parameter2);
        Assert.Equal(26u, Assert.Single(Assert.Single(records.Collection.Packages).Conditions).Parameter2);
        Assert.Equal(
            new ScriptVariableInfo(41, "ZInfoState", 1),
            Assert.Single(result.ScriptVariableAugmentations).Variable);
        Assert.Throws<InvalidOperationException>(() =>
            PluginBuilder.EnsureScriptVariableAugmentationsCanBeEmitted(
                result.ScriptVariableAugmentations,
                new HashSet<string>(["PACK", "SCPT"], StringComparer.Ordinal)));
    }

    [Fact]
    public void Apply_keeps_master_anchored_info_untouched()
    {
        const uint sharedInfo = 0x00003000;
        var records = BuildRecords(
            [new ScriptVariableInfo(9, "PrototypeOnly", 1)],
            [],
            NewDialogue(sharedInfo, QuestFormId, 9));
        records.MasterRecords.Add(sharedInfo, MasterRecord("INFO", sharedInfo));

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Single(records.Collection.Dialogues);
        Assert.Equal(0, result.SuppressedInfoCount);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Apply_keeps_condition_when_target_script_table_cannot_be_proven()
    {
        var dialogue = NewDialogue(0x01000005, QuestFormId, 99);
        var collection = new RecordCollection
        {
            Quests = [new QuestRecord { FormId = QuestFormId }],
            Dialogues = [dialogue]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [QuestFormId] = MasterQuest(QuestFormId, 0x0000DEAD)
        };

        var result = QuestVariableConditionSanitizer.Apply(collection, masterRecords, null);

        Assert.Single(collection.Dialogues);
        Assert.Equal(1, result.UnresolvedTargetCount);
        Assert.Contains(result.Diagnostics, d => d.Code == "quest-variable.target-unresolved");
    }

    [Fact]
    public void Apply_resolves_quest_and_script_aliases_before_matching_variables()
    {
        const uint sourceQuest = 0x00101000;
        const uint sourceScript = 0x00102000;
        var collection = new RecordCollection
        {
            Quests = [new QuestRecord { FormId = sourceQuest, Script = sourceScript }],
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = sourceScript,
                    Variables = [new ScriptVariableInfo(6, "SiriMentionedBrahmin", 1)],
                    SourceText = "scn PrototypeScript\nshort SiriMentionedBrahmin\nBegin GameMode\nEnd",
                    SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
                    SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
                    CompiledData = [0x00, 0x1D, 0x00, 0x00]
                }
            ],
            Dialogues = [NewDialogue(0x01000006, sourceQuest, 6)]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [QuestFormId] = MasterQuest(QuestFormId, ScriptFormId),
            [ScriptFormId] = MasterScript(
                ScriptFormId,
                [new ScriptVariableInfo(8, "SiriMentionedBrahmin", 1)])
        };
        var remap = new Dictionary<uint, uint>
        {
            [sourceQuest] = QuestFormId,
            [sourceScript] = ScriptFormId
        };

        var result = QuestVariableConditionSanitizer.Apply(collection, masterRecords, remap);

        Assert.Equal(8u, Assert.Single(Assert.Single(collection.Dialogues).Conditions).Parameter2);
        Assert.Equal(1, result.RemappedConditionCount);
    }

    [Fact]
    public void Apply_rejects_nonzero_runtime_header_and_variable_list_count_conflict()
    {
        const uint sourceQuest = 0x00101010;
        const uint sourceScript = 0x00102010;
        var collection = new RecordCollection
        {
            Quests = [new QuestRecord { FormId = sourceQuest, Script = sourceScript }],
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = sourceScript,
                    Variables = [new ScriptVariableInfo(6, "CapturedState", 1)],
                    SourceText = "scn PrototypeScript\nshort CapturedState\nBegin GameMode\nEnd",
                    SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
                    SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
                    CompiledData = [0x00, 0x1D, 0x00, 0x00]
                }
            ],
            RuntimeScripts =
            [
                new RuntimeScriptData
                {
                    FormId = sourceScript,
                    HeaderVariableCount = 2,
                    VariableCount = 2,
                    LastVariableId = 6,
                    Variables = [new ScriptVariableInfo(6, "CapturedState", 1)],
                    VariableMetadataComplete = true,
                    VariablesComplete = false
                }
            ],
            Dialogues = [NewDialogue(0x01000016, sourceQuest, 6)]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [QuestFormId] = MasterQuest(QuestFormId, ScriptFormId),
            [ScriptFormId] = MasterScript(
                ScriptFormId,
                [new ScriptVariableInfo(6, "CapturedState", 1)])
        };
        var aliases = new Dictionary<uint, uint>
        {
            [sourceQuest] = QuestFormId,
            [sourceScript] = ScriptFormId
        };

        var result = QuestVariableConditionSanitizer.Apply(collection, masterRecords, aliases);

        Assert.Empty(collection.Dialogues);
        Assert.Equal(1, result.SuppressedInfoCount);
        Assert.Equal("quest-variable.record-suppressed", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Apply_prefers_raw_dmp_source_table_over_unrelated_resolved_runtime_alias()
    {
        const uint sourceQuest = 0x00101020;
        const uint sourceScript = 0x00102020;
        var collection = new RecordCollection
        {
            Quests = [new QuestRecord { FormId = sourceQuest, Script = sourceScript }],
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = sourceScript,
                    Variables = [new ScriptVariableInfo(6, "PrototypeState", 1)],
                    SourceText = "scn PrototypeScript\nshort PrototypeState\nBegin GameMode\nEnd",
                    SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
                    SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
                    CompiledData = [0x00, 0x1D, 0x00, 0x00]
                }
            ],
            RuntimeScripts =
            [
                new RuntimeScriptData
                {
                    // This is the resolved retail identity, not the raw captured SCPT.
                    FormId = ScriptFormId,
                    HeaderVariableCount = 1,
                    VariableCount = 1,
                    LastVariableId = 6,
                    Variables = [new ScriptVariableInfo(6, "RetailState", 1)],
                    VariableMetadataComplete = true,
                    VariablesComplete = true
                }
            ],
            Dialogues = [NewDialogue(0x01000017, sourceQuest, 6)]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [QuestFormId] = MasterQuest(QuestFormId, ScriptFormId),
            [ScriptFormId] = MasterScript(
                ScriptFormId,
                [new ScriptVariableInfo(6, "RetailState", 1)])
        };
        var aliases = new Dictionary<uint, uint>
        {
            [sourceQuest] = QuestFormId,
            [sourceScript] = ScriptFormId
        };

        var result = QuestVariableConditionSanitizer.Apply(collection, masterRecords, aliases);

        var condition = Assert.Single(Assert.Single(collection.Dialogues).Conditions);
        Assert.Equal(7u, condition.Parameter2);
        var augmentation = Assert.Single(result.ScriptVariableAugmentations);
        Assert.Equal("PrototypeState", augmentation.Variable.Name);
        Assert.Equal(7u, augmentation.Variable.Index);
    }

    [Fact]
    public void Apply_augments_retained_master_scri_when_master_quest_model_points_to_new_dmp_script()
    {
        const uint prototypeScript = 0x00102000;
        var collection = new RecordCollection
        {
            Quests = [new QuestRecord { FormId = QuestFormId, Script = prototypeScript }],
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = prototypeScript,
                    Variables = [new ScriptVariableInfo(9, "PrototypeOnly", 1)],
                    SourceText = "scn PrototypeScript\nshort PrototypeOnly\nBegin GameMode\nEnd",
                    SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
                    SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
                    CompiledData = [0x00, 0x1D, 0x00, 0x00]
                }
            ],
            Dialogues = [NewDialogue(0x0100000A, QuestFormId, 9)]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [QuestFormId] = MasterQuest(QuestFormId, ScriptFormId),
            [ScriptFormId] = MasterScript(
                ScriptFormId,
                [new ScriptVariableInfo(4, "RetailState", 1)])
        };

        var result = QuestVariableConditionSanitizer.Apply(collection, masterRecords, null);

        Assert.Equal(5u, Assert.Single(Assert.Single(collection.Dialogues).Conditions).Parameter2);
        Assert.Equal(0, result.SuppressedInfoCount);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ScriptFormId, diagnostic.TargetScriptFormId);
        Assert.Equal("PrototypeOnly", diagnostic.VariableName);
        Assert.Equal(
            new ScriptVariableInfo(5, "PrototypeOnly", 1),
            Assert.Single(result.ScriptVariableAugmentations).Variable);
    }

    [Fact]
    public void Apply_uses_new_dmp_script_table_for_new_quest()
    {
        const uint newQuest = 0x00111000;
        const uint newScript = 0x00112000;
        var collection = new RecordCollection
        {
            Quests = [new QuestRecord { FormId = newQuest, Script = newScript }],
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = newScript,
                    Variables = [new ScriptVariableInfo(3, "PrototypeState", 1)],
                    SourceText = "scn PrototypeScript\nshort PrototypeState\nBegin GameMode\nEnd",
                    SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
                    SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
                    CompiledData = [0x00, 0x1D, 0x00, 0x00]
                }
            ],
            Dialogues = [NewDialogue(0x01000007, newQuest, 3)]
        };

        var result = QuestVariableConditionSanitizer.Apply(
            collection,
            new Dictionary<uint, ParsedMainRecord>(),
            null);

        Assert.Single(collection.Dialogues);
        Assert.Equal(0, result.SuppressedInfoCount);
    }

    [Fact]
    public void Apply_suppresses_only_unsafe_terminal_menu_item()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(7, "KnownState", 1)],
            [new ScriptVariableInfo(7, "KnownState", 1)]);
        records.Collection.Terminals.Add(new TerminalRecord
        {
            FormId = 0x01000070,
            EditorId = "PrototypeTerminal",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "Unsafe",
                    Conditions = [QuestVariable(QuestFormId, 99)]
                },
                new TerminalMenuItem { Text = "Safe" }
            ]
        });

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        var terminal = Assert.Single(records.Collection.Terminals);
        Assert.Equal("Safe", Assert.Single(terminal.MenuItems).Text);
        Assert.Equal(1, result.SuppressedTerminalMenuItemCount);
        Assert.Equal(0, result.SuppressedInfoCount);
        Assert.Equal(0, result.SuppressedPackageCount);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("TERM", diagnostic.RecordType);
        Assert.True(diagnostic.RecordSuppressed);
        Assert.Equal("0", diagnostic.Metadata["terminal-menu-index"]);
        Assert.Equal("Unsafe", diagnostic.Metadata["terminal-menu-text"]);
    }

    [Fact]
    public void Apply_remaps_terminal_quest_variable_condition_without_changing_sibling_items()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(7, "KnownState", 1)],
            [new ScriptVariableInfo(12, "KnownState", 1)]);
        records.Collection.Terminals.Add(new TerminalRecord
        {
            FormId = 0x01000071,
            EditorId = "PrototypeTerminal",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "Conditional",
                    Conditions = [QuestVariable(QuestFormId, 7)]
                },
                new TerminalMenuItem { Text = "Sibling" }
            ]
        });

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        var items = Assert.Single(records.Collection.Terminals).MenuItems;
        Assert.Equal(2, items.Count);
        Assert.Equal(12u, Assert.Single(items[0].Conditions).Parameter2);
        Assert.Equal("Sibling", items[1].Text);
        Assert.Equal(0, result.SuppressedTerminalMenuItemCount);
        Assert.Equal(1, result.RemappedConditionCount);
    }

    [Fact]
    public void Apply_allocates_fresh_append_only_local_for_terminal_only_condition()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(27, "TerminalPrototypeState", 1)],
            [
                new ScriptVariableInfo(27, "DifferentRetailVariable", 1),
                new ScriptVariableInfo(31, "RetailMaximum", 1)
            ]);
        records.Collection.Terminals.Add(new TerminalRecord
        {
            FormId = 0x01000072,
            EditorId = "PrototypeTerminal",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "Prototype-only state",
                    Conditions = [QuestVariable(QuestFormId, 27)]
                }
            ]
        });

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        var condition = Assert.Single(Assert.Single(Assert.Single(
            records.Collection.Terminals).MenuItems).Conditions);
        Assert.Equal(32u, condition.Parameter2);
        var augmentation = Assert.Single(result.ScriptVariableAugmentations);
        Assert.Equal(ScriptFormId, augmentation.TargetScriptFormId);
        Assert.Equal(new ScriptVariableInfo(32, "TerminalPrototypeState", 1), augmentation.Variable);
        var mapping = Assert.Single(result.VariableRecoveryMappings);
        Assert.Equal(new ScriptVariableInfo(27, "TerminalPrototypeState", 1), mapping.SourceVariable);
        Assert.Equal(augmentation.Variable, mapping.TargetVariable);
        Assert.Equal(0, result.SuppressedTerminalMenuItemCount);
    }

    [Fact]
    public void Apply_suppresses_only_get_script_variable_terminal_item_when_owner_is_unresolved()
    {
        var collection = new RecordCollection
        {
            Terminals =
            [
                new TerminalRecord
                {
                    FormId = 0x01000073,
                    EditorId = "PrototypeTerminal",
                    MenuItems =
                    [
                        new TerminalMenuItem
                        {
                            Text = "Unsafe owner",
                            Conditions =
                            [
                                new DialogueCondition
                                {
                                    FunctionIndex = QuestVariableConditionSanitizer.GetScriptVariableFunctionIndex,
                                    Parameter1 = 0x0010E70C,
                                    Parameter2 = 4
                                }
                            ]
                        },
                        new TerminalMenuItem { Text = "Safe sibling" }
                    ]
                }
            ]
        };

        var result = QuestVariableConditionSanitizer.Apply(
            collection,
            new Dictionary<uint, ParsedMainRecord>(),
            null);

        Assert.Equal("Safe sibling", Assert.Single(Assert.Single(collection.Terminals).MenuItems).Text);
        Assert.Equal(1, result.SuppressedTerminalMenuItemCount);
        Assert.Equal(0, result.SuppressedInfoCount);
        Assert.Equal(0, result.SuppressedPackageCount);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("script-variable.owner-unresolved", diagnostic.Code);
        Assert.Equal("TERM", diagnostic.RecordType);
        Assert.Equal("0", diagnostic.Metadata["terminal-menu-index"]);
    }

    [Fact]
    public void Apply_suppresses_get_script_variable_package_when_owner_is_unresolved()
    {
        var collection = new RecordCollection
        {
            Packages =
            [
                new PackageRecord
                {
                    FormId = 0x01000008,
                    EditorId = "BodyguardPackage",
                    Conditions =
                    [
                        new DialogueCondition
                        {
                            FunctionIndex = QuestVariableConditionSanitizer.GetScriptVariableFunctionIndex,
                            Parameter1 = 0x0010E70C,
                            Parameter2 = 4
                        }
                    ]
                }
            ]
        };

        var result = QuestVariableConditionSanitizer.Apply(
            collection,
            new Dictionary<uint, ParsedMainRecord>(),
            null);

        Assert.Empty(collection.Packages);
        Assert.Equal(1, result.SuppressedPackageCount);
        Assert.Equal(0, result.RetainedGetScriptVariableCount);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("script-variable.owner-unresolved", diagnostic.Code);
        Assert.True(diagnostic.RecordSuppressed);
    }

    [Fact]
    public void Apply_suppresses_entire_get_script_variable_info_when_owner_is_unresolved()
    {
        var collection = new RecordCollection
        {
            Dialogues = [NewScriptVariableDialogue(0x01000009, 0x0010E70C, 4)]
        };

        var result = QuestVariableConditionSanitizer.Apply(
            collection,
            new Dictionary<uint, ParsedMainRecord>(),
            null);

        Assert.Empty(collection.Dialogues);
        Assert.Equal(1, result.SuppressedInfoCount);
        Assert.Equal(0, result.RetainedGetScriptVariableCount);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("script-variable.owner-unresolved", diagnostic.Code);
        Assert.Equal("INFO", diagnostic.RecordType);
        Assert.True(diagnostic.RecordSuppressed);
    }

    [Fact]
    public void Apply_retains_info_get_script_variable_after_proving_exact_owner_chain()
    {
        const uint owner = 0x00010002;
        const uint npc = 0x00011002;
        const uint script = 0x00012002;
        var collection = new RecordCollection
        {
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = script,
                    Variables = [new ScriptVariableInfo(4, "GuardState", 1)],
                    SourceText = "scn PrototypeScript\nshort GuardState\nBegin GameMode\nEnd",
                    SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
                    SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
                    CompiledData = [0x00, 0x1D, 0x00, 0x00]
                }
            ],
            Dialogues = [NewScriptVariableDialogue(0x01000102, owner, 4)]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [owner] = MasterRecord("ACHR", owner, FormIdSubrecord("NAME", npc)),
            [npc] = MasterRecord("NPC_", npc, FormIdSubrecord("SCRI", script)),
            [script] = MasterScript(script, [new ScriptVariableInfo(4, "GuardState", 1)])
        };

        var result = QuestVariableConditionSanitizer.Apply(collection, masterRecords, null);

        var info = Assert.Single(collection.Dialogues);
        Assert.Equal(4u, Assert.Single(info.Conditions).Parameter2);
        Assert.Equal(1, result.RetainedGetScriptVariableCount);
        Assert.Equal(0, result.SuppressedInfoCount);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Apply_retains_get_script_variable_after_proving_master_owner_chain_and_captured_source_local()
    {
        const uint owner = 0x00010000;
        const uint npc = 0x00011000;
        const uint script = 0x00012000;
        var collection = new RecordCollection
        {
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = script,
                    Variables = [new ScriptVariableInfo(4, "GuardState", 1)],
                    SourceText = "scn PrototypeScript\nshort GuardState\nBegin GameMode\nEnd",
                    SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
                    SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
                    CompiledData = [0x00, 0x1D, 0x00, 0x00]
                }
            ],
            Packages = [NewScriptVariablePackage(0x01000100, owner, 4)]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [owner] = MasterRecord("ACHR", owner, FormIdSubrecord("NAME", npc)),
            [npc] = MasterRecord("NPC_", npc, FormIdSubrecord("SCRI", script)),
            [script] = MasterScript(script, [new ScriptVariableInfo(4, "GuardState", 1)])
        };

        var result = QuestVariableConditionSanitizer.Apply(collection, masterRecords, null);

        var package = Assert.Single(collection.Packages);
        Assert.Equal(4u, Assert.Single(package.Conditions).Parameter2);
        Assert.Equal(1, result.RetainedGetScriptVariableCount);
        Assert.Equal(0, result.SuppressedPackageCount);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Apply_suppresses_get_script_variable_for_conflicting_same_formid_raw_scripts()
    {
        const uint owner = 0x00010001;
        const uint npc = 0x00011001;
        const uint script = 0x00012001;
        var collection = new RecordCollection
        {
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = script,
                    Variables = [new ScriptVariableInfo(4, "GuardState", 1)],
                    SourceText = "scn PrototypeScriptA\nshort GuardState\nBegin GameMode\nEnd",
                    SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
                    SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
                    CompiledData = [0x00, 0x1D, 0x00, 0x00]
                },
                new ScriptRecord
                {
                    FormId = script,
                    Variables = [new ScriptVariableInfo(4, "GuardState", 1)],
                    SourceText = "scn PrototypeScriptB\nlong GuardState\nBegin GameMode\nEnd",
                    SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
                    SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
                    CompiledData = [0x00, 0x1D, 0x00, 0x00]
                }
            ],
            Packages = [NewScriptVariablePackage(0x01000101, owner, 4)]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [owner] = MasterRecord("ACHR", owner, FormIdSubrecord("NAME", npc)),
            [npc] = MasterRecord("NPC_", npc, FormIdSubrecord("SCRI", script)),
            [script] = MasterScript(script, [new ScriptVariableInfo(4, "GuardState", 1)])
        };

        var result = QuestVariableConditionSanitizer.Apply(collection, masterRecords, null);

        Assert.Empty(collection.Packages);
        Assert.Equal(1, result.SuppressedPackageCount);
        Assert.Equal("script-variable.source-script-unresolved", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Apply_uses_structurally_complete_runtime_metadata_after_standalone_source_acceptance()
    {
        const uint owner = 0x00010010;
        const uint npc = 0x00011010;
        const uint script = 0x00012010;
        var collection = new RecordCollection
        {
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = script,
                    Variables = [new ScriptVariableInfo(17, "ExactState", 1)],
                    SourceText = "scn RuntimeMetadataScript\nshort ExactState",
                    SourceTextOrigin = ScriptSourceTextOrigin.RuntimeSameObject,
                    SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
                    CompiledData = [0x00, 0x1D, 0x00, 0x00]
                }
            ],
            RuntimeScripts =
            [
                new RuntimeScriptData
                {
                    FormId = script,
                    EditorId = "RuntimeMetadataScript",
                    HeaderVariableCount = 0,
                    VariableCount = 1,
                    LastVariableId = 17,
                    SourceText = "scn RuntimeMetadataScript\nshort ExactState",
                    SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
                    CompiledData = [0x00, 0x1D, 0x00, 0x00],
                    DataSize = 4,
                    Variables = [new ScriptVariableInfo(17, "ExactState", 1)],
                    VariableMetadataComplete = true,
                    VariablesComplete = true,
                    ReferencedObjectsComplete = true
                }
            ],
            Packages = [NewScriptVariablePackage(0x01000110, owner, 17)]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [owner] = MasterRecord("ACHR", owner, FormIdSubrecord("NAME", npc)),
            [npc] = MasterRecord("NPC_", npc, FormIdSubrecord("SCRI", script)),
            [script] = MasterScript(script, [new ScriptVariableInfo(17, "ExactState", 1)])
        };

        var result = QuestVariableConditionSanitizer.Apply(collection, masterRecords, null);

        Assert.Single(collection.Scripts);
        Assert.Single(collection.RuntimeScripts);
        Assert.Equal(17u, Assert.Single(Assert.Single(collection.Packages).Conditions).Parameter2);
        Assert.Equal(1, result.RetainedGetScriptVariableCount);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Apply_retains_same_get_script_variable_slot_when_sctx_was_rejected()
    {
        const uint owner = 0x00010011;
        const uint npc = 0x00011011;
        const uint script = 0x00012011;
        var collection = new RecordCollection
        {
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = script,
                    Variables = [new ScriptVariableInfo(17, "StaleState", 1)],
                    // EnforceCapturedSourceCorrespondence represents rejection by clearing SCTX.
                    SourceText = null
                }
            ],
            RuntimeScripts =
            [
                new RuntimeScriptData
                {
                    FormId = script,
                    VariableCount = 1,
                    LastVariableId = 17,
                    SourceText = "scn RejectedRuntimeScript\nshort StaleState",
                    SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Rejected,
                    Variables = [new ScriptVariableInfo(17, "StaleState", 1)],
                    VariableMetadataComplete = true,
                    VariablesComplete = true,
                    ReferencedObjectsComplete = true
                }
            ],
            Packages = [NewScriptVariablePackage(0x01000111, owner, 17)]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [owner] = MasterRecord("ACHR", owner, FormIdSubrecord("NAME", npc)),
            [npc] = MasterRecord("NPC_", npc, FormIdSubrecord("SCRI", script)),
            [script] = MasterScript(script, [new ScriptVariableInfo(17, "StaleState", 1)])
        };

        var result = QuestVariableConditionSanitizer.Apply(collection, masterRecords, null);

        Assert.Equal(17u, Assert.Single(Assert.Single(collection.Packages).Conditions).Parameter2);
        Assert.Equal(0, result.SuppressedPackageCount);
        Assert.Equal(1, result.RetainedGetScriptVariableCount);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Apply_retains_same_get_script_variable_slot_without_using_source_only_text()
    {
        const uint owner = 0x00010012;
        const uint npc = 0x00011012;
        const uint script = 0x00012012;
        const string source = "scn SourceOnlyScript\nshort SourceOnlyState";
        var collection = new RecordCollection
        {
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = script,
                    Variables = [new ScriptVariableInfo(17, "SourceOnlyState", 1)],
                    SourceText = source,
                    SourceTextOrigin = ScriptSourceTextOrigin.RuntimeSameObject,
                    SourceTextCorrespondenceStatus =
                        ScriptSourceCorrespondenceStatus.AcceptedSourceOnly
                }
            ],
            RuntimeScripts =
            [
                new RuntimeScriptData
                {
                    FormId = script,
                    VariableCount = 1,
                    Variables = [new ScriptVariableInfo(17, "SourceOnlyState", 1)],
                    VariableMetadataComplete = true,
                    VariablesComplete = true,
                    SourceText = source,
                    SourceTextCorrespondenceStatus =
                        ScriptSourceCorrespondenceStatus.AcceptedSourceOnly
                }
            ],
            Packages = [NewScriptVariablePackage(0x01000112, owner, 17)]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [owner] = MasterRecord("ACHR", owner, FormIdSubrecord("NAME", npc)),
            [npc] = MasterRecord("NPC_", npc, FormIdSubrecord("SCRI", script)),
            [script] = MasterScript(script, [new ScriptVariableInfo(17, "SourceOnlyState", 1)])
        };

        var result = QuestVariableConditionSanitizer.Apply(collection, masterRecords, null);

        Assert.Equal(17u, Assert.Single(Assert.Single(collection.Packages).Conditions).Parameter2);
        Assert.Equal(0, result.SuppressedPackageCount);
        Assert.Equal(1, result.RetainedGetScriptVariableCount);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Apply_does_not_remap_get_script_variable_by_name_without_concrete_declaration()
    {
        const uint owner = 0x00010013;
        const uint npc = 0x00011013;
        const uint script = 0x00012013;
        var collection = new RecordCollection
        {
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = script,
                    Variables = [new ScriptVariableInfo(17, "OpaqueState", 1)],
                    SourceText = null
                }
            ],
            RuntimeScripts =
            [
                new RuntimeScriptData
                {
                    FormId = script,
                    VariableCount = 1,
                    Variables = [new ScriptVariableInfo(17, "OpaqueState", 1)],
                    VariableMetadataComplete = true,
                    VariablesComplete = true,
                    ReferencedObjectsComplete = true
                }
            ],
            Packages = [NewScriptVariablePackage(0x01000113, owner, 17)]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [owner] = MasterRecord("ACHR", owner, FormIdSubrecord("NAME", npc)),
            [npc] = MasterRecord("NPC_", npc, FormIdSubrecord("SCRI", script)),
            [script] = MasterScript(script, [new ScriptVariableInfo(18, "OpaqueState", 1)])
        };

        var result = QuestVariableConditionSanitizer.Apply(collection, masterRecords, null);

        Assert.Empty(collection.Packages);
        Assert.Equal(1, result.SuppressedPackageCount);
        Assert.Equal("script-variable.source-declaration-unresolved", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Apply_rejects_runtime_variable_prefix_when_walk_did_not_terminate()
    {
        const uint owner = 0x00010020;
        const uint npc = 0x00011020;
        const uint script = 0x00012020;
        var collection = new RecordCollection
        {
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = script,
                    Variables = [new ScriptVariableInfo(17, "ExactState", 1)],
                    SourceText = "scn PrototypeScript\nshort ExactState\nBegin GameMode\nEnd"
                }
            ],
            RuntimeScripts =
            [
                new RuntimeScriptData
                {
                    FormId = script,
                    Variables = [new ScriptVariableInfo(17, "ExactState", 1)],
                    VariableMetadataComplete = false
                }
            ],
            Packages = [NewScriptVariablePackage(0x01000120, owner, 17)]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [owner] = MasterRecord("ACHR", owner, FormIdSubrecord("NAME", npc)),
            [npc] = MasterRecord("NPC_", npc, FormIdSubrecord("SCRI", script)),
            [script] = MasterScript(script, [new ScriptVariableInfo(17, "ExactState", 1)])
        };

        var result = QuestVariableConditionSanitizer.Apply(collection, masterRecords, null);

        Assert.Empty(collection.Packages);
        Assert.Equal("script-variable.source-script-unresolved", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Apply_rejects_fragment_fallback_when_complete_runtime_table_is_empty()
    {
        const uint owner = 0x00010030;
        const uint npc = 0x00011030;
        const uint script = 0x00012030;
        var collection = new RecordCollection
        {
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = script,
                    Variables = [new ScriptVariableInfo(17, "FragmentOnlyState", 1)],
                    SourceText = "scn PrototypeScript\nshort FragmentOnlyState\nBegin GameMode\nEnd"
                }
            ],
            RuntimeScripts =
            [
                new RuntimeScriptData
                {
                    FormId = script,
                    HeaderVariableCount = 0,
                    VariableCount = 0,
                    Variables = [],
                    VariableMetadataComplete = true,
                    VariablesComplete = true
                }
            ],
            Packages = [NewScriptVariablePackage(0x01000130, owner, 17)]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [owner] = MasterRecord("ACHR", owner, FormIdSubrecord("NAME", npc)),
            [npc] = MasterRecord("NPC_", npc, FormIdSubrecord("SCRI", script)),
            [script] = MasterScript(script, [new ScriptVariableInfo(17, "FragmentOnlyState", 1)])
        };

        var result = QuestVariableConditionSanitizer.Apply(collection, masterRecords, null);

        Assert.Empty(collection.Packages);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("script-variable.source-metadata-missing", diagnostic.Code);
        Assert.Contains("no uniquely named local", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_suppresses_get_script_variable_without_captured_source_variable_table()
    {
        const uint owner = 0x00010100;
        const uint npc = 0x00011100;
        const uint script = 0x00012100;
        var collection = new RecordCollection
        {
            Packages = [NewScriptVariablePackage(0x01000101, owner, 4)]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [owner] = MasterRecord("ACHR", owner, FormIdSubrecord("NAME", npc)),
            [npc] = MasterRecord("NPC_", npc, FormIdSubrecord("SCRI", script)),
            [script] = MasterScript(script, [new ScriptVariableInfo(4, "GuardState", 1)])
        };

        var result = QuestVariableConditionSanitizer.Apply(collection, masterRecords, null);

        Assert.Empty(collection.Packages);
        Assert.Equal(1, result.SuppressedPackageCount);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("script-variable.source-script-unresolved", diagnostic.Code);
        Assert.Contains("no captured DMP variable table", diagnostic.Message);
    }

    [Fact]
    public void Apply_remaps_get_script_variable_by_exact_identity_on_retained_actor_script()
    {
        const uint owner = 0x00020000;
        const uint npc = 0x00021000;
        const uint prototypeScript = 0x00122000;
        const uint retailScript = 0x00022000;
        var collection = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    FormId = 0x00023000,
                    PlacedObjects =
                    [
                        new PlacedReference
                        {
                            FormId = owner,
                            RecordType = "ACHR",
                            BaseFormId = npc
                        }
                    ]
                }
            ],
            Npcs = [new NpcRecord { FormId = npc, Script = prototypeScript }],
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = prototypeScript,
                    Variables = [new ScriptVariableInfo(4, "GuardState", 1)],
                    SourceText = "scn PrototypeScript\nshort GuardState\nBegin GameMode\nEnd",
                    SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
                    SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
                    CompiledData = [0x00, 0x1D, 0x00, 0x00]
                }
            ],
            Packages = [NewScriptVariablePackage(0x01000200, owner, 4)]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [owner] = MasterRecord("ACHR", owner, FormIdSubrecord("NAME", npc)),
            [npc] = MasterRecord("NPC_", npc, FormIdSubrecord("SCRI", retailScript)),
            [retailScript] = MasterScript(
                retailScript,
                [
                    new ScriptVariableInfo(4, "RetailState", 1),
                    new ScriptVariableInfo(9, "GuardState", 1)
                ])
        };

        var result = QuestVariableConditionSanitizer.Apply(collection, masterRecords, null);

        var condition = Assert.Single(Assert.Single(collection.Packages).Conditions);
        Assert.Equal(9u, condition.Parameter2);
        Assert.Equal(1, result.RemappedConditionCount);
        Assert.Equal(0, result.RetainedGetScriptVariableCount);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("script-variable.remapped", diagnostic.Code);
        Assert.Equal("GuardState", diagnostic.VariableName);
        Assert.Equal(retailScript, diagnostic.TargetScriptFormId);
        Assert.Equal("0x00022000", diagnostic.Metadata["condition-target-script-form-id"]);
    }

    [Fact]
    public void Apply_suppresses_get_script_variable_when_retained_actor_has_no_exact_local()
    {
        const uint owner = 0x00030000;
        const uint npc = 0x00031000;
        const uint prototypeScript = 0x00132000;
        const uint retailScript = 0x00032000;
        var collection = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    PlacedObjects =
                    [
                        new PlacedReference
                        {
                            FormId = owner,
                            RecordType = "ACHR",
                            BaseFormId = npc
                        }
                    ]
                }
            ],
            Npcs = [new NpcRecord { FormId = npc, Script = prototypeScript }],
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = prototypeScript,
                    Variables = [new ScriptVariableInfo(4, "PrototypeOnly", 1)],
                    SourceText = "scn PrototypeScript\nshort PrototypeOnly\nBegin GameMode\nEnd",
                    SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
                    SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
                    CompiledData = [0x00, 0x1D, 0x00, 0x00]
                }
            ],
            Packages = [NewScriptVariablePackage(0x01000300, owner, 4)]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [owner] = MasterRecord("ACHR", owner, FormIdSubrecord("NAME", npc)),
            [npc] = MasterRecord("NPC_", npc, FormIdSubrecord("SCRI", retailScript)),
            [retailScript] = MasterScript(retailScript, [new ScriptVariableInfo(7, "RetailOnly", 1)])
        };

        var result = QuestVariableConditionSanitizer.Apply(collection, masterRecords, null);

        Assert.Empty(collection.Packages);
        Assert.Equal(1, result.SuppressedPackageCount);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("script-variable.exact-target-missing", diagnostic.Code);
        Assert.True(diagnostic.RecordSuppressed);
        Assert.Contains("exact captured name", diagnostic.Message);
    }

    [Fact]
    public void Apply_retains_get_script_variable_for_new_refr_base_and_script()
    {
        const uint owner = 0x00140000;
        const uint activator = 0x00141000;
        const uint script = 0x00142000;
        var collection = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    PlacedObjects =
                    [
                        new PlacedReference
                        {
                            FormId = owner,
                            RecordType = "REFR",
                            BaseFormId = activator
                        }
                    ]
                }
            ],
            Activators = [new ActivatorRecord { FormId = activator, Script = script }],
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = script,
                    Variables = [new ScriptVariableInfo(2, "EnabledState", 1)],
                    SourceText = "scn PrototypeScript\nshort EnabledState\nBegin GameMode\nEnd",
                    SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
                    SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
                    CompiledData = [0x00, 0x1D, 0x00, 0x00]
                }
            ],
            Packages = [NewScriptVariablePackage(0x01000400, owner, 2)]
        };

        var result = QuestVariableConditionSanitizer.Apply(
            collection,
            new Dictionary<uint, ParsedMainRecord>(),
            null);

        Assert.Single(collection.Packages);
        Assert.Equal(1, result.RetainedGetScriptVariableCount);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Apply_suppresses_get_script_variable_when_captured_and_master_owner_bases_differ()
    {
        const uint owner = 0x00050000;
        const uint capturedNpc = 0x00151000;
        const uint masterNpc = 0x00051000;
        var collection = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    PlacedObjects =
                    [
                        new PlacedReference
                        {
                            FormId = owner,
                            RecordType = "ACHR",
                            BaseFormId = capturedNpc
                        }
                    ]
                }
            ],
            Packages = [NewScriptVariablePackage(0x01000500, owner, 2)]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [owner] = MasterRecord("ACHR", owner, FormIdSubrecord("NAME", masterNpc))
        };

        var result = QuestVariableConditionSanitizer.Apply(collection, masterRecords, null);

        Assert.Empty(collection.Packages);
        Assert.Equal(1, result.SuppressedPackageCount);
        Assert.Equal("script-variable.owner-base-ambiguous", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Apply_resolves_get_script_variable_owner_base_and_script_aliases()
    {
        const uint sourceOwner = 0x00160000;
        const uint sourceNpc = 0x00161000;
        const uint sourceScript = 0x00162000;
        const uint targetOwner = 0x00060000;
        const uint targetNpc = 0x00061000;
        const uint targetScript = 0x00062000;
        var collection = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    PlacedObjects =
                    [
                        new PlacedReference
                        {
                            FormId = sourceOwner,
                            RecordType = "ACHR",
                            BaseFormId = sourceNpc
                        }
                    ]
                }
            ],
            Npcs = [new NpcRecord { FormId = sourceNpc, Script = sourceScript }],
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = sourceScript,
                    Variables = [new ScriptVariableInfo(3, "AliasedState", 1)],
                    SourceText = "scn PrototypeScript\nshort AliasedState\nBegin GameMode\nEnd",
                    SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
                    SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
                    CompiledData = [0x00, 0x1D, 0x00, 0x00]
                }
            ],
            Packages = [NewScriptVariablePackage(0x01000600, sourceOwner, 3)]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [targetOwner] = MasterRecord("ACHR", targetOwner, FormIdSubrecord("NAME", targetNpc)),
            [targetNpc] = MasterRecord("NPC_", targetNpc, FormIdSubrecord("SCRI", targetScript)),
            [targetScript] = MasterScript(targetScript, [new ScriptVariableInfo(8, "AliasedState", 1)])
        };
        var aliases = new Dictionary<uint, uint>
        {
            [sourceOwner] = targetOwner,
            [sourceNpc] = targetNpc,
            [sourceScript] = targetScript
        };

        var result = QuestVariableConditionSanitizer.Apply(collection, masterRecords, aliases);

        Assert.Equal(8u, Assert.Single(Assert.Single(collection.Packages).Conditions).Parameter2);
        Assert.Equal(1, result.RemappedConditionCount);
        Assert.Equal("script-variable.remapped", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Apply_get_script_variable_prefers_raw_dmp_table_over_resolved_runtime_alias()
    {
        const uint sourceOwner = 0x00160010;
        const uint sourceNpc = 0x00161010;
        const uint sourceScript = 0x00162010;
        const uint targetOwner = 0x00060010;
        const uint targetNpc = 0x00061010;
        const uint targetScript = 0x00062010;
        var collection = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    PlacedObjects =
                    [
                        new PlacedReference
                        {
                            FormId = sourceOwner,
                            RecordType = "ACHR",
                            BaseFormId = sourceNpc
                        }
                    ]
                }
            ],
            Npcs = [new NpcRecord { FormId = sourceNpc, Script = sourceScript }],
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = sourceScript,
                    Variables = [new ScriptVariableInfo(3, "PrototypeState", 1)],
                    SourceText = "scn PrototypeScript\nshort PrototypeState\nBegin GameMode\nEnd",
                    SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
                    SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
                    CompiledData = [0x00, 0x1D, 0x00, 0x00]
                }
            ],
            RuntimeScripts =
            [
                new RuntimeScriptData
                {
                    FormId = targetScript,
                    HeaderVariableCount = 1,
                    VariableCount = 1,
                    LastVariableId = 3,
                    Variables = [new ScriptVariableInfo(3, "RetailState", 1)],
                    VariableMetadataComplete = true,
                    VariablesComplete = true
                }
            ],
            Packages = [NewScriptVariablePackage(0x01000610, sourceOwner, 3)]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [targetOwner] = MasterRecord("ACHR", targetOwner, FormIdSubrecord("NAME", targetNpc)),
            [targetNpc] = MasterRecord("NPC_", targetNpc, FormIdSubrecord("SCRI", targetScript)),
            [targetScript] = MasterScript(
                targetScript,
                [
                    new ScriptVariableInfo(3, "RetailState", 1),
                    new ScriptVariableInfo(8, "PrototypeState", 1)
                ])
        };
        var aliases = new Dictionary<uint, uint>
        {
            [sourceOwner] = targetOwner,
            [sourceNpc] = targetNpc,
            [sourceScript] = targetScript
        };

        var result = QuestVariableConditionSanitizer.Apply(collection, masterRecords, aliases);

        Assert.Equal(8u, Assert.Single(Assert.Single(collection.Packages).Conditions).Parameter2);
        Assert.Equal(1, result.RemappedConditionCount);
        Assert.Equal("script-variable.remapped", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Apply_suppresses_get_script_variable_when_only_name_matches_but_type_differs()
    {
        const uint owner = 0x00070000;
        const uint npc = 0x00071000;
        const uint sourceScript = 0x00172000;
        const uint targetScript = 0x00072000;
        var collection = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    PlacedObjects =
                        [new PlacedReference { FormId = owner, RecordType = "ACHR", BaseFormId = npc }]
                }
            ],
            Npcs = [new NpcRecord { FormId = npc, Script = sourceScript }],
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = sourceScript,
                    Variables = [new ScriptVariableInfo(3, "SameName", 1)],
                    SourceText = "scn PrototypeScript\nshort SameName\nBegin GameMode\nEnd",
                    SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
                    SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
                    CompiledData = [0x00, 0x1D, 0x00, 0x00]
                }
            ],
            Packages = [NewScriptVariablePackage(0x01000700, owner, 3)]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [owner] = MasterRecord("ACHR", owner, FormIdSubrecord("NAME", npc)),
            [npc] = MasterRecord("NPC_", npc, FormIdSubrecord("SCRI", targetScript)),
            [targetScript] = MasterScript(targetScript, [new ScriptVariableInfo(8, "SameName", 0)])
        };

        var result = QuestVariableConditionSanitizer.Apply(collection, masterRecords, null);

        Assert.Empty(collection.Packages);
        Assert.Equal("script-variable.exact-target-missing", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Apply_suppresses_get_script_variable_when_float_and_ref_share_the_slsd_bit()
    {
        const uint owner = 0x00070010;
        const uint npc = 0x00071010;
        const uint sourceScript = 0x00172010;
        const uint targetScript = 0x00072010;
        var collection = new RecordCollection
        {
            Cells =
            [
                new CellRecord
                {
                    PlacedObjects =
                        [new PlacedReference { FormId = owner, RecordType = "ACHR", BaseFormId = npc }]
                }
            ],
            Npcs = [new NpcRecord { FormId = npc, Script = sourceScript }],
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = sourceScript,
                    Variables = [new ScriptVariableInfo(3, "SharedValue", 0)],
                    SourceText = "scn PrototypeActorScript\nref SharedValue\nBegin GameMode\nEnd",
                    SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
                    SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
                    CompiledData = [0x00, 0x1D, 0x00, 0x00]
                }
            ],
            Packages = [NewScriptVariablePackage(0x01000710, owner, 3)]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [owner] = MasterRecord("ACHR", owner, FormIdSubrecord("NAME", npc)),
            [npc] = MasterRecord("NPC_", npc, FormIdSubrecord("SCRI", targetScript)),
            [targetScript] = MasterScript(
                targetScript,
                [new ScriptVariableInfo(8, "SharedValue", 0)],
                "scn RetailActorScript\nfloat SharedValue\nBegin GameMode\nEnd")
        };

        var result = QuestVariableConditionSanitizer.Apply(collection, masterRecords, null);

        Assert.Empty(collection.Packages);
        Assert.Equal("script-variable.exact-target-missing", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Apply_suppresses_when_name_match_is_ambiguous()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(4, "DuplicateName", 1)],
            [
                new ScriptVariableInfo(7, "DuplicateName", 1),
                new ScriptVariableInfo(8, "DuplicateName", 1)
            ],
            NewDialogue(0x01000009, QuestFormId, 4),
            masterSourceText: "scn RetailScript\nshort DuplicateName\nBegin GameMode\nEnd");

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Empty(records.Collection.Dialogues);
        Assert.Equal(1, result.SuppressedInfoCount);
        Assert.Equal(0, result.RemappedConditionCount);
    }

    [Fact]
    public void Apply_suppresses_when_target_table_has_duplicate_numeric_ids()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(7, "PrototypeState", 1)],
            [
                new ScriptVariableInfo(7, "PrototypeState", 1),
                new ScriptVariableInfo(7, "ConflictingRetailState", 1)
            ],
            NewDialogue(0x01000019, QuestFormId, 7));

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Empty(records.Collection.Dialogues);
        Assert.Equal(1, result.SuppressedInfoCount);
        Assert.Empty(result.ScriptVariableAugmentations);
    }

    [Fact]
    public void Apply_reuses_exact_case_insensitive_augmentation_and_allocates_in_stable_name_order()
    {
        var records = BuildRecords(
            [
                new ScriptVariableInfo(9, "zPrototypeState", 1),
                new ScriptVariableInfo(10, "ZPROTOTYPESTATE", 1),
                new ScriptVariableInfo(11, "aPrototypeState", 0)
            ],
            [new ScriptVariableInfo(40, "RetailMaximum", 1)],
            sourceText:
            "scn PrototypeScript\nshort zPrototypeState\nfloat aPrototypeState\nBegin GameMode\nEnd");
        records.Collection.Dialogues.AddRange(
        [
            NewDialogue(0x01000020, QuestFormId, 10),
            NewDialogue(0x01000021, QuestFormId, 11),
            NewDialogue(0x01000022, QuestFormId, 9)
        ]);

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Equal(42u, Assert.Single(records.Collection.Dialogues[0].Conditions).Parameter2);
        Assert.Equal(41u, Assert.Single(records.Collection.Dialogues[1].Conditions).Parameter2);
        Assert.Equal(42u, Assert.Single(records.Collection.Dialogues[2].Conditions).Parameter2);
        Assert.Equal(3, result.RemappedConditionCount);
        Assert.Collection(
            result.ScriptVariableAugmentations,
            augmentation => Assert.Equal(
                new ScriptVariableInfo(41, "aPrototypeState", 0),
                augmentation.Variable),
            augmentation => Assert.Equal(
                new ScriptVariableInfo(42, "ZPROTOTYPESTATE", 1),
                augmentation.Variable));
    }

    [Fact]
    public void Apply_does_not_semantically_match_or_reuse_different_variable_type()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(7, "PrototypeEnabled", 1)],
            [
                new ScriptVariableInfo(8, "bPrototypeEnabled", 1),
                new ScriptVariableInfo(9, "PrototypeEnabled", 0)
            ],
            NewDialogue(0x01000023, QuestFormId, 7));

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Equal(10u, Assert.Single(Assert.Single(records.Collection.Dialogues).Conditions).Parameter2);
        Assert.Equal(
            new ScriptVariableInfo(10, "PrototypeEnabled_DmpRecovered", 1),
            Assert.Single(result.ScriptVariableAugmentations).Variable);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("PrototypeEnabled", diagnostic.Metadata["condition-variable-name"]);
        Assert.Equal(
            "PrototypeEnabled_DmpRecovered",
            diagnostic.Metadata["condition-augmented-variable-name"]);
    }

    [Fact]
    public void Apply_distinguishes_float_and_reference_using_each_scripts_exact_sctx()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(7, "SharedValue", 0)],
            [new ScriptVariableInfo(8, "SharedValue", 0)],
            NewDialogue(0x01000026, QuestFormId, 7),
            "scn PrototypeScript\nref SharedValue\nBegin GameMode\nEnd",
            "scn RetailScript\nfloat SharedValue\nBegin GameMode\nEnd");

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Equal(9u, Assert.Single(Assert.Single(records.Collection.Dialogues).Conditions).Parameter2);
        var augmentation = Assert.Single(result.ScriptVariableAugmentations);
        Assert.Equal(
            new ScriptVariableInfo(9, "SharedValue_DmpRecovered", 0),
            augmentation.Variable);
        Assert.Equal(ScriptVariableDeclarationKind.Reference, augmentation.DeclarationKind);
    }

    [Fact]
    public void Apply_does_not_collapse_distinct_integer_source_keywords()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(7, "SharedState", 1)],
            [new ScriptVariableInfo(8, "SharedState", 1)],
            NewDialogue(0x0100002A, QuestFormId, 7),
            "scn PrototypeScript\nshort SharedState\nBegin GameMode\nEnd",
            "scn RetailScript\nint SharedState\nBegin GameMode\nEnd");

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Equal(9u, Assert.Single(Assert.Single(records.Collection.Dialogues).Conditions).Parameter2);
        var augmentation = Assert.Single(result.ScriptVariableAugmentations);
        Assert.Equal(
            new ScriptVariableInfo(9, "SharedState_DmpRecovered", 1),
            augmentation.Variable);
        Assert.Equal(ScriptVariableDeclarationKind.Short, augmentation.DeclarationKind);
    }

    [Fact]
    public void Apply_allocates_storage_only_integer_without_reusing_or_inventing_keyword()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(7, "UnknownInteger", 1)],
            [],
            NewDialogue(0x0100002B, QuestFormId, 7),
            "scn PrototypeScript\nBegin GameMode\nEnd",
            "scn RetailScript\nBegin GameMode\nEnd");

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Equal(1u, Assert.Single(Assert.Single(records.Collection.Dialogues).Conditions).Parameter2);
        Assert.Equal(0, result.SuppressedInfoCount);
        var augmentation = Assert.Single(result.ScriptVariableAugmentations);
        Assert.Equal(new ScriptVariableInfo(1, "UnknownInteger", 1), augmentation.Variable);
        Assert.Equal(ScriptVariableDeclarationKind.Integer, augmentation.DeclarationKind);
        Assert.Single(result.VariableRecoveryMappings);
    }

    [Fact]
    public void Apply_retains_exact_same_serialized_master_slot_without_sctx_keyword()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(7, "OpaqueSameSlot", 1)],
            [new ScriptVariableInfo(7, "opaquesameslot", 1)],
            NewDialogue(0x0100002F, QuestFormId, 7),
            "scn PrototypeScript\nBegin GameMode\nEnd",
            "scn RetailScript\nBegin GameMode\nEnd");

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Equal(7u, Assert.Single(Assert.Single(records.Collection.Dialogues).Conditions).Parameter2);
        Assert.Equal(0, result.SuppressedInfoCount);
        Assert.Empty(result.ScriptVariableAugmentations);
        Assert.Empty(result.VariableRecoveryMappings);
    }

    [Fact]
    public void Apply_does_not_reuse_target_integer_when_its_exact_keyword_is_unavailable()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(7, "SharedState", 1)],
            [new ScriptVariableInfo(8, "SharedState", 1)],
            NewDialogue(0x0100002C, QuestFormId, 7),
            "scn PrototypeScript\nshort SharedState\nBegin GameMode\nEnd",
            "scn RetailScript\nBegin GameMode\nEnd");

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Equal(9u, Assert.Single(Assert.Single(records.Collection.Dialogues).Conditions).Parameter2);
        var augmentation = Assert.Single(result.ScriptVariableAugmentations);
        Assert.Equal(
            new ScriptVariableInfo(9, "SharedState_DmpRecovered", 1),
            augmentation.Variable);
        Assert.Equal(ScriptVariableDeclarationKind.Short, augmentation.DeclarationKind);
    }

    [Fact]
    public void Apply_suppresses_same_formid_raw_scripts_with_conflicting_declaration_identity()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(7, "SharedState", 1)],
            [new ScriptVariableInfo(12, "SharedState", 1)],
            NewDialogue(0x0100002D, QuestFormId, 7),
            "scn PrototypeScript\nshort SharedState\nBegin GameMode\nEnd",
            "scn RetailScript\nshort SharedState\nBegin GameMode\nEnd");
        records.Collection.Scripts.Add(new ScriptRecord
        {
            FormId = ScriptFormId,
            Variables = [new ScriptVariableInfo(7, "SharedState", 1)],
            SourceText = "scn PrototypeScriptCopy\nlong SharedState\nBegin GameMode\nEnd",
            SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
            SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
            CompiledData = [0x00, 0x1D, 0x00, 0x00]
        });

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Empty(records.Collection.Dialogues);
        Assert.Equal(1, result.SuppressedInfoCount);
        Assert.Empty(result.ScriptVariableAugmentations);
        Assert.Empty(result.VariableRecoveryMappings);
    }

    [Fact]
    public void Apply_accepts_same_formid_raw_scripts_when_full_declaration_identity_agrees()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(7, "SharedState", 1)],
            [new ScriptVariableInfo(12, "SharedState", 1)],
            NewDialogue(0x0100002E, QuestFormId, 7),
            "scn PrototypeScript\nshort SharedState\nBegin GameMode\nEnd",
            "scn RetailScript\nshort SharedState\nBegin GameMode\nEnd");
        records.Collection.Scripts.Add(new ScriptRecord
        {
            FormId = ScriptFormId,
            Variables = [new ScriptVariableInfo(7, "sharedstate", 1)],
            SourceText = "scn PrototypeScriptCopy\nshort sharedstate\nBegin MenuMode\nEnd",
            SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
            SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
            CompiledData = [0x00, 0x1D, 0x00, 0x00]
        });

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Equal(12u, Assert.Single(Assert.Single(records.Collection.Dialogues).Conditions).Parameter2);
        Assert.Equal(1, result.RemappedConditionCount);
        Assert.Empty(result.ScriptVariableAugmentations);
    }

    [Fact]
    public void Apply_remaps_type_zero_only_when_float_declarations_match_exactly()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(7, "ExactFloat", 0)],
            [new ScriptVariableInfo(12, "ExactFloat", 0)],
            NewDialogue(0x01000027, QuestFormId, 7),
            "scn PrototypeScript\nfloat ExactFloat\nBegin GameMode\nEnd",
            "scn RetailScript\nfloat ExactFloat\nBegin GameMode\nEnd");

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Equal(12u, Assert.Single(Assert.Single(records.Collection.Dialogues).Conditions).Parameter2);
        Assert.Empty(result.ScriptVariableAugmentations);
        Assert.Equal(1, result.RemappedConditionCount);
    }

    [Fact]
    public void Apply_allocates_storage_only_type_zero_without_guessing_float_or_reference()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(7, "UnknownKind", 0)],
            [],
            NewDialogue(0x01000028, QuestFormId, 7));

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Equal(1u, Assert.Single(Assert.Single(records.Collection.Dialogues).Conditions).Parameter2);
        Assert.Equal(0, result.SuppressedInfoCount);
        var augmentation = Assert.Single(result.ScriptVariableAugmentations);
        Assert.Equal(new ScriptVariableInfo(1, "UnknownKind", 0), augmentation.Variable);
        Assert.Equal(ScriptVariableDeclarationKind.FloatOrReference, augmentation.DeclarationKind);
    }

    [Fact]
    public void Apply_new_dmp_script_keeps_exact_same_table_index_without_sctx()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(7, "CapturedOnly", 1)],
            [],
            NewDialogue(0x01000029, QuestFormId, 7),
            "scn PrototypeScript\nBegin GameMode\nEnd");
        records.MasterRecords.Clear();

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Equal(7u, Assert.Single(Assert.Single(records.Collection.Dialogues).Conditions).Parameter2);
        Assert.Equal(0, result.SuppressedInfoCount);
        Assert.Empty(result.ScriptVariableAugmentations);
        Assert.Empty(result.VariableRecoveryMappings);
    }

    [Fact]
    public void Apply_requires_source_metadata_even_when_target_has_the_same_numeric_id()
    {
        var records = BuildRecords(
            [],
            [new ScriptVariableInfo(12, "RetailState", 1)],
            NewDialogue(0x01000024, QuestFormId, 12));

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Empty(records.Collection.Dialogues);
        Assert.Equal(1, result.SuppressedInfoCount);
        Assert.Empty(result.ScriptVariableAugmentations);
    }

    [Fact]
    public void Apply_rejects_augmentation_when_uint16_scda_variable_range_is_exhausted()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(7, "PrototypeState", 1)],
            [new ScriptVariableInfo(ushort.MaxValue, "RetailMaximum", 1)],
            NewDialogue(0x01000025, QuestFormId, 7));

        var error = Assert.Throws<InvalidDataException>(() =>
            QuestVariableConditionSanitizer.Apply(
                records.Collection,
                records.MasterRecords,
                null));

        Assert.Contains("UInt16 SCDA variable-ID range is exhausted", error.Message);
    }

    [Fact]
    public void Apply_stale_runtime_source_cannot_reuse_retail_but_can_allocate_storage_only_local()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(27, "StaleState", 1)],
            [new ScriptVariableInfo(31, "StaleState", 1)],
            NewDialogue(0x0100003E, QuestFormId, 27));
        records.Collection.Scripts[0] = records.Collection.Scripts[0] with
        {
            // Standalone SCTX/SCDA correspondence rejected this captured source.
            SourceText = null,
            SourceTextOrigin = ScriptSourceTextOrigin.None
        };
        records.Collection.RuntimeScripts.Add(new RuntimeScriptData
        {
            FormId = ScriptFormId,
            Variables = [new ScriptVariableInfo(27, "StaleState", 1)],
            VariableMetadataComplete = true,
            VariablesComplete = true,
            SourceText = "scn RejectedRuntimeScript\nshort StaleState\nBegin GameMode\nEnd",
            SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Rejected
        });

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Equal(32u, Assert.Single(Assert.Single(records.Collection.Dialogues).Conditions).Parameter2);
        Assert.Equal(0, result.SuppressedInfoCount);
        var augmentation = Assert.Single(result.ScriptVariableAugmentations);
        Assert.Equal(new ScriptVariableInfo(32, "StaleState_DmpRecovered", 1), augmentation.Variable);
        Assert.Equal(ScriptVariableDeclarationKind.Integer, augmentation.DeclarationKind);
        Assert.Single(result.VariableRecoveryMappings);
    }

    [Fact]
    public void Apply_source_only_text_does_not_prove_keyword_but_table_can_back_storage_only_local()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(27, "SourceOnlyState", 1)],
            [],
            NewDialogue(0x0100003D, QuestFormId, 27));
        records.Collection.Scripts[0] = records.Collection.Scripts[0] with
        {
            CompiledData = null,
            SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.AcceptedSourceOnly
        };

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Equal(1u, Assert.Single(Assert.Single(records.Collection.Dialogues).Conditions).Parameter2);
        var augmentation = Assert.Single(result.ScriptVariableAugmentations);
        Assert.Equal(new ScriptVariableInfo(1, "SourceOnlyState", 1), augmentation.Variable);
        Assert.Equal(ScriptVariableDeclarationKind.Integer, augmentation.DeclarationKind);
    }

    [Fact]
    public void Apply_rejected_runtime_source_does_not_prove_keyword_but_table_can_back_storage_only_local()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(27, "StalePrototypeOnly", 1)],
            [],
            NewDialogue(0x0100003F, QuestFormId, 27));
        records.Collection.Scripts[0] = records.Collection.Scripts[0] with
        {
            // RuntimeScripts retains raw diagnostic text after the standalone gate clears it.
            SourceText = null,
            SourceTextOrigin = ScriptSourceTextOrigin.None
        };
        records.Collection.RuntimeScripts.Add(new RuntimeScriptData
        {
            FormId = ScriptFormId,
            Variables = [new ScriptVariableInfo(27, "StalePrototypeOnly", 1)],
            VariableMetadataComplete = true,
            VariablesComplete = true,
            SourceText =
                "scn RejectedRuntimeScript\nshort StalePrototypeOnly\nBegin GameMode\nEnd",
            SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Rejected
        });

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Equal(1u, Assert.Single(Assert.Single(records.Collection.Dialogues).Conditions).Parameter2);
        var augmentation = Assert.Single(result.ScriptVariableAugmentations);
        Assert.Equal(new ScriptVariableInfo(1, "StalePrototypeOnly", 1), augmentation.Variable);
        Assert.Equal(ScriptVariableDeclarationKind.Integer, augmentation.DeclarationKind);
        Assert.Single(result.VariableRecoveryMappings);
    }

    [Fact]
    public void Apply_uses_accepted_complete_same_dump_runtime_script_for_exact_source_id_and_name()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(27, "RuntimeName", 1)],
            [new ScriptVariableInfo(31, "RuntimeName", 1)],
            NewDialogue(0x01000040, QuestFormId, 27),
            "scn PrototypeScript\nshort RuntimeName\nBegin GameMode\nEnd");
        records.Collection.RuntimeScripts.Add(new RuntimeScriptData
        {
            FormId = ScriptFormId,
            Variables = [new ScriptVariableInfo(27, "RuntimeName", 1)],
            VariableMetadataComplete = true,
            VariablesComplete = true,
            SourceText = "scn PrototypeScript\nshort RuntimeName\nBegin GameMode\nEnd",
            SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted
        });

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        var condition = Assert.Single(Assert.Single(records.Collection.Dialogues).Conditions);
        Assert.Equal(31u, condition.Parameter2);
        Assert.Equal(1, result.RemappedConditionCount);
        Assert.Equal(
            new ScriptVariableInfo(27, "RuntimeName", 1),
            Assert.Single(result.VariableRecoveryMappings).SourceVariable);
    }

    [Fact]
    public void Apply_uses_accepted_runtime_source_for_fresh_augmentation()
    {
        const string acceptedSource =
            "scn PrototypeScript\nshort AcceptedPrototypeOnly\nBegin GameMode\nEnd";
        var records = BuildRecords(
            [new ScriptVariableInfo(27, "AcceptedPrototypeOnly", 1)],
            [],
            NewDialogue(0x01000045, QuestFormId, 27),
            acceptedSource);
        records.Collection.RuntimeScripts.Add(new RuntimeScriptData
        {
            FormId = ScriptFormId,
            Variables = [new ScriptVariableInfo(27, "AcceptedPrototypeOnly", 1)],
            VariableMetadataComplete = true,
            VariablesComplete = true,
            SourceText = acceptedSource,
            SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted
        });

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Equal(
            1u,
            Assert.Single(Assert.Single(records.Collection.Dialogues).Conditions).Parameter2);
        var augmentation = Assert.Single(result.ScriptVariableAugmentations);
        Assert.Equal(
            new ScriptVariableInfo(1, "AcceptedPrototypeOnly", 1),
            augmentation.Variable);
        Assert.Equal(ScriptVariableDeclarationKind.Short, augmentation.DeclarationKind);
    }

    [Fact]
    public void Apply_resolves_sparse_runtime_variables_by_explicit_id_through_script_alias()
    {
        const uint sourceRuntimeScript = 0x00102000;
        var records = BuildRecords(
            [],
            [new ScriptVariableInfo(12, "SparseState", 1)],
            NewDialogue(0x01000041, QuestFormId, 7));
        records.Collection.Quests[0] = records.Collection.Quests[0] with { Script = sourceRuntimeScript };
        var runtimeVariables = new List<ScriptVariableInfo>
        {
            new(91, "LastInList", 1),
            new(7, "SparseState", 1),
            new(3, "FirstById", 1)
        };
        const string runtimeSource =
            "scn PrototypeScript\nshort LastInList\nshort SparseState\nshort FirstById\nBegin GameMode\nEnd";
        records.Collection.Scripts.Add(new ScriptRecord
        {
            FormId = sourceRuntimeScript,
            Variables = runtimeVariables,
            SourceText = runtimeSource,
            SourceTextOrigin = ScriptSourceTextOrigin.RuntimeSameObject,
            SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
            CompiledData = [0x00, 0x1D, 0x00, 0x00]
        });
        records.Collection.RuntimeScripts.Add(new RuntimeScriptData
        {
            FormId = sourceRuntimeScript,
            Variables = runtimeVariables,
            VariableMetadataComplete = true,
            VariablesComplete = true,
            SourceText = runtimeSource,
            SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted
        });
        var aliases = new Dictionary<uint, uint>
        {
            [sourceRuntimeScript] = ScriptFormId
        };

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            aliases);

        Assert.Equal(
            12u,
            Assert.Single(Assert.Single(records.Collection.Dialogues).Conditions).Parameter2);
        Assert.Equal(1, result.RemappedConditionCount);
    }

    [Fact]
    public void Apply_fails_closed_instead_of_falling_back_from_incomplete_runtime_metadata()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(7, "PlausibleFragmentName", 1)],
            [new ScriptVariableInfo(12, "PlausibleFragmentName", 1)],
            NewDialogue(0x01000042, QuestFormId, 7));
        records.Collection.RuntimeScripts.Add(new RuntimeScriptData
        {
            FormId = ScriptFormId,
            Variables = [new ScriptVariableInfo(7, "PlausibleFragmentName", 1)],
            VariableMetadataComplete = false
        });

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Empty(records.Collection.Dialogues);
        Assert.Equal(1, result.SuppressedInfoCount);
        Assert.Equal(1, result.InvalidConditionCount);
    }

    [Fact]
    public void Apply_fails_closed_for_conflicting_same_dump_runtime_script_metadata()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(7, "FirstRuntimeName", 1)],
            [new ScriptVariableInfo(12, "FirstRuntimeName", 1)],
            NewDialogue(0x01000043, QuestFormId, 7));
        records.Collection.RuntimeScripts.AddRange(
        [
            new RuntimeScriptData
            {
                FormId = ScriptFormId,
                Variables = [new ScriptVariableInfo(7, "FirstRuntimeName", 1)],
                VariableMetadataComplete = true,
                VariablesComplete = true
            },
            new RuntimeScriptData
            {
                FormId = ScriptFormId,
                Variables = [new ScriptVariableInfo(7, "ConflictingRuntimeName", 1)],
                VariableMetadataComplete = true,
                VariablesComplete = true
            }
        ]);

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Empty(records.Collection.Dialogues);
        Assert.Equal(1, result.SuppressedInfoCount);
        Assert.Equal(1, result.InvalidConditionCount);
    }

    [Fact]
    public void Apply_fails_closed_for_same_formid_runtime_scda_conflict()
    {
        const string source = "scn PrototypeScript\nshort ExactState\nBegin GameMode\nEnd";
        var records = BuildRecords(
            [new ScriptVariableInfo(7, "ExactState", 1)],
            [new ScriptVariableInfo(12, "ExactState", 1)],
            NewDialogue(0x01000046, QuestFormId, 7),
            source);
        var first = new RuntimeScriptData
        {
            FormId = ScriptFormId,
            DataSize = 4,
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            Variables = [new ScriptVariableInfo(7, "ExactState", 1)],
            VariableMetadataComplete = true,
            VariablesComplete = true,
            SourceText = source,
            SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted
        };
        records.Collection.RuntimeScripts.AddRange(
        [
            first,
            first with { CompiledData = [0x00, 0x1C, 0x00, 0x00] }
        ]);

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Empty(records.Collection.Dialogues);
        Assert.Equal(1, result.SuppressedInfoCount);
        Assert.Equal(1, result.InvalidConditionCount);
        Assert.Empty(result.VariableRecoveryMappings);
    }

    [Fact]
    public void Apply_uses_runtime_scripts_exact_sctx_to_prove_type_zero_declaration()
    {
        var records = BuildRecords(
            [new ScriptVariableInfo(7, "ExactFloat", 0)],
            [new ScriptVariableInfo(12, "ExactFloat", 0)],
            NewDialogue(0x01000044, QuestFormId, 7),
            "scn PrototypeScript\nfloat ExactFloat\nBegin GameMode\nEnd",
            "scn RetailScript\nfloat ExactFloat\nBegin GameMode\nEnd");
        records.Collection.RuntimeScripts.Add(new RuntimeScriptData
        {
            FormId = ScriptFormId,
            Variables = [new ScriptVariableInfo(7, "ExactFloat", 0)],
            VariableMetadataComplete = true,
            VariablesComplete = true,
            SourceText = "scn PrototypeScript\nfloat ExactFloat\nBegin GameMode\nEnd",
            SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted
        });

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Equal(
            12u,
            Assert.Single(Assert.Single(records.Collection.Dialogues).Conditions).Parameter2);
        Assert.Equal(1, result.RemappedConditionCount);
        Assert.Empty(result.ScriptVariableAugmentations);
    }

    private static TestRecords BuildRecords(
        IReadOnlyList<ScriptVariableInfo> sourceVariables,
        IReadOnlyList<ScriptVariableInfo> masterVariables,
        DialogueRecord? dialogue = null,
        string? sourceText = null,
        string? masterSourceText = null)
    {
        var collection = new RecordCollection
        {
            Quests = [new QuestRecord { FormId = QuestFormId, Script = ScriptFormId }],
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = ScriptFormId,
                    Variables = [.. sourceVariables],
                    SourceText = sourceText ?? BuildFixtureSource("PrototypeScript", sourceVariables),
                    SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
                    SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
                    CompiledData = [0x00, 0x1D, 0x00, 0x00]
                }
            ],
            Dialogues = dialogue is null ? [] : [dialogue]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [QuestFormId] = MasterQuest(QuestFormId, ScriptFormId),
            [ScriptFormId] = MasterScript(ScriptFormId, masterVariables, masterSourceText)
        };
        return new TestRecords(collection, masterRecords);
    }

    private static DialogueRecord NewDialogue(uint formId, uint questFormId, uint variableIndex)
    {
        return new DialogueRecord
        {
            FormId = formId,
            Conditions = [QuestVariable(questFormId, variableIndex)]
        };
    }

    private static PackageRecord NewPackage(uint formId, uint questFormId, uint variableIndex)
    {
        return new PackageRecord
        {
            FormId = formId,
            Conditions = [QuestVariable(questFormId, variableIndex)]
        };
    }

    private static PackageRecord NewScriptVariablePackage(
        uint formId,
        uint ownerReferenceFormId,
        uint variableIndex)
    {
        return new PackageRecord
        {
            FormId = formId,
            Conditions =
            [
                new DialogueCondition
                {
                    FunctionIndex = QuestVariableConditionSanitizer.GetScriptVariableFunctionIndex,
                    Parameter1 = ownerReferenceFormId,
                    Parameter2 = variableIndex
                }
            ]
        };
    }

    private static DialogueRecord NewScriptVariableDialogue(
        uint formId,
        uint ownerReferenceFormId,
        uint variableIndex)
    {
        return new DialogueRecord
        {
            FormId = formId,
            Conditions =
            [
                new DialogueCondition
                {
                    FunctionIndex = QuestVariableConditionSanitizer.GetScriptVariableFunctionIndex,
                    Parameter1 = ownerReferenceFormId,
                    Parameter2 = variableIndex
                }
            ]
        };
    }

    private static DialogueCondition QuestVariable(uint questFormId, uint variableIndex)
    {
        return new DialogueCondition
        {
            FunctionIndex = QuestVariableConditionSanitizer.GetQuestVariableFunctionIndex,
            Parameter1 = questFormId,
            Parameter2 = variableIndex
        };
    }

    private static ParsedMainRecord MasterQuest(uint formId, uint scriptFormId)
    {
        return MasterRecord("QUST", formId, FormIdSubrecord("SCRI", scriptFormId));
    }

    private static ParsedMainRecord MasterScript(
        uint formId,
        IReadOnlyList<ScriptVariableInfo> variables,
        string? sourceText = null)
    {
        sourceText ??= BuildFixtureSource("RetailScript", variables);
        var subrecords = new List<ParsedSubrecord>();
        if (sourceText is not null)
        {
            subrecords.Add(new ParsedSubrecord
            {
                Signature = "SCTX",
                Data = Encoding.Latin1.GetBytes(sourceText + '\0')
            });
        }

        foreach (var variable in variables)
        {
            var slsd = new byte[17];
            BinaryPrimitives.WriteUInt32LittleEndian(slsd, variable.Index);
            slsd[16] = variable.Type;
            subrecords.Add(new ParsedSubrecord { Signature = "SLSD", Data = slsd });
            subrecords.Add(new ParsedSubrecord
            {
                Signature = "SCVR",
                Data = Encoding.Latin1.GetBytes((variable.Name ?? string.Empty) + '\0')
            });
        }

        return MasterRecord("SCPT", formId, [.. subrecords]);
    }

    private static string BuildFixtureSource(
        string scriptName,
        IReadOnlyList<ScriptVariableInfo> variables)
    {
        var declarations = variables
            .Where(static variable => variable.Type == 1 && !string.IsNullOrWhiteSpace(variable.Name))
            .Select(static variable => $"short {variable.Name}")
            .ToArray();
        var declarationBlock = declarations.Length == 0
            ? string.Empty
            : string.Join("\n", declarations) + "\n";
        return $"scn {scriptName}\n{declarationBlock}Begin GameMode\nEnd";
    }

    private static ParsedMainRecord MasterRecord(
        string signature,
        uint formId,
        params ParsedSubrecord[] subrecords)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader { Signature = signature, FormId = formId },
            Subrecords = [.. subrecords]
        };
    }

    private static ParsedSubrecord FormIdSubrecord(string signature, uint formId)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, formId);
        return new ParsedSubrecord { Signature = signature, Data = data };
    }

    private sealed record TestRecords(
        RecordCollection Collection,
        Dictionary<uint, ParsedMainRecord> MasterRecords);
}