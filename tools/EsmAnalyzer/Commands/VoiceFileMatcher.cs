using System.Globalization;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Esm;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Data types and matching logic for voice heuristics analysis.
/// </summary>
internal static class VoiceFileMatcher
{
    internal readonly record struct VoiceFile(uint FormId, string TopicEditorId, string VoiceType, string FileName);

    internal readonly record struct InfoData(bool HasNam1, uint? AnamFormId, uint? QstiFormId);

    internal readonly record struct DialData(uint? QstiFormId, uint? TnamFormId);

    /// <summary>
    ///     Aggregated results from matching voice files against ESM records.
    /// </summary>
    internal sealed class MatchResults
    {
        public int FormIdMatched;
        public int FormIdWithNam1;
        public int FormIdWithAnam;
        public int FormIdWithQsti;
        public List<(uint FormId, string TopicEditorId)> UnmatchedFormIds = [];

        public int TopicsCaseSensitive;
        public int TopicsCaseInsensitive;
        public int TopicsWithQsti;
        public int TopicsWithTnam;
        public List<string> UnmatchedTopics = [];

        public int VtypMatchedUnique;
        public int VtypMatchedShared;
        public int VtypUnmatched;

        public int EnrichedSubtitle;
        public int EnrichedSpeaker;
        public int EnrichedSpeakerViaVtyp;
        public int EnrichedQuest;
        public int EnrichedQuestViaPrefix;
    }

    /// <summary>
    ///     Parse voice filename: {topicEditorId}_{formId:8hex}_{index}.{ext}
    ///     Inlined from FalloutAudioTranscriber.Models.VoiceFileNameParser (can't reference WinUI project).
    /// </summary>
    internal static bool TryParseVoiceFileName(string fileName, out uint formId, out int responseIndex,
        out string topicEditorId)
    {
        formId = 0;
        responseIndex = 0;
        topicEditorId = "";

        var dotIndex = fileName.LastIndexOf('.');
        if (dotIndex < 0) return false;

        var baseName = fileName[..dotIndex];

        var lastUnderscore = baseName.LastIndexOf('_');
        if (lastUnderscore < 0) return false;

        if (!int.TryParse(baseName[(lastUnderscore + 1)..], out responseIndex)) return false;

        var formIdUnderscore = baseName.LastIndexOf('_', lastUnderscore - 1);
        if (formIdUnderscore < 0) return false;

        var formIdPart = baseName[(formIdUnderscore + 1)..lastUnderscore];
        if (formIdPart.Length != 8 ||
            !uint.TryParse(formIdPart, NumberStyles.HexNumber, null, out formId))
        {
            return false;
        }

        topicEditorId = baseName[..formIdUnderscore];
        return true;
    }

    internal static string? FindEsm(string dataDir)
    {
        var esmPath = Path.Combine(dataDir, "FalloutNV.esm");
        if (File.Exists(esmPath)) return esmPath;

        var esmFiles = Directory.GetFiles(dataDir, "*.esm");
        return esmFiles.Length > 0 ? esmFiles[0] : null;
    }

    internal static string Frac(int n, int total)
    {
        if (total == 0) return "N/A";
        return $"{n:N0} / {total:N0} ({100.0 * n / total:F1}%)";
    }

    /// <summary>
    ///     Extract voice files from BSA archives under the data directory.
    /// </summary>
    internal static List<VoiceFile> ExtractVoiceFiles(List<string> bsaPaths)
    {
        var voiceFiles = new List<VoiceFile>();

        foreach (var bsaPath in bsaPaths)
        {
            var archive = BsaParser.Parse(bsaPath);
            foreach (var folder in archive.Folders)
            {
                if (folder.Name == null ||
                    !folder.Name.StartsWith(@"sound\voice\", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var pathParts = folder.Name.Split('\\');
                if (pathParts.Length < 4)
                {
                    continue;
                }

                var voiceType = pathParts[3];

                foreach (var file in folder.Files)
                {
                    if (file.Name == null)
                    {
                        continue;
                    }

                    var ext = Path.GetExtension(file.Name).TrimStart('.').ToLowerInvariant();
                    if (ext is not ("xma" or "wav" or "mp3" or "ogg"))
                    {
                        continue;
                    }

                    if (TryParseVoiceFileName(file.Name, out var formId, out _, out var topicEditorId))
                    {
                        voiceFiles.Add(new VoiceFile(formId, topicEditorId, voiceType, file.Name));
                    }
                }
            }
        }

        return voiceFiles;
    }

    /// <summary>
    ///     Compute all matching metrics between voice files and ESM data.
    /// </summary>
    internal static MatchResults ComputeMatches(
        List<VoiceFile> voiceFiles,
        HashSet<uint> uniqueFormIds,
        HashSet<string> uniqueTopics,
        Dictionary<uint, InfoData> infoEntries,
        Dictionary<string, DialData> dialEntries,
        HashSet<uint> npcFormIds,
        HashSet<uint> questFormIds,
        Dictionary<string, string> questEdidToName,
        Dictionary<string, List<string>> voiceTypeToNpcs)
    {
        var results = new MatchResults();

        // FormID -> INFO matching
        foreach (var vf in voiceFiles)
        {
            if (infoEntries.TryGetValue(vf.FormId, out var info))
            {
                results.FormIdMatched++;
                if (info.HasNam1) results.FormIdWithNam1++;
                if (info.AnamFormId.HasValue) results.FormIdWithAnam++;
                if (info.QstiFormId.HasValue) results.FormIdWithQsti++;
            }
            else
            {
                results.UnmatchedFormIds.Add((vf.FormId, vf.TopicEditorId));
            }
        }

        // TopicEditorId -> DIAL matching
        var dialEdidsCaseSensitive = new HashSet<string>(dialEntries.Keys, StringComparer.Ordinal);

        foreach (var topic in uniqueTopics)
        {
            if (dialEdidsCaseSensitive.Contains(topic))
            {
                results.TopicsCaseSensitive++;
            }

            if (dialEntries.TryGetValue(topic, out var dial))
            {
                results.TopicsCaseInsensitive++;
                if (dial.QstiFormId.HasValue) results.TopicsWithQsti++;
                if (dial.TnamFormId.HasValue) results.TopicsWithTnam++;
            }
            else
            {
                results.UnmatchedTopics.Add(topic);
            }
        }

        // VoiceType -> unique NPC matching
        foreach (var vf in voiceFiles)
        {
            if (vf.VoiceType.Length > 0 && voiceTypeToNpcs.TryGetValue(vf.VoiceType, out var npcs))
            {
                if (npcs.Count == 1)
                {
                    results.VtypMatchedUnique++;
                }
                else
                {
                    results.VtypMatchedShared++;
                }
            }
            else if (vf.VoiceType.Length > 0)
            {
                results.VtypUnmatched++;
            }
        }

        // Combined enrichment simulation
        foreach (var vf in voiceFiles)
        {
            var hasSubtitle = false;
            var hasSpeaker = false;
            var hasQuest = false;

            // Primary: INFO lookup
            if (infoEntries.TryGetValue(vf.FormId, out var info2))
            {
                if (info2.HasNam1) hasSubtitle = true;
                if (info2.AnamFormId.HasValue && npcFormIds.Contains(info2.AnamFormId.Value)) hasSpeaker = true;
                if (info2.QstiFormId.HasValue && questFormIds.Contains(info2.QstiFormId.Value)) hasQuest = true;
            }

            // Fallback 1: DIAL topic lookup (case-insensitive)
            if (vf.TopicEditorId.Length > 0 && dialEntries.TryGetValue(vf.TopicEditorId, out var dial2))
            {
                if (!hasSpeaker && dial2.TnamFormId.HasValue && npcFormIds.Contains(dial2.TnamFormId.Value))
                {
                    hasSpeaker = true;
                }

                if (!hasQuest && dial2.QstiFormId.HasValue && questFormIds.Contains(dial2.QstiFormId.Value))
                {
                    hasQuest = true;
                }
            }

            // Fallback 2: VoiceType -> unique NPC speaker
            if (!hasSpeaker && vf.VoiceType.Length > 0 &&
                voiceTypeToNpcs.TryGetValue(vf.VoiceType, out var vtypNpcs) && vtypNpcs.Count == 1)
            {
                hasSpeaker = true;
                results.EnrichedSpeakerViaVtyp++;
            }

            // Fallback 3: Quest EDID prefix from filename
            if (!hasQuest && vf.TopicEditorId.Length > 0)
            {
                var usIdx = vf.TopicEditorId.IndexOf('_');
                if (usIdx > 0 && questEdidToName.ContainsKey(vf.TopicEditorId[..usIdx]))
                {
                    hasQuest = true;
                    results.EnrichedQuestViaPrefix++;
                }
            }

            if (hasSubtitle) results.EnrichedSubtitle++;
            if (hasSpeaker) results.EnrichedSpeaker++;
            if (hasQuest) results.EnrichedQuest++;
        }

        return results;
    }
}
