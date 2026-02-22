using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Export;

namespace FalloutXbox360Utils.Core.Formats.SaveGame.Export;

/// <summary>
///     Generates text and CSV reports from save file data.
///     All FormIDs use consistent 0x{X8} format for cross-referencing with ESM/DMP reports.
/// </summary>
public static class SaveReportGenerator
{
    /// <summary>
    ///     Generate all reports from save data. Returns filename → content.
    /// </summary>
    public static Dictionary<string, string> GenerateAllReports(
        SaveFile save,
        Dictionary<int, DecodedFormData>? decodedForms,
        FormIdResolver? resolver)
    {
        var files = new Dictionary<string, string>();
        var formIdArray = save.FormIdArray.ToArray();

        files["save_summary.txt"] = GenerateSummary(save, decodedForms, formIdArray, resolver);
        files["changed_forms.csv"] = GenerateChangedFormsCsv(save, decodedForms, formIdArray, resolver);
        files["decode_coverage.txt"] = GenerateDecodeCoverage(save, decodedForms, formIdArray);
        files["player_data.txt"] = GeneratePlayerData(save, decodedForms, formIdArray, resolver);
        files["gameplay_stats.csv"] = GenerateStatsCsv(save);
        files["global_variables.csv"] = GenerateGlobalVariablesCsv(save, formIdArray, resolver);
        files["visited_worldspaces.csv"] = GenerateVisitedWorldspacesCsv(save, resolver);

        return files;
    }

    private static string FormatFormId(SaveRefId refId, uint[] formIdArray)
    {
        var resolved = refId.ResolveFormId(formIdArray);
        return resolved != 0 ? $"0x{resolved:X8}" : refId.ToString();
    }

    private static string ResolveName(uint formId, FormIdResolver? resolver)
    {
        return resolver?.GetBestNameWithRefChain(formId) ?? "";
    }

    /// <summary>
    ///     Formats a rotation float, treating float.MaxValue as a sentinel (no rotation set).
    /// </summary>
    private static string FormatRotation(float value)
    {
        return float.IsNaN(value) || float.IsInfinity(value) || MathF.Abs(value) > 1e10f
            ? ""
            : value.ToString("F4");
    }

    private static string GenerateSummary(
        SaveFile save,
        Dictionary<int, DecodedFormData>? decodedForms,
        uint[] formIdArray,
        FormIdResolver? resolver)
    {
        var sb = new StringBuilder();
        var h = save.Header;

        sb.AppendLine(new string('=', 80));
        sb.AppendLine("                         Save File Summary Report");
        sb.AppendLine(new string('=', 80));
        sb.AppendLine();

        // Header info
        sb.AppendLine("Header:");
        sb.AppendLine($"  Version:        0x{h.Version:X}");
        sb.AppendLine($"  Save Number:    {h.SaveNumber}");
        sb.AppendLine($"  Player Name:    {(string.IsNullOrEmpty(h.PlayerName) ? "(empty)" : h.PlayerName)}");
        sb.AppendLine($"  Player Level:   {h.PlayerLevel}");
        sb.AppendLine($"  Player Status:  {h.PlayerStatus}");
        sb.AppendLine($"  Current Cell:   {h.PlayerCell}");
        sb.AppendLine($"  Playtime:       {h.SaveDuration}");
        sb.AppendLine($"  Form Version:   {h.FormVersion}");
        sb.AppendLine($"  Screenshot:     {h.ScreenshotWidth}x{h.ScreenshotHeight} ({h.ScreenshotDataSize:N0} bytes)");
        sb.AppendLine();

        // Plugins
        sb.AppendLine($"Plugins ({h.Plugins.Count}):");
        for (int i = 0; i < h.Plugins.Count; i++)
        {
            sb.AppendLine($"  [{i:X2}] {h.Plugins[i]}");
        }
        sb.AppendLine();

        // Player location
        if (save.PlayerLocation != null)
        {
            var loc = save.PlayerLocation;
            sb.AppendLine("Player Location:");
            var wsFormId = loc.WorldspaceRefId.ResolveFormId(formIdArray);
            var wsName = ResolveName(wsFormId, resolver);
            if (wsFormId != 0)
                sb.AppendLine($"  Worldspace:  0x{wsFormId:X8}{(wsName != "" ? $" ({wsName})" : "")}");
            else
                sb.AppendLine("  Worldspace:  Interior");
            sb.AppendLine($"  Grid:        ({loc.CoordX}, {loc.CoordY})");
            var cellFormId = loc.CellRefId.ResolveFormId(formIdArray);
            var cellName = ResolveName(cellFormId, resolver);
            sb.AppendLine($"  Cell:        {FormatFormId(loc.CellRefId, formIdArray)}{(cellName != "" ? $" ({cellName})" : "")}");
            sb.AppendLine($"  Position:    ({loc.PosX:F2}, {loc.PosY:F2}, {loc.PosZ:F2})");
            sb.AppendLine();
        }

        // Body summary
        sb.AppendLine("Body Summary:");
        sb.AppendLine($"  Global Data 1:       {save.GlobalData1.Count} entries");
        sb.AppendLine($"  Global Data 2:       {save.GlobalData2.Count} entries");
        sb.AppendLine($"  Changed Forms:       {save.ChangedForms.Count}");
        sb.AppendLine($"  FormID Array:        {save.FormIdArray.Count} entries");
        sb.AppendLine($"  Visited Worldspaces: {save.VisitedWorldspaces.Count}");
        sb.AppendLine($"  Global Variables:    {save.GlobalVariables.Count}");
        sb.AppendLine();

        // Changed form type summary
        var typeCounts = save.ChangedForms
            .GroupBy(f => f.TypeName)
            .OrderByDescending(g => g.Count())
            .ToList();

        sb.AppendLine("Changed Forms by Type:");
        sb.AppendLine($"  {"Type",-12} {"Count",8}  {"With Position",14}");
        sb.AppendLine($"  {new string('-', 12)} {new string('-', 8)}  {new string('-', 14)}");
        foreach (var group in typeCounts)
        {
            int withPos = group.Count(f => f.Initial != null);
            sb.AppendLine($"  {group.Key,-12} {group.Count(),8}  {withPos,14}");
        }
        sb.AppendLine();

        // Decode coverage overview
        if (decodedForms != null)
        {
            long totalBytes = 0, decodedBytes = 0;
            int full = 0, partial = 0, failed = 0;
            foreach (var (idx, decoded) in decodedForms)
            {
                if (idx < save.ChangedForms.Count)
                {
                    totalBytes += save.ChangedForms[idx].Data.Length;
                }
                decodedBytes += decoded.BytesConsumed;
                if (decoded.FullyDecoded) full++;
                else if (decoded.BytesConsumed > 0) partial++;
                else failed++;
            }

            sb.AppendLine("Decode Coverage:");
            sb.AppendLine($"  Decoded forms:   {decodedForms.Count} / {save.ChangedForms.Count}");
            sb.AppendLine($"  Fully decoded:   {full}");
            sb.AppendLine($"  Partial:         {partial}");
            sb.AppendLine($"  Failed:          {failed}");
            if (totalBytes > 0)
            {
                sb.AppendLine($"  Byte coverage:   {100.0 * decodedBytes / totalBytes:F1}% ({decodedBytes:N0} / {totalBytes:N0})");
            }
        }

        return sb.ToString();
    }

    private static string GenerateChangedFormsCsv(
        SaveFile save,
        Dictionary<int, DecodedFormData>? decodedForms,
        uint[] formIdArray,
        FormIdResolver? resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FormID,Type,EditorID,DisplayName,Flags,FlagNames,DataSize,DecodeStatus,BytesConsumed,BytesRemaining,CellFormID,CellName,PosX,PosY,PosZ,RotX,RotY,RotZ,BaseFormID,BaseName");

        for (int i = 0; i < save.ChangedForms.Count; i++)
        {
            var form = save.ChangedForms[i];
            var resolved = form.RefId.ResolveFormId(formIdArray);
            var formIdStr = Fmt.FId(resolved);
            var editorId = resolved != 0 && resolver != null ? resolver.ResolveCsv(resolved) : "";
            var displayName = resolved != 0 && resolver != null ? resolver.ResolveDisplayNameCsv(resolved) : "";
            var flagNames = ChangeFlagRegistry.DescribeFlags(form.ChangeType, form.ChangeFlags);
            var flagStr = flagNames.Count > 0 ? string.Join("|", flagNames) : "";

            string status = "NotDecoded";
            int consumed = 0, remaining = form.Data.Length;
            if (decodedForms != null && decodedForms.TryGetValue(i, out var decoded))
            {
                consumed = decoded.BytesConsumed;
                remaining = decoded.UndecodedBytes;
                status = decoded.FullyDecoded ? "Full" : decoded.BytesConsumed > 0 ? "Partial" : "Failed";
            }

            // Position/cell data from InitialData (reference types only)
            var init = form.Initial;
            string cellFormId = "", cellName = "", posX = "", posY = "", posZ = "";
            string rotX = "", rotY = "", rotZ = "", baseFormId = "", baseName = "";
            if (init != null)
            {
                var cellResolved = init.CellRefId.ResolveFormId(formIdArray);
                cellFormId = Fmt.FId(cellResolved);
                cellName = cellResolved != 0 && resolver != null ? Fmt.CsvEscape(resolver.GetBestNameWithRefChain(cellResolved) ?? "") : "";
                posX = init.PosX.ToString("F2");
                posY = init.PosY.ToString("F2");
                posZ = init.PosZ.ToString("F2");
                rotX = FormatRotation(init.RotX);
                rotY = FormatRotation(init.RotY);
                rotZ = FormatRotation(init.RotZ);
                if (init.BaseFormRefId != null)
                {
                    var baseResolved = init.BaseFormRefId.Value.ResolveFormId(formIdArray);
                    baseFormId = Fmt.FId(baseResolved);
                    baseName = baseResolved != 0 && resolver != null ? Fmt.CsvEscape(resolver.GetBestNameWithRefChain(baseResolved) ?? "") : "";
                }
            }

            sb.AppendLine(string.Join(",",
                formIdStr,
                form.TypeName,
                Fmt.CsvEscape(editorId),
                Fmt.CsvEscape(displayName),
                $"0x{form.ChangeFlags:X8}",
                Fmt.CsvEscape(flagStr),
                form.Data.Length,
                status,
                consumed,
                remaining,
                cellFormId,
                cellName,
                posX, posY, posZ,
                rotX, rotY, rotZ,
                baseFormId,
                baseName));
        }

        return sb.ToString();
    }

    private static string GenerateDecodeCoverage(
        SaveFile save,
        Dictionary<int, DecodedFormData>? decodedForms,
        uint[] formIdArray)
    {
        var sb = new StringBuilder();
        sb.AppendLine(new string('=', 80));
        sb.AppendLine("                      Decode Coverage Report");
        sb.AppendLine(new string('=', 80));
        sb.AppendLine();

        if (decodedForms == null || decodedForms.Count == 0)
        {
            sb.AppendLine("No decoded form data available.");
            return sb.ToString();
        }

        // Per-type breakdown
        var typeStats = new Dictionary<string, (int Total, int Full, int Partial, int Fail, long TotalBytes, long DecodedBytes)>();

        for (int i = 0; i < save.ChangedForms.Count; i++)
        {
            var form = save.ChangedForms[i];
            if (form.Data.Length == 0) continue;

            var typeName = form.TypeName;
            if (!typeStats.TryGetValue(typeName, out var s))
            {
                s = (0, 0, 0, 0, 0, 0);
            }
            s.Total++;
            s.TotalBytes += form.Data.Length;

            if (decodedForms.TryGetValue(i, out var decoded))
            {
                s.DecodedBytes += decoded.BytesConsumed;
                if (decoded.FullyDecoded) s.Full++;
                else if (decoded.BytesConsumed > 0) s.Partial++;
                else s.Fail++;
            }

            typeStats[typeName] = s;
        }

        sb.AppendLine($"  {"Type",-12} {"Total",6} {"Full",6} {"Partial",8} {"Fail",6} {"Coverage",10}");
        sb.AppendLine($"  {new string('-', 12)} {new string('-', 6)} {new string('-', 6)} {new string('-', 8)} {new string('-', 6)} {new string('-', 10)}");

        foreach (var (type, stats) in typeStats.OrderByDescending(x => x.Value.Total))
        {
            var coverage = stats.TotalBytes > 0
                ? $"{100.0 * stats.DecodedBytes / stats.TotalBytes:F1}%"
                : "N/A";
            sb.AppendLine($"  {type,-12} {stats.Total,6} {stats.Full,6} {stats.Partial,8} {stats.Fail,6} {coverage,10}");
        }

        // Totals
        var totals = typeStats.Values.Aggregate(
            (Total: 0, Full: 0, Partial: 0, Fail: 0, TotalBytes: 0L, DecodedBytes: 0L),
            (acc, s) => (acc.Total + s.Total, acc.Full + s.Full, acc.Partial + s.Partial,
                acc.Fail + s.Fail, acc.TotalBytes + s.TotalBytes, acc.DecodedBytes + s.DecodedBytes));

        sb.AppendLine($"  {new string('-', 12)} {new string('-', 6)} {new string('-', 6)} {new string('-', 8)} {new string('-', 6)} {new string('-', 10)}");
        var totalCoverage = totals.TotalBytes > 0
            ? $"{100.0 * totals.DecodedBytes / totals.TotalBytes:F1}%"
            : "N/A";
        sb.AppendLine($"  {"TOTAL",-12} {totals.Total,6} {totals.Full,6} {totals.Partial,8} {totals.Fail,6} {totalCoverage,10}");

        return sb.ToString();
    }

    private static string GeneratePlayerData(
        SaveFile save,
        Dictionary<int, DecodedFormData>? decodedForms,
        uint[] formIdArray,
        FormIdResolver? resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine(new string('=', 80));
        sb.AppendLine("                           Player Data Report");
        sb.AppendLine(new string('=', 80));
        sb.AppendLine();

        var h = save.Header;
        sb.AppendLine($"  Name:     {(string.IsNullOrEmpty(h.PlayerName) ? "(empty)" : h.PlayerName)}");
        sb.AppendLine($"  Level:    {h.PlayerLevel}");
        sb.AppendLine($"  Status:   {h.PlayerStatus}");
        sb.AppendLine($"  Cell:     {h.PlayerCell}");
        sb.AppendLine($"  Playtime: {h.SaveDuration}");
        sb.AppendLine();

        if (save.PlayerLocation != null)
        {
            var loc = save.PlayerLocation;
            sb.AppendLine("World Position:");
            var wsFormId2 = loc.WorldspaceRefId.ResolveFormId(formIdArray);
            var wsName2 = ResolveName(wsFormId2, resolver);
            if (wsFormId2 != 0)
                sb.AppendLine($"  Worldspace:  0x{wsFormId2:X8}{(wsName2 != "" ? $" ({wsName2})" : "")}");
            else
                sb.AppendLine("  Worldspace:  Interior");
            var cellFormId2 = loc.CellRefId.ResolveFormId(formIdArray);
            var cellName2 = ResolveName(cellFormId2, resolver);
            sb.AppendLine($"  Cell:        {FormatFormId(loc.CellRefId, formIdArray)}{(cellName2 != "" ? $" ({cellName2})" : "")}");
            sb.AppendLine($"  Grid:        ({loc.CoordX}, {loc.CoordY})");
            sb.AppendLine($"  Position:    ({loc.PosX:F2}, {loc.PosY:F2}, {loc.PosZ:F2})");
            sb.AppendLine();
        }

        // Find player form (FormID 0x14) and dump decoded fields
        if (decodedForms != null)
        {
            for (int i = 0; i < save.ChangedForms.Count; i++)
            {
                var form = save.ChangedForms[i];
                var resolved = form.RefId.ResolveFormId(formIdArray);
                if (resolved != 0x14) continue;

                sb.AppendLine($"Player Changed Form (0x00000014, {form.TypeName}):");
                sb.AppendLine($"  Change Flags: 0x{form.ChangeFlags:X8}");
                var flagNames = ChangeFlagRegistry.DescribeFlags(form.ChangeType, form.ChangeFlags);
                if (flagNames.Count > 0)
                {
                    sb.AppendLine($"  Active Flags: {string.Join(" | ", flagNames)}");
                }
                sb.AppendLine($"  Data Size:    {form.Data.Length} bytes");
                sb.AppendLine();

                if (decodedForms.TryGetValue(i, out var decoded))
                {
                    sb.AppendLine($"  Decoded: {decoded.BytesConsumed}/{decoded.TotalBytes} bytes ({(decoded.FullyDecoded ? "FULL" : "PARTIAL")})");
                    sb.AppendLine();

                    foreach (var field in decoded.Fields)
                    {
                        var displayVal = EnrichFieldDisplay(field, formIdArray, resolver);
                        sb.AppendLine($"  {field.Name}: {displayVal}");

                        if (field.Children is { Count: > 0 })
                        {
                            foreach (var child in field.Children)
                            {
                                var childVal = EnrichFieldDisplay(child, formIdArray, resolver);
                                sb.AppendLine($"    {child.Name}: {childVal}");
                            }
                        }
                    }
                }

                break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Enrich a decoded field's display value with resolved FormID names.
    /// </summary>
    private static string EnrichFieldDisplay(DecodedField field, uint[] formIdArray, FormIdResolver? resolver)
    {
        if (field.Value is SaveRefId refId && !refId.IsNull)
        {
            var resolved = refId.ResolveFormId(formIdArray);
            if (resolved != 0)
            {
                var name = resolver?.GetBestNameWithRefChain(resolved);
                return name != null
                    ? $"{name} (0x{resolved:X8})"
                    : $"0x{resolved:X8}";
            }
        }

        return field.DisplayValue;
    }

    private static string GenerateStatsCsv(SaveFile save)
    {
        var sb = new StringBuilder();
        sb.AppendLine("StatLabel,Value");

        for (int i = 0; i < save.Statistics.Count; i++)
        {
            string label = i < SaveStatistics.Labels.Length
                ? SaveStatistics.Labels[i]
                : $"Unknown Stat {i}";
            sb.AppendLine($"{Fmt.CsvEscape(label)},{save.Statistics.Values[i]}");
        }

        return sb.ToString();
    }

    private static string GenerateGlobalVariablesCsv(
        SaveFile save,
        uint[] formIdArray,
        FormIdResolver? resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FormID,EditorID,DisplayName,Value");

        foreach (var gv in save.GlobalVariables)
        {
            var resolved = gv.RefId.ResolveFormId(formIdArray);
            var formIdStr = Fmt.FId(resolved);
            var editorId = resolved != 0 && resolver != null ? resolver.ResolveCsv(resolved) : "";
            var displayName = resolved != 0 && resolver != null ? resolver.ResolveDisplayNameCsv(resolved) : "";
            sb.AppendLine($"{formIdStr},{Fmt.CsvEscape(editorId)},{Fmt.CsvEscape(displayName)},{gv.Value}");
        }

        return sb.ToString();
    }

    private static string GenerateVisitedWorldspacesCsv(
        SaveFile save,
        FormIdResolver? resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FormID,EditorID,DisplayName");

        foreach (var wsFormId in save.VisitedWorldspaces)
        {
            var formIdStr = Fmt.FId(wsFormId);
            var editorId = resolver != null ? resolver.ResolveCsv(wsFormId) : "";
            var displayName = resolver != null ? resolver.ResolveDisplayNameCsv(wsFormId) : "";
            sb.AppendLine($"{formIdStr},{Fmt.CsvEscape(editorId)},{Fmt.CsvEscape(displayName)}");
        }

        return sb.ToString();
    }
}
