using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Strings;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Generates GECK-style text reports for Asset Lists, Runtime Editor IDs,
///     String Pools, Game Settings, Globals, and Leveled Lists.
/// </summary>
internal static class GeckMiscWriter
{
    internal static string GenerateAssetListReport(List<DetectedAssetString> assets)
    {
        var sb = new StringBuilder();
        GeckReportGenerator.AppendHeader(sb, "Runtime Asset String Pool");
        sb.AppendLine();

        var validAssets = assets
            .Where(a => a.Path.Length > 0 && char.IsLetterOrDigit(a.Path[0]))
            .Select(a => a with { Path = GeckReportGenerator.CleanAssetPath(a.Path) })
            .ToList();

        sb.AppendLine($"Total Assets: {validAssets.Count:N0} (filtered from {assets.Count:N0})");
        sb.AppendLine();

        var byCategory = validAssets.GroupBy(a => a.Category)
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key,
                g => g.Select(a => a.Path).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase).ToList());

        foreach (var (category, paths) in byCategory)
        {
            sb.AppendLine(new string('-', GeckReportGenerator.SeparatorWidth));
            sb.AppendLine($"  {category} ({paths.Count:N0})");
            sb.AppendLine(new string('-', GeckReportGenerator.SeparatorWidth));

            GeckReportGenerator.AppendPathTree(sb, paths, "  ");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    internal static string GenerateRuntimeEditorIdsReport(List<RuntimeEditorIdEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("EditorID,FormID,FormType,DisplayName,DialogueLine,TesFormOffset");

        foreach (var entry in entries.OrderBy(e => e.EditorId))
        {
            var formId = entry.FormId != 0 ? $"{entry.FormId:X8}" : "";
            var formType = entry.FormId != 0 ? $"{entry.FormType:D3}" : "";
            var displayName = GeckReportGenerator.CsvEscape(entry.DisplayName);
            var dialogueLine = GeckReportGenerator.CsvEscape(entry.DialogueLine);
            var offset = entry.TesFormOffset?.ToString() ?? "";

            sb.AppendLine($"{GeckReportGenerator.CsvEscape(entry.EditorId)},{formId},{formType},{displayName},{dialogueLine},{offset}");
        }

        return sb.ToString();
    }

    internal static void AppendStringPoolSection(StringBuilder sb, StringPoolSummary sp)
    {
        sb.AppendLine();
        GeckReportGenerator.AppendHeader(sb, "String Pool Data (from Runtime Memory)");
        sb.AppendLine();
        sb.AppendLine($"  Total strings:     {sp.TotalStrings,10:N0} ({sp.UniqueStrings:N0} unique)");
        sb.AppendLine($"  Across:            {sp.RegionCount,10:N0} regions ({GeckReportGenerator.FormatPoolSize(sp.TotalBytes)})");
        sb.AppendLine();
        sb.AppendLine($"  File paths:        {sp.FilePaths,10:N0}");

        if (sp.MatchedToCarvedFiles > 0)
        {
            sb.AppendLine(
                $"    Matched to carved: {sp.MatchedToCarvedFiles:N0}  |  Unmatched: {sp.UnmatchedFilePaths:N0}");
        }

        sb.AppendLine($"  EditorIDs:         {sp.EditorIds,10:N0}");
        sb.AppendLine($"  Dialogue lines:    {sp.DialogueLines,10:N0}");
        sb.AppendLine($"  Game settings:     {sp.GameSettings,10:N0}");
        sb.AppendLine($"  Other:             {sp.Other,10:N0}");

        if (sp.SampleDialogue.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  Sample dialogue/descriptions (from runtime memory, not ESM records):");
            foreach (var line in sp.SampleDialogue.Take(10))
            {
                var display = line.Length > 120 ? line[..117] + "..." : line;
                sb.AppendLine($"    \"{display}\"");
            }
        }

        sb.AppendLine();
        sb.AppendLine("  Note: These strings come from runtime memory pools, not ESM records.");
        sb.AppendLine("  Includes perk descriptions, skill descriptions, loading screen text,");
        sb.AppendLine("  and other game text not found in the dump's ESM data.");
        sb.AppendLine("  See string_pool_*.csv files for full datasets.");
    }

    internal static void AppendGlobalsSection(StringBuilder sb, List<GlobalRecord> globals)
    {
        GeckReportGenerator.AppendSectionHeader(sb, $"Global Variables ({globals.Count})");
        sb.AppendLine();

        var byType = globals.GroupBy(g => g.TypeName).OrderBy(g => g.Key).ToList();
        foreach (var group in byType)
        {
            sb.AppendLine($"  {group.Key}: {group.Count():N0}");
        }

        sb.AppendLine();

        foreach (var group in byType)
        {
            sb.AppendLine($"--- {group.Key} Globals ---");
            foreach (var g in group.OrderBy(x => x.EditorId, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"  {g.EditorId ?? "(none)",-50} = {g.DisplayValue,12}  [{GeckReportGenerator.FormatFormId(g.FormId)}]");
            }

            sb.AppendLine();
        }
    }

    internal static string GenerateGlobalsReport(List<GlobalRecord> globals)
    {
        var sb = new StringBuilder();
        AppendGlobalsSection(sb, globals);
        return sb.ToString();
    }

    internal static void AppendGameSettingsSection(StringBuilder sb, List<GameSettingRecord> settings)
    {
        GeckReportGenerator.AppendSectionHeader(sb, $"Game Settings ({settings.Count})");

        sb.AppendLine();
        sb.AppendLine($"Total Game Settings: {settings.Count:N0}");

        var floatSettings = settings.Where(s => s.ValueType == GameSettingType.Float).ToList();
        var intSettings = settings.Where(s => s.ValueType == GameSettingType.Integer).ToList();
        var boolSettings = settings.Where(s => s.ValueType == GameSettingType.Boolean).ToList();
        var stringSettings = settings.Where(s => s.ValueType == GameSettingType.String).ToList();

        sb.AppendLine($"  Float:   {floatSettings.Count:N0}");
        sb.AppendLine($"  Integer: {intSettings.Count:N0}");
        sb.AppendLine($"  Boolean: {boolSettings.Count:N0}");
        sb.AppendLine($"  String:  {stringSettings.Count:N0}");
        sb.AppendLine();

        if (floatSettings.Count > 0)
        {
            sb.AppendLine("--- Float Settings ---");
            foreach (var setting in floatSettings.OrderBy(s => s.EditorId, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(
                    $"  {setting.EditorId,-60} = {setting.DisplayValue,12}  [{GeckReportGenerator.FormatFormId(setting.FormId)}]");
            }

            sb.AppendLine();
        }

        if (intSettings.Count > 0)
        {
            sb.AppendLine("--- Integer Settings ---");
            foreach (var setting in intSettings.OrderBy(s => s.EditorId, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(
                    $"  {setting.EditorId,-60} = {setting.DisplayValue,12}  [{GeckReportGenerator.FormatFormId(setting.FormId)}]");
            }

            sb.AppendLine();
        }

        if (boolSettings.Count > 0)
        {
            sb.AppendLine("--- Boolean Settings ---");
            foreach (var setting in boolSettings.OrderBy(s => s.EditorId, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(
                    $"  {setting.EditorId,-60} = {setting.DisplayValue,12}  [{GeckReportGenerator.FormatFormId(setting.FormId)}]");
            }

            sb.AppendLine();
        }

        if (stringSettings.Count > 0)
        {
            sb.AppendLine("--- String Settings ---");
            foreach (var setting in stringSettings.OrderBy(s => s.EditorId, StringComparer.OrdinalIgnoreCase))
            {
                var displayValue = setting.StringValue?.Length > 50
                    ? setting.StringValue[..47] + "..."
                    : setting.StringValue;
                sb.AppendLine($"  {setting.EditorId,-60} = \"{displayValue}\"  [{GeckReportGenerator.FormatFormId(setting.FormId)}]");
            }

            sb.AppendLine();
        }
    }

    internal static string GenerateGameSettingsReport(List<GameSettingRecord> settings)
    {
        var sb = new StringBuilder();
        AppendGameSettingsSection(sb, settings);
        return sb.ToString();
    }

    internal static void AppendLeveledListsSection(StringBuilder sb, List<LeveledListRecord> lists,
        Dictionary<uint, string> lookup)
    {
        GeckReportGenerator.AppendSectionHeader(sb, $"Leveled Lists ({lists.Count})");
        sb.AppendLine();

        var byType = lists.GroupBy(l => l.ListType).OrderBy(g => g.Key).ToList();
        sb.AppendLine($"Total Leveled Lists: {lists.Count:N0}");
        foreach (var group in byType)
        {
            sb.AppendLine($"  {group.Key}: {group.Count():N0}");
        }

        var totalEntries = lists.Sum(l => l.Entries.Count);
        sb.AppendLine(
            $"  Total Entries: {totalEntries:N0} (avg {(lists.Count > 0 ? totalEntries / (double)lists.Count : 0):F1} per list)");
        sb.AppendLine();

        foreach (var list in lists.OrderBy(l => l.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  LIST: {list.EditorId ?? "(none)"}");
            sb.AppendLine($"  FormID:      {GeckReportGenerator.FormatFormId(list.FormId)}");
            sb.AppendLine($"  Type:        {list.ListType}");
            sb.AppendLine($"  Chance None: {list.ChanceNone}%");
            if (!string.IsNullOrEmpty(list.FlagsDescription))
            {
                sb.AppendLine($"  Flags:       {list.FlagsDescription}");
            }

            if (list.GlobalFormId is > 0)
            {
                sb.AppendLine($"  Global:      {GeckReportGenerator.FormatFormIdWithName(list.GlobalFormId.Value, lookup)}");
            }

            if (list.Entries.Count > 0)
            {
                sb.AppendLine(
                    $"  \u2500\u2500 Entries ({list.Entries.Count}) {new string('\u2500', 80 - 18 - list.Entries.Count.ToString().Length)}");
                sb.AppendLine($"  {"Level",7}  {"Item",-50} {"Count",6}");
                sb.AppendLine($"  {new string('\u2500', 67)}");
                foreach (var entry in list.Entries.OrderBy(e => e.Level))
                {
                    var itemName = entry.FormId != 0
                        ? GeckReportGenerator.FormatFormIdWithName(entry.FormId, lookup)
                        : "(none)";
                    sb.AppendLine($"  {entry.Level,7}  {GeckReportGenerator.Truncate(itemName, 50),-50} {entry.Count,6}");
                }
            }

            sb.AppendLine();
        }
    }

    internal static string GenerateLeveledListsReport(List<LeveledListRecord> lists,
        Dictionary<uint, string>? lookup = null)
    {
        var sb = new StringBuilder();
        AppendLeveledListsSection(sb, lists, lookup ?? []);
        return sb.ToString();
    }
}
