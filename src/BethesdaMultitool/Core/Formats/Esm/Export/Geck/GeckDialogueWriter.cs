using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Export.Report;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Presentation;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Geck;

/// <summary>Generates GECK-style text reports for Quest, Dialog Topic, Dialogue, and Dialogue Tree records.</summary>
internal static class GeckDialogueWriter
{
    /// <summary>Build a structured quest report from a <see cref="QuestRecord" />.</summary>
    internal static RecordReport BuildQuestReport(QuestRecord quest, FormIdResolver resolver)
    {
        var sections = new List<ReportSection>();

        // Identity
        var identityFields = new List<ReportField>
        {
            new("Flags", ReportValue.String($"0x{quest.Flags:X2}")),
            new("Priority", ReportValue.Int(quest.Priority))
        };
        if (quest.Script.HasValue)
            identityFields.Add(new ReportField("Script", ReportValue.FormId(quest.Script.Value, resolver),
                $"0x{quest.Script.Value:X8}"));
        sections.Add(new ReportSection("Identity", identityFields));

        // Stages
        if (quest.Stages.Count > 0)
        {
            var stageItems = quest.Stages.OrderBy(s => s.Index)
                .Select(s =>
                {
                    var flagsStr = s.Flags != 0 ? $" [Flags: 0x{s.Flags:X2}]" : "";
                    var logStr = !string.IsNullOrEmpty(s.LogEntry) ? $" {s.LogEntry}" : "";
                    return (ReportValue)new ReportValue.CompositeVal(
                        [
                            new ReportField("Index", ReportValue.Int(s.Index)),
                            new ReportField("Log", ReportValue.String(s.LogEntry ?? ""))
                        ], $"[{s.Index,3}]{flagsStr}{logStr}");
                })
                .ToList();
            sections.Add(new ReportSection("Stages", [new ReportField("Stages", ReportValue.List(stageItems))]));
        }

        // Objectives
        if (quest.Objectives.Count > 0)
        {
            var objItems = quest.Objectives.OrderBy(o => o.Index)
                .Select(o =>
                {
                    var text = !string.IsNullOrEmpty(o.DisplayText) ? o.DisplayText : "(no text)";
                    return (ReportValue)new ReportValue.CompositeVal(
                        [
                            new ReportField("Index", ReportValue.Int(o.Index)),
                            new ReportField("Text", ReportValue.String(text))
                        ], $"[{o.Index,3}] {text}");
                })
                .ToList();
            sections.Add(new ReportSection("Objectives", [new ReportField("Objectives", ReportValue.List(objItems))]));
        }

        return new RecordReport("Quest", quest.FormId, quest.EditorId, quest.FullName, sections);
    }

    internal static void AppendQuestsSection(StringBuilder sb, List<QuestRecord> quests,
        FormIdResolver resolver)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Quests ({quests.Count})");

        foreach (var quest in quests.OrderBy(q => q.EditorId ?? ""))
        {
            GeckReportHelpers.AppendRecordHeader(sb, "QUEST", quest.EditorId);

            sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(quest.FormId)}");
            sb.AppendLine($"Editor ID:      {quest.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {quest.FullName ?? "(none)"}");
            sb.AppendLine($"Flags:          0x{quest.Flags:X2}");
            sb.AppendLine($"Priority:       {quest.Priority}");
            sb.AppendLine($"Endianness:     {(quest.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{quest.Offset:X8}");

            if (quest.Script.HasValue)
            {
                sb.AppendLine($"Script:         {resolver.FormatFull(quest.Script.Value)}");
            }

            if (quest.Stages.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Stages:");
                foreach (var stage in quest.Stages.OrderBy(s => s.Index))
                {
                    var flagsStr = stage.Flags != 0 ? $" [Flags: 0x{stage.Flags:X2}]" : "";
                    var logStr = !string.IsNullOrEmpty(stage.LogEntry)
                        ? $" {stage.LogEntry}"
                        : "";
                    sb.AppendLine($"  [{stage.Index,3}]{flagsStr}{logStr}");
                }
            }

            if (quest.Objectives.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Objectives:");
                foreach (var obj in quest.Objectives.OrderBy(o => o.Index))
                {
                    var text = !string.IsNullOrEmpty(obj.DisplayText)
                        ? obj.DisplayText
                        : "(no text)";
                    sb.AppendLine($"  [{obj.Index,3}] {text}");
                }
            }
        }
    }

    /// <summary>
    ///     Generate a report for Quests only.
    /// </summary>
    internal static string GenerateQuestsReport(List<QuestRecord> quests, FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendQuestsSection(sb, quests, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }

    internal static void AppendDialogTopicsSection(StringBuilder sb, List<DialogTopicRecord> topics,
        FormIdResolver resolver)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Dialog Topics ({topics.Count})");

        foreach (var topic in topics.OrderBy(t => t.EditorId ?? ""))
        {
            GeckReportHelpers.AppendRecordHeader(sb, "DIAL", topic.EditorId);

            sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(topic.FormId)}");
            sb.AppendLine($"Editor ID:      {topic.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {ResolveDialogTopicPlayerText(topic) ?? "(none)"}");
            sb.AppendLine($"Type:           {topic.TopicTypeName}");
            sb.AppendLine($"Endianness:     {(topic.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{topic.Offset:X8}");

            if (topic.QuestFormId.HasValue)
            {
                sb.AppendLine($"Quest:          {resolver.FormatFull(topic.QuestFormId.Value)}");
            }

            if (topic.ResponseCount > 0)
            {
                sb.AppendLine($"Responses:      {topic.ResponseCount}");
            }

            if (topic.JournalIndex != 0)
            {
                sb.AppendLine($"Journal Index:  {topic.JournalIndex}");
            }
        }
    }

    /// <summary>
    ///     Generate a report for Dialog Topics only.
    /// </summary>
    internal static string GenerateDialogTopicsReport(List<DialogTopicRecord> topics,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendDialogTopicsSection(sb, topics, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }

    /// <summary>
    ///     Appends the Dialogue Responses section. Every legacy line keeps its place; after each INFO's legacy
    ///     lines come its serialized DATA fields (<c>Data:</c>, named from the game's generated schema), its
    ///     conditions (<c>Conditions (n):</c>, through the shared <see cref="ConditionDescriber" />) and its
    ///     result-script slots (<c>Result Scripts:</c>, Begin/End with provenance-labeled source and the
    ///     separately labeled BethesdaMultitool decompilation). Nothing is truncated.
    /// </summary>
    /// <param name="sb">The report being built.</param>
    /// <param name="dialogues">The INFO records.</param>
    /// <param name="resolver">EditorID/display-name source.</param>
    /// <param name="conditions">
    ///     Condition context (game and quest-variable names); null assumes <see cref="GameProfiles.DefaultGame" />
    ///     and says so.
    /// </param>
    /// <param name="isMemoryDumpInput">True when the records came from a memory dump (see <see cref="ScriptSourceProvenance" />).</param>
    internal static void AppendDialogueSection(
        StringBuilder sb,
        List<DialogueRecord> dialogues,
        FormIdResolver resolver,
        ConditionDisplayContext? conditions = null,
        bool isMemoryDumpInput = false)
    {
        conditions ??= ConditionDisplayContext.ForResolver(resolver, GameProfiles.DefaultGame, gameAssumed: true);
        GeckReportHelpers.AppendSectionHeader(sb, $"Dialogue Responses ({dialogues.Count})");
        if (conditions.GameAssumed && dialogues.Any(d => d.Conditions.Count > 0 || d.SerializedInfoData is not null))
        {
            sb.AppendLine(
                $"Conditions:     function names and DATA fields assume {conditions.Game} (not detected from the input)");
        }

        // Group by quest if possible
        var grouped = dialogues
            .GroupBy(d => d.QuestFormId ?? 0)
            .OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            if (group.Key != 0)
            {
                sb.AppendLine();
                sb.AppendLine($"--- Quest: {resolver.FormatFull(group.Key)} ---");
            }

            foreach (var dialogue in group.OrderBy(d => d.EditorId ?? ""))
            {
                GeckReportHelpers.AppendRecordHeader(sb, "INFO", dialogue.EditorId);

                sb.AppendLine($"FormID:         {GeckReportHelpers.FormatFormId(dialogue.FormId)}");
                sb.AppendLine($"Editor ID:      {dialogue.EditorId ?? "(none)"}");

                if (dialogue.TopicFormId.HasValue)
                {
                    sb.AppendLine($"Topic:          {DialogueTopicLabels.FormatReference(dialogue.TopicFormId.Value, resolver)}");
                }

                if (dialogue.QuestFormId.HasValue)
                {
                    sb.AppendLine($"Quest:          {resolver.FormatFull(dialogue.QuestFormId.Value)}");
                }

                if (dialogue.SpeakerFormId.HasValue)
                {
                    sb.AppendLine($"Speaker:        {resolver.FormatFull(dialogue.SpeakerFormId.Value)}");
                }

                if (dialogue.SpeakerAnimationFormId.HasValue)
                {
                    sb.AppendLine($"Speaker Anim:   {resolver.FormatFull(dialogue.SpeakerAnimationFormId.Value)}");
                }

                if (dialogue.PreviousInfo.HasValue)
                {
                    sb.AppendLine($"Previous INFO:  {resolver.FormatFull(dialogue.PreviousInfo.Value)}");
                }

                if (!string.IsNullOrEmpty(dialogue.PromptText))
                {
                    sb.AppendLine($"Prompt:         \"{dialogue.PromptText}\"");
                }

                // Flags
                var flags = new List<string>();
                if (dialogue.IsGoodbye) flags.Add("Goodbye");
                if (dialogue.IsSayOnce) flags.Add("Say Once");
                if (dialogue.IsSpeechChallenge) flags.Add($"Speech Challenge: {dialogue.DifficultyName}");
                if (flags.Count > 0)
                {
                    sb.AppendLine($"Flags:          {string.Join(", ", flags)}");
                }

                sb.AppendLine(
                    $"Endianness:     {(dialogue.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
                sb.AppendLine($"Offset:         0x{dialogue.Offset:X8}");

                if (dialogue.Responses.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Responses:");
                    foreach (var response in dialogue.Responses.OrderBy(r => r.ResponseNumber))
                    {
                        var emotionStr = response.EmotionType != 0 || response.EmotionValue != 0
                            ? $" [{response.EmotionName}: {response.EmotionValue}]"
                            : "";
                        sb.AppendLine($"  [{response.ResponseNumber}]{emotionStr}");
                        if (response.SoundFormId is > 0)
                        {
                            sb.AppendLine($"    Sound: {resolver.FormatFull(response.SoundFormId.Value)}");
                        }

                        if (!string.IsNullOrEmpty(response.Text))
                        {
                            sb.AppendLine($"    \"{response.Text}\"");
                        }
                    }
                }

                AppendInfoDetail(sb, dialogue, resolver, conditions, isMemoryDumpInput);
            }
        }
    }

    /// <summary>
    ///     Generate a report for Dialogue only.
    /// </summary>
    internal static string GenerateDialogueReport(
        List<DialogueRecord> dialogues,
        FormIdResolver? resolver = null,
        ConditionDisplayContext? conditions = null,
        bool isMemoryDumpInput = false)
    {
        var sb = new StringBuilder();
        AppendDialogueSection(sb, dialogues, resolver ?? conditions?.Resolver ?? FormIdResolver.Empty, conditions,
            isMemoryDumpInput);
        return sb.ToString();
    }

    /// <summary>
    ///     The additive per-INFO lines of dialogue_report.txt: <c>Data:</c> (when a DATA subrecord was parsed),
    ///     <c>Conditions (n):</c> with the GECK-convention grouping, and <c>Result Scripts:</c> with one entry per
    ///     serialized slot, empty SCHR-only slots included.
    /// </summary>
    private static void AppendInfoDetail(
        StringBuilder sb,
        DialogueRecord dialogue,
        FormIdResolver resolver,
        ConditionDisplayContext conditions,
        bool isMemoryDumpInput)
    {
        var dataLine = dialogue.SerializedInfoData is { } data
            ? string.Join("; ", DialogueInfoDetailBuilder.DescribeSerializedInfoData(data, conditions.Game)
                .Select(pair => $"{pair.Label}: {pair.Value}"))
            : null;
        var descriptions = ConditionDescriber.DescribeAll(dialogue.Conditions, conditions);
        var slots = ResolveResultScriptSlots(dialogue);
        var hasScripts = slots.Count > 0 || dialogue.HasResultScript;
        if (dataLine is null && descriptions.Count == 0 && !hasScripts)
        {
            return;
        }

        sb.AppendLine();
        if (dataLine is not null)
        {
            sb.AppendLine($"Data:           {dataLine}");
        }

        if (descriptions.Count > 0)
        {
            sb.AppendLine($"Conditions ({descriptions.Count}):");
            foreach (var description in descriptions)
            {
                sb.AppendLine($"  {description.Index}: {ConditionTextFormatter.FormatLine(description)}");
            }

            if (ConditionTextFormatter.FormatLogicSummary(descriptions) is { } grouping)
            {
                sb.AppendLine($"Grouping:       {grouping}");
            }
        }

        if (!hasScripts)
        {
            return;
        }

        sb.AppendLine("Result Scripts:");
        if (slots.Count == 0)
        {
            sb.AppendLine(
                $"  {DialogueInfoDetailBuilder.NoCompiledCode}: an SCHR header is present, but no block carried SCDA, " +
                "SCTX, locals or references");
            return;
        }

        foreach (var slot in slots)
        {
            GeckTextContentWriter.AppendEmbeddedScript(sb, "  ",
                DescribeResultScriptSlot(slot, resolver, isMemoryDumpInput));
        }
    }

    /// <summary>
    ///     Pairs each result-script slot with its script, as the INFO <c>show</c> panel does: the parser's block
    ///     list names every slot, empty SCHR-only ones included, when its script indices are exactly
    ///     0..ResultScripts.Count-1; otherwise the scripts are listed alone.
    /// </summary>
    private static List<ResultScriptSlot> ResolveResultScriptSlots(DialogueRecord info)
    {
        var blocks = info.ResultScriptBlocks;
        if (blocks.Count > 0 && BlocksDescribeScripts(blocks, info.ResultScripts.Count))
        {
            return blocks
                .Select(block => new ResultScriptSlot(
                    ResultScriptSlotLabel(block.Slot, blocks.Count, block.HasNextSeparator),
                    block.ResultScriptIndex is { } index ? info.ResultScripts[index] : null,
                    block))
                .ToList();
        }

        return info.ResultScripts
            .Select((script, index) => new ResultScriptSlot(
                ResultScriptSlotLabel(index, info.ResultScripts.Count, script.HasNextSeparator),
                script,
                null))
            .ToList();
    }

    private static bool BlocksDescribeScripts(IReadOnlyList<InfoResultScriptBlock> blocks, int scriptCount)
    {
        var expected = 0;
        foreach (var block in blocks)
        {
            if (block.ResultScriptIndex is not { } index)
            {
                continue;
            }

            if (index != expected)
            {
                return false;
            }

            expected++;
        }

        return expected == scriptCount;
    }

    /// <summary>The slot heading: a lone block with no NEXT is "Result Script"; otherwise Begin, then End.</summary>
    private static string ResultScriptSlotLabel(int index, int count, bool hasNextSeparator)
    {
        if (count == 1 && !hasNextSeparator)
        {
            return "Result Script";
        }

        return index switch
        {
            0 => "Result Script (Begin)",
            1 => "Result Script (End)",
            _ => $"Result Script (block {index + 1}, beyond Begin/End)"
        };
    }

    private static GeckTextContentWriter.EmbeddedScriptText DescribeResultScriptSlot(
        ResultScriptSlot slot,
        FormIdResolver resolver,
        bool isMemoryDumpInput)
    {
        const string NoCompiledCode = DialogueInfoDetailBuilder.NoCompiledCode;
        if (slot.Script is not { } script)
        {
            var summary = slot.Block switch
            {
                { DeclaredCompiledSize: 0 } =>
                    $"{NoCompiledCode} (the SCHR header declares 0 compiled bytes; the block has no SCDA, SCTX, " +
                    "locals or references)",
                { HasSchrHeader: true } =>
                    $"{NoCompiledCode} (SCHR header only; the block has no SCDA, SCTX, locals or references)",
                _ => $"{NoCompiledCode} (the block has no SCDA, SCTX, locals or references)"
            };
            return new GeckTextContentWriter.EmbeddedScriptText(
                slot.Label,
                summary,
                ScriptSourceProvenance.Classify((string?)null, ScriptSourceTextOrigin.None, isMemoryDumpInput),
                null,
                null,
                null,
                [],
                [],
                null,
                []);
        }

        var dumpInput = isMemoryDumpInput || script.IsDmpDerived;
        var classification = ScriptSourceProvenance.Classify(script, isMemoryDumpInput);
        var compiledLength = script.CompiledData?.Length ?? 0;
        string scriptSummary;
        if (compiledLength > 0)
        {
            scriptSummary = $"{compiledLength} bytes of compiled code (SCDA)";
            if (slot.Block?.DeclaredCompiledSize is { } declared && declared != (uint)compiledLength)
            {
                scriptSummary += $"; the SCHR header declares {declared}";
            }
        }
        else if (!string.IsNullOrEmpty(script.SourceText))
        {
            scriptSummary = $"{NoCompiledCode} (the block holds source text but no SCDA)";
        }
        else if (script.Variables.Count > 0 || script.ReferencedObjects.Count > 0)
        {
            scriptSummary = $"{NoCompiledCode} (the block holds locals or references but no SCDA or source text)";
        }
        else if (script.WithheldSourceReason is not null)
        {
            scriptSummary = $"{NoCompiledCode}; captured source was withheld by validation";
        }
        else
        {
            scriptSummary = $"{NoCompiledCode} (the block holds no SCDA or source text)";
        }

        string? bundleNote = null;
        if (script.IsIncompleteExecutableBundle)
        {
            const string Disagreement =
                "the SCHR/SCDA/local/reference bundle is structurally inconsistent or failed emission safety validation; " +
                "this is not a validated, runnable script";
            bundleNote = dumpInput
                ? $"incomplete or unsafe in this capture: {Disagreement}"
                : $"incomplete: {Disagreement}";
        }

        return new GeckTextContentWriter.EmbeddedScriptText(
            slot.Label,
            scriptSummary,
            classification,
            classification.HasSourceText ? script.SourceText : null,
            string.IsNullOrEmpty(script.DecompiledText) ? null : script.DecompiledText,
            compiledLength > 0 ? (script.IsBigEndianBytecode ? "Big-Endian" : "Little-Endian") : null,
            script.Variables.OrderBy(variable => variable.Index).ToArray(),
            script.ReferencedObjects
                .Select(reference => GeckScriptWriter.FormatScriptReference(reference, script.Variables, resolver))
                .ToArray(),
            bundleNote,
            script.ReferencedObjects);
    }

    /// <summary>
    ///     Generate a standalone GECK-style dialogue tree report from the hierarchical tree result.
    /// </summary>
    internal static string GenerateDialogueTreeReport(
        DialogueTreeResult tree,
        FormIdResolver resolver)
    {
        var sb = new StringBuilder();

        var totalQuests = tree.QuestTrees.Count;
        var totalTopics = tree.QuestTrees.Values.Sum(q => q.Topics.Count) + tree.OrphanTopics.Count;
        var totalInfos = tree.QuestTrees.Values
            .SelectMany(q => q.Topics)
            .Sum(t => t.InfoChain.Count) + tree.OrphanTopics.Sum(t => t.InfoChain.Count);

        GeckReportHelpers.AppendHeader(sb, "Dialogue Tree");
        sb.AppendLine();
        sb.AppendLine($"  Quests:     {totalQuests:N0}");
        sb.AppendLine($"  Topics:     {totalTopics:N0}");
        sb.AppendLine($"  Responses:  {totalInfos:N0}");
        if (tree.Edges.Count > 0)
        {
            sb.AppendLine("  View: selected static records; condition outcomes and engine order are unavailable.");
            sb.AppendLine($"  Links: {tree.Edges.Count:N0}; unresolved: {tree.Edges.Count(e => e.Status == "unresolved"):N0}");
            sb.AppendLine($"  Ordering issues: {tree.OrderingIssues.Count:N0} (see dialogue_ordering.csv)");
        }
        sb.AppendLine();

        // One visited set for the WHOLE report: each topic's full chain renders exactly once
        // (its first encounter) and every later encounter emits the "(see above)" stub. The old
        // per-quest sets re-rendered shared topics in full under every linked quest, which is
        // quadratic in shared-topic size × quest links — retail Fallout3.esm links one DIAL into
        // 107 quests with GREETING carrying 2,542 INFOs, and the report OOM'd the process.
        // FalloutNV.esm merely fit under the limit; its report shrinks (deduplicates) too.
        var visited = new HashSet<uint>();

        // Render quest trees
        foreach (var (_, questNode) in tree.QuestTrees.OrderBy(q => q.Value.QuestName ?? ""))
        {
            RenderQuestTree(sb, questNode, resolver, visited);
        }

        // Render orphan topics
        if (tree.OrphanTopics.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(new string('=', GeckReportHelpers.SeparatorWidth));
            sb.AppendLine("  Orphan Topics (no quest link)");
            sb.AppendLine(new string('=', GeckReportHelpers.SeparatorWidth));

            foreach (var topic in tree.OrphanTopics)
            {
                RenderTopicTree(sb, topic, visited, "  ", resolver);
            }
        }

        return sb.ToString();
    }

    internal static void RenderQuestTree(StringBuilder sb, QuestDialogueNode questNode,
        FormIdResolver resolver, HashSet<uint>? reportVisited = null)
    {
        sb.AppendLine();
        var questLabel = questNode.QuestName ?? GeckReportHelpers.FormatFormId(questNode.QuestFormId);
        sb.AppendLine($"{"",3}{new string('=', GeckReportHelpers.SeparatorWidth - 6)}");
        sb.AppendLine($"{"",3}Quest: {questLabel} ({GeckReportHelpers.FormatFormId(questNode.QuestFormId)})");
        sb.AppendLine($"{"",3}{new string('=', GeckReportHelpers.SeparatorWidth - 6)}");

        // A caller-supplied set spans the whole report (see GenerateDialogueTreeReport); the
        // per-quest fallback remains for direct single-quest rendering.
        var visited = reportVisited ?? new HashSet<uint>();
        for (var i = 0; i < questNode.Topics.Count; i++)
        {
            var isLast = i == questNode.Topics.Count - 1;
            var connector = isLast ? "  +-- " : "  |-- ";
            var continuation = isLast ? "      " : "  |   ";

            RenderTopicTree(sb, questNode.Topics[i], visited, connector, resolver, continuation);
        }
    }

    internal static void RenderTopicTree(StringBuilder sb, TopicDialogueNode topic,
        HashSet<uint> visited, string indent, FormIdResolver resolver,
        string? continuationIndent = null)
    {
        continuationIndent ??= indent;

        // Deduplication: prevent infinite recursion from circular cross-topic links
        if (topic.TopicFormId != 0 && !visited.Add(topic.TopicFormId))
        {
            sb.AppendLine();
            sb.AppendLine(
                $"{indent}Topic: {topic.TopicName ?? GeckReportHelpers.FormatFormId(topic.TopicFormId)} (see above)");
            return;
        }

        sb.AppendLine();

        // Topic header
        var topicLabel = topic.TopicName ?? "(unnamed topic)";
        var topicTypeStr = topic.Topic != null ? $" [{topic.Topic.TopicTypeName}]" : "";
        var formIdStr = topic.TopicFormId != 0 ? $" ({GeckReportHelpers.FormatFormId(topic.TopicFormId)})" : "";
        sb.AppendLine($"{indent}Topic: {topicLabel}{formIdStr}{topicTypeStr}");

        // Topic metadata
        if (topic.Topic is { DummyPrompt: not null })
        {
            sb.AppendLine($"{continuationIndent}  Prompt: \"{topic.Topic.DummyPrompt}\"");
        }

        if (topic.Topic is { Priority: not 0f })
        {
            sb.AppendLine($"{continuationIndent}  Priority: {topic.Topic.Priority:F1}");
        }

        // INFO chain
        for (var i = 0; i < topic.InfoChain.Count; i++)
        {
            var infoNode = topic.InfoChain[i];

            sb.AppendLine();
            RenderInfoNode(sb, infoNode, i + 1, continuationIndent, resolver);

            // Render linked topics recursively
            for (var j = 0; j < infoNode.ChoiceTopics.Count; j++)
            {
                var linkedTopic = infoNode.ChoiceTopics[j];
                var linkIndent = continuationIndent + "        ";
                var linkCont = continuationIndent + "        ";
                sb.AppendLine($"{continuationIndent}      -> Links to:");
                RenderTopicTree(sb, linkedTopic, visited, linkIndent, resolver, linkCont);
            }
        }
    }

    internal static void RenderInfoNode(StringBuilder sb, InfoDialogueNode infoNode, int index,
        string indent, FormIdResolver resolver)
    {
        var info = infoNode.Info;

        // Speaker name resolution
        var speakerStr = "";
        if (info.SpeakerFormId.HasValue && info.SpeakerFormId.Value != 0)
        {
            speakerStr = resolver.FormatFull(info.SpeakerFormId.Value) + ": ";
        }

        // Prompt text (player's line)
        if (!string.IsNullOrEmpty(info.PromptText))
        {
            sb.AppendLine($"{indent}  [{index}] Player: \"{info.PromptText}\"");
        }

        // Response text (NPC's lines)
        if (info.Responses.Count > 0)
        {
            foreach (var response in info.Responses.OrderBy(r => r.ResponseNumber))
            {
                var emotionStr = response.EmotionType != 0 || response.EmotionValue != 0
                    ? $" [{response.EmotionName}: {response.EmotionValue}]"
                    : "";
                if (!string.IsNullOrEmpty(response.Text))
                {
                    sb.AppendLine($"{indent}      {speakerStr}\"{response.Text}\"{emotionStr}");
                }

                if (response.SoundFormId is > 0)
                {
                    sb.AppendLine($"{indent}      Sound: {resolver.FormatFull(response.SoundFormId.Value)}");
                }
            }
        }
        else if (string.IsNullOrEmpty(info.PromptText))
        {
            // No prompt and no responses — show FormID reference
            sb.AppendLine($"{indent}  [{index}] {GeckReportHelpers.FormatFormId(info.FormId)} (no text recovered)");
        }

        // Flags line
        var flags = new List<string>();
        if (info.IsGoodbye)
        {
            flags.Add("Goodbye");
        }

        if (info.IsSayOnce)
        {
            flags.Add("Say Once");
        }

        if (info.IsSpeechChallenge)
        {
            flags.Add($"Speech Challenge: {info.DifficultyName}");
        }

        if (flags.Count > 0)
        {
            sb.AppendLine($"{indent}      [{string.Join("] [", flags)}]");
        }

        // Captured link edges, raw. "-> Links to:" only renders TCLT targets the tree builder
        // RESOLVED into ChoiceTopics; captured edges on a content-less INFO (or edges to topics
        // outside the tree) were invisible, which is exactly what hid the Ulysses greeting
        // stub's authored menu (0x00133FCD) during the 2026-08-05 playtest analysis. AddTopics
        // never rendered here at all.
        if (info.LinkToTopics.Count > infoNode.ChoiceTopics.Count)
        {
            sb.AppendLine(
                $"{indent}      TCLT (raw): {string.Join(", ", info.LinkToTopics.Select(resolver.FormatFull))}");
        }

        if (info.AddTopics.Count > 0)
        {
            sb.AppendLine(
                $"{indent}      AddTopics: {string.Join(", ", info.AddTopics.Select(resolver.FormatFull))}");
        }
    }

    /// <summary>
    ///     Delegates to <see cref="GeckTextContentWriter" />.
    /// </summary>
    internal static void AppendNotesSection(StringBuilder sb, List<NoteRecord> notes)
    {
        GeckTextContentWriter.AppendNotesSection(sb, notes);
    }

    /// <summary>
    ///     Generate a report for Notes only.
    /// </summary>
    internal static string GenerateNotesReport(List<NoteRecord> notes, Dictionary<uint, string>? lookup = null)
    {
        return GeckTextContentWriter.GenerateNotesReport(notes, lookup);
    }

    /// <summary>
    ///     Delegates to <see cref="GeckTextContentWriter" />.
    /// </summary>
    internal static void AppendBooksSection(StringBuilder sb, List<BookRecord> books,
        FormIdResolver resolver)
    {
        GeckTextContentWriter.AppendBooksSection(sb, books, resolver);
    }

    /// <summary>
    ///     Generate a report for Books only.
    /// </summary>
    internal static string GenerateBooksReport(List<BookRecord> books, FormIdResolver? resolver = null)
    {
        return GeckTextContentWriter.GenerateBooksReport(books, resolver);
    }

    /// <summary>
    ///     Delegates to <see cref="GeckTextContentWriter" />.
    /// </summary>
    internal static void AppendTerminalsSection(
        StringBuilder sb,
        List<TerminalRecord> terminals,
        FormIdResolver resolver,
        ConditionDisplayContext? conditions = null,
        bool isMemoryDumpInput = false)
    {
        GeckTextContentWriter.AppendTerminalsSection(sb, terminals, resolver, conditions, isMemoryDumpInput);
    }

    /// <summary>
    ///     Generate a report for Terminals only.
    /// </summary>
    internal static string GenerateTerminalsReport(
        List<TerminalRecord> terminals,
        FormIdResolver? resolver = null,
        ConditionDisplayContext? conditions = null,
        bool isMemoryDumpInput = false)
    {
        return GeckTextContentWriter.GenerateTerminalsReport(terminals, resolver, conditions, isMemoryDumpInput);
    }

    /// <summary>
    ///     Delegates to <see cref="GeckTextContentWriter" />.
    /// </summary>
    internal static void AppendMessagesSection(StringBuilder sb, List<MessageRecord> messages,
        FormIdResolver resolver, ConditionDisplayContext? conditions = null)
    {
        GeckTextContentWriter.AppendMessagesSection(sb, messages, resolver, conditions);
    }

    internal static string GenerateMessagesReport(List<MessageRecord> messages,
        FormIdResolver? resolver = null, ConditionDisplayContext? conditions = null)
    {
        return GeckTextContentWriter.GenerateMessagesReport(messages, resolver, conditions);
    }

    /// <summary>Build a structured dialog topic report from a <see cref="DialogTopicRecord" />.</summary>
    internal static RecordReport BuildDialogTopicReport(DialogTopicRecord topic, FormIdResolver resolver)
    {
        var sections = new List<ReportSection>();

        // Identity
        var identityFields = new List<ReportField>
        {
            new("TopicType", ReportValue.String(topic.TopicTypeName)),
            new("Flags", ReportValue.String($"0x{topic.Flags:X2}"))
        };
        if (topic.IsRumors)
            identityFields.Add(new ReportField("Rumors", ReportValue.Bool(true)));
        if (topic.IsTopLevel)
            identityFields.Add(new ReportField("TopLevel", ReportValue.Bool(true)));
        if (topic.ResponseCount > 0)
            identityFields.Add(new ReportField("ResponseCount", ReportValue.Int(topic.ResponseCount)));
        if (Math.Abs(topic.Priority) > 0f)
            identityFields.Add(new ReportField("Priority", ReportValue.Float(topic.Priority)));
        if (topic.JournalIndex != 0)
            identityFields.Add(new ReportField("JournalIndex", ReportValue.Int(topic.JournalIndex)));
        sections.Add(new ReportSection("Identity", identityFields));

        var promptFields = new List<ReportField>();
        var playerPrompt = ResolveDialogTopicPlayerText(topic);
        if (!string.IsNullOrWhiteSpace(playerPrompt))
            promptFields.Add(new ReportField("Player", ReportValue.String($"\"{playerPrompt}\"")));
        if (!DialogTopicTextLooksEditorOnly(topic.FullName, topic.EditorId) &&
            !string.IsNullOrWhiteSpace(topic.FullName) &&
            !string.IsNullOrWhiteSpace(topic.DummyPrompt) &&
            !string.Equals(topic.DummyPrompt, topic.FullName, StringComparison.Ordinal))
            promptFields.Add(new ReportField("Fallback", ReportValue.String($"\"{topic.DummyPrompt}\"")));
        if (promptFields.Count > 0)
            sections.Add(new ReportSection("Prompt", promptFields));

        // References
        var refFields = new List<ReportField>();
        if (topic.QuestFormId.HasValue)
            refFields.Add(new ReportField("Quest", ReportValue.FormId(topic.QuestFormId.Value, resolver),
                $"0x{topic.QuestFormId.Value:X8}"));
        if (topic.SpeakerFormId.HasValue)
            refFields.Add(new ReportField("Speaker", ReportValue.FormId(topic.SpeakerFormId.Value, resolver),
                $"0x{topic.SpeakerFormId.Value:X8}"));
        if (refFields.Count > 0)
            sections.Add(new ReportSection("References", refFields));

        return new RecordReport("DialogTopic", topic.FormId, topic.EditorId, playerPrompt, sections);
    }

    private static string? ResolveDialogTopicPlayerText(DialogTopicRecord topic)
    {
        if (DialogTopicTextLooksEditorOnly(topic.FullName, topic.EditorId) &&
            !string.IsNullOrWhiteSpace(topic.DummyPrompt))
        {
            return topic.DummyPrompt;
        }

        return !string.IsNullOrWhiteSpace(topic.FullName)
            ? topic.FullName
            : topic.DummyPrompt;
    }

    private static bool DialogTopicTextLooksEditorOnly(string? value, string? editorId)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (!string.IsNullOrWhiteSpace(editorId) &&
            string.Equals(trimmed, editorId.Trim(), StringComparison.Ordinal))
        {
            return true;
        }

        return char.IsDigit(trimmed[0]) &&
               trimmed.Any(char.IsLetter) &&
               !trimmed.Any(char.IsWhiteSpace);
    }

    /// <summary>Build a structured dialogue report from a <see cref="DialogueRecord" />.</summary>
    /// <param name="dialogue">The INFO record.</param>
    /// <param name="resolver">EditorID/display-name source.</param>
    /// <param name="conditions">
    ///     Condition context (game and quest-variable names). Null assumes <see cref="GameProfiles.DefaultGame" />,
    ///     the game the cross-dump comparison (this report's caller) reads.
    /// </param>
    internal static RecordReport BuildDialogueReport(
        DialogueRecord dialogue,
        FormIdResolver resolver,
        ConditionDisplayContext? conditions = null)
    {
        var sections = new List<ReportSection>();

        // References — topic, quest, speaker (shown prominently)
        var refFields = new List<ReportField>();
        if (dialogue.TopicFormId.HasValue)
            refFields.Add(new ReportField("Topic", ReportValue.FormId(dialogue.TopicFormId.Value, resolver),
                $"0x{dialogue.TopicFormId.Value:X8}"));
        if (dialogue.QuestFormId.HasValue)
            refFields.Add(new ReportField("Quest", ReportValue.FormId(dialogue.QuestFormId.Value, resolver),
                $"0x{dialogue.QuestFormId.Value:X8}"));
        if (dialogue.SpeakerFormId.HasValue)
            refFields.Add(new ReportField("Speaker", ReportValue.FormId(dialogue.SpeakerFormId.Value, resolver),
                $"0x{dialogue.SpeakerFormId.Value:X8}"));
        if (refFields.Count > 0)
            sections.Add(new ReportSection("References", refFields));

        // Prompt — the player-visible text (most important for comparison)
        if (!string.IsNullOrEmpty(dialogue.PromptText))
            sections.Add(new ReportSection("Prompt",
                [new ReportField("Player", ReportValue.String($"\"{dialogue.PromptText}\""))]));

        // Responses — each response as its own field, not nested in a list wrapper
        if (dialogue.Responses.Count > 0)
        {
            var responseFields = new List<ReportField>();
            foreach (var r in dialogue.Responses.OrderBy(r => r.ResponseNumber))
            {
                var emotionTag = r.EmotionType != 0 || r.EmotionValue != 0
                    ? $"  [{r.EmotionName} ({r.EmotionValue:+0;-0})]"
                    : "";
                var text = !string.IsNullOrEmpty(r.Text) ? $"\"{r.Text}\"" : "(no text)";
                var soundTag = r.SoundFormId is > 0
                    ? $"  [Sound: {resolver.FormatFull(r.SoundFormId.Value)}]"
                    : "";
                responseFields.Add(new ReportField(
                    $"Response {r.ResponseNumber}",
                    ReportValue.String($"{text}{emotionTag}{soundTag}")));
            }

            sections.Add(new ReportSection("Responses", responseFields));
        }

        // Flags — only if any are set
        var flagParts = new List<string>();
        if (dialogue.IsGoodbye) flagParts.Add("Goodbye");
        if (dialogue.IsRandom) flagParts.Add("Random");
        if (dialogue.IsSayOnce) flagParts.Add("SayOnce");
        if (dialogue.IsSpeechChallenge) flagParts.Add($"Speech Challenge ({dialogue.DifficultyName})");
        if (flagParts.Count > 0 || dialogue.InfoFlags != 0)
        {
            var flagFields = new List<ReportField>();
            if (flagParts.Count > 0)
                flagFields.Add(new ReportField("Flags", ReportValue.String(string.Join(", ", flagParts))));
            if (dialogue.InfoFlags != 0 && flagParts.Count == 0)
                flagFields.Add(new ReportField("InfoFlags", ReportValue.String($"0x{dialogue.InfoFlags:X2}")));
            sections.Add(new ReportSection("Flags", flagFields));
        }

        // Conditions — the shared describer, so a condition reads the same here as in show, the reports
        // and the package listing (function table of the context's game, Run On always stated).
        if (dialogue.Conditions.Count > 0)
        {
            var context = conditions ??
                          ConditionDisplayContext.ForResolver(resolver, GameProfiles.DefaultGame, gameAssumed: true);
            var descriptions = ConditionDescriber.DescribeAll(dialogue.Conditions, context);
            var condFields = new List<ReportField>();
            foreach (var description in descriptions)
            {
                condFields.Add(new ReportField(
                    $"Condition {description.Index}{(description.Raw.IsOr ? " (OR)" : "")}",
                    ReportValue.String(ConditionTextFormatter.FormatLine(description, includeConnector: false))));
            }

            if (ConditionTextFormatter.FormatLogicSummary(descriptions) is { } grouping)
            {
                sections.Add(new ReportSection("Condition Grouping",
                    [new ReportField("Grouping", ReportValue.String(grouping))]));
            }

            sections.Add(new ReportSection("Conditions", condFields));
        }

        // Result Scripts — source or decompiled text. A BethesdaMultitool decompilation is always labeled as
        // one, whether it stands in for missing SCTX or is the only text there is.
        if (dialogue.ResultScripts.Count > 0)
        {
            var scriptFields = new List<ReportField>();
            for (var i = 0; i < dialogue.ResultScripts.Count; i++)
            {
                var script = dialogue.ResultScripts[i];
                var label = dialogue.ResultScripts.Count > 1 ? $"Script {i + 1}" : "Result Script";
                var classification = ScriptSourceProvenance.Classify(script, false);
                if (classification.HasSourceText)
                    scriptFields.Add(new ReportField(
                        classification.IsReconstructed
                            ? $"{label} (reconstruction)"
                            : label,
                        ReportValue.String(script.SourceText!)));
                else if (!string.IsNullOrEmpty(script.DecompiledText))
                    scriptFields.Add(new ReportField(
                        $"{label} (reconstruction)",
                        ReportValue.String(script.DecompiledText)));
            }

            if (scriptFields.Count > 0)
                sections.Add(new ReportSection("Result Scripts", scriptFields));
        }

        // Links — previous INFO, topic links
        var linkFields = new List<ReportField>();
        if (dialogue.PreviousInfo.HasValue)
            linkFields.Add(new ReportField("Previous INFO",
                ReportValue.FormId(dialogue.PreviousInfo.Value, resolver),
                $"0x{dialogue.PreviousInfo.Value:X8}"));
        if (dialogue.LinkToTopics.Count > 0)
        {
            var linkToItems = dialogue.LinkToTopics
                .Select(id => (ReportValue)ReportValue.FormId(id, resolver)).ToList();
            linkFields.Add(new ReportField("Links To", ReportValue.List(linkToItems)));
        }

        if (dialogue.LinkFromTopics.Count > 0)
        {
            var linkFromItems = dialogue.LinkFromTopics
                .Select(id => (ReportValue)ReportValue.FormId(id, resolver)).ToList();
            linkFields.Add(new ReportField("Links From", ReportValue.List(linkFromItems)));
        }

        if (dialogue.AddTopics.Count > 0)
        {
            var addItems = dialogue.AddTopics
                .Select(id => (ReportValue)ReportValue.FormId(id, resolver)).ToList();
            linkFields.Add(new ReportField("Unlocks Topics", ReportValue.List(addItems)));
        }

        if (linkFields.Count > 0)
            sections.Add(new ReportSection("Links", linkFields));

        return new RecordReport("Dialogue", dialogue.FormId, dialogue.EditorId, null, sections);
    }

    /// <summary>One result-script slot: its heading, its script (null for an empty block) and its block, if known.</summary>
    private readonly record struct ResultScriptSlot(
        string Label,
        DialogueResultScript? Script,
        InfoResultScriptBlock? Block);
}
