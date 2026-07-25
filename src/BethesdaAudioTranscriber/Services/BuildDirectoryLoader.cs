using BethesdaAudioTranscriber.Models;
using BethesdaMultitool.Core.Formats.Bsa.Index;

namespace BethesdaAudioTranscriber.Services;

/// <summary>
///     Loads a Fallout NV build directory: finds BSAs and ESM, parses voice files,
///     and cross-references against the ESM index.
/// </summary>
public static class BuildDirectoryLoader
{
    /// <summary>
    ///     Load all voice files from a build's Data directory.
    /// </summary>
    /// <param name="dataDirectory">
    ///     The Data directory (e.g., "Fallout New Vegas (July 21, 2010)/FalloutNV/Data/").
    /// </param>
    /// <param name="progress">Progress reporter for UI updates.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Build load result with entries and file record lookup.</returns>
    public static async Task<BuildLoadResult> LoadAsync(
        string dataDirectory,
        IProgress<(string message, double percent)>? progress = null,
        string? esmOverridePath = null,
        CancellationToken ct = default)
    {
        // Step 1: Find voice archives (BSA or BA2)
        progress?.Report(("Scanning for voice archives...", 0));
        var archivePaths = FindVoiceArchives(dataDirectory);

        if (archivePaths.Count == 0)
        {
            throw new FileNotFoundException(
                "No voice archives (Fallout - Voices*.bsa / *.ba2) found in the Data directory.");
        }

        // Step 2: Determine ESM file path (user override takes priority)
        string? esmPath;
        string? esmSourceDescription;
        if (esmOverridePath != null)
        {
            esmPath = esmOverridePath;
            esmSourceDescription = "user-provided";
        }
        else
        {
            esmPath = FindEsm(dataDirectory);
            esmSourceDescription = esmPath != null ? "auto-discovered" : null;
        }

        // Step 3: Build ESM index (if ESM exists)
        EsmLookupIndex? esmIndex = null;
        if (esmPath != null)
        {
            var esmName = Path.GetFileName(esmPath);
            progress?.Report(($"Parsing ESM ({esmSourceDescription}): {esmName}...", 10));
            esmIndex = await EsmIndexBuilder.BuildAsync(
                esmPath,
                new Progress<string>(msg => progress?.Report((msg, 20))),
                ct);
        }

        // Step 4: Parse BSAs and enumerate voice files
        var allEntries = new List<VoiceFileEntry>();
        var fileRecords = new Dictionary<string, ArchiveReader.ArchiveEntry>();
        var totalArchives = archivePaths.Count;

        for (var i = 0; i < totalArchives; i++)
        {
            ct.ThrowIfCancellationRequested();

            var archivePath = archivePaths[i];
            var archiveName = Path.GetFileName(archivePath);
            var basePercent = 30 + 60.0 * i / totalArchives;
            progress?.Report(($"Parsing {archiveName}...", basePercent));

            ParseVoiceFilesFromArchive(archivePath, allEntries, fileRecords);
        }

        // Step 5: Enrich with ESM data
        if (esmIndex != null)
        {
            progress?.Report(("Cross-referencing with ESM...", 92));
            foreach (var entry in allEntries)
            {
                esmIndex.Enrich(entry);
            }
        }

        // Note: saved transcriptions are loaded and applied by the caller
        // (PlaylistView.SetBuildResult) so migration runs on the actual _project
        // used for export. Applying here with a temporary project would set SubtitleText
        // on entries, causing the caller's ApplyToEntries to skip them all.

        progress?.Report(($"Loaded {allEntries.Count} voice files from {totalArchives} archive(s)", 100));

        var result = new BuildLoadResult
        {
            Entries = allEntries,
            FileRecords = fileRecords
        };

        // Populate ESM enrichment heuristics
        result.EsmSourceDescription = esmSourceDescription;
        if (esmIndex != null)
        {
            result.EsmInfoCount = esmIndex.InfoCount;
            result.EsmNpcCount = esmIndex.NpcCount;
            result.EsmQuestCount = esmIndex.QuestCount;
            result.EsmTopicCount = esmIndex.TopicCount;
            result.EnrichedSubtitleCount = allEntries.Count(e => e.SubtitleText != null);
            result.EnrichedSpeakerCount = allEntries.Count(e => e.SpeakerName != null);
            result.EnrichedQuestCount = allEntries.Count(e => e.QuestName != null);
        }

        return result;
    }

    private static List<string> FindVoiceArchives(string dataDirectory)
    {
        var archives = new List<string>();

        if (!Directory.Exists(dataDirectory))
        {
            return archives;
        }

        foreach (var file in Directory.EnumerateFiles(dataDirectory)
                     .Where(f => f.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase) ||
                                 f.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileName(file);
            if (name.Contains("Voices", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Voice", StringComparison.OrdinalIgnoreCase))
            {
                archives.Add(file);
            }
        }

        archives.Sort(StringComparer.OrdinalIgnoreCase);
        return archives;
    }

    private static string? FindEsm(string dataDirectory)
    {
        if (!Directory.Exists(dataDirectory))
        {
            return null;
        }

        // Look for FalloutNV.esm specifically
        var esmPath = Path.Combine(dataDirectory, "FalloutNV.esm");
        if (File.Exists(esmPath))
        {
            return esmPath;
        }

        // Fallback: any .esm file
        var esmFiles = Directory.GetFiles(dataDirectory, "*.esm");
        return esmFiles.Length > 0 ? esmFiles[0] : null;
    }

    private static void ParseVoiceFilesFromArchive(
        string archivePath,
        List<VoiceFileEntry> entries,
        Dictionary<string, ArchiveReader.ArchiveEntry> fileRecords)
    {
        using var reader = ArchiveReader.Open(archivePath);

        // Flat walk works for both a BSA folder tree and a flat BA2 list. Voice files live at
        // sound\voice\{plugin}\{voicetype}\{file}; BA2 paths may use forward slashes, so normalize.
        foreach (var file in reader.ListFiles())
        {
            var normalized = file.FullPath.Replace('/', '\\');
            if (!normalized.StartsWith(@"sound\voice\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var pathParts = normalized.Split('\\');
            if (pathParts.Length < 5)
            {
                continue;
            }

            var voiceType = pathParts[3]; // sound\voice\plugin\voicetype\file
            var fileName = pathParts[^1];

            // Only process audio files (xma, wav, mp3, ogg)
            var ext = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
            if (ext is not ("xma" or "wav" or "mp3" or "ogg"))
            {
                continue;
            }

            // Try to parse FormID from filename
            if (!VoiceFileNameParser.TryParse(fileName, out var formId, out var responseIndex,
                    out var topicEditorId))
            {
                continue;
            }

            var entry = new VoiceFileEntry
            {
                FormId = formId,
                ResponseIndex = responseIndex,
                VoiceType = voiceType,
                TopicEditorId = topicEditorId,
                Extension = ext,
                BsaPath = file.FullPath,
                BsaFilePath = archivePath
            };

            entries.Add(entry);
            fileRecords[entry.ExtractionKey] = file;
        }
    }
}
