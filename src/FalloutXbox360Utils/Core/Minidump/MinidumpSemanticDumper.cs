using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Minidump;

/// <summary>
///     Generates detailed semantic dumps of ESM records found in memory dumps.
///     Exports records grouped by type with human-readable meanings.
/// </summary>
internal static class MinidumpSemanticDumper
{
    /// <summary>
    ///     Generate a detailed semantic dump of all ESM records found.
    ///     This exports records grouped by type with human-readable meanings.
    /// </summary>
    internal static string GenerateSemanticDump(AnalysisResult result)
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
            foreach (var record in group.Take(10).Select(item => item.Record))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  0x{record.Offset:X8}: {record.Name}");
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

    internal static string GetRecordDescription(string recordType)
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

    internal static string GetRefDescription(string subrecordType)
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

    internal static string GetEditorIdPrefix(string editorId)
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
