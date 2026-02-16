using System.Text;
using FalloutXbox360Utils.Core.VersionTracking.Models;

namespace FalloutXbox360Utils.Core.VersionTracking.Reporting;

/// <summary>
///     Generates monospace tables in fenced code blocks.
///     Suitable for pasting into markdown-based chat platforms.
/// </summary>
public static class CodeBlockTimelineWriter
{
    private static readonly (string Label, Func<VersionDiffResult, List<RecordChange>> Selector,
        Func<VersionSnapshot, int> Counter)[] Categories =
    [
        ("Quests", d => d.QuestChanges, s => s.Quests.Count),
        ("NPCs", d => d.NpcChanges, s => s.Npcs.Count),
        ("Dialogues", d => d.DialogueChanges, s => s.Dialogues.Count),
        ("Weapons", d => d.WeaponChanges, s => s.Weapons.Count),
        ("Armor", d => d.ArmorChanges, s => s.Armor.Count),
        ("Items", d => d.ItemChanges, s => s.Items.Count),
        ("Scripts", d => d.ScriptChanges, s => s.Scripts.Count),
        ("Locations", d => d.LocationChanges, s => s.Locations.Count),
        ("Placements", d => d.PlacementChanges, s => s.Placements.Count),
        ("Creatures", d => d.CreatureChanges, s => s.Creatures.Count),
        ("Perks", d => d.PerkChanges, s => s.Perks.Count),
        ("Ammo", d => d.AmmoChanges, s => s.Ammo.Count),
        ("Leveled Lists", d => d.LeveledListChanges, s => s.LeveledLists.Count),
        ("Notes", d => d.NoteChanges, s => s.Notes.Count),
        ("Terminals", d => d.TerminalChanges, s => s.Terminals.Count)
    ];

    /// <summary>
    ///     Generates a full timeline summary with code-block tables.
    /// </summary>
    public static string WriteTimeline(
        List<VersionSnapshot> snapshots,
        List<VersionDiffResult> diffs)
    {
        var sb = new StringBuilder();

        sb.AppendLine("**Fallout: New Vegas — Version Tracking**");
        sb.AppendLine();

        // Build Timeline
        WriteBuildTimeline(sb, snapshots);

        // Diff summaries
        foreach (var diff in diffs)
        {
            if (diff.TotalAdded + diff.TotalRemoved + diff.TotalChanged == 0)
            {
                continue;
            }

            WriteDiffSummary(sb, diff);
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Generates a focused diff summary for a single build vs baseline.
    /// </summary>
    public static string WriteDiffSummary(
        VersionDiffResult diff,
        VersionSnapshot targetBuild,
        VersionSnapshot baselineBuild)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"**{targetBuild.Build.Label} vs {baselineBuild.Build.Label}**");
        sb.AppendLine();

        WriteDiffSummary(sb, diff);

        // Cut content details
        var cutRecords = diff.AllChanges.Where(c => c.ChangeType == ChangeType.Added).ToList();
        if (cutRecords.Count > 0)
        {
            WriteCutDetails(sb, cutRecords);
        }

        return sb.ToString();
    }

    private static void WriteBuildTimeline(StringBuilder sb, List<VersionSnapshot> snapshots)
    {
        sb.AppendLine("__Build Timeline__");

        // Compute column widths
        var rows = snapshots.Select((s, i) => new[]
        {
            (i + 1).ToString(),
            s.Build.Label,
            s.Build.BuildDate?.ToString("yyyy-MM-dd") ?? "Unknown",
            s.Build.SourceType == BuildSourceType.Esm ? "ESM" : "DMP",
            s.Quests.Count.ToString("N0"),
            s.Npcs.Count.ToString("N0"),
            s.Weapons.Count.ToString("N0"),
            s.Armor.Count.ToString("N0"),
            s.TotalRecordCount.ToString("N0")
        }).ToList();

        var headers = new[] { "#", "Build", "Date", "Type", "Quests", "NPCs", "Weapons", "Armor", "Total" };
        WriteCodeBlockTable(sb, headers, rows);
        sb.AppendLine();
    }

    private static void WriteDiffSummary(StringBuilder sb, VersionDiffResult diff)
    {
        sb.AppendLine($"__{diff.FromBuild.Label} → {diff.ToBuild.Label}__");

        var rows = new List<string[]>();
        foreach (var (label, selector, _) in Categories)
        {
            var changes = selector(diff);
            if (changes.Count == 0)
            {
                continue;
            }

            var added = changes.Count(c => c.ChangeType == ChangeType.Added);
            var removed = changes.Count(c => c.ChangeType == ChangeType.Removed);
            var changed = changes.Count(c => c.ChangeType == ChangeType.Changed);
            rows.Add([label, added.ToString(), removed.ToString(), changed.ToString()]);
        }

        var headers = new[] { "Category", "Added", "Removed", "Changed" };
        WriteCodeBlockTable(sb, headers, rows);
        sb.AppendLine();
    }

    private static void WriteCutDetails(StringBuilder sb, List<RecordChange> cutRecords)
    {
        // Group by record type
        var grouped = cutRecords
            .GroupBy(r => r.RecordType)
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var group in grouped)
        {
            var typeName = group.Key switch
            {
                "QUST" => "Quests",
                "NPC_" => "NPCs",
                "WEAP" => "Weapons",
                "ARMO" => "Armor",
                "INFO" => "Dialogues",
                "SCPT" => "Scripts",
                _ => group.Key
            };

            sb.AppendLine($"__Cut {typeName}__");

            var rows = group
                .OrderBy(r => r.FormId)
                .Select(r => new[]
                {
                    $"0x{r.FormId:X8}",
                    Truncate(r.EditorId ?? "", 28),
                    Truncate(r.FullName ?? "", 30)
                })
                .ToList();

            var headers = new[] { "FormID", "EditorID", "Name" };
            WriteCodeBlockTable(sb, headers, rows);
            sb.AppendLine();
        }
    }

    private static void WriteCodeBlockTable(StringBuilder sb, string[] headers, List<string[]> rows)
    {
        // Compute column widths
        var widths = new int[headers.Length];
        for (var i = 0; i < headers.Length; i++)
        {
            widths[i] = headers[i].Length;
        }

        foreach (var row in rows)
        {
            for (var i = 0; i < Math.Min(row.Length, widths.Length); i++)
            {
                widths[i] = Math.Max(widths[i], row[i].Length);
            }
        }

        // Add padding
        for (var i = 0; i < widths.Length; i++)
        {
            widths[i] += 2;
        }

        sb.AppendLine("```");

        // Header row
        var headerLine = new StringBuilder(" ");
        for (var i = 0; i < headers.Length; i++)
        {
            headerLine.Append(headers[i].PadRight(widths[i]));
        }

        sb.AppendLine(headerLine.ToString().TrimEnd());

        // Data rows
        foreach (var row in rows)
        {
            var line = new StringBuilder(" ");
            for (var i = 0; i < Math.Min(row.Length, widths.Length); i++)
            {
                line.Append(row[i].PadRight(widths[i]));
            }

            var rowStr = line.ToString().TrimEnd();
            sb.AppendLine(rowStr);
        }

        sb.AppendLine("```");
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        return text[..(maxLength - 3)] + "...";
    }
}
