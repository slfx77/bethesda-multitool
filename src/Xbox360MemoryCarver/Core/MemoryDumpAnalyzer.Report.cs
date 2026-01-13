using System.Globalization;
using System.Text;

namespace Xbox360MemoryCarver.Core;

// Report generation methods for MemoryDumpAnalyzer
public sealed partial class MemoryDumpAnalyzer
{
    /// <summary>
    ///     Generate a markdown report from analysis results.
    /// </summary>
    public static string GenerateReport(AnalysisResult result)
    {
        var sb = new StringBuilder();

        AppendHeader(sb, result);
        AppendCarvedFilesSection(sb, result);
        AppendModuleSection(sb, result);
        AppendScriptSection(sb, result);
        AppendEsmSection(sb, result);
        AppendFormIdSection(sb, result);

        return sb.ToString();
    }

    /// <summary>
    ///     Generate a brief text summary suitable for console output.
    /// </summary>
    public static string GenerateSummary(AnalysisResult result)
    {
        var sb = new StringBuilder();

        sb.AppendLine(CultureInfo.InvariantCulture, $"Dump: {Path.GetFileName(result.FilePath)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Size: {result.FileSize / (1024.0 * 1024.0):F2} MB");

        if (result.MinidumpInfo?.IsValid == true)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Build: {result.BuildType ?? "Unknown"}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Modules: {result.MinidumpInfo.Modules.Count}");

            var gameModule = FindGameModule(result.MinidumpInfo);
            if (gameModule != null)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"Game: {Path.GetFileName(gameModule.Name)} ({gameModule.Size / 1024.0:F0} KB)");
        }

        if (result.CarvedFiles.Count > 0)
            sb.AppendLine(CultureInfo.InvariantCulture, $"Carved Files: {result.CarvedFiles.Count}");

        if (result.ScdaRecords.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"SCDA Records: {result.ScdaRecords.Count}");
            var withSource = result.ScdaRecords.Count(s => s.HasAssociatedSctx);
            if (withSource > 0) sb.AppendLine(CultureInfo.InvariantCulture, $"With Source: {withSource}");
        }

        if (result.EsmRecords != null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Editor IDs: {result.EsmRecords.EditorIds.Count}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"FormID Map: {result.FormIdMap.Count}");
        }

        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, AnalysisResult result)
    {
        sb.AppendLine("# Memory Dump Analysis Report");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**File**: {Path.GetFileName(result.FilePath)}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"**Size**: {result.FileSize:N0} bytes ({result.FileSize / (1024.0 * 1024.0):F2} MB)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Analysis Time**: {result.AnalysisTime.TotalSeconds:F2}s");
    }

    private static void AppendCarvedFilesSection(StringBuilder sb, AnalysisResult result)
    {
        if (result.CarvedFiles.Count == 0) return;

        sb.AppendLine();
        sb.AppendLine("## Carved Files Summary");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Total Files**: {result.CarvedFiles.Count}");
        sb.AppendLine();

        // Group by type
        var byType = result.TypeCounts.OrderByDescending(kv => kv.Value).ToList();
        sb.AppendLine("| File Type | Count |");
        sb.AppendLine("|-----------|-------|");
        foreach (var (type, count) in byType) sb.AppendLine(CultureInfo.InvariantCulture, $"| {type} | {count} |");

        sb.AppendLine();
    }

    private static void AppendModuleSection(StringBuilder sb, AnalysisResult result)
    {
        if (result.MinidumpInfo?.IsValid != true) return;

        var info = result.MinidumpInfo;
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"**Architecture**: {(info.IsXbox360 ? "Xbox 360 (PowerPC)" : "Other")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Build Type**: {result.BuildType ?? "Unknown"}");
        sb.AppendLine();

        sb.AppendLine("## Loaded Modules");
        sb.AppendLine();
        sb.AppendLine("| Module | Base Address | Size |");
        sb.AppendLine("|--------|-------------|------|");

        foreach (var module in info.Modules.OrderBy(m => m.BaseAddress32))
        {
            var fileName = Path.GetFileName(module.Name);
            var sizeKb = module.Size / 1024.0;
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {fileName} | 0x{module.BaseAddress32:X8} | {sizeKb:F0} KB |");
        }

        sb.AppendLine();

        var totalMemory = info.MemoryRegions.Sum(r => r.Size);
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Memory Regions**: {info.MemoryRegions.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"**Total Captured**: {totalMemory:N0} bytes ({totalMemory / (1024.0 * 1024.0):F2} MB)");
        sb.AppendLine();
    }

    private static void AppendScriptSection(StringBuilder sb, AnalysisResult result)
    {
        sb.AppendLine("## Compiled Scripts (SCDA)");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Total SCDA Records**: {result.ScdaRecords.Count}");

        var withSource = result.ScdaRecords.Count(s => s.HasAssociatedSctx);
        var withNames = result.ScdaRecords.Count(s => !string.IsNullOrEmpty(s.ScriptName));
        sb.AppendLine(CultureInfo.InvariantCulture, $"**With Source (SCTX)**: {withSource}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**With Script Names**: {withNames}");
        sb.AppendLine();

        if (result.ScdaRecords.Count == 0) return;

        sb.AppendLine("| Offset | Script Name | Bytecode Size | Has Source |");
        sb.AppendLine("|--------|-------------|--------------|------------|");

        foreach (var scda in result.ScdaRecords.Take(30))
        {
            var name = !string.IsNullOrEmpty(scda.ScriptName) ? scda.ScriptName : "-";
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| 0x{scda.Offset:X8} | {name} | {scda.BytecodeLength} bytes | {(scda.HasAssociatedSctx ? "Yes" : "No")} |");
        }

        if (result.ScdaRecords.Count > 30)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| ... | ... | ({result.ScdaRecords.Count - 30} more) | ... |");

        sb.AppendLine();
    }

    private static void AppendEsmSection(StringBuilder sb, AnalysisResult result)
    {
        if (result.EsmRecords == null) return;

        sb.AppendLine("## ESM Records");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Editor IDs (EDID)**: {result.EsmRecords.EditorIds.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"**Game Settings (GMST)**: {result.EsmRecords.GameSettings.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"**Script Sources (SCTX)**: {result.EsmRecords.ScriptSources.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"**FormID Refs (SCRO)**: {result.EsmRecords.FormIdReferences.Count}");
        sb.AppendLine();
    }

    private static void AppendFormIdSection(StringBuilder sb, AnalysisResult result)
    {
        if (result.FormIdMap.Count == 0) return;

        sb.AppendLine("## FormID Correlations");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Mapped FormIDs**: {result.FormIdMap.Count}");
        sb.AppendLine();

        sb.AppendLine("| FormID | Editor ID |");
        sb.AppendLine("|--------|-----------|");

        foreach (var (formId, name) in result.FormIdMap.Take(30).OrderBy(kv => kv.Key))
            sb.AppendLine(CultureInfo.InvariantCulture, $"| 0x{formId:X8} | {name} |");

        if (result.FormIdMap.Count > 30)
            sb.AppendLine(CultureInfo.InvariantCulture, $"| ... | ({result.FormIdMap.Count - 30} more) |");
    }
}
