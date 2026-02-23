using System.Text;
using Spectre.Console;
using static EsmAnalyzer.Commands.OrphanedRefAnalyzer;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Display and export logic for orphaned FormID analysis results.
///     Renders Spectre.Console tables and writes TSV output files.
/// </summary>
internal static class OrphanedRefDisplay
{
    public static void DisplayStats(
        OrphanStats stats, bool hasDumps, bool hasCompare, bool allRecords, int allRecordOrphanCount)
    {
        AnsiConsole.MarkupLine("[bold]Scan Statistics:[/]");
        var table = new Table();
        table.AddColumn("Metric");
        table.AddColumn(new TableColumn("Count").RightAligned());
        table.Border = TableBorder.Rounded;

        table.AddRow("ESM scripts scanned", $"{stats.ScriptsScanned:N0}");
        if (hasDumps)
        {
            table.AddRow("Dump scripts scanned", $"{stats.DumpScriptsScanned:N0}");
        }

        table.AddRow("Total SCRO references", $"{stats.TotalScroRefs:N0}");
        table.AddRow("[yellow]Orphaned references (local)[/]", $"[yellow]{stats.OrphanedRefs:N0}[/]");
        table.AddRow("[grey]External plugin references[/]", $"[grey]{stats.ExternalRefs:N0}[/]");
        table.AddRow("Unique orphaned FormIDs", $"{stats.UniqueOrphanedFormIds:N0}");

        if (hasCompare)
        {
            table.AddRow("[green]Orphans found in compare file[/]", $"[green]{stats.ExistInCompareFile:N0}[/]");
        }

        if (allRecords)
        {
            table.AddRow("All-record FormID fields checked", $"{stats.AllRecordFormIdFieldsChecked:N0}");
            table.AddRow("[yellow]All-record orphans[/]", $"[yellow]{allRecordOrphanCount:N0}[/]");
        }

        AnsiConsole.Write(table);
    }

    public static void DisplayOrphansByFormId(
        List<OrphanedReference> orphans, int limit, bool hasCompare)
    {
        if (orphans.Count == 0)
        {
            return;
        }

        // Group by orphan FormID
        var grouped = orphans
            .GroupBy(o => o.OrphanedFormId)
            .OrderByDescending(g => g.Count())
            .ToList();

        AnsiConsole.MarkupLine(
            $"[bold yellow]Orphaned FormID References ({orphans.Count:N0} total, {grouped.Count:N0} unique FormIDs)[/]");
        if (orphans.Count > limit)
        {
            AnsiConsole.MarkupLine($"[grey](showing first {limit} references)[/]");
        }

        AnsiConsole.WriteLine();

        // Detail table
        var table = new Table();
        table.AddColumn("Source");
        table.AddColumn("Script");
        table.AddColumn("Script FID");
        table.AddColumn("Orphaned FID");
        if (hasCompare)
        {
            table.AddColumn("In Compare?");
        }

        table.AddColumn("Context");
        table.Border = TableBorder.Rounded;

        var shown = 0;
        foreach (var orphan in orphans.Take(limit))
        {
            var context = orphan.DecompiledContext ?? "[dim](no context)[/]";
            // Escape Spectre markup characters in context
            context = context.Replace("[", "[[").Replace("]", "]]");

            var row = new List<string>
            {
                orphan.Source,
                Markup.Escape(orphan.ScriptEditorId),
                $"0x{orphan.ScriptFormId:X8}",
                $"0x{orphan.OrphanedFormId:X8}"
            };

            if (hasCompare)
            {
                if (orphan.ExistsInCompareFile)
                {
                    var info = orphan.CompareRecordType ?? "?";
                    if (orphan.CompareEdid != null)
                    {
                        info += $" {Markup.Escape(orphan.CompareEdid)}";
                    }

                    row.Add($"[green]Yes[/] ({info})");
                }
                else
                {
                    row.Add("[dim]No[/]");
                }
            }

            row.Add(context);
            table.AddRow(row.ToArray());
            shown++;
        }

        AnsiConsole.Write(table);

        // Multi-reference summary
        var multiRef = grouped.Where(g => g.Count() > 1).ToList();
        if (multiRef.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Orphaned FormIDs referenced by multiple scripts:[/]");
            foreach (var group in multiRef.Take(20))
            {
                var scriptNames = string.Join(", ",
                    group.Select(o => o.ScriptEditorId).Distinct().Take(5));
                AnsiConsole.MarkupLine(
                    $"  0x{group.Key:X8}: Referenced by {group.Count()} scripts ({Markup.Escape(scriptNames)})");
            }
        }
    }

    public static void DisplayExternalRefs(List<OrphanedReference> externals)
    {
        var byPlugin = externals.GroupBy(e => e.PluginIndex).OrderBy(g => g.Key).ToList();
        AnsiConsole.MarkupLine(
            $"[bold grey]External Plugin References ({externals.Count:N0} total, {byPlugin.Count} plugins)[/]");
        AnsiConsole.MarkupLine("[grey]These reference master files not in this ESM — expected, not orphans.[/]");

        var table = new Table();
        table.AddColumn("Plugin Index");
        table.AddColumn(new TableColumn("Count").RightAligned());
        table.AddColumn("Sample FormIDs");
        table.Border = TableBorder.Rounded;

        foreach (var group in byPlugin)
        {
            var samples = string.Join(", ",
                group.Select(e => $"0x{e.OrphanedFormId:X8}").Distinct().Take(3));
            table.AddRow($"0x{group.Key:X2}", $"{group.Count():N0}", samples);
        }

        AnsiConsole.Write(table);
    }

    public static void DisplayAllRecordOrphans(
        List<AllRecordOrphanedReference> orphans, int limit,
        Dictionary<uint, string> edidMap,
        HashSet<uint>? compareFormIds,
        Dictionary<uint, string>? compareEdidMap,
        Dictionary<uint, string>? compareRecordTypeMap)
    {
        AnsiConsole.MarkupLine($"[bold yellow]All-Record Orphaned FormID References ({orphans.Count:N0})[/]");
        if (orphans.Count > limit)
        {
            AnsiConsole.MarkupLine($"[grey](showing first {limit})[/]");
        }

        // Group by record type
        var byType = orphans.GroupBy(o => o.RecordType).OrderByDescending(g => g.Count()).ToList();
        var summaryTable = new Table();
        summaryTable.AddColumn("Record Type");
        summaryTable.AddColumn(new TableColumn("Orphan Count").RightAligned());
        summaryTable.Border = TableBorder.Rounded;
        foreach (var group in byType.Take(20))
        {
            summaryTable.AddRow(group.Key, $"{group.Count():N0}");
        }

        AnsiConsole.Write(summaryTable);
        AnsiConsole.WriteLine();

        var table = new Table();
        table.AddColumn("Record");
        table.AddColumn("Subrecord");
        table.AddColumn("Field");
        table.AddColumn("Orphaned FID");
        if (compareFormIds != null)
        {
            table.AddColumn("In Compare?");
        }

        table.Border = TableBorder.Rounded;

        foreach (var orphan in orphans.Take(limit))
        {
            var recordEdid = edidMap.GetValueOrDefault(orphan.RecordFormId);
            var recordLabel = recordEdid != null
                ? $"{orphan.RecordType} {Markup.Escape(recordEdid)}"
                : $"{orphan.RecordType} 0x{orphan.RecordFormId:X8}";

            var row = new List<string>
            {
                recordLabel,
                orphan.SubrecordType,
                orphan.FieldName,
                $"0x{orphan.OrphanedFormId:X8}"
            };

            if (compareFormIds != null)
            {
                if (compareFormIds.Contains(orphan.OrphanedFormId))
                {
                    var info = compareRecordTypeMap?.GetValueOrDefault(orphan.OrphanedFormId) ?? "?";
                    var edid = compareEdidMap?.GetValueOrDefault(orphan.OrphanedFormId);
                    if (edid != null)
                    {
                        info += $" {Markup.Escape(edid)}";
                    }

                    row.Add($"[green]Yes[/] ({info})");
                }
                else
                {
                    row.Add("[dim]No[/]");
                }
            }

            table.AddRow(row.ToArray());
        }

        AnsiConsole.Write(table);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // TSV Export
    // ═══════════════════════════════════════════════════════════════════════

    public static void WriteTsvOutput(
        string outputPath,
        List<OrphanedReference> localOrphans,
        List<OrphanedReference> externalOrphans,
        List<AllRecordOrphanedReference> allRecordOrphans,
        Dictionary<uint, string> edidMap,
        HashSet<uint>? compareFormIds,
        Dictionary<uint, string>? compareEdidMap,
        Dictionary<uint, string>? compareRecordTypeMap)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");
        using var writer = new StreamWriter(outputPath, false, Encoding.UTF8);

        writer.WriteLine(
            "Category\tSource\tScriptEditorId\tScriptFormId\tOrphanedFormId\tInCompareFile\tCompareEdid\tCompareRecordType\tContext");

        foreach (var o in localOrphans)
        {
            var inCompare = compareFormIds?.Contains(o.OrphanedFormId) == true ? "Yes" : "No";
            var compareEdid = compareEdidMap?.GetValueOrDefault(o.OrphanedFormId) ?? "";
            var compareType = compareRecordTypeMap?.GetValueOrDefault(o.OrphanedFormId) ?? "";
            writer.WriteLine(
                $"Script\t{o.Source}\t{o.ScriptEditorId}\t0x{o.ScriptFormId:X8}\t0x{o.OrphanedFormId:X8}\t{inCompare}\t{compareEdid}\t{compareType}\t{o.DecompiledContext ?? ""}");
        }

        foreach (var o in externalOrphans)
        {
            writer.WriteLine(
                $"External\t{o.Source}\t{o.ScriptEditorId}\t0x{o.ScriptFormId:X8}\t0x{o.OrphanedFormId:X8}\t\t\t\tPlugin index 0x{o.PluginIndex:X2}");
        }

        foreach (var o in allRecordOrphans)
        {
            var inCompare = compareFormIds?.Contains(o.OrphanedFormId) == true ? "Yes" : "No";
            var compareEdid = compareEdidMap?.GetValueOrDefault(o.OrphanedFormId) ?? "";
            var compareType = compareRecordTypeMap?.GetValueOrDefault(o.OrphanedFormId) ?? "";
            var recordEdid = edidMap.GetValueOrDefault(o.RecordFormId) ?? o.RecordType;
            writer.WriteLine(
                $"AllRecord\t{o.RecordType}\t{recordEdid}\t0x{o.RecordFormId:X8}\t0x{o.OrphanedFormId:X8}\t{inCompare}\t{compareEdid}\t{compareType}\t{o.SubrecordType}.{o.FieldName}");
        }

        AnsiConsole.MarkupLine($"[grey]Full results written to: {Path.GetFullPath(outputPath)}[/]");
    }
}
