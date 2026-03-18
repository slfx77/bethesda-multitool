using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>Generates GECK-style text reports for Quest, Dialog Topic, Dialogue, and Dialogue Tree records.</summary>
internal static class GeckDialogueWriter
{
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
                foreach (var stage in quest.Stages)
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
                foreach (var obj in quest.Objectives)
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
            sb.AppendLine($"Display Name:   {topic.FullName ?? "(none)"}");
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

    internal static void AppendDialogueSection(StringBuilder sb, List<DialogueRecord> dialogues,
        FormIdResolver resolver)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Dialogue Responses ({dialogues.Count})");

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
                    sb.AppendLine($"Topic:          {resolver.FormatFull(dialogue.TopicFormId.Value)}");
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
                        if (!string.IsNullOrEmpty(response.Text))
                        {
                            sb.AppendLine($"    \"{response.Text}\"");
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Generate a report for Dialogue only.
    /// </summary>
    internal static string GenerateDialogueReport(List<DialogueRecord> dialogues,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendDialogueSection(sb, dialogues, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
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
        sb.AppendLine();

        // Render quest trees
        foreach (var (_, questNode) in tree.QuestTrees.OrderBy(q => q.Value.QuestName ?? ""))
        {
            RenderQuestTree(sb, questNode, resolver);
        }

        // Render orphan topics
        if (tree.OrphanTopics.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(new string('=', GeckReportHelpers.SeparatorWidth));
            sb.AppendLine("  Orphan Topics (no quest link)");
            sb.AppendLine(new string('=', GeckReportHelpers.SeparatorWidth));

            var visited = new HashSet<uint>();
            foreach (var topic in tree.OrphanTopics)
            {
                RenderTopicTree(sb, topic, visited, "  ", resolver);
            }
        }

        return sb.ToString();
    }

    internal static void RenderQuestTree(StringBuilder sb, QuestDialogueNode questNode,
        FormIdResolver resolver)
    {
        sb.AppendLine();
        var questLabel = questNode.QuestName ?? GeckReportHelpers.FormatFormId(questNode.QuestFormId);
        sb.AppendLine($"{"",3}{new string('=', GeckReportHelpers.SeparatorWidth - 6)}");
        sb.AppendLine($"{"",3}Quest: {questLabel} ({GeckReportHelpers.FormatFormId(questNode.QuestFormId)})");
        sb.AppendLine($"{"",3}{new string('=', GeckReportHelpers.SeparatorWidth - 6)}");

        var visited = new HashSet<uint>();
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
    internal static void AppendTerminalsSection(StringBuilder sb, List<TerminalRecord> terminals)
    {
        GeckTextContentWriter.AppendTerminalsSection(sb, terminals);
    }

    /// <summary>
    ///     Generate a report for Terminals only.
    /// </summary>
    internal static string GenerateTerminalsReport(List<TerminalRecord> terminals,
        Dictionary<uint, string>? lookup = null)
    {
        return GeckTextContentWriter.GenerateTerminalsReport(terminals, lookup);
    }

    /// <summary>
    ///     Delegates to <see cref="GeckTextContentWriter" />.
    /// </summary>
    internal static void AppendMessagesSection(StringBuilder sb, List<MessageRecord> messages,
        FormIdResolver resolver)
    {
        GeckTextContentWriter.AppendMessagesSection(sb, messages, resolver);
    }

    internal static string GenerateMessagesReport(List<MessageRecord> messages,
        FormIdResolver? resolver = null)
    {
        return GeckTextContentWriter.GenerateMessagesReport(messages, resolver);
    }
}
