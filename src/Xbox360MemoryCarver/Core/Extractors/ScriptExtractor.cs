using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Xbox360MemoryCarver.Core.Parsers;

namespace Xbox360MemoryCarver.Core.Extractors;

/// <summary>
///     Extracts and groups compiled script bytecode (SCDA records) from memory dumps.
///     Groups related quest stage scripts together for easier analysis.
/// </summary>
public static partial class ScriptExtractor
{
    /// <summary>
    ///     Summary of script extraction results.
    /// </summary>
    public record ScriptExtractionResult
    {
        public int TotalRecords { get; init; }
        public int GroupedQuests { get; init; }
        public int UngroupedScripts { get; init; }
        public int TotalBytecodeBytes { get; init; }
        public int RecordsWithSource { get; init; }
    }

    /// <summary>
    ///     Extract all SCDA records from a dump, grouping by quest name.
    /// </summary>
    public static async Task<ScriptExtractionResult> ExtractGroupedAsync(
        byte[] dumpData,
        string outputDir,
        IProgress<string>? progress = null,
        bool verbose = false)
    {
        Directory.CreateDirectory(outputDir);

        progress?.Report("Scanning for SCDA records...");
        var records = ScdaParser.ScanForRecords(dumpData);

        if (records.Count == 0)
        {
            return new ScriptExtractionResult();
        }

        progress?.Report($"Found {records.Count} SCDA records, grouping by quest...");

        // First pass: Group records by detected quest name
        var groups = new Dictionary<string, List<ScdaParser.ScdaRecord>>();
        var ungrouped = new List<ScdaParser.ScdaRecord>();

        foreach (var record in records)
        {
            var questName = ExtractQuestName(record.SourceText);

            if (!string.IsNullOrEmpty(questName))
            {
                if (!groups.TryGetValue(questName, out var list))
                {
                    list = [];
                    groups[questName] = list;
                }
                list.Add(record);
            }
            else
            {
                ungrouped.Add(record);
            }
        }

        // Second pass: Try to assign ungrouped scripts by offset proximity
        // If an ungrouped script falls within the offset range of a group, assign it there
        var stillUngrouped = new List<ScdaParser.ScdaRecord>();
        foreach (var record in ungrouped)
        {
            string? assignedGroup = null;

            foreach (var (questName, stages) in groups)
            {
                if (stages.Count < 2) continue;

                var minOffset = stages.Min(s => s.Offset);
                var maxOffset = stages.Max(s => s.Offset);

                // Check if this record falls within the offset range (with some padding)
                if (record.Offset >= minOffset && record.Offset <= maxOffset)
                {
                    assignedGroup = questName;
                    break;
                }
            }

            if (assignedGroup != null)
            {
                groups[assignedGroup].Add(record);
            }
            else
            {
                stillUngrouped.Add(record);
            }
        }

        // Sort each group by offset
        foreach (var list in groups.Values)
        {
            list.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        }

        ungrouped = stillUngrouped;
        progress?.Report($"Grouped into {groups.Count} quests, {ungrouped.Count} ungrouped");

        // Write grouped files
        foreach (var (questName, stages) in groups.OrderBy(g => g.Value[0].Offset))
        {
            var scriptPath = Path.Combine(outputDir, $"{SanitizeFilename(questName)}_stages.txt");
            var content = FormatGroupedScript(questName, stages);
            await File.WriteAllTextAsync(scriptPath, content);

            if (verbose)
            {
                Console.WriteLine($"  [Script] {questName}: {stages.Count} stages ({stages.Sum(s => s.BytecodeLength)} bytes)");
            }
        }

        // Write ungrouped as individual files
        for (int i = 0; i < ungrouped.Count; i++)
        {
            var record = ungrouped[i];
            var baseName = ExtractScriptNameFromSource(record.SourceText) ?? $"unknown_{i:D3}";
            var scriptPath = Path.Combine(outputDir, $"ungrouped_{SanitizeFilename(baseName)}.txt");
            var content = FormatSingleScript(record);
            await File.WriteAllTextAsync(scriptPath, content);
        }

        return new ScriptExtractionResult
        {
            TotalRecords = records.Count,
            GroupedQuests = groups.Count,
            UngroupedScripts = ungrouped.Count,
            TotalBytecodeBytes = records.Sum(r => r.BytecodeLength),
            RecordsWithSource = records.Count(r => r.HasAssociatedSctx)
        };
    }

    /// <summary>
    ///     Extract quest/script name from source text by looking for patterns like "QuestName.variable".
    /// </summary>
    private static string? ExtractQuestName(string? source)
    {
        if (string.IsNullOrEmpty(source))
            return null;

        // Look for patterns like "VMS03.nPowerConfiguration", "VCG01.fTimer", etc.
        var match = QuestNamePattern().Match(source);
        if (match.Success)
            return match.Groups[1].Value;

        // Look for quest names in commands like "SetObjectiveDisplayed VFreeformCampGolf 10 1"
        match = CommandQuestPattern().Match(source);
        if (match.Success)
            return match.Groups[1].Value;

        // Also try patterns with .variable access
        match = VariableAccessPattern().Match(source);
        if (match.Success)
        {
            var name = match.Groups[1].Value;
            // Filter out common non-quest prefixes
            if (!name.StartsWith("player", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("Get", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith("Set", StringComparison.OrdinalIgnoreCase) &&
                name.Length > 3)
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>
    ///     Extract a simple script name from source text for ungrouped scripts.
    /// </summary>
    private static string? ExtractScriptNameFromSource(string? source)
    {
        if (string.IsNullOrEmpty(source))
            return null;

        // Look for common patterns like "set XXX", "Get XXX", etc.
        var patterns = new[] { "set ", "Set ", "Get", "If ", "if " };
        foreach (var p in patterns)
        {
            var idx = source.IndexOf(p, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var start = idx + p.Length;
                var end = source.IndexOfAny([' ', '.', '(', '\r', '\n'], start);
                if (end > start)
                {
                    var name = source[start..end];
                    if (name.Length > 3 && name.Length < 50)
                        return name;
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Format a grouped quest script with all stages.
    /// </summary>
    private static string FormatGroupedScript(string questName, List<ScdaParser.ScdaRecord> stages)
    {
        var sb = new StringBuilder();

        sb.AppendLine(CultureInfo.InvariantCulture, $"; Quest: {questName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"; Stage count: {stages.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"; Total bytecode: {stages.Sum(s => s.BytecodeLength)} bytes");
        sb.AppendLine(CultureInfo.InvariantCulture, $"; Offset range: 0x{stages[0].Offset:X8} - 0x{stages[^1].Offset:X8}");
        sb.AppendLine();

        int stageNum = 0;
        foreach (var stage in stages)
        {
            stageNum++;
            sb.AppendLine("; ═══════════════════════════════════════════════════════════════");
            sb.AppendLine(CultureInfo.InvariantCulture, $"; Stage {stageNum} - SCDA at 0x{stage.Offset:X8} ({stage.BytecodeLength} bytes)");
            sb.AppendLine("; ═══════════════════════════════════════════════════════════════");

            if (stage.FormIdReferences.Count > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"; SCRO References: {string.Join(", ", stage.FormIdReferences.Select((id, i) => $"#{i + 1}=0x{id:X8}"))}");
            }

            if (stage.HasAssociatedSctx)
            {
                sb.AppendLine();
                sb.AppendLine("; Source:");
                foreach (var line in stage.SourceText!.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
                {
                    sb.Append(";   ").AppendLine(line.Trim());
                }
            }

            sb.AppendLine();
            sb.AppendLine("; Bytecode (hex):");
            sb.Append(";   ").AppendLine(Convert.ToHexString(stage.Bytecode));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Format a single ungrouped script.
    /// </summary>
    private static string FormatSingleScript(ScdaParser.ScdaRecord record)
    {
        var sb = new StringBuilder();

        sb.AppendLine("; Ungrouped script");
        sb.AppendLine(CultureInfo.InvariantCulture, $"; SCDA offset: 0x{record.Offset:X8}, size: {record.BytecodeLength} bytes");

        if (record.FormIdReferences.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"; SCRO References ({record.FormIdReferences.Count}):");
            for (int j = 0; j < record.FormIdReferences.Count; j++)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $";   SCRO#{j + 1} = FormID 0x{record.FormIdReferences[j]:X8}");
            }
        }
        sb.AppendLine();

        if (record.HasAssociatedSctx)
        {
            sb.AppendLine("; === Original Source ===");
            sb.AppendLine(record.SourceText);
            sb.AppendLine();
        }

        sb.AppendLine("; === Bytecode (hex) ===");
        sb.AppendLine(Convert.ToHexString(record.Bytecode));

        return sb.ToString();
    }

    private static string SanitizeFilename(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"\b(V(?:MS|CG|Free|Dialogue|ES|MQ)\d{2,4}[A-Za-z0-9]*|NVDLC\d+[A-Za-z0-9]+)\b", RegexOptions.Compiled)]
    private static partial Regex QuestNamePattern();

    // Pattern for quest names in commands like "SetObjectiveDisplayed VFreeformCampGolf 10 1"
    [GeneratedRegex(@"(?:SetObjective\w+|setstage|CompleteQuest|StartQuest)\s+(V[A-Za-z]+[A-Za-z0-9]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex CommandQuestPattern();

    [GeneratedRegex(@"([A-Z][a-zA-Z0-9]+)\.[a-z][A-Za-z0-9]+", RegexOptions.Compiled)]
    private static partial Regex VariableAccessPattern();
}
