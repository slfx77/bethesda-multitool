using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>Generates GECK-style text reports for Quest, Dialog Topic, Dialogue, and Dialogue Tree records.</summary>
internal static class GeckDialogueWriter
{
    internal static void AppendQuestsSection(StringBuilder sb, List<QuestRecord> quests,
        FormIdResolver resolver)
    {
        GeckReportGenerator.AppendSectionHeader(sb, $"Quests ({quests.Count})");

        foreach (var quest in quests.OrderBy(q => q.EditorId ?? ""))
        {
            GeckReportGenerator.AppendRecordHeader(sb, "QUEST", quest.EditorId);

            sb.AppendLine($"FormID:         {GeckReportGenerator.FormatFormId(quest.FormId)}");
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
        GeckReportGenerator.AppendSectionHeader(sb, $"Dialog Topics ({topics.Count})");

        foreach (var topic in topics.OrderBy(t => t.EditorId ?? ""))
        {
            GeckReportGenerator.AppendRecordHeader(sb, "DIAL", topic.EditorId);

            sb.AppendLine($"FormID:         {GeckReportGenerator.FormatFormId(topic.FormId)}");
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
        GeckReportGenerator.AppendSectionHeader(sb, $"Dialogue Responses ({dialogues.Count})");

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
                GeckReportGenerator.AppendRecordHeader(sb, "INFO", dialogue.EditorId);

                sb.AppendLine($"FormID:         {GeckReportGenerator.FormatFormId(dialogue.FormId)}");
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

        GeckReportGenerator.AppendHeader(sb, "Dialogue Tree");
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
            sb.AppendLine(new string('=', GeckReportGenerator.SeparatorWidth));
            sb.AppendLine("  Orphan Topics (no quest link)");
            sb.AppendLine(new string('=', GeckReportGenerator.SeparatorWidth));

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
        var questLabel = questNode.QuestName ?? GeckReportGenerator.FormatFormId(questNode.QuestFormId);
        sb.AppendLine($"{"",3}{new string('=', GeckReportGenerator.SeparatorWidth - 6)}");
        sb.AppendLine($"{"",3}Quest: {questLabel} ({GeckReportGenerator.FormatFormId(questNode.QuestFormId)})");
        sb.AppendLine($"{"",3}{new string('=', GeckReportGenerator.SeparatorWidth - 6)}");

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
                $"{indent}Topic: {topic.TopicName ?? GeckReportGenerator.FormatFormId(topic.TopicFormId)} (see above)");
            return;
        }

        sb.AppendLine();

        // Topic header
        var topicLabel = topic.TopicName ?? "(unnamed topic)";
        var topicTypeStr = topic.Topic != null ? $" [{topic.Topic.TopicTypeName}]" : "";
        var formIdStr = topic.TopicFormId != 0 ? $" ({GeckReportGenerator.FormatFormId(topic.TopicFormId)})" : "";
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
            for (var j = 0; j < infoNode.LinkedTopics.Count; j++)
            {
                var linkedTopic = infoNode.LinkedTopics[j];
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
            sb.AppendLine($"{indent}  [{index}] {GeckReportGenerator.FormatFormId(info.FormId)} (no text recovered)");
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

    internal static void AppendNotesSection(StringBuilder sb, List<NoteRecord> notes)
    {
        GeckReportGenerator.AppendSectionHeader(sb, $"Notes ({notes.Count})");

        foreach (var note in notes.OrderBy(n => n.EditorId ?? ""))
        {
            GeckReportGenerator.AppendRecordHeader(sb, "NOTE", note.EditorId);

            sb.AppendLine($"FormID:         {GeckReportGenerator.FormatFormId(note.FormId)}");
            sb.AppendLine($"Editor ID:      {note.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {note.FullName ?? "(none)"}");
            sb.AppendLine($"Type:           {note.NoteTypeName}");
            sb.AppendLine($"Endianness:     {(note.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{note.Offset:X8}");

            if (!string.IsNullOrEmpty(note.Text))
            {
                sb.AppendLine();
                sb.AppendLine("Text:");
                // Indent each line of the note text
                foreach (var line in note.Text.Split('\n'))
                {
                    sb.AppendLine($"  {line.TrimEnd('\r')}");
                }
            }
        }
    }

    /// <summary>
    ///     Generate a report for Notes only.
    /// </summary>
    internal static string GenerateNotesReport(List<NoteRecord> notes, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendNotesSection(sb, notes);
        return sb.ToString();
    }

    internal static void AppendBooksSection(StringBuilder sb, List<BookRecord> books)
    {
        GeckReportGenerator.AppendSectionHeader(sb, $"Books ({books.Count})");

        foreach (var book in books.OrderBy(b => b.EditorId ?? ""))
        {
            GeckReportGenerator.AppendRecordHeader(sb, "BOOK", book.EditorId);

            sb.AppendLine($"FormID:         {GeckReportGenerator.FormatFormId(book.FormId)}");
            sb.AppendLine($"Editor ID:      {book.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {book.FullName ?? "(none)"}");
            sb.AppendLine($"Value:          {book.Value} caps");
            sb.AppendLine($"Weight:         {book.Weight:F1}");
            sb.AppendLine($"Endianness:     {(book.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{book.Offset:X8}");

            if (book.TeachesSkill)
            {
                sb.AppendLine($"Teaches Skill:  {book.SkillTaught}");
            }

            if (!string.IsNullOrEmpty(book.Text))
            {
                sb.AppendLine();
                sb.AppendLine("Text:");
                foreach (var line in book.Text.Split('\n'))
                {
                    sb.AppendLine($"  {line.TrimEnd('\r')}");
                }
            }
        }
    }

    /// <summary>
    ///     Generate a report for Books only.
    /// </summary>
    internal static string GenerateBooksReport(List<BookRecord> books, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendBooksSection(sb, books);
        return sb.ToString();
    }

    internal static void AppendTerminalsSection(StringBuilder sb, List<TerminalRecord> terminals)
    {
        GeckReportGenerator.AppendSectionHeader(sb, $"Terminals ({terminals.Count})");

        foreach (var terminal in terminals.OrderBy(t => t.EditorId ?? ""))
        {
            GeckReportGenerator.AppendRecordHeader(sb, "TERM", terminal.EditorId);

            sb.AppendLine($"FormID:         {GeckReportGenerator.FormatFormId(terminal.FormId)}");
            sb.AppendLine($"Editor ID:      {terminal.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {terminal.FullName ?? "(none)"}");
            sb.AppendLine($"Difficulty:     {terminal.DifficultyName}");
            sb.AppendLine($"Endianness:     {(terminal.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{terminal.Offset:X8}");

            if (!string.IsNullOrEmpty(terminal.HeaderText))
            {
                sb.AppendLine();
                sb.AppendLine("Header:");
                foreach (var line in terminal.HeaderText.Split('\n'))
                {
                    sb.AppendLine($"  {line.TrimEnd('\r')}");
                }
            }

            if (terminal.MenuItems.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Menu Items ({terminal.MenuItems.Count}):");
                foreach (var item in terminal.MenuItems)
                {
                    sb.AppendLine($"  - {item.Text ?? "(no text)"}");
                }
            }
        }
    }

    /// <summary>
    ///     Generate a report for Terminals only.
    /// </summary>
    internal static string GenerateTerminalsReport(List<TerminalRecord> terminals,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendTerminalsSection(sb, terminals);
        return sb.ToString();
    }

    internal static void AppendMessagesSection(StringBuilder sb, List<MessageRecord> messages,
        FormIdResolver resolver)
    {
        GeckReportGenerator.AppendSectionHeader(sb, $"Messages ({messages.Count})");
        sb.AppendLine();

        var messageBoxes = messages.Count(m => m.IsMessageBox);
        var autoDisplay = messages.Count(m => m.IsAutoDisplay);
        var withButtons = messages.Count(m => m.Buttons.Count > 0);
        var withQuest = messages.Count(m => m.QuestFormId != 0);
        sb.AppendLine($"Total Messages: {messages.Count:N0}");
        sb.AppendLine($"  Message Boxes:  {messageBoxes:N0}");
        sb.AppendLine($"  Auto-Display:   {autoDisplay:N0}");
        sb.AppendLine($"  With Buttons:   {withButtons:N0}");
        sb.AppendLine($"  With Quest Link: {withQuest:N0}");
        sb.AppendLine();

        foreach (var msg in messages.OrderBy(m => m.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  MESSAGE: {msg.EditorId ?? "(none)"} \u2014 {msg.FullName ?? "(unnamed)"}");
            sb.AppendLine($"  FormID:      {GeckReportGenerator.FormatFormId(msg.FormId)}");
            var flags = new List<string>();
            if (msg.IsMessageBox)
            {
                flags.Add("MessageBox");
            }

            if (msg.IsAutoDisplay)
            {
                flags.Add("AutoDisplay");
            }

            if (flags.Count > 0)
            {
                sb.AppendLine($"  Flags:       {string.Join(", ", flags)}");
            }

            if (msg.DisplayTime != 0)
            {
                sb.AppendLine($"  Display Time: {msg.DisplayTime}");
            }

            if (msg.QuestFormId != 0)
            {
                sb.AppendLine($"  Quest:       {resolver.FormatFull(msg.QuestFormId)}");
            }

            if (!string.IsNullOrEmpty(msg.Description))
            {
                sb.AppendLine($"  Text:        {msg.Description}");
            }

            if (msg.Buttons.Count > 0)
            {
                sb.AppendLine(
                    $"  \u2500\u2500 Buttons ({msg.Buttons.Count}) {new string('\u2500', 80 - 18 - msg.Buttons.Count.ToString().Length)}");
                for (var i = 0; i < msg.Buttons.Count; i++)
                {
                    sb.AppendLine($"    [{i + 1}] {msg.Buttons[i]}");
                }
            }

            if (!string.IsNullOrEmpty(msg.Icon))
            {
                sb.AppendLine($"  Icon:        {msg.Icon}");
            }

            sb.AppendLine();
        }
    }

    internal static string GenerateMessagesReport(List<MessageRecord> messages,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendMessagesSection(sb, messages, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }
}
