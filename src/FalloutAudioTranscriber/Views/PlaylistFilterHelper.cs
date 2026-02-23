using FalloutAudioTranscriber.Models;

namespace FalloutAudioTranscriber.Views;

/// <summary>
///     Pure-computation helpers for filtering and sorting the playlist view.
///     Extracted from PlaylistView code-behind to keep it under 500 lines.
/// </summary>
internal static class PlaylistFilterHelper
{
    /// <summary>
    ///     Build distinct, sorted dropdown values from the entry list.
    /// </summary>
    internal static List<string> BuildSpeakerList(IEnumerable<VoiceFileEntry> source)
    {
        return source
            .Select(e => e.SpeakerName ?? "(Unknown)")
            .Distinct()
            .OrderBy(s => s)
            .Prepend("All Speakers")
            .ToList();
    }

    /// <summary>
    ///     Build distinct, sorted quest dropdown values from the entry list.
    /// </summary>
    internal static List<string> BuildQuestList(IEnumerable<VoiceFileEntry> source)
    {
        return source
            .Select(e => e.QuestName ?? "(No Quest)")
            .Distinct()
            .OrderBy(q => q)
            .Prepend("All Quests")
            .ToList();
    }

    /// <summary>
    ///     Build distinct, sorted voice type dropdown values from the entry list.
    /// </summary>
    internal static List<string> BuildVoiceTypeList(IEnumerable<VoiceFileEntry> source)
    {
        return source
            .Select(e => e.VoiceType)
            .Where(v => !string.IsNullOrEmpty(v))
            .Distinct()
            .OrderBy(v => v)
            .Prepend("All Voice Types")
            .ToList();
    }

    /// <summary>
    ///     Apply all filters and sorting to produce the displayed entry list.
    /// </summary>
    internal static List<VoiceFileEntry> ApplyFiltersAndSort(
        IEnumerable<VoiceFileEntry> allEntries,
        bool showEsmSubtitles,
        string? speakerSelection,
        string? questSelection,
        string? voiceTypeSelection,
        string searchQuery,
        string sortColumn,
        bool sortAscending)
    {
        var filtered = allEntries.AsEnumerable();

        // ESM subtitle checkbox
        if (!showEsmSubtitles)
        {
            filtered = filtered.Where(e => e.Status != TranscriptionStatus.EsmSubtitle);
        }

        // Speaker filter
        if (speakerSelection != null && speakerSelection != "All Speakers")
        {
            var match = speakerSelection == "(Unknown)" ? null : speakerSelection;
            filtered = filtered.Where(e => e.SpeakerName == match);
        }

        // Quest filter
        if (questSelection != null && questSelection != "All Quests")
        {
            var match = questSelection == "(No Quest)" ? null : questSelection;
            filtered = filtered.Where(e => e.QuestName == match);
        }

        // Voice type filter
        if (voiceTypeSelection != null && voiceTypeSelection != "All Voice Types")
        {
            filtered = filtered.Where(e => e.VoiceType == voiceTypeSelection);
        }

        // Search query
        if (!string.IsNullOrEmpty(searchQuery))
        {
            var query = searchQuery;
            filtered = filtered.Where(e =>
                e.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.VoiceType.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.FormId.ToString("X8").Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (e.SpeakerName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.QuestName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.SubtitleText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        // Sort by selected column
        var sorted = sortColumn switch
        {
            "Name" => sortAscending
                ? filtered.OrderBy(e => e.TopicEditorId).ThenBy(e => e.FormId)
                : filtered.OrderByDescending(e => e.TopicEditorId).ThenByDescending(e => e.FormId),
            "Speaker" => sortAscending
                ? filtered.OrderBy(e => e.SpeakerName ?? "\uffff").ThenBy(e => e.TopicEditorId)
                : filtered.OrderByDescending(e => e.SpeakerName ?? "").ThenBy(e => e.TopicEditorId),
            "Quest" => sortAscending
                ? filtered.OrderBy(e => e.QuestName ?? "\uffff").ThenBy(e => e.TopicEditorId)
                : filtered.OrderByDescending(e => e.QuestName ?? "").ThenBy(e => e.TopicEditorId),
            _ => sortAscending // "Status" default
                ? filtered.OrderBy(e => (int)e.Status).ThenBy(e => e.VoiceType).ThenBy(e => e.TopicEditorId)
                : filtered.OrderByDescending(e => (int)e.Status).ThenBy(e => e.VoiceType).ThenBy(e => e.TopicEditorId)
        };

        return sorted.ToList();
    }

    /// <summary>
    ///     Check whether there are untranscribed work items.
    /// </summary>
    internal static bool HasWorkItems(IReadOnlyList<VoiceFileEntry> allEntries, bool transcribeEsmLines)
    {
        if (transcribeEsmLines)
        {
            return allEntries.Any(e =>
                e.Status == TranscriptionStatus.Untranscribed
                || e.Status == TranscriptionStatus.EsmSubtitle);
        }

        return allEntries.Any(e => e.Status == TranscriptionStatus.Untranscribed);
    }

    /// <summary>
    ///     Determine if an entry qualifies as a work item for batch or navigation.
    /// </summary>
    internal static bool IsWorkItem(VoiceFileEntry entry, bool transcribeEsmLines)
    {
        return entry.Status is TranscriptionStatus.Untranscribed or TranscriptionStatus.Automatic
               || (transcribeEsmLines && entry.Status == TranscriptionStatus.EsmSubtitle);
    }
}
