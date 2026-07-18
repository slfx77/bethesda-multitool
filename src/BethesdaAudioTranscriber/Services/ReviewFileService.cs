using System.Text.Json;
using BethesdaAudioTranscriber.Models;

namespace BethesdaAudioTranscriber.Services;

/// <summary>
///     Handles load/save of .fnvreview.json sidecar files (suspected-typo flags generated
///     by tools/scripts/transcript_typo_check.py) and attaches them to voice file entries.
/// </summary>
public static class ReviewFileService
{
    private const string FileName = ".fnvreview.json";

    /// <summary>
    ///     Load the review sidecar from the data directory, if one exists.
    /// </summary>
    public static async Task<ReviewFile?> LoadAsync(string dataDirectory, CancellationToken ct = default)
    {
        var path = Path.Combine(dataDirectory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize(json, TranscriptionJsonContext.Default.ReviewFile);
    }

    /// <summary>
    ///     Save the review sidecar (persists Resolved state).
    /// </summary>
    public static async Task SaveAsync(
        string dataDirectory,
        ReviewFile review,
        CancellationToken ct = default)
    {
        var path = Path.Combine(dataDirectory, FileName);
        var json = JsonSerializer.Serialize(review, TranscriptionJsonContext.Default.ReviewFile);
        await File.WriteAllTextAsync(path, json, ct);
    }

    /// <summary>
    ///     Attach review suggestions to matching voice file entries.
    ///     Lip-sync files share keys with their audio siblings and are skipped.
    /// </summary>
    /// <returns>The number of entries with an unresolved flag.</returns>
    public static int ApplyToEntries(ReviewFile review, List<VoiceFileEntry> entries)
    {
        var pending = 0;

        foreach (var entry in entries)
        {
            if (string.Equals(entry.Extension, "lip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = $"{entry.VoiceType}|{entry.FormId:X8}_{entry.ResponseIndex}";
            if (review.Entries.TryGetValue(key, out var suggestion))
            {
                entry.Review = suggestion;
                if (!suggestion.Resolved)
                {
                    pending++;
                }
            }
        }

        return pending;
    }

    /// <summary>
    ///     Count entries with an unresolved flag.
    /// </summary>
    public static int CountPending(IEnumerable<VoiceFileEntry> entries)
    {
        return entries.Count(e => e.HasPendingReview);
    }
}
