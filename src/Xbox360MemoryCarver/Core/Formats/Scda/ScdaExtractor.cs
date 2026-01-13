using System.Text;
using System.Text.RegularExpressions;

namespace Xbox360MemoryCarver.Core.Formats.Scda;

/// <summary>
///     Extracts and groups compiled script bytecode (SCDA records) from memory dumps.
///     Groups related quest stage scripts together for easier analysis.
/// </summary>
public static partial class ScdaExtractor
{
    private static readonly Logger Log = Logger.Instance;
    /// <summary>
    ///     Extract all SCDA records from a dump, grouping by quest name.
    /// </summary>
    public static async Task<ScdaExtractionResult> ExtractGroupedAsync(
        byte[] dumpData,
        string outputDir,
        IProgress<string>? progress = null,
        string? opcodeTablePath = null)
    {
        Directory.CreateDirectory(outputDir);

        // Initialize decompiler with opcode table
        progress?.Report("Initializing decompiler...");
        await ScdaFormatter.InitializeAsync(opcodeTablePath);

        progress?.Report("Scanning for SCDA records...");
        var records = ScdaFormat.ScanForRecords(dumpData);

        if (records.Records.Count == 0) return new ScdaExtractionResult();

        progress?.Report($"Found {records.Records.Count} SCDA records, grouping by quest...");

        var (groups, ungrouped) = GroupRecordsByQuest(records.Records);
        progress?.Report($"Grouped into {groups.Count} quests, {ungrouped.Count} ungrouped");

        await WriteGroupedFilesAsync(groups, outputDir);
        await WriteUngroupedFilesAsync(ungrouped, outputDir);

        // Build script info list for analysis
        var scripts = BuildScriptInfoList(groups, ungrouped);

        return new ScdaExtractionResult
        {
            TotalRecords = records.Records.Count,
            GroupedQuests = groups.Count,
            UngroupedScripts = ungrouped.Count,
            TotalBytecodeBytes = records.Records.Sum(r => r.BytecodeLength),
            RecordsWithSource = records.Records.Count(r => r.HasAssociatedSctx),
            Scripts = scripts
        };
    }

    private static List<ScriptInfo> BuildScriptInfoList(
        Dictionary<string, List<ScdaRecord>> groups,
        List<ScdaRecord> ungrouped)
    {
        var scripts = new List<ScriptInfo>();

        // Add grouped scripts with quest names
        foreach (var (questName, records) in groups)
            foreach (var record in records)
            {
                var scriptName = ExtractScriptNameFromSource(record.SourceText);
                scripts.Add(new ScriptInfo
                {
                    Offset = record.Offset,
                    BytecodeSize = record.BytecodeLength,
                    ScriptName = scriptName,
                    QuestName = questName,
                    HasSource = record.HasAssociatedSctx
                });
            }

        // Add ungrouped scripts
        foreach (var record in ungrouped)
        {
            var scriptName = ExtractScriptNameFromSource(record.SourceText);
            scripts.Add(new ScriptInfo
            {
                Offset = record.Offset,
                BytecodeSize = record.BytecodeLength,
                ScriptName = scriptName,
                QuestName = null,
                HasSource = record.HasAssociatedSctx
            });
        }

        // Sort by offset
        scripts.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return scripts;
    }

    private static (Dictionary<string, List<ScdaRecord>> Groups, List<ScdaRecord> Ungrouped) GroupRecordsByQuest(
        List<ScdaRecord> records)
    {
        var groups = new Dictionary<string, List<ScdaRecord>>();
        var ungrouped = new List<ScdaRecord>();

        // First pass: Group by detected quest name
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

        // Second pass: Assign ungrouped by proximity
        ungrouped = AssignByProximity(groups, ungrouped);

        // Sort each group by offset
        foreach (var list in groups.Values) list.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        return (groups, ungrouped);
    }

    private static List<ScdaRecord> AssignByProximity(
        Dictionary<string, List<ScdaRecord>> groups,
        List<ScdaRecord> ungrouped)
    {
        var stillUngrouped = new List<ScdaRecord>();

        foreach (var record in ungrouped)
        {
            var assignedGroup = FindGroupByOffset(groups, record.Offset);

            if (assignedGroup != null)
                groups[assignedGroup].Add(record);
            else
                stillUngrouped.Add(record);
        }

        return stillUngrouped;
    }

    private static string? FindGroupByOffset(Dictionary<string, List<ScdaRecord>> groups, long offset)
    {
        foreach (var (questName, stages) in groups)
        {
            if (stages.Count < 2) continue;

            var minOffset = stages.Min(s => s.Offset);
            var maxOffset = stages.Max(s => s.Offset);

            if (offset >= minOffset && offset <= maxOffset) return questName;
        }

        return null;
    }

    private static async Task WriteGroupedFilesAsync(
        Dictionary<string, List<ScdaRecord>> groups,
        string outputDir)
    {
        foreach (var (questName, stages) in groups.OrderBy(g => g.Value[0].Offset))
        {
            var scriptPath = Path.Combine(outputDir, $"{SanitizeFilename(questName)}_stages.txt");
            var content = ScdaFormatter.FormatGroupedScript(questName, stages);
            await File.WriteAllTextAsync(scriptPath, content);

            Log.Debug(
                $"  [Script] {questName}: {stages.Count} stages ({stages.Sum(s => s.BytecodeLength)} bytes)");
        }
    }

    private static async Task WriteUngroupedFilesAsync(List<ScdaRecord> ungrouped, string outputDir)
    {
        foreach (var record in ungrouped)
        {
            // Use script name from source if available, otherwise use offset as hex identifier
            var baseName = ExtractScriptNameFromSource(record.SourceText) ?? $"{record.Offset:X8}";
            var scriptPath = Path.Combine(outputDir, $"{SanitizeFilename(baseName)}.txt");
            var content = ScdaFormatter.FormatSingleScript(record);
            await File.WriteAllTextAsync(scriptPath, content);
        }
    }

    private static string SanitizeFilename(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in name) sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString();
    }

    [GeneratedRegex(@"\b(V(?:MS|CG|Free|Dialogue|ES|MQ)\d{2,4}[A-Za-z0-9]*|NVDLC\d+[A-Za-z0-9]+)\b",
        RegexOptions.Compiled)]
    private static partial Regex QuestNamePattern();

    [GeneratedRegex(@"(?:SetObjective\w+|setstage|CompleteQuest|StartQuest)\s+(V[A-Za-z]+[A-Za-z0-9]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex CommandQuestPattern();

    [GeneratedRegex(@"([A-Z][a-zA-Z0-9]+)\.[a-z][A-Za-z0-9]+", RegexOptions.Compiled)]
    private static partial Regex VariableAccessPattern();

    #region Quest Name Extraction

    private static string? ExtractQuestName(string? source)
    {
        if (string.IsNullOrEmpty(source)) return null;

        // Try quest variable pattern (e.g., "VMS03.nPowerConfiguration")
        var match = QuestNamePattern().Match(source);
        if (match.Success) return match.Groups[1].Value;

        // Try command pattern (e.g., "SetObjectiveDisplayed VFreeformCampGolf 10 1")
        match = CommandQuestPattern().Match(source);
        if (match.Success) return match.Groups[1].Value;

        // Try variable access pattern
        return TryExtractFromVariableAccess(source);
    }

    private static string? TryExtractFromVariableAccess(string source)
    {
        var match = VariableAccessPattern().Match(source);
        if (!match.Success) return null;

        var name = match.Groups[1].Value;
        if (IsValidQuestPrefix(name)) return name;
        return null;
    }

    private static bool IsValidQuestPrefix(string name)
    {
        return name.Length > 3 &&
               !name.StartsWith("player", StringComparison.OrdinalIgnoreCase) &&
               !name.StartsWith("Get", StringComparison.OrdinalIgnoreCase) &&
               !name.StartsWith("Set", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Public wrapper for extracting script name from source text.
    /// </summary>
    public static string? ExtractScriptNameFromSourcePublic(string? source)
    {
        return ExtractScriptNameFromSource(source);
    }

    private static string? ExtractScriptNameFromSource(string? source)
    {
        if (string.IsNullOrEmpty(source)) return null;

        var patterns = new[] { "set ", "Set ", "Get", "If ", "if " };
        foreach (var p in patterns)
        {
            var name = TryExtractNameAfterPattern(source, p);
            if (name != null) return name;
        }

        return null;
    }

    private static string? TryExtractNameAfterPattern(string source, string pattern)
    {
        var idx = source.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var start = idx + pattern.Length;
        var end = source.IndexOfAny([' ', '.', '(', '\r', '\n'], start);
        if (end <= start) return null;

        var name = source[start..end];
        return name.Length > 3 && name.Length < 50 ? name : null;
    }

    #endregion
}
