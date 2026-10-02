using BethesdaMultitool.Core.Formats.Esm.Export.Geck;
using BethesdaMultitool.Core.Formats.Esm.Export.Report;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Export;

/// <summary>
///     dialogue_report.txt and the structured INFO report. Before this, the text report carried no conditions,
///     no DATA fields and no result scripts at all, and the structured report formatted conditions with a
///     private, New-Vegas-only formatter that dropped a zero parameter, printed FormIDs without their hex and
///     never named a quest variable.
///     <para>
///         Both now take conditions from the shared <see cref="ConditionDescriber" />, so an INFO condition reads
///         exactly as it does in <c>show</c>, and the text report keeps every legacy line where it was and appends
///         the new ones after it. Expected text is written out literally.
///     </para>
/// </summary>
public sealed class DialogueReportConditionTests
{
    [Fact]
    public void BuildDialogueReport_AppendingConditionKeepsExistingCanonicalValue()
    {
        var first = new DialogueCondition { FunctionIndex = 0x002E, ComparisonValue = 1 };
        var a = new DialogueRecord { FormId = InfoFormId, Conditions = [first] };
        var b = a with { Conditions = [first, new() { FunctionIndex = 0x0023, ComparisonValue = 0 }] };
        var left = GeckDialogueWriter.BuildDialogueReport(a, Resolver, Context(a));
        var right = GeckDialogueWriter.BuildDialogueReport(b, Resolver, Context(b));
        var leftConditions = Assert.Single(left.Sections, section => section.Name == "Conditions");
        var rightConditions = Assert.Single(right.Sections, section => section.Name == "Conditions");
        Assert.Equal(leftConditions.Fields[0], rightConditions.Fields[0]);
        Assert.DoesNotContain(rightConditions.Fields, field => field.Key == "Grouping");
    }

    private const uint InfoFormId = 0x0010E001;
    private const uint TopicFormId = 0x0010F001;
    private const uint QuestFormId = 0x0010AAAA;
    private const uint SpeakerFormId = 0x0010BBBB;

    private const string EndSource = "set iCount to 1\r\n\tplayer.additem Caps001 10";
    private const string EndDecompiled = "; synthetic decompiled body\r\nset iCount to 1";

    private const string DecompiledLabel =
        "Reconstruction (SCDA)";

    private const string GetIsIdLine = "GetIsID(SyntheticSpeaker [0x0010BBBB]) == 1 [Run On: Subject] AND";

    private const string GetQuestVariableLine =
        "GetQuestVariable(SyntheticQuest [0x0010AAAA], iTimesAsked [var 11]) >= 1 [Run On: Subject]";

    private static readonly Dictionary<uint, string> EditorIds = new()
    {
        [TopicFormId] = "SyntheticTopic",
        [QuestFormId] = "SyntheticQuest",
        [SpeakerFormId] = "SyntheticSpeaker"
    };

    private static readonly FormIdResolver Resolver = new(EditorIds, []);

    [Fact]
    public void DialogueReport_AppendsConditionsAndResultScripts_LegacyLinesUnchanged()
    {
        var info = SyntheticInfo();
        var report = Lines(GeckDialogueWriter.GenerateDialogueReport([info], Resolver, Context(info)));

        // Every legacy line, in its legacy order.
        AssertInOrder(report,
            "--- Quest: SyntheticQuest (0x0010AAAA) ---",
            "FormID:         0x0010E001",
            "Editor ID:      SyntheticInfo",
            "Topic:          SyntheticTopic (0x0010F001)",
            "Quest:          SyntheticQuest (0x0010AAAA)",
            "Speaker:        SyntheticSpeaker (0x0010BBBB)",
            "Prompt:         \"Tell me about the vault.\"",
            "Flags:          Goodbye, Say Once",
            "Endianness:     Little-Endian (PC)",
            "Offset:         0x00000000",
            "Responses:",
            "  [1]",
            "    \"The vault is sealed.\"");

        // The additions follow the legacy lines.
        AssertInOrder(report,
            "    \"The vault is sealed.\"",
            "Data:           Type: Conversation (1); Next Speaker: Target (0); Flags 1: 0x05 (Goodbye, Say Once); " +
            "Flags 2: 0x31 (Say Once a Day, Low Intelligence, High Intelligence); DATA Length: 4 bytes",
            "Conditions (2):",
            $"  1: {GetIsIdLine}",
            $"  2: {GetQuestVariableLine}",
            "Grouping:       1 AND 2 (GECK convention: conditions joined by the OR flag form a group, and groups are " +
            "joined by AND; not verified against the engine)",
            "Result Scripts:",
            "  Result Script (Begin): no compiled code (the SCHR header declares 0 compiled bytes; the block has no " +
            "SCDA, SCTX, locals or references)",
            "  Result Script (End): 4 bytes of compiled code (SCDA)",
            "    Provenance:     plugin-record",
            "    Bytecode Order: Little-Endian",
            "    Source (SCTX):",
            "      set iCount to 1",
            "      \tplayer.additem Caps001 10",
            $"    {DecompiledLabel}:",
            "      ; synthetic decompiled body",
            "      set iCount to 1",
            "    Variables (1):",
            "      [1] ref iCount (scrv-local-reference; conflicts-with-integer-storage; storage type byte 1)",
            "    References (2):",
            "      1: PlayerRef (0x00000014)",
            "      2: local #1 (iCount)");

        // The empty Begin slot is one line: no provenance is claimed for code that is not there.
        var begin = report.FindIndex(line => line.StartsWith("  Result Script (Begin):", StringComparison.Ordinal));
        Assert.StartsWith("  Result Script (End):", report[begin + 1], StringComparison.Ordinal);
        Assert.DoesNotContain(report, line => line.Contains("function names", StringComparison.Ordinal));
    }

    [Fact]
    public void DialogueReport_InfoWithoutDataConditionsOrScripts_KeepsTheLegacyBlockOnly()
    {
        var info = new DialogueRecord
        {
            FormId = InfoFormId,
            EditorId = "PlainInfo",
            Responses = [new DialogueResponse { ResponseNumber = 1, Text = "Hello." }]
        };

        var report = Lines(GeckDialogueWriter.GenerateDialogueReport([info], Resolver, Context(info)));

        Assert.Contains("    \"Hello.\"", report);
        Assert.DoesNotContain(report, line =>
            line.StartsWith("Data:", StringComparison.Ordinal) ||
            line.StartsWith("Conditions", StringComparison.Ordinal) ||
            line.StartsWith("Result Scripts:", StringComparison.Ordinal));
    }

    [Fact]
    public void DialogueReport_WithoutAContext_SaysTheGameWasAssumed_AndLeavesTheVariableAnIndex()
    {
        var report = Lines(GeckDialogueWriter.GenerateDialogueReport([SyntheticInfo()], Resolver));

        Assert.Contains(
            "Conditions:     function names and DATA fields assume FalloutNewVegas (not detected from the input)",
            report);
        Assert.Contains("  2: GetQuestVariable(SyntheticQuest [0x0010AAAA], var 11) >= 1 [Run On: Subject]", report);
    }

    [Fact]
    public void DialogueReport_DumpScriptWithAnIncompleteBundle_UsesTheCaptureWording()
    {
        var info = new DialogueRecord
        {
            FormId = InfoFormId,
            EditorId = "DumpInfo",
            HasResultScript = true,
            ResultScripts =
            [
                new DialogueResultScript
                {
                    SourceText = "set iCount to 1",
                    SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
                    IsDmpDerived = true,
                    IsIncompleteExecutableBundle = true
                }
            ]
        };

        var report = Lines(GeckDialogueWriter.GenerateDialogueReport([info], Resolver, Context(info)));

        AssertInOrder(report,
            "Result Scripts:",
            "  Result Script:  no compiled code (the block holds source text but no SCDA)",
            "    Provenance:     dmp-fragment (correspondence: not-recorded)",
            "    Recovered source (dump fragment):",
            "      set iCount to 1",
            "    Bundle:         incomplete or unsafe in this capture: the SCHR/SCDA/local/reference bundle is " +
            "structurally inconsistent or failed emission safety validation; this is not a validated, runnable script");
    }

    [Fact]
    public void BuildDialogueReport_UsesSharedDescriber()
    {
        var info = SyntheticInfo();

        var report = GeckDialogueWriter.BuildDialogueReport(info, Resolver, Context(info));

        var conditions = Assert.Single(report.Sections, section => section.Name == "Conditions");
        Assert.Equal(["Condition 1", "Condition 2"], conditions.Fields.Select(field => field.Key).ToArray());
        Assert.Equal("GetIsID(SyntheticSpeaker [0x0010BBBB]) == 1 [Run On: Subject]", StringValue(conditions.Fields[0]));
        Assert.Equal(GetQuestVariableLine, StringValue(conditions.Fields[1]));
        var grouping = Assert.Single(report.Sections, section => section.Name == "Condition Grouping");
        Assert.StartsWith("1 AND 2 (GECK convention", StringValue(Assert.Single(grouping.Fields)), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDialogueReport_DeclaredZeroParameterIsPrinted()
    {
        // GetIsSex (FNV condition 0x46) declares its Sex parameter; 0 is Male and must not vanish.
        var info = new DialogueRecord
        {
            FormId = InfoFormId,
            Conditions = [new DialogueCondition { FunctionIndex = 0x0046, ComparisonValue = 1f }]
        };

        var report = GeckDialogueWriter.BuildDialogueReport(info, Resolver, Context(info));

        var conditions = Assert.Single(report.Sections, section => section.Name == "Conditions");
        Assert.Equal("GetIsSex(Male) == 1 [Run On: Subject]", StringValue(Assert.Single(conditions.Fields)));
    }

    [Fact]
    public void BuildDialogueReport_LabelsEveryDecompilationAsABethesdaMultitoolReconstruction()
    {
        var info = new DialogueRecord
        {
            FormId = InfoFormId,
            ResultScripts =
            [
                new DialogueResultScript
                {
                    SourceText = "set iCount to 1",
                    SourceTextOrigin = ScriptSourceTextOrigin.DecompiledFromBytecode,
                    IsDmpDerived = true
                },
                new DialogueResultScript { DecompiledText = "set iCount to 2" },
                new DialogueResultScript { SourceText = "set iCount to 3" }
            ]
        };

        var report = GeckDialogueWriter.BuildDialogueReport(info, Resolver);

        var scripts = Assert.Single(report.Sections, section => section.Name == "Result Scripts");
        Assert.Equal(
        [
            "Script 1 (reconstruction)",
            "Script 2 (reconstruction)",
            "Script 3"
        ], scripts.Fields.Select(field => field.Key).ToArray());
        Assert.Equal(["set iCount to 1", "set iCount to 2", "set iCount to 3"],
            scripts.Fields.Select(StringValue).ToArray());
    }

    [Fact]
    public void GenerateAllReports_DialogueReportUsesTheCollectionsGameAndQuestVariables()
    {
        var info = SyntheticInfo();
        var records = new RecordCollection
        {
            Game = BethesdaGame.FalloutNewVegas,
            Dialogues = [info],
            Quests = [SyntheticQuest()],
            FormIdToEditorId = new Dictionary<uint, string>(EditorIds)
        };

        var files = GeckReportGenerator.GenerateAllReports(new ReportDataSources(records));
        var report = Lines(files["dialogue_report.txt"]);

        Assert.Contains($"  2: {GetQuestVariableLine}", report);
        Assert.Contains("  Result Script (End): 4 bytes of compiled code (SCDA)", report);
        Assert.DoesNotContain(report, line => line.Contains("function names", StringComparison.Ordinal));
    }

    private static ConditionDisplayContext Context(DialogueRecord info)
    {
        return ConditionDisplayContext.From(
            new RecordCollection
            {
                Game = BethesdaGame.FalloutNewVegas,
                Dialogues = [info],
                Quests = [SyntheticQuest()]
            },
            Resolver);
    }

    private static QuestRecord SyntheticQuest()
    {
        return new QuestRecord
        {
            FormId = QuestFormId,
            EditorId = "SyntheticQuest",
            Variables = [new ScriptVariableInfo(11, "iTimesAsked", 1)]
        };
    }

    private static DialogueRecord SyntheticInfo()
    {
        return new DialogueRecord
        {
            FormId = InfoFormId,
            EditorId = "SyntheticInfo",
            TopicFormId = TopicFormId,
            QuestFormId = QuestFormId,
            SpeakerFormId = SpeakerFormId,
            PromptText = "Tell me about the vault.",
            // The legacy Flags line reads the model's InfoFlags; DATA is shown separately and never feeds it.
            InfoFlags = 0x05,
            SerializedInfoData = new InfoSerializedData
            {
                DataLength = 4,
                InfoType = 1,
                NextSpeaker = 0,
                Flags1 = 0x05,
                Flags2 = 0x31
            },
            Responses = [new DialogueResponse { ResponseNumber = 1, Text = "The vault is sealed." }],
            Conditions =
            [
                // FNV condition 0x48 = GetIsID; 0x4F = GetQuestVariable; Type 0x60 = operator 3 (>=).
                new DialogueCondition { FunctionIndex = 0x0048, ComparisonValue = 1f, Parameter1 = SpeakerFormId },
                new DialogueCondition
                {
                    Type = 0x60,
                    FunctionIndex = 0x004F,
                    ComparisonValue = 1f,
                    Parameter1 = QuestFormId,
                    Parameter2 = 11
                }
            ],
            HasResultScript = true,
            ResultScripts =
            [
                new DialogueResultScript
                {
                    SourceText = EndSource,
                    DecompiledText = EndDecompiled,
                    CompiledData = [0x1D, 0x00, 0x00, 0x00],
                    Variables = [new ScriptVariableInfo(1, "iCount", 1)],
                    ReferencedObjects = [0x00000014, 0x80000001]
                }
            ],
            ResultScriptBlocks =
            [
                new InfoResultScriptBlock
                {
                    Slot = 0,
                    HasSchrHeader = true,
                    DeclaredCompiledSize = 0,
                    DeclaredReferenceCount = 0,
                    DeclaredVariableCount = 0,
                    HasNextSeparator = true
                },
                new InfoResultScriptBlock
                {
                    Slot = 1,
                    HasSchrHeader = true,
                    DeclaredCompiledSize = 4,
                    DeclaredReferenceCount = 2,
                    DeclaredVariableCount = 1,
                    HasNextSeparator = false,
                    ResultScriptIndex = 0
                }
            ]
        };
    }

    private static string StringValue(ReportField field)
    {
        return Assert.IsType<ReportValue.StringVal>(field.Value).Raw;
    }

    private static List<string> Lines(string text)
    {
        return [.. text.Split(["\r\n", "\n"], StringSplitOptions.None)];
    }

    /// <summary>Asserts each expected line occurs, in the given order (not necessarily adjacent).</summary>
    private static void AssertInOrder(List<string> lines, params string[] expected)
    {
        var from = 0;
        foreach (var line in expected)
        {
            var index = lines.IndexOf(line, from);
            Assert.True(index >= 0,
                $"expected line not found after line {from}: [{line}]{Environment.NewLine}{string.Join(Environment.NewLine, lines)}");
            from = index + 1;
        }
    }
}
