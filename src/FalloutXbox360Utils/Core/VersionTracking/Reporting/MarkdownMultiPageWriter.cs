using System.Text;
using FalloutXbox360Utils.Core.VersionTracking.Models;
using FalloutXbox360Utils.Core.VersionTracking.Processing;

namespace FalloutXbox360Utils.Core.VersionTracking.Reporting;

/// <summary>
///     Generates a multi-page markdown report: hub/index page, build timeline page,
///     and one page per record category with logical sub-groupings.
/// </summary>
public static class MarkdownMultiPageWriter
{
    #region Types

    private record ChangePoint(string BuildLabel, DateTimeOffset? BuildDate, List<FieldChange> FieldChanges);

    private record RecordHistory(
        uint FormId, string? EditorId, string? FullName, string Category,
        List<ChangePoint> ChangePoints);

    private record CutRecord(uint FormId, string? EditorId, string? Name, string FirstSeen, string LastSeen);

    /// <summary>Defines a record category with accessors for snapshots and diffs.</summary>
    private record CategoryDef(
        string Name,
        string FileName,
        string Description,
        Func<VersionDiffResult, List<RecordChange>> DiffSelector,
        Func<VersionSnapshot, int> Counter,
        Func<VersionSnapshot, IEnumerable<KeyValuePair<uint, object>>> RecordSelector,
        Func<object, List<VersionSnapshot>, string> GroupClassifier);

    #endregion

    #region Category Definitions

    private static readonly CategoryDef[] Categories =
    [
        new("Quests", "quests.md", "Quest records including stages and objectives",
            d => d.QuestChanges, s => s.Quests.Count,
            s => s.Quests.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (r, _) => ClassifyQuest(r)),
        new("NPCs", "npcs.md", "Non-player characters",
            d => d.NpcChanges, s => s.Npcs.Count,
            s => s.Npcs.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (_, _) => "NPCs"),
        new("Weapons", "weapons.md", "Weapons grouped by type",
            d => d.WeaponChanges, s => s.Weapons.Count,
            s => s.Weapons.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (r, _) => GetWeaponTypeName(r)),
        new("Armor", "armor.md", "Armor and clothing grouped by slot",
            d => d.ArmorChanges, s => s.Armor.Count,
            s => s.Armor.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (r, _) => GetArmorSlotGroup(r)),
        new("Items", "items.md", "Consumables, miscellaneous items, and keys",
            d => d.ItemChanges, s => s.Items.Count,
            s => s.Items.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (r, _) => GetItemSubtype(r)),
        new("Scripts", "scripts.md", "Game scripts",
            d => d.ScriptChanges, s => s.Scripts.Count,
            s => s.Scripts.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (r, _) => GetScriptGroup(r)),
        new("Dialogues", "dialogue.md", "Dialogue lines grouped by parent quest",
            d => d.DialogueChanges, s => s.Dialogues.Count,
            s => s.Dialogues.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (r, snaps) => GetDialogueQuestGroup(r, snaps)),
        new("Locations", "locations.md", "Cells and worldspaces",
            d => d.LocationChanges, s => s.Locations.Count,
            s => s.Locations.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (r, _) => GetLocationGroup(r)),
        new("Placements", "placements.md", "Placed references and map markers",
            d => d.PlacementChanges, s => s.Placements.Count,
            s => s.Placements.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (r, _) => GetPlacementGroup(r)),
        new("Creatures", "creatures.md", "Creatures grouped by type",
            d => d.CreatureChanges, s => s.Creatures.Count,
            s => s.Creatures.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (r, _) => GetCreatureTypeGroup(r)),
        new("Perks", "perks.md", "Perks and traits",
            d => d.PerkChanges, s => s.Perks.Count,
            s => s.Perks.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (r, _) => GetPerkGroup(r)),
        new("Ammo", "ammo.md", "Ammunition types",
            d => d.AmmoChanges, s => s.Ammo.Count,
            s => s.Ammo.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (_, _) => "Ammo"),
        new("Leveled Lists", "leveled_lists.md", "Leveled lists for creatures, NPCs, and items",
            d => d.LeveledListChanges, s => s.LeveledLists.Count,
            s => s.LeveledLists.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (r, _) => GetLeveledListGroup(r)),
        new("Notes", "notes.md", "Notes and holotapes",
            d => d.NoteChanges, s => s.Notes.Count,
            s => s.Notes.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (r, _) => GetNoteGroup(r)),
        new("Terminals", "terminals.md", "Computer terminals",
            d => d.TerminalChanges, s => s.Terminals.Count,
            s => s.Terminals.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (_, _) => "Terminals")
    ];

    #endregion

    #region Public API

    /// <summary>
    ///     Generates a multi-page markdown report.
    ///     Returns a dictionary of filename → content for the caller to write to disk.
    /// </summary>
    public static Dictionary<string, string> WritePages(
        List<VersionSnapshot> snapshots,
        List<VersionDiffResult> diffs,
        uint? trackFormId = null,
        HashSet<uint>? fo3LeftoverFormIds = null)
    {
        var pages = new Dictionary<string, string>();

        // Pre-compute shared data
        var finalEsm = snapshots.LastOrDefault(s => s.Build.IsAuthoritative);
        var dmpSnapshots = snapshots.Where(s => !s.Build.IsAuthoritative).ToList();
        var histories = BuildAllHistories(snapshots, diffs);
        var cutData = finalEsm != null && dmpSnapshots.Count > 0
            ? ComputeAllCutRecords(snapshots, dmpSnapshots, finalEsm, fo3LeftoverFormIds)
            : new Dictionary<string, List<CutRecord>>();

        // Build index with stats
        var categoryStats = ComputeCategoryStats(finalEsm, histories, cutData);
        pages["index.md"] = WriteIndexPage(snapshots, categoryStats);
        pages["build_timeline.md"] = WriteBuildTimelinePage(snapshots, diffs, trackFormId);

        // Per-category pages
        foreach (var cat in Categories)
        {
            var catHistories = histories
                .Where(kvp => kvp.Key.Category == cat.Name)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            cutData.TryGetValue(cat.Name, out var catCutRecords);
            catCutRecords ??= [];

            var page = WriteCategoryPage(cat, snapshots, catHistories, catCutRecords);
            pages[cat.FileName] = page;
        }

        return pages;
    }

    #endregion

    #region Index Page

    private static string WriteIndexPage(
        List<VersionSnapshot> snapshots,
        List<(string Name, string FileName, int Total, int Cut, int Changed)> stats)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Fallout: New Vegas — Version Tracking Report");
        sb.AppendLine();

        // Overview
        var dates = snapshots
            .Where(s => s.Build.BuildDate.HasValue)
            .Select(s => s.Build.BuildDate!.Value)
            .OrderBy(d => d)
            .ToList();

        var dateRange = dates.Count >= 2
            ? $"{dates[0]:MMMM d, yyyy} to {dates[^1]:MMMM d, yyyy}"
            : "Unknown";

        sb.AppendLine($"Analysis of **{snapshots.Count} builds** spanning {dateRange}.");
        sb.AppendLine();

        // Summary table
        sb.AppendLine("## Overview");
        sb.AppendLine();
        sb.AppendLine("| Category | In Final ESM | Cut | Changed |");
        sb.AppendLine("|----------|-------------|-----|---------|");

        var totalFinal = 0;
        var totalCut = 0;
        var totalChanged = 0;

        foreach (var (name, _, total, cut, changed) in stats)
        {
            sb.AppendLine($"| [{name}]({Categories.First(c => c.Name == name).FileName}) | {total:N0} | {cut:N0} | {changed:N0} |");
            totalFinal += total;
            totalCut += cut;
            totalChanged += changed;
        }

        sb.AppendLine($"| **Total** | **{totalFinal:N0}** | **{totalCut:N0}** | **{totalChanged:N0}** |");
        sb.AppendLine();

        // Sub-page links
        sb.AppendLine("## Pages");
        sb.AppendLine();
        sb.AppendLine($"- [Build Timeline](build_timeline.md) — {snapshots.Count} builds, chronological progression");

        foreach (var (name, fileName, _, cut, changed) in stats)
        {
            var parts = new List<string>();
            if (cut > 0) { parts.Add($"{cut} cut"); }
            if (changed > 0) { parts.Add($"{changed} changed"); }

            var desc = parts.Count > 0 ? string.Join(", ", parts) : "no changes detected";
            sb.AppendLine($"- [{name}]({fileName}) — {desc}");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    #endregion

    #region Build Timeline Page

    private static string WriteBuildTimelinePage(
        List<VersionSnapshot> snapshots, List<VersionDiffResult> diffs, uint? trackFormId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Build Timeline");
        sb.AppendLine();
        sb.AppendLine("[Back to Index](index.md)");
        sb.AppendLine();

        // Build progression table
        sb.AppendLine("## Build Progression");
        sb.AppendLine();
        sb.AppendLine("| # | Build | Date | Source | Quests | NPCs | Weapons | Armor | Items | Scripts | Dialogues | Locations | Total |");
        sb.AppendLine("|---|-------|------|--------|--------|------|---------|-------|-------|---------|-----------|-----------|-------|");

        for (var i = 0; i < snapshots.Count; i++)
        {
            var s = snapshots[i];
            var date = s.Build.BuildDate?.ToString("yyyy-MM-dd") ?? "Unknown";
            var source = s.Build.SourceType == BuildSourceType.Esm ? "ESM" : "DMP";

            sb.AppendLine($"| {i + 1} | {Esc(s.Build.Label)} | {date} | {source} | " +
                          $"{s.Quests.Count:N0} | {s.Npcs.Count:N0} | {s.Weapons.Count:N0} | " +
                          $"{s.Armor.Count:N0} | {s.Items.Count:N0} | {s.Scripts.Count:N0} | " +
                          $"{s.Dialogues.Count:N0} | {s.Locations.Count:N0} | {s.TotalRecordCount:N0} |");
        }

        sb.AppendLine();

        // Change summary
        sb.AppendLine("## Change Summary");
        sb.AppendLine();
        sb.AppendLine("| # | From → To | Added | Removed | Changed | Total |");
        sb.AppendLine("|---|-----------|-------|---------|---------|-------|");

        for (var i = 0; i < diffs.Count; i++)
        {
            var d = diffs[i];
            var total = d.TotalAdded + d.TotalRemoved + d.TotalChanged;
            if (total == 0)
            {
                continue;
            }

            sb.AppendLine($"| {i + 1} | {Esc(d.FromBuild.Label)} → {Esc(d.ToBuild.Label)} | " +
                          $"{d.TotalAdded:N0} | {d.TotalRemoved:N0} | {d.TotalChanged:N0} | {total:N0} |");
        }

        // FormID history
        if (trackFormId.HasValue)
        {
            sb.AppendLine();
            WriteFormIdHistory(sb, snapshots, trackFormId.Value);
        }

        sb.AppendLine();
        return sb.ToString();
    }

    #endregion

    #region Category Pages

    private static string WriteCategoryPage(
        CategoryDef cat,
        List<VersionSnapshot> snapshots,
        Dictionary<(string Category, uint FormId), RecordHistory> catHistories,
        List<CutRecord> cutRecords)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {cat.Name}");
        sb.AppendLine();
        sb.AppendLine($"> {cat.Description} | [Back to Index](index.md)");
        sb.AppendLine();

        // Overview stats
        var finalEsm = snapshots.LastOrDefault(s => s.Build.IsAuthoritative);
        var totalInFinal = finalEsm != null ? cat.Counter(finalEsm) : 0;

        sb.AppendLine("## Overview");
        sb.AppendLine();
        sb.AppendLine($"- **Total in final ESM**: {totalInFinal:N0}");
        sb.AppendLine($"- **Cut content**: {cutRecords.Count:N0} records");
        sb.AppendLine($"- **Changed across builds**: {catHistories.Count:N0} records");
        sb.AppendLine();

        // Section 1: Cut Content
        WriteCutContentSection(sb, cat, cutRecords, snapshots);

        // Section 2: Changed Content
        WriteChangedContentSection(sb, cat, catHistories, snapshots);

        return sb.ToString();
    }

    #endregion

    #region Cut Content Section

    private static void WriteCutContentSection(
        StringBuilder sb, CategoryDef cat, List<CutRecord> cutRecords, List<VersionSnapshot> snapshots)
    {
        if (cutRecords.Count == 0)
        {
            return;
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Cut Content");
        sb.AppendLine();
        sb.AppendLine("Records present in development builds but absent from the final ESM.");
        sb.AppendLine();

        // Group by sub-type
        var grouped = GroupCutRecords(cat, cutRecords, snapshots);

        foreach (var (groupName, records) in grouped.OrderByDescending(g => g.Records.Count))
        {
            if (grouped.Count > 1)
            {
                sb.AppendLine($"### {groupName}");
                sb.AppendLine();
            }

            sb.AppendLine("| Name | Editor ID | Form ID | First Seen | Last Seen |");
            sb.AppendLine("|------|-----------|---------|------------|-----------|");

            foreach (var r in records.OrderBy(r => r.Name ?? r.EditorId ?? $"0x{r.FormId:X8}"))
            {
                var heading = GetTcrfHeading(r.Name, r.EditorId, r.FormId);
                sb.AppendLine($"| {Esc(heading)} | {r.EditorId ?? ""} | 0x{r.FormId:X8} | {Esc(r.FirstSeen)} | {Esc(r.LastSeen)} |");
            }

            sb.AppendLine();
        }
    }

    private static List<(string GroupName, List<CutRecord> Records)> GroupCutRecords(
        CategoryDef cat, List<CutRecord> records, List<VersionSnapshot> snapshots)
    {
        var groups = new Dictionary<string, List<CutRecord>>();

        foreach (var record in records)
        {
            var groupName = LookupGroupForFormId(cat, record.FormId, snapshots);
            if (!groups.TryGetValue(groupName, out var list))
            {
                list = [];
                groups[groupName] = list;
            }

            list.Add(record);
        }

        return groups.Select(kvp => (kvp.Key, kvp.Value)).ToList();
    }

    #endregion

    #region Changed Content Section

    private static void WriteChangedContentSection(
        StringBuilder sb, CategoryDef cat,
        Dictionary<(string Category, uint FormId), RecordHistory> catHistories,
        List<VersionSnapshot> snapshots)
    {
        if (catHistories.Count == 0)
        {
            return;
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Changed Content");
        sb.AppendLine();
        sb.AppendLine("Records that changed across development builds.");
        sb.AppendLine();

        // Build index
        var buildIndex = new Dictionary<string, int>();
        for (var i = 0; i < snapshots.Count; i++)
        {
            buildIndex[snapshots[i].Build.Label] = i + 1;
        }

        // Separate significant vs minor
        var significantHistories = new List<(string Group, RecordHistory History)>();
        var minorHistories = new List<(string Group, RecordHistory History)>();

        foreach (var ((_, formId), history) in catHistories)
        {
            var group = LookupGroupForFormId(cat, formId, snapshots);
            if (HasSignificantChanges(history))
            {
                significantHistories.Add((group, history));
            }
            else
            {
                minorHistories.Add((group, history));
            }
        }

        // Write significant changes grouped
        var significantGroups = significantHistories
            .GroupBy(x => x.Group)
            .OrderByDescending(g => g.Count());

        foreach (var group in significantGroups)
        {
            var groupHistories = group.OrderBy(x => x.History.FullName ?? x.History.EditorId ?? "").ToList();

            if (significantGroups.Count() > 1 || minorHistories.Count > 0)
            {
                sb.AppendLine($"### {group.Key} ({groupHistories.Count})");
                sb.AppendLine();
            }

            foreach (var (_, history) in groupHistories)
            {
                WriteRecordTable(sb, history, buildIndex);
            }
        }

        // Write minor changes in collapsible block
        if (minorHistories.Count > 0)
        {
            sb.AppendLine($"<details>");
            sb.AppendLine($"<summary>{minorHistories.Count} records with minor changes (flags, padding, etc.)</summary>");
            sb.AppendLine();

            var minorGroups = minorHistories
                .GroupBy(x => x.Group)
                .OrderByDescending(g => g.Count());

            foreach (var group in minorGroups)
            {
                if (minorGroups.Count() > 1)
                {
                    sb.AppendLine($"### {group.Key}");
                    sb.AppendLine();
                }

                foreach (var (_, history) in group.OrderBy(x => x.History.FormId))
                {
                    WriteRecordTable(sb, history, buildIndex);
                }
            }

            sb.AppendLine("</details>");
            sb.AppendLine();
        }
    }

    private static void WriteRecordTable(
        StringBuilder sb, RecordHistory history, Dictionary<string, int> buildIndex,
        string headingLevel = "####")
    {
        var heading = GetTcrfHeading(history.FullName, history.EditorId, history.FormId);
        sb.AppendLine($"{headingLevel} {Esc(heading)} (0x{history.FormId:X8})");
        sb.AppendLine();

        // Collect all field names in order of first appearance
        var allFields = new List<string>();
        var seenFields = new HashSet<string>();
        foreach (var cp in history.ChangePoints)
        {
            foreach (var fc in cp.FieldChanges)
            {
                if (seenFields.Add(fc.FieldName))
                {
                    allFields.Add(fc.FieldName);
                }
            }
        }

        // Pre-compute Before/Final values for each field
        var beforeValues = new Dictionary<string, string?>();
        var finalValues = new Dictionary<string, string?>();
        foreach (var field in allFields)
        {
            var first = history.ChangePoints.FirstOrDefault(cp => cp.FieldChanges.Any(f => f.FieldName == field));
            var last = history.ChangePoints.LastOrDefault(cp => cp.FieldChanges.Any(f => f.FieldName == field));
            beforeValues[field] = first?.FieldChanges.First(f => f.FieldName == field).OldValue;
            finalValues[field] = last?.FieldChanges.First(f => f.FieldName == field).NewValue;
        }

        // Determine if Final column is needed (skip if every field's last change is the last column)
        var lastCp = history.ChangePoints[^1];
        var needsFinalColumn = allFields.Any(field =>
        {
            var lastForField = history.ChangePoints.LastOrDefault(cp => cp.FieldChanges.Any(f => f.FieldName == field));
            return lastForField != lastCp;
        });

        // Column headers
        var columnHeaders = history.ChangePoints
            .Select(cp =>
            {
                var num = buildIndex.TryGetValue(cp.BuildLabel, out var idx) ? $"#{idx}" : "?";
                var date = cp.BuildDate?.ToString("MMM d, yyyy") ?? "Unknown";
                return $"{num} ({date})";
            })
            .ToList();

        sb.Append("| | Before |");
        foreach (var header in columnHeaders)
        {
            sb.Append($" {Esc(header)} |");
        }

        if (needsFinalColumn)
        {
            sb.Append(" Final |");
        }

        sb.AppendLine();

        sb.Append("|---|---|");
        foreach (var _ in columnHeaders)
        {
            sb.Append("---|");
        }

        if (needsFinalColumn)
        {
            sb.Append("---|");
        }

        sb.AppendLine();

        // Data rows
        foreach (var field in allFields)
        {
            var before = beforeValues[field] ?? "(none)";
            sb.Append($"| **{Esc(field)}** | {Esc(before)} |");

            foreach (var cp in history.ChangePoints)
            {
                var fc = cp.FieldChanges.FirstOrDefault(f => f.FieldName == field);
                var value = fc != null ? (fc.NewValue ?? "(none)") : "-";
                sb.Append($" {Esc(value)} |");
            }

            if (needsFinalColumn)
            {
                var final_ = finalValues[field] ?? "(none)";
                sb.Append($" {Esc(final_)} |");
            }

            sb.AppendLine();
        }

        sb.AppendLine();
    }

    #endregion

    #region Data Computation

    private static Dictionary<(string Category, uint FormId), RecordHistory> BuildAllHistories(
        List<VersionSnapshot> snapshots, List<VersionDiffResult> diffs)
    {
        var histories = new Dictionary<(string Category, uint FormId), RecordHistory>();

        for (var i = 0; i < diffs.Count; i++)
        {
            var toBuild = snapshots[i + 1].Build;

            foreach (var cat in Categories)
            {
                foreach (var change in cat.DiffSelector(diffs[i]))
                {
                    if (change.ChangeType != ChangeType.Changed || change.FieldChanges.Count == 0)
                    {
                        continue;
                    }

                    var key = (cat.Name, change.FormId);
                    if (!histories.TryGetValue(key, out var history))
                    {
                        history = new RecordHistory(change.FormId, change.EditorId, change.FullName, cat.Name, []);
                        histories[key] = history;
                    }

                    history.ChangePoints.Add(new ChangePoint(toBuild.Label, toBuild.BuildDate, change.FieldChanges));
                }
            }
        }

        return histories;
    }

    private static Dictionary<string, List<CutRecord>> ComputeAllCutRecords(
        List<VersionSnapshot> allSnapshots,
        List<VersionSnapshot> dmpSnapshots,
        VersionSnapshot finalEsm,
        HashSet<uint>? fo3LeftoverFormIds)
    {
        var result = new Dictionary<string, List<CutRecord>>();

        result["Quests"] = FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Quests), finalEsm.Quests, allSnapshots, fo3LeftoverFormIds);
        result["NPCs"] = FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Npcs), finalEsm.Npcs, allSnapshots, fo3LeftoverFormIds);
        result["Weapons"] = FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Weapons), finalEsm.Weapons, allSnapshots, fo3LeftoverFormIds);
        result["Armor"] = FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Armor), finalEsm.Armor, allSnapshots, fo3LeftoverFormIds);
        result["Items"] = FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Items), finalEsm.Items, allSnapshots, fo3LeftoverFormIds);
        result["Scripts"] = FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Scripts), finalEsm.Scripts, allSnapshots, fo3LeftoverFormIds);
        result["Dialogues"] = FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Dialogues), finalEsm.Dialogues, allSnapshots, fo3LeftoverFormIds);
        result["Locations"] = FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Locations), finalEsm.Locations, allSnapshots, fo3LeftoverFormIds);
        result["Placements"] = FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Placements), finalEsm.Placements, allSnapshots, fo3LeftoverFormIds);
        result["Creatures"] = FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Creatures), finalEsm.Creatures, allSnapshots, fo3LeftoverFormIds);
        result["Perks"] = FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Perks), finalEsm.Perks, allSnapshots, fo3LeftoverFormIds);
        result["Ammo"] = FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Ammo), finalEsm.Ammo, allSnapshots, fo3LeftoverFormIds);
        result["Leveled Lists"] = FindCutRecords(
            dmpSnapshots.SelectMany(s => s.LeveledLists), finalEsm.LeveledLists, allSnapshots, fo3LeftoverFormIds);
        result["Notes"] = FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Notes), finalEsm.Notes, allSnapshots, fo3LeftoverFormIds);
        result["Terminals"] = FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Terminals), finalEsm.Terminals, allSnapshots, fo3LeftoverFormIds);

        return result;
    }

    private static List<CutRecord> FindCutRecords<T>(
        IEnumerable<KeyValuePair<uint, T>> dmpRecords,
        Dictionary<uint, T> finalEsmRecords,
        List<VersionSnapshot> allSnapshots,
        HashSet<uint>? fo3LeftoverFormIds)
    {
        var seen = new Dictionary<uint, CutRecord>();

        foreach (var (formId, record) in dmpRecords)
        {
            if (finalEsmRecords.ContainsKey(formId))
            {
                continue;
            }

            if (fo3LeftoverFormIds != null && fo3LeftoverFormIds.Contains(formId))
            {
                continue;
            }

            if (!seen.ContainsKey(formId))
            {
                var editorId = GetEditorId(record);
                var name = GetName(record);
                var firstSnapshot = allSnapshots.FirstOrDefault(s => ContainsFormId(s, formId));
                var lastSnapshot = allSnapshots.LastOrDefault(s => ContainsFormId(s, formId));

                seen[formId] = new CutRecord(formId, editorId, name,
                    firstSnapshot?.Build.Label ?? "?",
                    lastSnapshot?.Build.Label ?? "?");
            }
        }

        return seen.Values.OrderBy(r => r.FormId).ToList();
    }

    private static List<(string Name, string FileName, int Total, int Cut, int Changed)>
        ComputeCategoryStats(
            VersionSnapshot? finalEsm,
            Dictionary<(string Category, uint FormId), RecordHistory> histories,
            Dictionary<string, List<CutRecord>> cutData)
    {
        var stats = new List<(string, string, int, int, int)>();

        foreach (var cat in Categories)
        {
            var total = finalEsm != null ? cat.Counter(finalEsm) : 0;
            cutData.TryGetValue(cat.Name, out var cutRecords);
            var cut = cutRecords?.Count ?? 0;
            var changed = histories.Count(kvp => kvp.Key.Category == cat.Name);

            stats.Add((cat.Name, cat.FileName, total, cut, changed));
        }

        return stats;
    }

    #endregion

    #region Grouping Classifiers

    private static string LookupGroupForFormId(CategoryDef cat, uint formId, List<VersionSnapshot> snapshots)
    {
        // Find the record in any snapshot to classify it
        foreach (var snapshot in snapshots)
        {
            foreach (var (key, value) in cat.RecordSelector(snapshot))
            {
                if (key == formId)
                {
                    return cat.GroupClassifier(value, snapshots);
                }
            }
        }

        return "Other";
    }

    private static string ClassifyQuest(object record)
    {
        if (record is TrackedQuest quest)
        {
            if (quest.Stages.Count > 0 || quest.Objectives.Count > 0)
            {
                return "Quests with Stages/Objectives";
            }

            return "Other Quests";
        }

        return "Other";
    }

    private static string GetWeaponTypeName(object record)
    {
        if (record is TrackedWeapon weapon)
        {
            return weapon.WeaponType switch
            {
                0 => "Hand-to-Hand",
                1 => "Melee (1-Handed)",
                2 => "Melee (2-Handed)",
                3 => "Pistols",
                4 => "Rifles",
                5 => "Automatic Weapons",
                6 => "Energy Pistols",
                7 => "Energy Rifles",
                8 => "Energy Automatic",
                9 => "Thrown",
                10 => "Mines",
                11 => "Launchers",
                12 => "Fat Man",
                _ => $"Unknown Type ({weapon.WeaponType})"
            };
        }

        return "Weapons";
    }

    private static string GetArmorSlotGroup(object record)
    {
        if (record is TrackedArmor armor)
        {
            var flags = armor.BipedFlags;

            // Biped flags (Fallout NV): bit 0 = Head, bit 1 = Hair, bit 2 = Upper Body,
            // bit 3 = Left Hand, bit 4 = Right Hand, bit 5 = Weapon, bit 14 = Body Addon,
            // etc. Simplified classification:
            var hasHead = (flags & 0x3) != 0; // Head or Hair
            var hasBody = (flags & 0x4) != 0; // Upper Body
            var hasHands = (flags & 0x18) != 0; // Left/Right Hand

            if (hasBody && hasHead)
            {
                return "Full Suits / Power Armor";
            }

            if (hasBody)
            {
                return "Body Armor";
            }

            if (hasHead)
            {
                return "Headgear";
            }

            if (hasHands)
            {
                return "Gloves / Gauntlets";
            }

            return "Accessories / Other";
        }

        return "Armor";
    }

    private static string GetItemSubtype(object record)
    {
        if (record is TrackedItem item)
        {
            return item.RecordType switch
            {
                "ALCH" => "Consumables",
                "MISC" => "Miscellaneous Items",
                "KEYM" => "Keys",
                _ => item.RecordType
            };
        }

        return "Items";
    }

    private static string GetScriptGroup(object record)
    {
        if (record is TrackedScript script)
        {
            return $"{script.ScriptType} Scripts";
        }

        return "Scripts";
    }

    private static string GetDialogueQuestGroup(object record, List<VersionSnapshot> snapshots)
    {
        if (record is TrackedDialogue dialogue && dialogue.QuestFormId.HasValue)
        {
            var questId = dialogue.QuestFormId.Value;
            foreach (var snapshot in snapshots)
            {
                if (snapshot.Quests.TryGetValue(questId, out var quest))
                {
                    var name = quest.FullName ?? quest.EditorId ?? $"0x{questId:X8}";
                    return $"{name} (0x{questId:X8})";
                }
            }

            return $"Quest 0x{questId:X8}";
        }

        return "Other Dialogues";
    }

    private static string GetLocationGroup(object record)
    {
        if (record is TrackedLocation loc)
        {
            if (loc.RecordType == "WRLD")
            {
                return "Worldspaces";
            }

            return loc.IsInterior ? "Interior Cells" : "Exterior Cells";
        }

        return "Locations";
    }

    private static string GetPlacementGroup(object record)
    {
        if (record is TrackedPlacement placement)
        {
            return placement.IsMapMarker ? "Map Markers" : "Other Placements";
        }

        return "Placements";
    }

    private static string GetCreatureTypeGroup(object record)
    {
        if (record is TrackedCreature creature)
        {
            return creature.CreatureType switch
            {
                0 => "Animals",
                1 => "Mutated Animals",
                2 => "Mutated Insects",
                3 => "Abominations",
                4 => "Super Mutants",
                5 => "Feral Ghouls",
                6 => "Robots",
                7 => "Giant Insects",
                _ => $"Unknown Type ({creature.CreatureType})"
            };
        }

        return "Creatures";
    }

    private static string GetPerkGroup(object record)
    {
        if (record is TrackedPerk perk)
        {
            return perk.Trait != 0 ? "Traits" : "Perks";
        }

        return "Perks";
    }

    private static string GetLeveledListGroup(object record)
    {
        if (record is TrackedLeveledList list)
        {
            return list.ListType switch
            {
                "LVLC" => "Leveled Creatures",
                "LVLN" => "Leveled NPCs",
                "LVLI" => "Leveled Items",
                _ => $"Leveled Lists ({list.ListType})"
            };
        }

        return "Leveled Lists";
    }

    private static string GetNoteGroup(object record)
    {
        if (record is TrackedNote note)
        {
            return note.NoteType switch
            {
                0 => "Sound Notes",
                1 => "Text Notes",
                2 => "Image Notes",
                3 => "Voice Notes",
                _ => $"Notes (Type {note.NoteType})"
            };
        }

        return "Notes";
    }

    #endregion

    #region Helpers

    private static bool HasSignificantChanges(RecordHistory history)
    {
        return history.ChangePoints.Any(cp => cp.FieldChanges.Any(f =>
            f.FieldName is "FullName" or "PromptText" or "ResponseTexts" or "SourceText" ||
            f.FieldName.StartsWith("Stage ", StringComparison.Ordinal) ||
            f.FieldName.StartsWith("Objective ", StringComparison.Ordinal) ||
            f.FieldName is "Damage" or "Value" or "DamageThreshold" or "ClipSize" or "Level" ||
            f.FieldName is "WeaponType" or "AmmoFormId" or "MarkerName" ||
            f.FieldName is "AttackDamage" or "CreatureType" or "Description" or "Ranks" or "MinLevel" ||
            f.FieldName is "Speed" or "ChanceNone" or "EntryCount" or "NoteType" or "Text" ||
            f.FieldName is "Difficulty" or "Password" or "HeaderText" or "MenuItemCount"));
    }

    private static string GetTcrfHeading(string? fullName, string? editorId, uint formId)
    {
        if (!string.IsNullOrEmpty(fullName))
        {
            return fullName;
        }

        if (!string.IsNullOrEmpty(editorId))
        {
            return editorId;
        }

        return $"0x{formId:X8}";
    }

    private static string? GetEditorId<T>(T record)
    {
        return record switch
        {
            TrackedQuest q => q.EditorId,
            TrackedNpc n => n.EditorId,
            TrackedWeapon w => w.EditorId,
            TrackedArmor a => a.EditorId,
            TrackedItem i => i.EditorId,
            TrackedScript s => s.EditorId,
            TrackedDialogue d => d.EditorId,
            TrackedLocation l => l.EditorId,
            TrackedPlacement p => p.EditorId,
            TrackedCreature c => c.EditorId,
            TrackedPerk p => p.EditorId,
            TrackedAmmo a => a.EditorId,
            TrackedLeveledList l => l.EditorId,
            TrackedNote n => n.EditorId,
            TrackedTerminal t => t.EditorId,
            _ => null
        };
    }

    private static string? GetName<T>(T record)
    {
        return record switch
        {
            TrackedQuest q => q.FullName,
            TrackedNpc n => n.FullName,
            TrackedWeapon w => w.FullName,
            TrackedArmor a => a.FullName,
            TrackedItem i => i.FullName,
            TrackedLocation l => l.FullName,
            TrackedPlacement p => p.MarkerName,
            TrackedCreature c => c.FullName,
            TrackedPerk p => p.FullName,
            TrackedAmmo a => a.FullName,
            TrackedNote n => n.FullName,
            TrackedTerminal t => t.FullName,
            _ => null
        };
    }

    private static bool ContainsFormId(VersionSnapshot snapshot, uint formId)
    {
        return snapshot.Quests.ContainsKey(formId) || snapshot.Npcs.ContainsKey(formId) ||
               snapshot.Weapons.ContainsKey(formId) || snapshot.Armor.ContainsKey(formId) ||
               snapshot.Items.ContainsKey(formId) || snapshot.Scripts.ContainsKey(formId) ||
               snapshot.Dialogues.ContainsKey(formId) || snapshot.Locations.ContainsKey(formId) ||
               snapshot.Placements.ContainsKey(formId) || snapshot.Creatures.ContainsKey(formId) ||
               snapshot.Perks.ContainsKey(formId) || snapshot.Ammo.ContainsKey(formId) ||
               snapshot.LeveledLists.ContainsKey(formId) || snapshot.Notes.ContainsKey(formId) ||
               snapshot.Terminals.ContainsKey(formId);
    }

    private static void WriteFormIdHistory(StringBuilder sb, List<VersionSnapshot> snapshots, uint formId)
    {
        sb.AppendLine($"## FormID History: 0x{formId:X8}");
        sb.AppendLine();
        sb.AppendLine("| Build | Date | Present | Name | Details |");
        sb.AppendLine("|-------|------|---------|------|---------|");

        foreach (var snapshot in snapshots)
        {
            var date = snapshot.Build.BuildDate?.ToString("yyyy-MM-dd") ?? "?";

            if (snapshot.Quests.TryGetValue(formId, out var quest))
            {
                var stages = string.Join(", ", quest.Stages.Select(s => s.Index));
                var objectives = string.Join("; ", quest.Objectives.Select(o => o.DisplayText ?? "?"));
                sb.AppendLine($"| {Esc(snapshot.Build.Label)} | {date} | QUST | {quest.FullName ?? quest.EditorId ?? ""} | Stages: [{stages}] Objectives: [{objectives}] |");
            }
            else if (snapshot.Npcs.TryGetValue(formId, out var npc))
            {
                sb.AppendLine($"| {Esc(snapshot.Build.Label)} | {date} | NPC_ | {npc.FullName ?? npc.EditorId ?? ""} | Level: {npc.Level} |");
            }
            else if (snapshot.Weapons.TryGetValue(formId, out var weapon))
            {
                sb.AppendLine($"| {Esc(snapshot.Build.Label)} | {date} | WEAP | {weapon.FullName ?? weapon.EditorId ?? ""} | Dmg: {weapon.Damage}, Clip: {weapon.ClipSize} |");
            }
            else if (snapshot.Armor.TryGetValue(formId, out var armor))
            {
                sb.AppendLine($"| {Esc(snapshot.Build.Label)} | {date} | ARMO | {armor.FullName ?? armor.EditorId ?? ""} | DT: {armor.DamageThreshold:F1} |");
            }
            else if (snapshot.Items.TryGetValue(formId, out var item))
            {
                sb.AppendLine($"| {Esc(snapshot.Build.Label)} | {date} | {item.RecordType} | {item.FullName ?? item.EditorId ?? ""} | Value: {item.Value} |");
            }
            else if (snapshot.Scripts.TryGetValue(formId, out var script))
            {
                var hasSource = !string.IsNullOrEmpty(script.SourceText);
                sb.AppendLine($"| {Esc(snapshot.Build.Label)} | {date} | SCPT | {script.EditorId ?? ""} | {script.ScriptType}, Vars: {script.VariableCount}, Source: {(hasSource ? "Yes" : "No")} |");
            }
            else if (snapshot.Dialogues.TryGetValue(formId, out var dialogue))
            {
                var text = dialogue.ResponseTexts.FirstOrDefault() ?? "";
                sb.AppendLine($"| {Esc(snapshot.Build.Label)} | {date} | INFO | {dialogue.EditorId ?? ""} | {Esc(text)} |");
            }
            else if (snapshot.Locations.TryGetValue(formId, out var location))
            {
                sb.AppendLine($"| {Esc(snapshot.Build.Label)} | {date} | {location.RecordType} | {location.FullName ?? location.EditorId ?? ""} | |");
            }
            else if (snapshot.Creatures.TryGetValue(formId, out var creature))
            {
                sb.AppendLine($"| {Esc(snapshot.Build.Label)} | {date} | CREA | {creature.FullName ?? creature.EditorId ?? ""} | Level: {creature.Level}, Dmg: {creature.AttackDamage} |");
            }
            else if (snapshot.Perks.TryGetValue(formId, out var perk))
            {
                sb.AppendLine($"| {Esc(snapshot.Build.Label)} | {date} | PERK | {perk.FullName ?? perk.EditorId ?? ""} | Ranks: {perk.Ranks}, MinLevel: {perk.MinLevel} |");
            }
            else if (snapshot.Ammo.TryGetValue(formId, out var ammo))
            {
                sb.AppendLine($"| {Esc(snapshot.Build.Label)} | {date} | AMMO | {ammo.FullName ?? ammo.EditorId ?? ""} | Value: {ammo.Value}, Speed: {ammo.Speed:F1} |");
            }
            else if (snapshot.LeveledLists.TryGetValue(formId, out var leveledList))
            {
                sb.AppendLine($"| {Esc(snapshot.Build.Label)} | {date} | {leveledList.ListType} | {leveledList.EditorId ?? ""} | Entries: {leveledList.Entries.Count}, ChanceNone: {leveledList.ChanceNone} |");
            }
            else if (snapshot.Notes.TryGetValue(formId, out var note))
            {
                sb.AppendLine($"| {Esc(snapshot.Build.Label)} | {date} | NOTE | {note.FullName ?? note.EditorId ?? ""} | Type: {note.NoteType} |");
            }
            else if (snapshot.Terminals.TryGetValue(formId, out var terminal))
            {
                sb.AppendLine($"| {Esc(snapshot.Build.Label)} | {date} | TERM | {terminal.FullName ?? terminal.EditorId ?? ""} | MenuItems: {terminal.MenuItemCount} |");
            }
            else
            {
                sb.AppendLine($"| {Esc(snapshot.Build.Label)} | {date} | - | (not found) | |");
            }
        }
    }

    /// <summary>Escape pipe characters for markdown table cells.</summary>
    private static string Esc(string text) => text.Replace("|", "\\|");

    #endregion
}
