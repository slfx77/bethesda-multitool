using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Subtitles;

namespace FalloutXbox360Utils;

/// <summary>
///     Builds detail row data for dialogue record and topic detail panels.
///     Pure computation — produces structured data that the UI layer renders into grids.
/// </summary>
internal static class DialogueRecordDetailBuilder
{
    /// <summary>
    ///     A single row in a record detail panel: label, display value, and optional link target.
    /// </summary>
    internal readonly record struct DetailRow(string Label, string? Value, uint? LinkFormId = null);

    /// <summary>
    ///     Builds the detail rows for a dialogue INFO record, including identity, subtitle enrichment,
    ///     relationships, flags, linking, response detail, and location.
    /// </summary>
    /// <param name="info">The dialogue record to describe.</param>
    /// <param name="csvSubtitle">Optional subtitle CSV entry for enrichment.</param>
    /// <param name="resolveFormName">Delegate to resolve a FormID to a display name.</param>
    /// <param name="resolveSpeakerName">Delegate to resolve a speaker FormID to a display name.</param>
    public static List<DetailRow> BuildRecordDetailRows(
        DialogueRecord info,
        SubtitleEntry? csvSubtitle,
        Func<uint, string> resolveFormName,
        Func<uint?, string> resolveSpeakerName)
    {
        var rows = new List<DetailRow>();

        // Identity
        rows.Add(new DetailRow("FormID", $"0x{info.FormId:X8}"));
        AddIfNotEmpty(rows, "EditorID", info.EditorId);

        // Subtitle CSV enrichment
        if (csvSubtitle != null)
        {
            AddIfNotEmpty(rows, "CSV Subtitle", csvSubtitle.Text);
            AddIfNotEmpty(rows, "CSV Speaker", csvSubtitle.Speaker);
            AddIfNotEmpty(rows, "CSV Quest", csvSubtitle.Quest);
            AddIfNotEmpty(rows, "CSV VoiceType", csvSubtitle.VoiceType);
            AddIfNotEmpty(rows, "CSV Source", csvSubtitle.Source);
        }

        // Relationships (with navigable links)
        if (info.TopicFormId is > 0)
        {
            var topicDisplay = resolveFormName(info.TopicFormId.Value);
            rows.Add(new DetailRow("Topic", topicDisplay, info.TopicFormId.Value));
        }

        if (info.QuestFormId is > 0)
        {
            var questName = resolveFormName(info.QuestFormId.Value);
            rows.Add(new DetailRow("Quest", questName, info.QuestFormId.Value));
        }

        if (info.SpeakerFormId is > 0)
        {
            var speakerDisplay = resolveSpeakerName(info.SpeakerFormId);
            rows.Add(new DetailRow("Speaker",
                $"{speakerDisplay} (0x{info.SpeakerFormId.Value:X8})", info.SpeakerFormId.Value));
        }

        if (info.SpeakerAnimationFormId is > 0)
        {
            var animDisplay = resolveFormName(info.SpeakerAnimationFormId.Value);
            rows.Add(new DetailRow("Speaker Animation",
                $"{animDisplay} (0x{info.SpeakerAnimationFormId.Value:X8})", info.SpeakerAnimationFormId.Value));
        }

        // Flags
        rows.Add(new DetailRow("Info Index", info.InfoIndex.ToString()));
        if (info.InfoFlags != 0)
        {
            var flags = new List<string>();
            if (info.IsGoodbye) flags.Add("Goodbye");
            if ((info.InfoFlags & 0x02) != 0) flags.Add("Random");
            if ((info.InfoFlags & 0x04) != 0) flags.Add("RandomEnd");
            if (info.IsSayOnce) flags.Add("SayOnce");
            if (info.IsSpeechChallenge) flags.Add("SpeechChallenge");
            rows.Add(new DetailRow("Flags", $"0x{info.InfoFlags:X2} ({string.Join(", ", flags)})"));
        }

        if (info.InfoFlagsExt != 0)
        {
            rows.Add(new DetailRow("Extended Flags", $"0x{info.InfoFlagsExt:X2}"));
        }

        if (info.Difficulty > 0)
        {
            rows.Add(new DetailRow("Difficulty", info.DifficultyName));
        }

        // Linking
        if (info.PreviousInfo is > 0)
        {
            rows.Add(new DetailRow("Previous INFO", $"0x{info.PreviousInfo.Value:X8}", info.PreviousInfo.Value));
        }

        if (info.LinkToTopics.Count > 0)
        {
            rows.Add(new DetailRow("Link To Topics",
                string.Join(", ", info.LinkToTopics.Select(id => $"0x{id:X8}"))));
        }

        if (info.LinkFromTopics.Count > 0)
        {
            rows.Add(new DetailRow("Link From Topics",
                string.Join(", ", info.LinkFromTopics.Select(id => $"0x{id:X8}"))));
        }

        if (info.AddTopics.Count > 0)
        {
            rows.Add(new DetailRow("Add Topics",
                string.Join(", ", info.AddTopics.Select(id => $"0x{id:X8}"))));
        }

        // Response detail
        for (var i = 0; i < info.Responses.Count; i++)
        {
            var r = info.Responses[i];
            var prefix = info.Responses.Count > 1 ? $"Response {i + 1}" : "Response";
            rows.Add(new DetailRow($"{prefix} Emotion", $"{r.EmotionName} ({r.EmotionValue:+#;-#;0})"));
            rows.Add(new DetailRow($"{prefix} Number", r.ResponseNumber.ToString()));
        }

        // Location
        rows.Add(new DetailRow("Offset", $"0x{info.Offset:X8}"));
        rows.Add(new DetailRow("Endianness",
            info.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)"));

        return rows;
    }

    /// <summary>
    ///     Builds the detail rows for a linked topic (player choice detail panel).
    /// </summary>
    /// <param name="linkedTopic">The target topic.</param>
    /// <param name="sourceInfo">The INFO record that links to this topic.</param>
    /// <param name="resolveFormName">Delegate to resolve a FormID to a display name.</param>
    public static List<DetailRow> BuildTopicDetailRows(
        TopicDialogueNode linkedTopic,
        InfoDialogueNode sourceInfo,
        Func<uint, string> resolveFormName)
    {
        var rows = new List<DetailRow>();

        // Topic metadata
        rows.Add(new DetailRow("Topic FormID", $"0x{linkedTopic.TopicFormId:X8}"));
        AddIfNotEmpty(rows, "Topic EditorID", linkedTopic.Topic?.EditorId);
        AddIfNotEmpty(rows, "Topic Name", linkedTopic.Topic?.FullName);
        AddIfNotEmpty(rows, "Topic Type", linkedTopic.Topic?.TopicTypeName);
        rows.Add(new DetailRow("INFO Count", linkedTopic.InfoChain.Count.ToString()));

        // Quest link
        if (linkedTopic.Topic?.QuestFormId is > 0)
        {
            var questName = resolveFormName(linkedTopic.Topic.QuestFormId.Value);
            rows.Add(new DetailRow("Quest", questName, linkedTopic.Topic.QuestFormId.Value));
        }

        // Source INFO that leads here
        rows.Add(new DetailRow("Source INFO", $"0x{sourceInfo.Info.FormId:X8}"));
        AddIfNotEmpty(rows, "Source EditorID", sourceInfo.Info.EditorId);

        return rows;
    }

    /// <summary>
    ///     Collects metadata tag strings for a dialogue INFO record (emotions, flags, etc.).
    /// </summary>
    public static List<string> CollectMetadataTags(DialogueRecord info)
    {
        var tags = new List<string>();

        foreach (var response in info.Responses)
        {
            if (response.EmotionType != 0) // Not Neutral
            {
                var sign = response.EmotionValue >= 0 ? "+" : "";
                tags.Add($"Emotion: {response.EmotionName} {sign}{response.EmotionValue}");
            }
        }

        if (info.IsGoodbye)
        {
            tags.Add("Goodbye");
        }

        if (info.IsSayOnce)
        {
            tags.Add("Say Once");
        }

        if (info.IsSpeechChallenge)
        {
            tags.Add($"Speech {info.DifficultyName}");
        }

        return tags;
    }

    private static void AddIfNotEmpty(List<DetailRow> rows, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            rows.Add(new DetailRow(label, value));
        }
    }
}
