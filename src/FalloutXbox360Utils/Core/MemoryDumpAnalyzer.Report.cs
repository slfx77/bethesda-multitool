using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FalloutXbox360Utils.Core.Formats.EsmRecord;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Subrecords;

namespace FalloutXbox360Utils.Core;

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
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"Game: {Path.GetFileName(gameModule.Name)} ({gameModule.Size / 1024.0:F0} KB)");
            }
        }

        if (result.CarvedFiles.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Carved Files: {result.CarvedFiles.Count}");
        }

        if (result.ScdaRecords.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"SCDA Records: {result.ScdaRecords.Count}");
            var withSource = result.ScdaRecords.Count(s => s.HasAssociatedSctx);
            if (withSource > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"With Source: {withSource}");
            }
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

        if (result.ScdaRecords.Count == 0)
        {
            return;
        }

        sb.AppendLine("| Offset | Script Name | Bytecode Size | Has Source |");
        sb.AppendLine("|--------|-------------|--------------|------------|");

        foreach (var scda in result.ScdaRecords.Take(30))
        {
            var name = !string.IsNullOrEmpty(scda.ScriptName) ? scda.ScriptName : "-";
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| 0x{scda.Offset:X8} | {name} | {scda.BytecodeLength} bytes | {(scda.HasAssociatedSctx ? "Yes" : "No")} |");
        }

        if (result.ScdaRecords.Count > 30)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| ... | ... | ({result.ScdaRecords.Count - 30} more) | ... |");
        }

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

    /// <summary>
    ///     Generate a detailed semantic dump of all ESM records found.
    ///     This exports records grouped by type with human-readable meanings.
    /// </summary>
    public static string GenerateSemanticDump(AnalysisResult result)
    {
        if (result.EsmRecords == null)
        {
            return "No ESM records found.";
        }

        var sb = new StringBuilder();
        var esm = result.EsmRecords;

        sb.AppendLine("================================================================================");
        sb.AppendLine("                     ESM SEMANTIC DUMP - Memory Carver");
        sb.AppendLine("================================================================================");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Source: {Path.GetFileName(result.FilePath)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        // Main Record Headers
        DumpMainRecords(sb, esm);

        // Editor IDs (crucial for understanding what's loaded)
        DumpEditorIds(sb, esm);

        // Dialogue/Responses
        DumpDialogue(sb, esm);

        // Display Names (FULL)
        DumpTextSubrecords(sb, "DISPLAY NAMES (FULL)", esm.FullNames);

        // Descriptions
        DumpTextSubrecords(sb, "DESCRIPTIONS (DESC)", esm.Descriptions);

        // Model Paths
        DumpTextSubrecords(sb, "MODEL PATHS (MODL)", esm.ModelPaths);

        // Texture Paths
        DumpTextSubrecords(sb, "TEXTURE PATHS (TX*)", esm.TexturePaths);

        // FormID References
        DumpFormIdRefs(sb, esm);

        // Positions (DATA)
        DumpPositions(sb, esm);

        // Conditions
        DumpConditions(sb, esm);

        // Correlation attempt
        DumpCorrelations(sb, result);

        return sb.ToString();
    }

    private static void DumpMainRecords(StringBuilder sb, EsmRecordScanResult esm)
    {
        if (esm.MainRecords.Count == 0)
        {
            return;
        }

        sb.AppendLine("================================================================================");
        sb.AppendLine("                           MAIN RECORD HEADERS");
        sb.AppendLine("================================================================================");
        sb.AppendLine();

        // Group by type
        var byType = esm.MainRecords.GroupBy(r => r.RecordType).OrderByDescending(g => g.Count());

        foreach (var group in byType)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"--- {group.Key} ({GetRecordDescription(group.Key)}) - {group.Count()} records ---");
            foreach (var record in group.Take(20))
            {
                var flags = new List<string>();
                if (record.IsCompressed)
                {
                    flags.Add("COMPRESSED");
                }

                if (record.IsDeleted)
                {
                    flags.Add("DELETED");
                }

                var flagStr = flags.Count > 0 ? $" [{string.Join(", ", flags)}]" : "";
                var endian = record.IsBigEndian ? "BE" : "LE";

                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"  0x{record.Offset:X8}: FormID=0x{record.FormId:X8} Size={record.DataSize} ({endian}){flagStr}");
            }

            if (group.Count() > 20)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  ... ({group.Count() - 20} more)");
            }

            sb.AppendLine();
        }
    }

    private static void DumpEditorIds(StringBuilder sb, EsmRecordScanResult esm)
    {
        if (esm.EditorIds.Count == 0)
        {
            return;
        }

        sb.AppendLine("================================================================================");
        sb.AppendLine("                              EDITOR IDs (EDID)");
        sb.AppendLine("================================================================================");
        sb.AppendLine("These identify what game objects are loaded in memory.");
        sb.AppendLine();

        // Group by prefix to show categories
        var byPrefix = esm.EditorIds
            .Select(e => new { Record = e, Prefix = GetEditorIdPrefix(e.Name) })
            .GroupBy(x => x.Prefix)
            .OrderByDescending(g => g.Count());

        foreach (var group in byPrefix.Take(30))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"--- {group.Key} ({group.Count()} items) ---");
            foreach (var item in group.Take(10))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  0x{item.Record.Offset:X8}: {item.Record.Name}");
            }

            if (group.Count() > 10)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  ... ({group.Count() - 10} more)");
            }

            sb.AppendLine();
        }
    }

    private static void DumpDialogue(StringBuilder sb, EsmRecordScanResult esm)
    {
        if (esm.ResponseTexts.Count == 0)
        {
            return;
        }

        sb.AppendLine("================================================================================");
        sb.AppendLine("                           DIALOGUE RESPONSES (NAM1)");
        sb.AppendLine("================================================================================");
        sb.AppendLine("NPC dialogue text loaded in memory.");
        sb.AppendLine();

        foreach (var response in esm.ResponseTexts.Take(50))
        {
            var text = response.Text.Length > 100 ? response.Text[..97] + "..." : response.Text;
            sb.AppendLine(CultureInfo.InvariantCulture, $"  0x{response.Offset:X8}: \"{text}\"");
        }

        if (esm.ResponseTexts.Count > 50)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  ... ({esm.ResponseTexts.Count - 50} more)");
        }

        sb.AppendLine();
    }

    private static void DumpTextSubrecords(StringBuilder sb, string title, List<TextSubrecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        sb.AppendLine("================================================================================");
        sb.AppendLine(CultureInfo.InvariantCulture, $"                           {title}");
        sb.AppendLine("================================================================================");
        sb.AppendLine();

        foreach (var record in records.Take(50))
        {
            var text = record.Text.Length > 80 ? record.Text[..77] + "..." : record.Text;
            sb.AppendLine(CultureInfo.InvariantCulture, $"  0x{record.Offset:X8}: {text}");
        }

        if (records.Count > 50)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  ... ({records.Count - 50} more)");
        }

        sb.AppendLine();
    }

    private static void DumpFormIdRefs(StringBuilder sb, EsmRecordScanResult esm)
    {
        var allRefs = esm.ScriptRefs.Concat(esm.EffectRefs).Concat(esm.SoundRefs).Concat(esm.QuestRefs).ToList();
        if (allRefs.Count == 0)
        {
            return;
        }

        sb.AppendLine("================================================================================");
        sb.AppendLine("                           FORMID REFERENCES");
        sb.AppendLine("================================================================================");
        sb.AppendLine("Cross-references to other game objects.");
        sb.AppendLine();

        foreach (var group in allRefs.GroupBy(r => r.SubrecordType))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"--- {group.Key} ({GetRefDescription(group.Key)}) ---");
            foreach (var r in group.Take(20))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  0x{r.Offset:X8}: -> 0x{r.FormId:X8}");
            }

            if (group.Count() > 20)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  ... ({group.Count() - 20} more)");
            }

            sb.AppendLine();
        }
    }

    private static void DumpPositions(StringBuilder sb, EsmRecordScanResult esm)
    {
        if (esm.Positions.Count == 0)
        {
            return;
        }

        sb.AppendLine("================================================================================");
        sb.AppendLine("                           POSITIONS (DATA)");
        sb.AppendLine("================================================================================");
        sb.AppendLine("World coordinates of placed objects.");
        sb.AppendLine();

        foreach (var pos in esm.Positions.Take(30))
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  0x{pos.Offset:X8}: Pos({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1}) Rot({pos.RotX:F2}, {pos.RotY:F2}, {pos.RotZ:F2})");
        }

        if (esm.Positions.Count > 30)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  ... ({esm.Positions.Count - 30} more)");
        }

        sb.AppendLine();
    }

    private static void DumpConditions(StringBuilder sb, EsmRecordScanResult esm)
    {
        if (esm.Conditions.Count == 0)
        {
            return;
        }

        sb.AppendLine("================================================================================");
        sb.AppendLine("                           CONDITIONS (CTDA)");
        sb.AppendLine("================================================================================");
        sb.AppendLine("Script/quest conditions evaluated at runtime.");
        sb.AppendLine();

        foreach (var cond in esm.Conditions.Take(30))
        {
            var opStr = cond.Operator switch
            {
                0 => "==",
                1 => "!=",
                2 => ">",
                3 => ">=",
                4 => "<",
                5 => "<=",
                _ => $"op{cond.Operator}"
            };
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  0x{cond.Offset:X8}: Func[{cond.FunctionIndex}]({cond.Param1:X}, {cond.Param2:X}) {opStr} {cond.ComparisonValue}");
        }

        if (esm.Conditions.Count > 30)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  ... ({esm.Conditions.Count - 30} more)");
        }

        sb.AppendLine();
    }

    private static void DumpCorrelations(StringBuilder sb, AnalysisResult result)
    {
        if (result.FormIdMap.Count == 0)
        {
            return;
        }

        sb.AppendLine("================================================================================");
        sb.AppendLine("                       FORMID -> EDITOR ID CORRELATIONS");
        sb.AppendLine("================================================================================");
        sb.AppendLine("Mapping between FormIDs and their Editor IDs (for identification).");
        sb.AppendLine();

        foreach (var (formId, editorId) in result.FormIdMap.OrderBy(kv => kv.Key).Take(100))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  0x{formId:X8} = {editorId}");
        }

        if (result.FormIdMap.Count > 100)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"  ... ({result.FormIdMap.Count - 100} more)");
        }

        sb.AppendLine();
    }

    private static string GetRecordDescription(string recordType)
    {
        return recordType switch
        {
            "REFR" => "Placed Object Reference",
            "ACHR" => "Placed NPC",
            "ACRE" => "Placed Creature",
            "NPC_" => "NPC Definition",
            "CREA" => "Creature Definition",
            "WEAP" => "Weapon",
            "ARMO" => "Armor",
            "AMMO" => "Ammunition",
            "ALCH" => "Ingestible/Chem",
            "MISC" => "Miscellaneous Item",
            "CELL" => "Cell/Interior",
            "WRLD" => "Worldspace",
            "LAND" => "Landscape/Terrain",
            "NAVM" => "Navigation Mesh",
            "QUST" => "Quest",
            "DIAL" => "Dialog Topic",
            "INFO" => "Dialog Response",
            "SCPT" => "Script",
            "FACT" => "Faction",
            "RACE" => "Race",
            "CONT" => "Container",
            "DOOR" => "Door",
            "STAT" => "Static Object",
            "FURN" => "Furniture",
            "PACK" => "AI Package",
            _ => "Unknown"
        };
    }

    private static string GetRefDescription(string subrecordType)
    {
        return subrecordType switch
        {
            "SCRI" => "Script Reference",
            "ENAM" => "Effect Reference",
            "SNAM" => "Sound Reference",
            "QNAM" => "Quest Reference",
            _ => "Reference"
        };
    }

    private static string GetEditorIdPrefix(string editorId)
    {
        // Extract common prefixes to categorize editor IDs
        if (editorId.StartsWith("GS", StringComparison.OrdinalIgnoreCase))
        {
            return "GS* (Game Setting)";
        }

        if (editorId.StartsWith("NVDLC", StringComparison.OrdinalIgnoreCase))
        {
            return "NVDLC* (DLC Content)";
        }

        if (editorId.StartsWith("VMS", StringComparison.OrdinalIgnoreCase))
        {
            return "VMS* (Voice/Script)";
        }

        if (editorId.StartsWith("VFNV", StringComparison.OrdinalIgnoreCase))
        {
            return "VFNV* (Voice FNV)";
        }

        // Look for common naming patterns
        var match = Regex.Match(editorId, @"^([A-Z][a-z]+|[A-Z]+)");
        return match.Success ? $"{match.Value}*" : "Other";
    }
}
