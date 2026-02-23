using System.Text;
using System.Text.Json;
using FalloutAudioTranscriber.Models;

namespace FalloutAudioTranscriber.Services;

/// <summary>
///     Handles save/load/export of transcription projects as .fnvtranscript.json files.
/// </summary>
public static class TranscriptionFileService
{
    private const string FileName = ".fnvtranscript.json";

    /// <summary>
    ///     Save a transcription project to the data directory.
    /// </summary>
    public static async Task SaveAsync(
        string dataDirectory,
        TranscriptionProject project,
        CancellationToken ct = default)
    {
        project.ModifiedAt = DateTimeOffset.UtcNow;
        var path = Path.Combine(dataDirectory, FileName);
        var json = JsonSerializer.Serialize(project, TranscriptionJsonContext.Default.TranscriptionProject);
        await File.WriteAllTextAsync(path, json, ct);
    }

    /// <summary>
    ///     Load a transcription project from the data directory, if one exists.
    /// </summary>
    public static async Task<TranscriptionProject?> LoadAsync(
        string dataDirectory,
        CancellationToken ct = default)
    {
        var path = Path.Combine(dataDirectory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize(json, TranscriptionJsonContext.Default.TranscriptionProject);
    }

    /// <summary>
    ///     Apply saved transcriptions to voice file entries (entries without ESM subtitles
    ///     get their text from saved data).
    /// </summary>
    public static void ApplyToEntries(TranscriptionProject project, List<VoiceFileEntry> entries)
    {
        var legacyKeysToRemove = new HashSet<string>();

        foreach (var entry in entries)
        {
            // Preserve original ESM text and mark ESM entries
            if (entry.SubtitleText != null && entry.TranscriptionSource == null)
            {
                entry.EsmSubtitleText = entry.SubtitleText;
                entry.TranscriptionSource = "esm";
            }

            // Always check for project overrides (even for ESM entries — a whisper
            // transcription on an ESM line must persist across reload).
            var saved = FindSavedEntry(project, entry, out var matchedKey);
            if (saved != null)
            {
                entry.SubtitleText = saved.Text;
                entry.TranscriptionSource = saved.Source;

                // Migrate: ensure new-format key exists so each voice type
                // gets its own entry (legacy keys collapsed voice types).
                var newKey = $"{entry.VoiceType}|{entry.FormId:X8}_{entry.ResponseIndex}";
                if (matchedKey != newKey)
                {
                    legacyKeysToRemove.Add(matchedKey);

                    if (!project.Entries.ContainsKey(newKey))
                    {
                        project.Entries[newKey] = new TranscriptionEntry
                        {
                            Text = saved.Text,
                            Source = saved.Source,
                            VoiceType = entry.VoiceType,
                            SpeakerName = entry.SpeakerName ?? saved.SpeakerName,
                            QuestName = entry.QuestName ?? saved.QuestName,
                            TranscribedAt = saved.TranscribedAt
                        };
                    }
                }
            }
        }

        // Remove legacy keys that were migrated to new format
        foreach (var key in legacyKeysToRemove)
        {
            project.Entries.Remove(key);
        }
    }

    private static TranscriptionEntry? FindSavedEntry(
        TranscriptionProject project, VoiceFileEntry entry, out string matchedKey)
    {
        // Current key format: voicetype|FormID_ResponseIndex
        matchedKey = $"{entry.VoiceType}|{entry.FormId:X8}_{entry.ResponseIndex}";
        if (project.Entries.TryGetValue(matchedKey, out var saved))
        {
            return saved;
        }

        // Backward compat: legacy FormID_ResponseIndex key (no voice type)
        matchedKey = $"{entry.FormId:X8}_{entry.ResponseIndex}";
        if (project.Entries.TryGetValue(matchedKey, out saved))
        {
            return saved;
        }

        // Backward compat: legacy FormID-only key for response index 0
        if (entry.ResponseIndex == 0)
        {
            matchedKey = entry.FormId.ToString("X8");
            if (project.Entries.TryGetValue(matchedKey, out saved))
            {
                return saved;
            }
        }

        matchedKey = "";
        return null;
    }

    /// <summary>
    ///     Clear all transcription entries with a given source (e.g., "whisper") from
    ///     both the project dictionary and in-memory voice file entries.
    /// </summary>
    public static int ClearBySource(
        TranscriptionProject project,
        List<VoiceFileEntry> entries,
        string source)
    {
        var keysToRemove = project.Entries
            .Where(kv => kv.Value.Source == source)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            project.Entries.Remove(key);
        }

        foreach (var entry in entries.Where(e => e.TranscriptionSource == source))
        {
            // Restore ESM text if available, otherwise clear completely
            if (entry.EsmSubtitleText != null)
            {
                entry.SubtitleText = entry.EsmSubtitleText;
                entry.TranscriptionSource = "esm";
            }
            else
            {
                entry.SubtitleText = null;
                entry.TranscriptionSource = null;
            }
        }

        return keysToRemove.Count;
    }

    /// <summary>
    ///     Export all transcriptions as CSV.
    /// </summary>
    public static async Task ExportCsvAsync(
        string outputPath,
        TranscriptionProject project,
        List<VoiceFileEntry>? entries = null,
        bool includeEsm = false,
        CancellationToken ct = default)
    {
        var rows = BuildExportRows(project, entries, includeEsm);

        var sb = new StringBuilder();
        sb.AppendLine("File,FormID,VoiceType,Speaker,Quest,Source,Text");

        foreach (var r in rows)
        {
            sb.AppendLine(
                $"{Escape(r.FilePath)},{r.FormId},{Escape(r.VoiceType)},{Escape(r.Speaker)},{Escape(r.Quest)},{r.Source},{Escape(r.Text)}");
        }

        await File.WriteAllTextAsync(outputPath, sb.ToString(), ct);
    }

    /// <summary>
    ///     Export all transcriptions as plain text, grouped by quest and speaker.
    /// </summary>
    public static async Task ExportTextAsync(
        string outputPath,
        TranscriptionProject project,
        List<VoiceFileEntry>? entries = null,
        bool includeEsm = false,
        CancellationToken ct = default)
    {
        var rows = BuildExportRows(project, entries, includeEsm);

        var sb = new StringBuilder();
        sb.AppendLine($"# {project.GameName} Transcriptions");
        sb.AppendLine($"# Data: {project.DataDirectory}");
        sb.AppendLine($"# Exported: {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        var byQuest = rows
            .GroupBy(r => r.Quest ?? "(No Quest)")
            .OrderBy(g => g.Key);

        foreach (var questGroup in byQuest)
        {
            sb.AppendLine($"## {questGroup.Key}");
            sb.AppendLine();

            var bySpeaker = questGroup
                .GroupBy(r => r.Speaker ?? "(Unknown Speaker)")
                .OrderBy(g => g.Key);

            foreach (var speakerGroup in bySpeaker)
            {
                sb.AppendLine($"### {speakerGroup.Key}");

                foreach (var r in speakerGroup)
                {
                    if (r.FilePath.Length > 0)
                        sb.AppendLine($"  [{r.FilePath}] {r.Text}");
                    else
                        sb.AppendLine($"  [{r.FormId}] {r.Text}");
                }

                sb.AppendLine();
            }
        }

        await File.WriteAllTextAsync(outputPath, sb.ToString(), ct);
    }

    /// <summary>
    ///     Build the unified export row list from project entries + optional ESM entries.
    /// </summary>
    private static List<ExportRow> BuildExportRows(
        TranscriptionProject project, List<VoiceFileEntry>? entries, bool includeEsm)
    {
        var pathLookup = BuildPathLookup(entries);
        var rows = new List<ExportRow>();

        // Project entries (whisper, manual, accepted)
        foreach (var (key, entry) in project.Entries)
        {
            rows.Add(new ExportRow(
                pathLookup?.GetValueOrDefault(key) ?? "",
                ExtractFormId(key),
                entry.VoiceType,
                entry.SpeakerName,
                entry.QuestName,
                entry.Source,
                entry.Text));
        }

        // ESM subtitle entries (only when checkbox is checked)
        if (includeEsm && entries != null)
        {
            // Only skip ESM entries that already have a project entry (whisper/manual override)
            var projectKeys = project.Entries.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var e in entries)
            {
                if (e.TranscriptionSource != "esm" || e.SubtitleText == null)
                    continue;

                var key = $"{e.VoiceType}|{e.FormId:X8}_{e.ResponseIndex}";
                if (projectKeys.Contains(key))
                    continue; // whisper/manual transcription takes priority

                rows.Add(new ExportRow(
                    e.BsaPath,
                    e.FormId.ToString("X8"),
                    e.VoiceType,
                    e.SpeakerName,
                    e.QuestName,
                    "esm",
                    e.SubtitleText));
            }
        }

        rows.Sort((a, b) =>
        {
            var cmp = string.Compare(a.VoiceType, b.VoiceType, StringComparison.OrdinalIgnoreCase);
            return cmp != 0 ? cmp : string.Compare(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase);
        });

        return rows;
    }

    /// <summary>
    ///     Extract the 8-char hex FormID from a project key.
    ///     Handles: "voicetype|AABBCCDD_0", "AABBCCDD_0", "AABBCCDD".
    /// </summary>
    private static string ExtractFormId(string key)
    {
        // New format: voicetype|FormID_ResponseIndex
        var pipeIdx = key.IndexOf('|');
        var segment = pipeIdx >= 0 ? key[(pipeIdx + 1)..] : key;

        // Strip _ResponseIndex suffix if present
        var underIdx = segment.IndexOf('_');
        return underIdx >= 0 ? segment[..underIdx] : segment;
    }

    /// <summary>
    ///     Build a lookup from project key → BSA path for export enrichment.
    /// </summary>
    private static Dictionary<string, string>? BuildPathLookup(List<VoiceFileEntry>? entries)
    {
        if (entries == null || entries.Count == 0)
            return null;

        var lookup = new Dictionary<string, string>(entries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            var key = $"{e.VoiceType}|{e.FormId:X8}_{e.ResponseIndex}";
            lookup.TryAdd(key, e.BsaPath);
        }

        return lookup;
    }

    private static string Escape(string? value)
    {
        if (value == null)
        {
            return "";
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }

    private readonly record struct ExportRow(
        string FilePath,
        string FormId,
        string? VoiceType,
        string? Speaker,
        string? Quest,
        string Source,
        string Text);
}
