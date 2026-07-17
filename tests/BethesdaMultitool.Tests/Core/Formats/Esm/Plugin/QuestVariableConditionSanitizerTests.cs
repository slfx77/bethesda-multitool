using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public class QuestVariableConditionSanitizerTests
{
    private const uint QuestFormId = 0x00001000;
    private const uint ScriptFormId = 0x00002000;

    [Fact]
    public void Apply_keeps_new_info_when_variable_name_and_id_match_retained_master_script()
    {
        var records = BuildRecords(
            sourceVariables: [new ScriptVariableInfo(12, "HastingsBribed", 1)],
            masterVariables: [new ScriptVariableInfo(12, "HastingsBribed", 1)],
            dialogue: NewDialogue(0x01000001, QuestFormId, 12));

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
            sourceVariables: [new ScriptVariableInfo(27, "TroopersDead", 1)],
            masterVariables:
            [
                new ScriptVariableInfo(27, "DifferentRetailVariable", 1),
                new ScriptVariableInfo(31, "TroopersDead", 1)
            ],
            dialogue: NewDialogue(0x01000002, QuestFormId, 27));

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        var condition = Assert.Single(Assert.Single(records.Collection.Dialogues).Conditions);
        Assert.Equal(31u, condition.Parameter2);
        Assert.Equal(1, result.RemappedConditionCount);
        Assert.Contains(result.Diagnostics, d => d.Code == "quest-variable.remapped");
    }

    [Fact]
    public void Apply_suppresses_entire_new_info_when_variable_is_absent()
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
            sourceVariables: [new ScriptVariableInfo(9, "bRoseComplete", 1)],
            masterVariables: [new ScriptVariableInfo(4, "RetailState", 1)],
            dialogue: dialogue);

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Empty(records.Collection.Dialogues);
        Assert.Equal(1, result.SuppressedInfoCount);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.True(diagnostic.RecordSuppressed);
        Assert.Equal("bRoseComplete", diagnostic.VariableName);
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
        Assert.Equal("bRoseComplete", diagnostic.Metadata["condition-variable-name"]);
        Assert.Equal("0x00002000", diagnostic.Metadata["condition-target-script-form-id"]);
    }

    [Fact]
    public void Apply_suppresses_entire_new_package_instead_of_widening_it()
    {
        var records = BuildRecords(
            sourceVariables: [new ScriptVariableInfo(25, "bUlyssesFired", 1)],
            masterVariables: [new ScriptVariableInfo(7, "ArcadeHired", 1)]);
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

        Assert.Empty(records.Collection.Packages);
        Assert.Equal(1, result.SuppressedPackageCount);
        Assert.Equal(1, result.InvalidConditionCount);
    }

    [Fact]
    public void Apply_keeps_master_anchored_info_untouched()
    {
        const uint sharedInfo = 0x00003000;
        var records = BuildRecords(
            sourceVariables: [new ScriptVariableInfo(9, "PrototypeOnly", 1)],
            masterVariables: [],
            dialogue: NewDialogue(sharedInfo, QuestFormId, 9));
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
                    Variables = [new ScriptVariableInfo(6, "SiriMentionedBrahmin", 1)]
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
    public void Apply_uses_retained_master_scri_when_master_quest_model_points_to_new_dmp_script()
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
                    Variables = [new ScriptVariableInfo(9, "PrototypeOnly", 1)]
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

        Assert.Empty(collection.Dialogues);
        Assert.Equal(1, result.SuppressedInfoCount);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ScriptFormId, diagnostic.TargetScriptFormId);
        Assert.Equal("PrototypeOnly", diagnostic.VariableName);
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
                    Variables = [new ScriptVariableInfo(3, "PrototypeState", 1)]
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
    public void Apply_retains_get_script_variable_package_with_explicit_diagnostic()
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

        Assert.Single(collection.Packages);
        Assert.Equal(1, result.RetainedGetScriptVariableCount);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("script-variable.owner-unresolved", diagnostic.Code);
        Assert.False(diagnostic.RecordSuppressed);
    }

    [Fact]
    public void Apply_suppresses_when_name_match_is_ambiguous()
    {
        var records = BuildRecords(
            sourceVariables: [new ScriptVariableInfo(4, "DuplicateName", 1)],
            masterVariables:
            [
                new ScriptVariableInfo(7, "DuplicateName", 1),
                new ScriptVariableInfo(8, "DuplicateName", 1)
            ],
            dialogue: NewDialogue(0x01000009, QuestFormId, 4));

        var result = QuestVariableConditionSanitizer.Apply(
            records.Collection,
            records.MasterRecords,
            null);

        Assert.Empty(records.Collection.Dialogues);
        Assert.Equal(1, result.SuppressedInfoCount);
        Assert.Equal(0, result.RemappedConditionCount);
    }

    private static TestRecords BuildRecords(
        IReadOnlyList<ScriptVariableInfo> sourceVariables,
        IReadOnlyList<ScriptVariableInfo> masterVariables,
        DialogueRecord? dialogue = null)
    {
        var collection = new RecordCollection
        {
            Quests = [new QuestRecord { FormId = QuestFormId, Script = ScriptFormId }],
            Scripts =
            [
                new ScriptRecord
                {
                    FormId = ScriptFormId,
                    Variables = [.. sourceVariables]
                }
            ],
            Dialogues = dialogue is null ? [] : [dialogue]
        };
        var masterRecords = new Dictionary<uint, ParsedMainRecord>
        {
            [QuestFormId] = MasterQuest(QuestFormId, ScriptFormId),
            [ScriptFormId] = MasterScript(ScriptFormId, masterVariables)
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
        IReadOnlyList<ScriptVariableInfo> variables)
    {
        var subrecords = new List<ParsedSubrecord>();
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
