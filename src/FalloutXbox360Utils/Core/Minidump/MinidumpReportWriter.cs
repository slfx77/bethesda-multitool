using System.Globalization;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Minidump;

/// <summary>
///     Generates markdown reports and text summaries from minidump analysis results.
/// </summary>
internal static class MinidumpReportWriter
{
    /// <summary>
    ///     Generate a markdown report from analysis results.
    /// </summary>
    internal static string GenerateReport(AnalysisResult result)
    {
        var sb = new StringBuilder();

        AppendHeader(sb, result);
        AppendCarvedFilesSection(sb, result);
        AppendModuleSection(sb, result);
        AppendEsmSection(sb, result);
        AppendFormIdSection(sb, result);

        return sb.ToString();
    }

    /// <summary>
    ///     Generate a brief text summary suitable for console output.
    /// </summary>
    internal static string GenerateSummary(AnalysisResult result)
    {
        var sb = new StringBuilder();

        sb.AppendLine(CultureInfo.InvariantCulture, $"Dump: {Path.GetFileName(result.FilePath)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Size: {result.FileSize / (1024.0 * 1024.0):F2} MB");

        if (result.MinidumpInfo?.IsValid == true)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Build: {result.BuildType ?? "Unknown"}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Modules: {result.MinidumpInfo.Modules.Count}");

            var gameModule = MinidumpAnalyzer.FindGameModule(result.MinidumpInfo);
            if (gameModule != null)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"Game: {Path.GetFileName(gameModule.Name)} ({gameModule.Size / 1024.0:F0} KB)");
            }
        }

        if (result.CarvedFiles.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Carved Files: {result.CarvedFiles.Count}");
        }

        if (result.EsmRecords != null)
        {
            AppendEsmSummary(sb, result.EsmRecords, result.FormIdMap.Count);
        }

        return sb.ToString();
    }

    private static void AppendEsmSummary(StringBuilder sb, EsmRecordScanResult esm, int formIdMapCount)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"Editor IDs: {esm.EditorIds.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"FormID Map: {formIdMapCount}");

        if (esm.MainRecords.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"Main Records: {esm.MainRecords.Count} (LE: {esm.LittleEndianRecords}, BE: {esm.BigEndianRecords})");
        }

        AppendSubrecordCounts(sb, "Extended",
            ("NAME", esm.NameReferences.Count),
            ("DATA/Pos", esm.Positions.Count),
            ("ACBS", esm.ActorBases.Count));

        AppendSubrecordCounts(sb, "Dialogue",
            ("NAM1", esm.ResponseTexts.Count),
            ("TRDT", esm.ResponseData.Count));

        AppendSubrecordCounts(sb, "Text",
            ("FULL", esm.FullNames.Count),
            ("DESC", esm.Descriptions.Count),
            ("MODL", esm.ModelPaths.Count),
            ("ICON", esm.IconPaths.Count),
            ("TX*", esm.TexturePaths.Count));

        AppendSubrecordCounts(sb, "Refs",
            ("SCRI", esm.ScriptRefs.Count),
            ("ENAM", esm.EffectRefs.Count),
            ("SNAM", esm.SoundRefs.Count),
            ("QNAM", esm.QuestRefs.Count));

        if (esm.Conditions.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Conditions: CTDA={esm.Conditions.Count}");
        }
    }

    private static void AppendSubrecordCounts(StringBuilder sb, string label, params (string Name, int Count)[] counts)
    {
        var nonZero = counts.Where(c => c.Count > 0).Select(c => $"{c.Name}={c.Count}").ToList();
        if (nonZero.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{label}: {string.Join(", ", nonZero)}");
        }
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
        if (result.CarvedFiles.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("## Carved Files Summary");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Total Files**: {result.CarvedFiles.Count}");
        sb.AppendLine();

        // Group by type
        var byType = result.TypeCounts.OrderByDescending(kv => kv.Value).ToList();
        sb.AppendLine("| File Type | Count |");
        sb.AppendLine("|-----------|-------|");
        foreach (var (type, count) in byType)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {type} | {count} |");
        }

        sb.AppendLine();
    }

    private static void AppendModuleSection(StringBuilder sb, AnalysisResult result)
    {
        if (result.MinidumpInfo?.IsValid != true)
        {
            return;
        }

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

    private static void AppendEsmSection(StringBuilder sb, AnalysisResult result)
    {
        if (result.EsmRecords == null)
        {
            return;
        }

        var esm = result.EsmRecords;

        sb.AppendLine("## ESM Records");
        sb.AppendLine();

        AppendBasicEsmCounts(sb, esm);
        AppendMainRecordSection(sb, esm);
        AppendExtendedSubrecordsSection(sb, esm);
        AppendDialogueSection(sb, esm);
        AppendTextSubrecordsSection(sb, esm);
        AppendFormIdRefsSection(sb, esm);

        // Conditions section
        if (esm.Conditions.Count > 0)
        {
            sb.AppendLine("### Conditions (CTDA)");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"**Total**: {esm.Conditions.Count}");
            sb.AppendLine();
        }
    }

    private static void AppendBasicEsmCounts(StringBuilder sb, EsmRecordScanResult esm)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Editor IDs (EDID)**: {esm.EditorIds.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Game Settings (GMST)**: {esm.GameSettings.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Script Sources (SCTX)**: {esm.ScriptSources.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**FormID Refs (SCRO)**: {esm.FormIdReferences.Count}");
        sb.AppendLine();
    }

    private static void AppendMainRecordSection(StringBuilder sb, EsmRecordScanResult esm)
    {
        if (esm.MainRecords.Count == 0)
        {
            return;
        }

        sb.AppendLine("### Main Record Headers");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"**Total**: {esm.MainRecords.Count} (Little-Endian: {esm.LittleEndianRecords}, Big-Endian: {esm.BigEndianRecords})");
        sb.AppendLine();

        var typeCounts = esm.MainRecordCounts;
        if (typeCounts.Count > 0)
        {
            sb.AppendLine("| Record Type | Count |");
            sb.AppendLine("|-------------|-------|");
            foreach (var (type, count) in typeCounts.OrderByDescending(kv => kv.Value))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"| {type} | {count} |");
            }

            sb.AppendLine();
        }
    }

    private static void AppendExtendedSubrecordsSection(StringBuilder sb, EsmRecordScanResult esm)
    {
        if (esm.NameReferences.Count == 0 && esm.Positions.Count == 0 && esm.ActorBases.Count == 0)
        {
            return;
        }

        sb.AppendLine("### Extended Subrecords");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**NAME (Base Object Refs)**: {esm.NameReferences.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**DATA (Positions)**: {esm.Positions.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**ACBS (Actor Base Stats)**: {esm.ActorBases.Count}");
        sb.AppendLine();
    }

    private static void AppendDialogueSection(StringBuilder sb, EsmRecordScanResult esm)
    {
        if (esm.ResponseTexts.Count == 0 && esm.ResponseData.Count == 0)
        {
            return;
        }

        sb.AppendLine("### Dialogue (INFO) Subrecords");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**NAM1 (Response Text)**: {esm.ResponseTexts.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**TRDT (Response Data)**: {esm.ResponseData.Count}");
        sb.AppendLine();

        if (esm.ResponseTexts.Count > 0)
        {
            sb.AppendLine("Sample Dialogue:");
            sb.AppendLine();

            var samples = esm.ResponseTexts
                .Take(10)
                .Select(r => r.Text.Length > 80 ? r.Text[..77] + "..." : r.Text);

            foreach (var preview in samples)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- \"{preview}\"");
            }

            if (esm.ResponseTexts.Count > 10)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- ... ({esm.ResponseTexts.Count - 10} more)");
            }

            sb.AppendLine();
        }
    }

    private static void AppendTextSubrecordsSection(StringBuilder sb, EsmRecordScanResult esm)
    {
        var hasAny = esm.FullNames.Count > 0 || esm.Descriptions.Count > 0 ||
                     esm.ModelPaths.Count > 0 || esm.IconPaths.Count > 0 ||
                     esm.TexturePaths.Count > 0;

        if (!hasAny)
        {
            return;
        }

        sb.AppendLine("### Text Subrecords");
        sb.AppendLine();
        sb.AppendLine("| Type | Count | Sample |");
        sb.AppendLine("|------|-------|--------|");

        AppendTextSubrecordRow(sb, "FULL (Names)", esm.FullNames);
        AppendTextSubrecordRow(sb, "DESC (Descriptions)", esm.Descriptions);
        AppendTextSubrecordRow(sb, "MODL (Model Paths)", esm.ModelPaths);
        AppendTextSubrecordRow(sb, "ICON (Icon Paths)", esm.IconPaths);
        AppendTextSubrecordRow(sb, "TX* (Texture Paths)", esm.TexturePaths);

        sb.AppendLine();
    }

    private static void AppendTextSubrecordRow(StringBuilder sb, string label, List<TextSubrecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        var sample = records[0].Text;
        if (sample.Length > 40)
        {
            sample = sample[..37] + "...";
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"| {label} | {records.Count} | {sample} |");
    }

    private static void AppendFormIdRefsSection(StringBuilder sb, EsmRecordScanResult esm)
    {
        var hasAny = esm.ScriptRefs.Count > 0 || esm.EffectRefs.Count > 0 ||
                     esm.SoundRefs.Count > 0 || esm.QuestRefs.Count > 0;

        if (!hasAny)
        {
            return;
        }

        sb.AppendLine("### FormID References");
        sb.AppendLine();
        sb.AppendLine("| Type | Count |");
        sb.AppendLine("|------|-------|");

        if (esm.ScriptRefs.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| SCRI (Scripts) | {esm.ScriptRefs.Count} |");
        }

        if (esm.EffectRefs.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| ENAM (Effects) | {esm.EffectRefs.Count} |");
        }

        if (esm.SoundRefs.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| SNAM (Sounds) | {esm.SoundRefs.Count} |");
        }

        if (esm.QuestRefs.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| QNAM (Quests) | {esm.QuestRefs.Count} |");
        }

        sb.AppendLine();
    }

    private static void AppendFormIdSection(StringBuilder sb, AnalysisResult result)
    {
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
}
