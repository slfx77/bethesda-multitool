using FalloutAudioTranscriber.Models;

namespace FalloutAudioTranscriber.Views;

/// <summary>
///     Pure-computation helpers for batch transcription and export operations.
///     Extracted from PlaylistView code-behind to keep it under 500 lines.
/// </summary>
internal static class BatchOperationHelper
{
    /// <summary>
    ///     Build the project entry key for a voice file entry.
    /// </summary>
    internal static string BuildProjectKey(VoiceFileEntry entry)
    {
        return $"{entry.VoiceType}|{entry.FormId:X8}_{entry.ResponseIndex}";
    }

    /// <summary>
    ///     Create a <see cref="TranscriptionEntry" /> for saving into the project.
    /// </summary>
    internal static TranscriptionEntry CreateTranscriptionEntry(
        string text,
        string source,
        VoiceFileEntry entry)
    {
        return new TranscriptionEntry
        {
            Text = text,
            Source = source,
            VoiceType = entry.VoiceType,
            SpeakerName = entry.SpeakerName,
            QuestName = entry.QuestName,
            TranscribedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    ///     Apply a transcription result to both the entry and the project.
    /// </summary>
    internal static void ApplyTranscription(
        VoiceFileEntry entry,
        string text,
        string source,
        TranscriptionProject project)
    {
        entry.SubtitleText = text;
        entry.TranscriptionSource = source;

        var key = BuildProjectKey(entry);
        project.Entries[key] = CreateTranscriptionEntry(text, source, entry);
    }

    /// <summary>
    ///     Revert an entry to its ESM subtitle text and remove from the project.
    /// </summary>
    /// <returns>True if the entry was reverted, false if no ESM text was available.</returns>
    internal static bool RevertToEsm(VoiceFileEntry entry, TranscriptionProject project)
    {
        if (entry.EsmSubtitleText == null)
        {
            return false;
        }

        entry.SubtitleText = entry.EsmSubtitleText;
        entry.TranscriptionSource = "esm";

        var key = BuildProjectKey(entry);
        project.Entries.Remove(key);
        return true;
    }

    /// <summary>
    ///     Get the list of entries to process in a batch transcription run.
    /// </summary>
    internal static List<VoiceFileEntry> GetBatchWorkItems(
        IReadOnlyList<VoiceFileEntry> allEntries,
        bool transcribeEsmLines)
    {
        return transcribeEsmLines
            ? allEntries
                .Where(e => e.Status is TranscriptionStatus.Untranscribed or TranscriptionStatus.EsmSubtitle)
                .ToList()
            : allEntries
                .Where(e => e.Status == TranscriptionStatus.Untranscribed)
                .ToList();
    }

    /// <summary>
    ///     Create a snapshot of the project for safe background saving.
    /// </summary>
    internal static TranscriptionProject CreateProjectSnapshot(
        TranscriptionProject source,
        Dictionary<string, TranscriptionEntry> snapshotEntries)
    {
        return new TranscriptionProject
        {
            GameName = source.GameName,
            DataDirectory = source.DataDirectory,
            CreatedAt = source.CreatedAt,
            ModifiedAt = DateTimeOffset.UtcNow,
            Entries = snapshotEntries
        };
    }

    /// <summary>
    ///     Determine the recommended number of Whisper worker threads.
    /// </summary>
    internal static int GetWorkerCount()
    {
        return Math.Max(1, Environment.ProcessorCount / 2);
    }

    /// <summary>
    ///     Check whether the export button should be enabled.
    /// </summary>
    internal static bool ShouldEnableExport(
        TranscriptionProject? project,
        IReadOnlyList<VoiceFileEntry> allEntries)
    {
        return project?.Entries.Count > 0 || allEntries.Any(e => e.SubtitleText != null);
    }

    /// <summary>
    ///     Check whether there are exportable transcriptions.
    /// </summary>
    internal static bool HasExportableContent(
        TranscriptionProject? project,
        bool showEsmSubtitles,
        IReadOnlyList<VoiceFileEntry> allEntries)
    {
        var hasTranscriptions = project != null && project.Entries.Count > 0;
        var hasEsm = showEsmSubtitles && allEntries.Any(e => e.TranscriptionSource == "esm");
        return hasTranscriptions || hasEsm;
    }

    /// <summary>
    ///     Create a <see cref="BatchProgressItem" /> for a result entry.
    /// </summary>
    internal static BatchProgressItem CreateProgressItem(
        VoiceFileEntry entry,
        BatchItemStatus status,
        string? transcriptionPreview)
    {
        return new BatchProgressItem
        {
            DisplayName = entry.DisplayName,
            VoiceType = entry.VoiceType,
            ItemStatus = status,
            TranscriptionPreview = transcriptionPreview
        };
    }

    /// <summary>
    ///     Format the batch completion message.
    /// </summary>
    internal static string FormatCompletionMessage(int processed, int errors, TimeSpan elapsed)
    {
        return $"Done! {processed:N0} entries, {errors} errors, {elapsed:m\\:ss}";
    }

    /// <summary>
    ///     Format the cancellation message.
    /// </summary>
    internal static string FormatCancellationMessage(int processed)
    {
        return $"Cancelled after {processed:N0} entries";
    }

    /// <summary>
    ///     Safely await consumer tasks, swallowing exceptions (consumers handle their own errors).
    /// </summary>
    internal static async Task DrainConsumersAsync(Task[]? consumerTasks)
    {
        if (consumerTasks == null)
        {
            return;
        }

        try
        {
            await Task.WhenAll(consumerTasks);
        }
        catch
        {
            /* consumers already handle their own errors */
        }
    }
}
