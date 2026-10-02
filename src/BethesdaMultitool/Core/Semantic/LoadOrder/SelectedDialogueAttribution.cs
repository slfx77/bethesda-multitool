using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Semantic.LoadOrder;

/// <summary>Rebuilds parent-derived labels and links; captured/runtime attribution is never rewritten.</summary>
internal static class SelectedDialogueAttribution
{
    internal static void Rebuild(RecordCollection records)
    {
        for (var i = 0; i < records.DialogTopics.Count; i++)
        {
            var topic = records.DialogTopics[i];
            if (topic.LocalAttribution is not { } local) { continue; }
            records.DialogTopics[i] = topic with
            {
                FullName = local.FullName, DummyPrompt = local.DummyPrompt,
                QuestFormId = local.QuestFormId, SpeakerFormId = local.SpeakerFormId
            };
            if (string.IsNullOrWhiteSpace(local.FullName)) { records.FormIdToDisplayName.Remove(topic.FormId); }
            else { records.FormIdToDisplayName[topic.FormId] = local.FullName; }
        }
        var topics = records.DialogTopics.ToDictionary(t => t.FormId);
        for (var i = 0; i < records.Dialogues.Count; i++)
        {
            var info = records.Dialogues[i];
            if (info.LocalAttribution is not { } local) { continue; }
            var topicId = info.RawParentTopicFormId ?? local.TopicFormId;
            var parent = topicId is { } id ? topics.GetValueOrDefault(id) : null;
            records.Dialogues[i] = info with
            {
                TopicFormId = topicId,
                QuestFormId = local.QuestFormId is > 0 ? local.QuestFormId : parent?.QuestFormId,
                QuestAttributionSource = local.QuestFormId is > 0 ? "record-local-QSTI" : "inferred-parent-DIAL",
                SpeakerFormId = local.SpeakerFormId is > 0 ? local.SpeakerFormId : parent?.SpeakerFormId,
                SpeakerAttributionSource = local.SpeakerFormId is > 0 ? "record-local-ANAM-or-CTDA" : "inferred-topic-TNAM",
                SpeakerFactionFormId = local.SpeakerFactionFormId,
                SpeakerRaceFormId = local.SpeakerRaceFormId,
                SpeakerVoiceTypeFormId = local.SpeakerVoiceTypeFormId
            };
        }
        // Sibling/quest-majority and EditorID-prefix guesses are deliberately not promoted into
        // selected record fields. The record-local conditions and unresolved links remain available.
    }
}
