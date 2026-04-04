using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.RuntimeBuffer;
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
        GeckReportHelpers.AppendHeader(sb, "Runtime Asset String Pool");
        sb.AppendLine();

        var validAssets = assets
            .Where(a => a.Path.Length > 0 && char.IsLetterOrDigit(a.Path[0]))
            .Select(a => a with { Path = GeckReportHelpers.CleanAssetPath(a.Path) })
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
            sb.AppendLine(new string('-', GeckReportHelpers.SeparatorWidth));
            sb.AppendLine($"  {category} ({paths.Count:N0})");
            sb.AppendLine(new string('-', GeckReportHelpers.SeparatorWidth));

            GeckReportHelpers.AppendPathTree(sb, paths, "  ");
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
            var displayName = GeckReportHelpers.CsvEscape(entry.DisplayName);
            var dialogueLine = GeckReportHelpers.CsvEscape(entry.DialogueLine);
            var offset = entry.TesFormOffset?.ToString() ?? "";

            sb.AppendLine(
                $"{GeckReportHelpers.CsvEscape(entry.EditorId)},{formId},{formType},{displayName},{dialogueLine},{offset}");
        }

        return sb.ToString();
    }

    internal static void AppendStringPoolSection(StringBuilder sb, StringPoolSummary sp)
    {
        sb.AppendLine();
        GeckReportHelpers.AppendHeader(sb, "String Pool Data (from Runtime Memory)");
        sb.AppendLine();
        sb.AppendLine($"  Total strings:     {sp.TotalStrings,10:N0} ({sp.UniqueStrings:N0} unique)");
        sb.AppendLine(
            $"  Across:            {sp.RegionCount,10:N0} regions ({GeckReportHelpers.FormatPoolSize(sp.TotalBytes)})");
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
        sb.AppendLine("  See string_owned_*.csv / string_unknown_owners.csv / string_unreferenced.csv for full datasets.");
    }

    internal static string GenerateStringOwnershipSummaryReport(RuntimeStringOwnershipAnalysis analysis)
    {
        var sb = new StringBuilder();
        GeckReportHelpers.AppendHeader(sb, "Runtime String Ownership Summary");
        sb.AppendLine();
        sb.AppendLine($"Analyzed String Hits: {analysis.AllHits.Count:N0}");
        sb.AppendLine($"  Owned:                    {analysis.OwnedHits.Count:N0}");
        sb.AppendLine($"  Referenced, owner unknown:{analysis.ReferencedOwnerUnknownHits.Count,8:N0}");
        sb.AppendLine($"  Unreferenced:             {analysis.UnreferencedHits.Count:N0}");
        sb.AppendLine();
        sb.AppendLine("By Category:");

        foreach (var category in analysis.CategoryCounts.Keys.OrderBy(c => c.ToString(), StringComparer.Ordinal))
        {
            var total = analysis.CategoryCounts.GetValueOrDefault(category);
            var owned = analysis.OwnedHits.Count(h => h.Category == category);
            var referencedUnknown = analysis.ReferencedOwnerUnknownHits.Count(h => h.Category == category);
            var unreferenced = analysis.UnreferencedHits.Count(h => h.Category == category);

            sb.AppendLine(
                $"  {category,-16} total {total,8:N0} | owned {owned,8:N0} | unknown {referencedUnknown,8:N0} | unreferenced {unreferenced,8:N0}");
        }

        sb.AppendLine();
        sb.AppendLine("Notes:");
        sb.AppendLine("  Owned strings require direct typed evidence from runtime EditorID tables or manager/global walkers.");
        sb.AppendLine("  ReferencedOwnerUnknown strings have live inbound pointers, but no conservative owner match.");
        sb.AppendLine("  Unreferenced strings have no 4-byte-aligned inbound pointer to the exact string start.");
        return sb.ToString();
    }

    internal static void AppendGlobalsSection(StringBuilder sb, List<GlobalRecord> globals)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Global Variables ({globals.Count})");
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
                sb.AppendLine(
                    $"  {g.EditorId ?? "(none)",-50} = {g.DisplayValue,12}  [{GeckReportHelpers.FormatFormId(g.FormId)}]");
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
        GeckReportHelpers.AppendSectionHeader(sb, $"Game Settings ({settings.Count})");

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
                    $"  {setting.EditorId,-60} = {setting.DisplayValue,12}  [{GeckReportHelpers.FormatFormId(setting.FormId)}]");
            }

            sb.AppendLine();
        }

        if (intSettings.Count > 0)
        {
            sb.AppendLine("--- Integer Settings ---");
            foreach (var setting in intSettings.OrderBy(s => s.EditorId, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(
                    $"  {setting.EditorId,-60} = {setting.DisplayValue,12}  [{GeckReportHelpers.FormatFormId(setting.FormId)}]");
            }

            sb.AppendLine();
        }

        if (boolSettings.Count > 0)
        {
            sb.AppendLine("--- Boolean Settings ---");
            foreach (var setting in boolSettings.OrderBy(s => s.EditorId, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine(
                    $"  {setting.EditorId,-60} = {setting.DisplayValue,12}  [{GeckReportHelpers.FormatFormId(setting.FormId)}]");
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
                sb.AppendLine(
                    $"  {setting.EditorId,-60} = \"{displayValue}\"  [{GeckReportHelpers.FormatFormId(setting.FormId)}]");
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

    /// <summary>Build a structured leveled list report from a <see cref="LeveledListRecord" />.</summary>
    internal static RecordReport BuildLeveledListReport(LeveledListRecord list, FormIdResolver resolver)
    {
        var sections = new List<ReportSection>();

        // Identity
        var identityFields = new List<ReportField>
        {
            new("Type", ReportValue.String(list.ListType)),
            new("Chance None", ReportValue.Int(list.ChanceNone, $"{list.ChanceNone}%"))
        };
        if (!string.IsNullOrEmpty(list.FlagsDescription) && list.FlagsDescription != "None")
            identityFields.Add(new("Flags", ReportValue.String(list.FlagsDescription)));
        if (list.GlobalFormId is > 0)
            identityFields.Add(new("Global", ReportValue.FormId(list.GlobalFormId.Value, resolver),
                $"0x{list.GlobalFormId.Value:X8}"));
        sections.Add(new("Identity", identityFields));

        // Entries
        if (list.Entries.Count > 0)
        {
            var entryItems = list.Entries.OrderBy(e => e.Level)
                .Select(e =>
                {
                    var itemName = e.FormId != 0 ? resolver.FormatFull(e.FormId) : "(none)";
                    return (ReportValue)new ReportValue.CompositeVal(
                    [
                        new("Level", ReportValue.Int(e.Level)),
                        new("Item", ReportValue.String(itemName)),
                        new("Count", ReportValue.Int(e.Count))
                    ], $"Lv{e.Level} {itemName} x{e.Count}");
                })
                .ToList();

            sections.Add(new($"Entries ({list.Entries.Count})",
            [
                new("Entries", ReportValue.List(entryItems))
            ]));
        }

        return new RecordReport("Leveled List", list.FormId, list.EditorId, null, sections);
    }

    internal static void AppendLeveledListsSection(StringBuilder sb, List<LeveledListRecord> lists,
        FormIdResolver resolver)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Leveled Lists ({lists.Count})");
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
            sb.AppendLine($"  FormID:      {GeckReportHelpers.FormatFormId(list.FormId)}");
            sb.AppendLine($"  Type:        {list.ListType}");
            sb.AppendLine($"  Chance None: {list.ChanceNone}%");
            if (!string.IsNullOrEmpty(list.FlagsDescription))
            {
                sb.AppendLine($"  Flags:       {list.FlagsDescription}");
            }

            if (list.GlobalFormId is > 0)
            {
                sb.AppendLine($"  Global:      {resolver.FormatFull(list.GlobalFormId.Value)}");
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
                        ? resolver.FormatFull(entry.FormId)
                        : "(none)";
                    sb.AppendLine(
                        $"  {entry.Level,7}  {GeckReportHelpers.Truncate(itemName, 50),-50} {entry.Count,6}");
                }
            }

            sb.AppendLine();
        }
    }

    internal static string GenerateLeveledListsReport(List<LeveledListRecord> lists,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendLeveledListsSection(sb, lists, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }

    // ── Form Lists ───────────────────────────────────────────────────────

    internal static void AppendFormListsSection(StringBuilder sb, List<FormListRecord> formLists,
        FormIdResolver resolver)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Form Lists ({formLists.Count})");
        sb.AppendLine();

        var totalEntries = formLists.Sum(fl => fl.FormIds.Count);
        sb.AppendLine($"Total Form Lists: {formLists.Count:N0}");
        sb.AppendLine($"  Total Entries:  {totalEntries:N0}");
        sb.AppendLine();

        foreach (var fl in formLists.OrderBy(f => f.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  FLST: {fl.EditorId ?? "(none)"} ({fl.FormIds.Count} entries)");
            sb.AppendLine($"  FormID:      {GeckReportHelpers.FormatFormId(fl.FormId)}");

            foreach (var entryId in fl.FormIds)
            {
                sb.AppendLine($"    - {resolver.FormatFull(entryId)}");
            }

            sb.AppendLine();
        }
    }

    internal static string GenerateFormListsReport(List<FormListRecord> formLists,
        FormIdResolver? resolver = null)
    {
        var sb = new StringBuilder();
        AppendFormListsSection(sb, formLists, resolver ?? FormIdResolver.Empty);
        return sb.ToString();
    }

    // ── Combat Styles ────────────────────────────────────────────────────

    internal static void AppendCombatStylesSection(StringBuilder sb, List<CombatStyleRecord> styles)
    {
        GeckReportHelpers.AppendSectionHeader(sb, $"Combat Styles ({styles.Count})");
        sb.AppendLine();

        foreach (var cs in styles.OrderBy(s => s.EditorId, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(new string('\u2500', 80));
            sb.AppendLine($"  CSTY: {cs.EditorId ?? "(none)"}");
            sb.AppendLine($"  FormID:      {GeckReportHelpers.FormatFormId(cs.FormId)}");

            AppendCombatStyleData(sb, "Style Data (CSTD)", cs.StyleData);
            AppendCombatStyleData(sb, "Advanced Data (CSAD)", cs.AdvancedData);
            AppendCombatStyleData(sb, "Simple Data (CSSD)", cs.SimpleData);

            sb.AppendLine();
        }
    }

    private static void AppendCombatStyleData(StringBuilder sb, string label,
        Dictionary<string, object?>? data)
    {
        if (data is not { Count: > 0 })
        {
            return;
        }

        sb.AppendLine($"  {label}:");
        foreach (var (key, value) in data.OrderBy(kv => kv.Key))
        {
            var formatted = value switch
            {
                float f => f.ToString("F4"),
                uint u => $"0x{u:X8}",
                int i => i.ToString(),
                byte b => b.ToString(),
                _ => value?.ToString() ?? "(null)"
            };
            sb.AppendLine($"    {key,-30} {formatted}");
        }
    }

    internal static string GenerateCombatStylesReport(List<CombatStyleRecord> styles)
    {
        var sb = new StringBuilder();
        AppendCombatStylesSection(sb, styles);
        return sb.ToString();
    }
}
