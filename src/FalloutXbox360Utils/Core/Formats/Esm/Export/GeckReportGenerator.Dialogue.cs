using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

public static partial class GeckReportGenerator
{
    #region Quest Methods

    private static void AppendQuestsSection(StringBuilder sb, List<ReconstructedQuest> quests,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Quests ({quests.Count})");

        foreach (var quest in quests.OrderBy(q => q.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "QUEST", quest.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(quest.FormId)}");
            sb.AppendLine($"Editor ID:      {quest.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {quest.FullName ?? "(none)"}");
            sb.AppendLine($"Flags:          0x{quest.Flags:X2}");
            sb.AppendLine($"Priority:       {quest.Priority}");
            sb.AppendLine($"Endianness:     {(quest.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{quest.Offset:X8}");

            if (quest.Script.HasValue)
            {
                sb.AppendLine($"Script:         {FormatFormIdWithName(quest.Script.Value, lookup)}");
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
    public static string GenerateQuestsReport(List<ReconstructedQuest> quests, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendQuestsSection(sb, quests, lookup ?? []);
        return sb.ToString();
    }

    #endregion

    #region DialogTopic Methods

    private static void AppendDialogTopicsSection(StringBuilder sb, List<ReconstructedDialogTopic> topics,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Dialog Topics ({topics.Count})");

        foreach (var topic in topics.OrderBy(t => t.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "DIAL", topic.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(topic.FormId)}");
            sb.AppendLine($"Editor ID:      {topic.EditorId ?? "(none)"}");
            sb.AppendLine($"Display Name:   {topic.FullName ?? "(none)"}");
            sb.AppendLine($"Type:           {topic.TopicTypeName}");
            sb.AppendLine($"Endianness:     {(topic.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)")}");
            sb.AppendLine($"Offset:         0x{topic.Offset:X8}");

            if (topic.QuestFormId.HasValue)
            {
                sb.AppendLine($"Quest:          {FormatFormIdWithName(topic.QuestFormId.Value, lookup)}");
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
    public static string GenerateDialogTopicsReport(List<ReconstructedDialogTopic> topics,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendDialogTopicsSection(sb, topics, lookup ?? []);
        return sb.ToString();
    }

    #endregion

    #region Dialogue Methods

    private static void AppendDialogueSection(StringBuilder sb, List<ReconstructedDialogue> dialogues,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Dialogue Responses ({dialogues.Count})");

        // Group by quest if possible
        var grouped = dialogues
            .GroupBy(d => d.QuestFormId ?? 0)
            .OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            if (group.Key != 0)
            {
                sb.AppendLine();
                sb.AppendLine($"--- Quest: {FormatFormIdWithName(group.Key, lookup)} ---");
            }

            foreach (var dialogue in group.OrderBy(d => d.EditorId ?? ""))
            {
                AppendRecordHeader(sb, "INFO", dialogue.EditorId);

                sb.AppendLine($"FormID:         {FormatFormId(dialogue.FormId)}");
                sb.AppendLine($"Editor ID:      {dialogue.EditorId ?? "(none)"}");

                if (dialogue.TopicFormId.HasValue)
                {
                    sb.AppendLine($"Topic:          {FormatFormIdWithName(dialogue.TopicFormId.Value, lookup)}");
                }

                if (dialogue.QuestFormId.HasValue)
                {
                    sb.AppendLine($"Quest:          {FormatFormIdWithName(dialogue.QuestFormId.Value, lookup)}");
                }

                if (dialogue.SpeakerFormId.HasValue)
                {
                    sb.AppendLine($"Speaker:        {FormatFormIdWithName(dialogue.SpeakerFormId.Value, lookup)}");
                }

                if (dialogue.PreviousInfo.HasValue)
                {
                    sb.AppendLine($"Previous INFO:  {FormatFormIdWithName(dialogue.PreviousInfo.Value, lookup)}");
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
    public static string GenerateDialogueReport(List<ReconstructedDialogue> dialogues,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendDialogueSection(sb, dialogues, lookup ?? []);
        return sb.ToString();
    }

    #endregion

    #region DialogueTree Methods

    /// <summary>
    ///     Generate a standalone GECK-style dialogue tree report from the hierarchical tree result.
    /// </summary>
    public static string GenerateDialogueTreeReport(
        DialogueTreeResult tree,
        Dictionary<uint, string> lookup,
        Dictionary<uint, string>? displayNameLookup = null)
    {
        var sb = new StringBuilder();
        var combined = CombineLookups(lookup, displayNameLookup ?? []);

        var totalQuests = tree.QuestTrees.Count;
        var totalTopics = tree.QuestTrees.Values.Sum(q => q.Topics.Count) + tree.OrphanTopics.Count;
        var totalInfos = tree.QuestTrees.Values
            .SelectMany(q => q.Topics)
            .Sum(t => t.InfoChain.Count) + tree.OrphanTopics.Sum(t => t.InfoChain.Count);

        AppendHeader(sb, "Dialogue Tree");
        sb.AppendLine();
        sb.AppendLine($"  Quests:     {totalQuests:N0}");
        sb.AppendLine($"  Topics:     {totalTopics:N0}");
        sb.AppendLine($"  Responses:  {totalInfos:N0}");
        sb.AppendLine();

        // Render quest trees
        foreach (var (_, questNode) in tree.QuestTrees.OrderBy(q => q.Value.QuestName ?? ""))
        {
            RenderQuestTree(sb, questNode, combined);
        }

        // Render orphan topics
        if (tree.OrphanTopics.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(new string('=', SeparatorWidth));
            sb.AppendLine("  Orphan Topics (no quest link)");
            sb.AppendLine(new string('=', SeparatorWidth));

            var visited = new HashSet<uint>();
            foreach (var topic in tree.OrphanTopics)
            {
                RenderTopicTree(sb, topic, visited, "  ", combined);
            }
        }

        return sb.ToString();
    }

    private static void RenderQuestTree(StringBuilder sb, QuestDialogueNode questNode,
        Dictionary<uint, string> lookup)
    {
        sb.AppendLine();
        var questLabel = questNode.QuestName ?? FormatFormId(questNode.QuestFormId);
        sb.AppendLine($"{"",3}{new string('=', SeparatorWidth - 6)}");
        sb.AppendLine($"{"",3}Quest: {questLabel} ({FormatFormId(questNode.QuestFormId)})");
        sb.AppendLine($"{"",3}{new string('=', SeparatorWidth - 6)}");

        var visited = new HashSet<uint>();
        for (var i = 0; i < questNode.Topics.Count; i++)
        {
            var isLast = i == questNode.Topics.Count - 1;
            var connector = isLast ? "  +-- " : "  |-- ";
            var continuation = isLast ? "      " : "  |   ";

            RenderTopicTree(sb, questNode.Topics[i], visited, connector, lookup, continuation);
        }
    }

    private static void RenderTopicTree(StringBuilder sb, TopicDialogueNode topic,
        HashSet<uint> visited, string indent, Dictionary<uint, string> lookup,
        string? continuationIndent = null)
    {
        continuationIndent ??= indent;

        // Deduplication: prevent infinite recursion from circular cross-topic links
        if (topic.TopicFormId != 0 && !visited.Add(topic.TopicFormId))
        {
            sb.AppendLine();
            sb.AppendLine(
                $"{indent}Topic: {topic.TopicName ?? FormatFormId(topic.TopicFormId)} (see above)");
            return;
        }

        sb.AppendLine();

        // Topic header
        var topicLabel = topic.TopicName ?? "(unnamed topic)";
        var topicTypeStr = topic.Topic != null ? $" [{topic.Topic.TopicTypeName}]" : "";
        var formIdStr = topic.TopicFormId != 0 ? $" ({FormatFormId(topic.TopicFormId)})" : "";
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
            RenderInfoNode(sb, infoNode, i + 1, continuationIndent, lookup);

            // Render linked topics recursively
            for (var j = 0; j < infoNode.LinkedTopics.Count; j++)
            {
                var linkedTopic = infoNode.LinkedTopics[j];
                var linkIndent = continuationIndent + "        ";
                var linkCont = continuationIndent + "        ";
                sb.AppendLine($"{continuationIndent}      -> Links to:");
                RenderTopicTree(sb, linkedTopic, visited, linkIndent, lookup, linkCont);
            }
        }
    }

    private static void RenderInfoNode(StringBuilder sb, InfoDialogueNode infoNode, int index,
        string indent, Dictionary<uint, string> lookup)
    {
        var info = infoNode.Info;

        // Speaker name resolution
        var speakerStr = "";
        if (info.SpeakerFormId.HasValue && info.SpeakerFormId.Value != 0)
        {
            speakerStr = FormatFormIdWithName(info.SpeakerFormId.Value, lookup) + ": ";
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
            sb.AppendLine($"{indent}  [{index}] {FormatFormId(info.FormId)} (no text recovered)");
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

    #endregion

    #region Note Methods

    private static void AppendNotesSection(StringBuilder sb, List<ReconstructedNote> notes)
    {
        AppendSectionHeader(sb, $"Notes ({notes.Count})");

        foreach (var note in notes.OrderBy(n => n.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "NOTE", note.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(note.FormId)}");
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
    public static string GenerateNotesReport(List<ReconstructedNote> notes, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendNotesSection(sb, notes);
        return sb.ToString();
    }

    #endregion

    #region Book Methods

    private static void AppendBooksSection(StringBuilder sb, List<ReconstructedBook> books)
    {
        AppendSectionHeader(sb, $"Books ({books.Count})");

        foreach (var book in books.OrderBy(b => b.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "BOOK", book.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(book.FormId)}");
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
    public static string GenerateBooksReport(List<ReconstructedBook> books, Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendBooksSection(sb, books);
        return sb.ToString();
    }

    #endregion

    #region Terminal Methods

    private static void AppendTerminalsSection(StringBuilder sb, List<ReconstructedTerminal> terminals)
    {
        AppendSectionHeader(sb, $"Terminals ({terminals.Count})");

        foreach (var terminal in terminals.OrderBy(t => t.EditorId ?? ""))
        {
            AppendRecordHeader(sb, "TERM", terminal.EditorId);

            sb.AppendLine($"FormID:         {FormatFormId(terminal.FormId)}");
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
    public static string GenerateTerminalsReport(List<ReconstructedTerminal> terminals,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendTerminalsSection(sb, terminals);
        return sb.ToString();
    }

    #endregion

    #region Message Methods

    private static void AppendMessagesSection(StringBuilder sb, List<ReconstructedMessage> messages,
        Dictionary<uint, string> lookup)
    {
        AppendSectionHeader(sb, $"Messages ({messages.Count})");
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
            sb.AppendLine($"  FormID:      {FormatFormId(msg.FormId)}");
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
                sb.AppendLine($"  Quest:       {FormatFormIdWithName(msg.QuestFormId, lookup)}");
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

    public static string GenerateMessagesReport(List<ReconstructedMessage> messages,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendMessagesSection(sb, messages, lookup ?? []);
        return sb.ToString();
    }

    #endregion
}
