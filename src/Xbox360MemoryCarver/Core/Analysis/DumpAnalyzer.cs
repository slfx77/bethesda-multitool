using System.Globalization;
using System.Text;
using Xbox360MemoryCarver.Core.Minidump;
using Xbox360MemoryCarver.Core.Parsers;

namespace Xbox360MemoryCarver.Core.Analysis;

/// <summary>
///     Provides advanced analysis capabilities for memory dumps.
///     Includes module correlation, ESM record extraction, and FormID mapping.
/// </summary>
public static class DumpAnalyzer
{
    /// <summary>
    ///     Full analysis results for a memory dump.
    /// </summary>
    public record AnalysisResult
    {
        public string DumpPath { get; init; } = "";
        public long DumpSize { get; init; }
        public MinidumpInfo? MinidumpInfo { get; init; }
        public string? BuildType { get; init; }
        public List<ScdaParser.ScdaRecord> ScdaRecords { get; init; } = [];
        public EsmRecordParser.EsmRecordScanResult? EsmRecords { get; init; }
        public Dictionary<uint, string> FormIdMap { get; init; } = [];
    }

    /// <summary>
    ///     Detect the build type (Debug, Release Beta, Release MemDebug) from minidump modules.
    /// </summary>
    public static string? DetectBuildType(MinidumpInfo info)
    {
        foreach (var module in info.Modules)
        {
            var name = Path.GetFileName(module.Name);
            if (name.Contains("Debug", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("MemDebug", StringComparison.OrdinalIgnoreCase))
            {
                return "Debug";
            }

            if (name.Contains("MemDebug", StringComparison.OrdinalIgnoreCase))
            {
                return "Release MemDebug";
            }

            if (name.Contains("Release_Beta", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("ReleaseBeta", StringComparison.OrdinalIgnoreCase))
            {
                return "Release Beta";
            }
        }

        // Default to Release if game exe found but no debug indicators
        if (info.Modules.Any(m => Path.GetFileName(m.Name).StartsWith("Fallout", StringComparison.OrdinalIgnoreCase)))
        {
            return "Release";
        }

        return null;
    }

    /// <summary>
    ///     Find the game executable module (Fallout*.exe).
    /// </summary>
    public static MinidumpModule? FindGameModule(MinidumpInfo info)
    {
        return info.Modules.FirstOrDefault(m =>
            Path.GetFileName(m.Name).StartsWith("Fallout", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(m.Name).EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Analyze a memory dump file with all available analyzers.
    /// </summary>
    public static async Task<AnalysisResult> AnalyzeAsync(
        string dumpPath,
        bool includeEsmRecords = true,
        bool includeFormIdMapping = true,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new AnalysisResult { DumpPath = dumpPath };

        progress?.Report("Loading dump file...");
        var fileInfo = new FileInfo(dumpPath);
        result = result with { DumpSize = fileInfo.Length };

        // Parse minidump header
        progress?.Report("Parsing minidump header...");
        var minidumpInfo = MinidumpParser.Parse(dumpPath);
        result = result with { MinidumpInfo = minidumpInfo };

        if (!minidumpInfo.IsValid)
        {
            return result;
        }

        // Detect build type
        result = result with { BuildType = DetectBuildType(minidumpInfo) };

        // Load dump data for further analysis
        progress?.Report("Loading dump data...");
        var data = await File.ReadAllBytesAsync(dumpPath, cancellationToken);

        // Scan for SCDA records (compiled scripts)
        progress?.Report("Scanning for SCDA records...");
        var scdaRecords = ScdaParser.ScanForRecords(data);
        result = result with { ScdaRecords = scdaRecords };

        // Scan for ESM records if requested
        if (includeEsmRecords)
        {
            progress?.Report("Scanning for ESM records...");
            var esmRecords = EsmRecordParser.ScanForRecords(data);
            result = result with { EsmRecords = esmRecords };

            // Build FormID map if requested
            if (includeFormIdMapping)
            {
                progress?.Report("Correlating FormIDs...");
                var formIdMap = EsmRecordParser.CorrelateFormIdsToNames(data, esmRecords);
                result = result with { FormIdMap = formIdMap };
            }
        }

        progress?.Report("Analysis complete.");
        return result;
    }

    /// <summary>
    ///     Generate a markdown report from analysis results.
    /// </summary>
    public static string GenerateReport(AnalysisResult result)
    {
        var sb = new StringBuilder();

        AppendHeader(sb, result);
        AppendModuleSection(sb, result);
        AppendScriptSection(sb, result);
        AppendEsmSection(sb, result);
        AppendFormIdSection(sb, result);

        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, AnalysisResult result)
    {
        sb.AppendLine("# Memory Dump Analysis Report");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**File**: {Path.GetFileName(result.DumpPath)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Size**: {result.DumpSize:N0} bytes ({result.DumpSize / (1024.0 * 1024.0):F2} MB)");
    }

    private static void AppendModuleSection(StringBuilder sb, AnalysisResult result)
    {
        if (result.MinidumpInfo?.IsValid != true)
        {
            return;
        }

        var info = result.MinidumpInfo;
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Architecture**: {(info.IsXbox360 ? "Xbox 360 (PowerPC)" : "Other")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Build Type**: {result.BuildType ?? "Unknown"}");
        sb.AppendLine();

        // Module summary
        sb.AppendLine("## Loaded Modules");
        sb.AppendLine();
        sb.AppendLine("| Module | Base Address | Size |");
        sb.AppendLine("|--------|-------------|------|");

        foreach (var module in info.Modules.OrderBy(m => m.BaseAddress32))
        {
            var fileName = Path.GetFileName(module.Name);
            var sizeKB = module.Size / 1024.0;
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {fileName} | 0x{module.BaseAddress32:X8} | {sizeKB:F0} KB |");
        }

        sb.AppendLine();

        // Memory summary
        var totalMemory = info.MemoryRegions.Sum(r => r.Size);
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Memory Regions**: {info.MemoryRegions.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Total Captured**: {totalMemory:N0} bytes ({totalMemory / (1024.0 * 1024.0):F2} MB)");
        sb.AppendLine();
    }

    private static void AppendScriptSection(StringBuilder sb, AnalysisResult result)
    {
        // Script summary
        sb.AppendLine("## Compiled Scripts (SCDA)");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Total SCDA Records**: {result.ScdaRecords.Count}");

        var withSource = result.ScdaRecords.Count(s => s.HasAssociatedSctx);
        sb.AppendLine(CultureInfo.InvariantCulture, $"**With Source (SCTX)**: {withSource}");
        sb.AppendLine();

        if (result.ScdaRecords.Count == 0)
        {
            return;
        }

        sb.AppendLine("| Offset | Bytecode Size | Has Source |");
        sb.AppendLine("|--------|--------------|------------|");

        foreach (var scda in result.ScdaRecords.Take(20))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| 0x{scda.Offset:X8} | {scda.BytecodeLength} bytes | {(scda.HasAssociatedSctx ? "Yes" : "No")} |");
        }

        if (result.ScdaRecords.Count > 20)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| ... | ({result.ScdaRecords.Count - 20} more) | ... |");
        }

        sb.AppendLine();
    }

    private static void AppendEsmSection(StringBuilder sb, AnalysisResult result)
    {
        // ESM records summary
        if (result.EsmRecords == null)
        {
            return;
        }

        sb.AppendLine("## ESM Records");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Editor IDs (EDID)**: {result.EsmRecords.EditorIds.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Game Settings (GMST)**: {result.EsmRecords.GameSettings.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Script Sources (SCTX)**: {result.EsmRecords.ScriptSources.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**FormID Refs (SCRO)**: {result.EsmRecords.FormIdReferences.Count}");
        sb.AppendLine();
    }

    private static void AppendFormIdSection(StringBuilder sb, AnalysisResult result)
    {
        // FormID correlations
        if (result.FormIdMap.Count == 0)
        {
            return;
        }

        sb.AppendLine("## FormID Correlations");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Mapped FormIDs**: {result.FormIdMap.Count}");
        sb.AppendLine();

        sb.AppendLine("| FormID | Editor ID |");
        sb.AppendLine("|--------|-----------|");

        foreach (var (formId, name) in result.FormIdMap.Take(30).OrderBy(kv => kv.Key))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| 0x{formId:X8} | {name} |");
        }

        if (result.FormIdMap.Count > 30)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| ... | ({result.FormIdMap.Count - 30} more) |");
        }
    }

    /// <summary>
    ///     Generate a brief text summary suitable for console output.
    /// </summary>
    public static string GenerateSummary(AnalysisResult result)
    {
        var sb = new StringBuilder();

        sb.AppendLine(CultureInfo.InvariantCulture, $"Dump: {Path.GetFileName(result.DumpPath)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Size: {result.DumpSize / (1024.0 * 1024.0):F2} MB");

        if (result.MinidumpInfo?.IsValid == true)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Build: {result.BuildType ?? "Unknown"}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Modules: {result.MinidumpInfo.Modules.Count}");

            var gameModule = FindGameModule(result.MinidumpInfo);
            if (gameModule != null)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"Game: {Path.GetFileName(gameModule.Name)} ({gameModule.Size / 1024.0:F0} KB)");
            }
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"SCDA Records: {result.ScdaRecords.Count}");

        var withSource = result.ScdaRecords.Count(s => s.HasAssociatedSctx);
        if (withSource > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"With Source: {withSource}");
        }

        if (result.EsmRecords != null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Editor IDs: {result.EsmRecords.EditorIds.Count}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"FormID Map: {result.FormIdMap.Count}");
        }

        return sb.ToString();
    }
}
