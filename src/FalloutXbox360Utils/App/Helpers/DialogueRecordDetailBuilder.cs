using FalloutXbox360Utils.App.Helpers;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Dialogue;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Subtitles;

namespace FalloutXbox360Utils;

/// <summary>
///     Builds detail row data for dialogue record and topic detail panels.
///     Pure computation — produces structured data that the UI layer renders into grids.
/// </summary>
internal static class DialogueRecordDetailBuilder
{
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
        Func<uint?, string> resolveSpeakerName,
        string? topicEditorId = null,
        Func<uint, string>? resolveEditorId = null,
        Func<uint, uint, string?>? resolveQuestVariable = null)
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

        AddIfNotEmpty(rows, "Topic EditorID", topicEditorId);

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

        if (info.SpeakerFactionFormId is > 0)
        {
            var factionDisplay = resolveFormName(info.SpeakerFactionFormId.Value);
            rows.Add(new DetailRow("Speaker Faction",
                $"{factionDisplay} (0x{info.SpeakerFactionFormId.Value:X8})", info.SpeakerFactionFormId.Value));
        }

        if (info.SpeakerRaceFormId is > 0)
        {
            var raceDisplay = resolveFormName(info.SpeakerRaceFormId.Value);
            rows.Add(new DetailRow("Speaker Race",
                $"{raceDisplay} (0x{info.SpeakerRaceFormId.Value:X8})", info.SpeakerRaceFormId.Value));
        }

        if (info.SpeakerVoiceTypeFormId is > 0)
        {
            var voiceDisplay = resolveFormName(info.SpeakerVoiceTypeFormId.Value);
            rows.Add(new DetailRow("Speaker Voice Type",
                $"{voiceDisplay} (0x{info.SpeakerVoiceTypeFormId.Value:X8})", info.SpeakerVoiceTypeFormId.Value));
        }

        if (info.Conditions.Count > 0)
        {
            for (var i = 0; i < info.Conditions.Count; i++)
            {
                rows.Add(new DetailRow(
                    info.Conditions.Count > 1 ? $"Condition {i + 1}" : "Condition",
                    DialogueConditionDisplayFormatter.FormatCondition(
                        info.Conditions[i], resolveFormName, resolveEditorId)));
            }

            // Condition Refs — one per line with navigable links
            var conditionRefs = CollectConditionFormRefs(info.Conditions);
            var refIndex = 0;
            foreach (var formId in conditionRefs)
            {
                refIndex++;
                var refLabel = conditionRefs.Count > 1 ? $"Condition Ref {refIndex}" : "Condition Ref";
                rows.Add(new DetailRow(refLabel, $"{resolveFormName(formId)} (0x{formId:X8})", formId));
            }

            // Quest Variable resolution for GetQuestVariable conditions
            if (resolveQuestVariable != null)
            {
                AddQuestVariableRows(rows, info.Conditions, resolveEditorId ?? resolveFormName,
                    resolveQuestVariable);
            }
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

        if (info.FollowUpInfos.Count > 0)
        {
            rows.Add(new DetailRow("Follow-Up INFOs",
                string.Join(", ", info.FollowUpInfos.Select(id => $"0x{id:X8}"))));
        }

        if (info.ResultScripts.Count > 0)
        {
            for (var i = 0; i < info.ResultScripts.Count; i++)
            {
                var script = info.ResultScripts[i];
                string label;
                if (info.ResultScripts.Count == 1 && !script.HasNextSeparator)
                    label = "Result Script";
                else if (i == 0)
                    label = "Result Script Start";
                else
                    label = "Result Script End";
                var scriptText = script.SourceText ?? script.DecompiledText;
                if (!string.IsNullOrWhiteSpace(scriptText))
                {
                    rows.Add(new DetailRow(label, scriptText));
                }
                else if (script.CompiledData is { Length: > 0 })
                {
                    rows.Add(new DetailRow(label, $"Compiled only ({script.CompiledData.Length} bytes)"));
                }

                for (var r = 0; r < script.ReferencedObjects.Count; r++)
                {
                    var refFormId = script.ReferencedObjects[r];
                    var refLabel = script.ReferencedObjects.Count > 1
                        ? $"{label} Ref {r + 1}"
                        : $"{label} Ref";
                    rows.Add(new DetailRow(refLabel,
                        $"{resolveFormName(refFormId)} (0x{refFormId:X8})", refFormId));
                }
            }
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
        Func<uint, string> resolveFormName,
        Func<uint, string>? resolveEditorId = null)
    {
        var rows = new List<DetailRow>();

        // Topic metadata
        rows.Add(new DetailRow("Topic FormID", $"0x{linkedTopic.TopicFormId:X8}"));
        AddIfNotEmpty(rows, "Topic EditorID", linkedTopic.Topic?.EditorId);
        AddIfNotEmpty(rows, "Topic Name", linkedTopic.Topic?.FullName);
        AddIfNotEmpty(rows, "Topic Type", linkedTopic.Topic?.TopicTypeName);
        if (linkedTopic.Topic != null)
        {
            rows.Add(new DetailRow("Priority", linkedTopic.Topic.Priority.ToString("F0")));
        }

        if (linkedTopic.Topic is { JournalIndex: not 0 })
        {
            rows.Add(new DetailRow("Journal Index", linkedTopic.Topic.JournalIndex.ToString()));
        }

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

        // Conditions from the linked topic's INFO records
        var allConditions = linkedTopic.InfoChain
            .SelectMany(i => i.Info.Conditions)
            .ToList();
        if (allConditions.Count > 0)
        {
            for (var i = 0; i < allConditions.Count; i++)
            {
                rows.Add(new DetailRow(
                    allConditions.Count > 1 ? $"Condition {i + 1}" : "Condition",
                    DialogueConditionDisplayFormatter.FormatCondition(
                        allConditions[i], resolveFormName, resolveEditorId)));
            }
        }

        return rows;
    }

    /// <summary>
    ///     Builds detail rows for a DIAL topic record itself (used when no INFO records are available).
    /// </summary>
    public static List<DetailRow> BuildDialTopicDetailRows(
        DialogTopicRecord topic, Func<uint, string> resolveFormName)
    {
        var rows = new List<DetailRow>();
        rows.Add(new DetailRow("FormID", $"0x{topic.FormId:X8}"));
        AddIfNotEmpty(rows, "EditorID", topic.EditorId);
        AddIfNotEmpty(rows, "Full Name", topic.FullName);
        rows.Add(new DetailRow("Topic Type", topic.TopicTypeName));

        if (topic.QuestFormId is > 0)
        {
            var questName = resolveFormName(topic.QuestFormId.Value);
            rows.Add(new DetailRow("Quest", questName, topic.QuestFormId.Value));
        }

        if (topic.ResponseCount > 0)
        {
            rows.Add(new DetailRow("Expected Responses", topic.ResponseCount.ToString()));
        }

        if (topic.Priority is not 0f)
        {
            rows.Add(new DetailRow("Priority", topic.Priority.ToString("F0")));
        }

        if (topic.JournalIndex != 0)
        {
            rows.Add(new DetailRow("Journal Index", topic.JournalIndex.ToString()));
        }

        AddIfNotEmpty(rows, "Dummy Prompt", topic.DummyPrompt);

        var flags = new List<string>();
        if (topic.IsRumors) flags.Add("Rumors");
        if (topic.IsTopLevel) flags.Add("TopLevel");
        if (flags.Count > 0)
        {
            rows.Add(new DetailRow("Flags", string.Join(", ", flags)));
        }

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

    /// <summary>
    ///     Collects unique FormID references from condition parameters and references.
    /// </summary>
    private static HashSet<uint> CollectConditionFormRefs(List<DialogueCondition> conditions)
    {
        var refs = new HashSet<uint>();
        foreach (var cond in conditions)
        {
            if (cond.Parameter1 != 0 && DialogueConditionDisplayFormatter.IsFormReference(cond, 0))
            {
                refs.Add(cond.Parameter1);
            }

            if (cond.Parameter2 != 0 && DialogueConditionDisplayFormatter.IsFormReference(cond, 1))
            {
                refs.Add(cond.Parameter2);
            }

            if (cond.Reference != 0)
            {
                refs.Add(cond.Reference);
            }
        }

        return refs;
    }

    /// <summary>
    ///     Adds "Condition Variable" rows for GetQuestVariable conditions with resolved variable names.
    /// </summary>
    private static void AddQuestVariableRows(
        List<DetailRow> rows,
        List<DialogueCondition> conditions,
        Func<uint, string> resolveEditorId,
        Func<uint, uint, string?> resolveQuestVariable)
    {
        const ushort getQuestVariableFunctionIndex = 0x004F;
        var questVarRefs = new List<(uint QuestFormId, uint VarIndex, string? VarName)>();

        foreach (var cond in conditions)
        {
            if (cond.FunctionIndex == getQuestVariableFunctionIndex && cond.Parameter1 != 0)
            {
                var varName = resolveQuestVariable(cond.Parameter1, cond.Parameter2);
                questVarRefs.Add((cond.Parameter1, cond.Parameter2, varName));
            }
        }

        for (var i = 0; i < questVarRefs.Count; i++)
        {
            var (questId, varIdx, varName) = questVarRefs[i];
            var label = questVarRefs.Count > 1 ? $"Condition Variable {i + 1}" : "Condition Variable";
            var display = varName != null
                ? $"{resolveEditorId(questId)}.{varName} (index {varIdx})"
                : $"{resolveEditorId(questId)}[{varIdx}]";
            rows.Add(new DetailRow(label, display, questId));
        }
    }

    private static void AddIfNotEmpty(List<DetailRow> rows, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            rows.Add(new DetailRow(label, value));
        }
    }

    /// <summary>
    ///     A single row in a record detail panel: label, display value, and optional link target.
    /// </summary>
    internal readonly record struct DetailRow(string Label, string? Value, uint? LinkFormId = null);
}
