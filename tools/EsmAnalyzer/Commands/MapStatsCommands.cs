using System.CommandLine;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Per-worldspace placed object category statistics, useful for tuning the world map color scheme.
/// </summary>
public static class MapStatsCommands
{
    public static Command CreateMapStatsCommand()
    {
        var command = new Command("map-stats",
            "Per-worldspace placed object category counts (for color scheme tuning)");

        var fileArg = new Argument<string>("file") { Description = "Path to ESM or DMP file" };
        var wsOption = new Option<string?>("--worldspace", "-w")
        {
            Description = "Filter to a single worldspace by EditorID (e.g. WastelandNV)"
        };

        command.Arguments.Add(fileArg);
        command.Options.Add(wsOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var wsFilter = parseResult.GetValue(wsOption);
            await RunAsync(file, wsFilter, cancellationToken);
        });

        return command;
    }

    private static async Task RunAsync(string filePath, string? wsFilter, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] File not found: {filePath}");
            return;
        }

        AnsiConsole.MarkupLine($"[blue]Analyzing:[/] {Path.GetFileName(filePath)}");
        var result = await EsmFileAnalyzer.AnalyzeAsync(filePath, cancellationToken: cancellationToken);

        if (result.EsmRecords == null)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Failed to parse ESM records");
            return;
        }

        RecordCollection records;
        AnsiConsole.MarkupLine("[blue]Reconstructing records...[/]");
        using (var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0,
                   MemoryMappedFileAccess.Read))
        using (var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
        {
            var parser = new RecordParser(result.EsmRecords, result.FormIdMap, accessor, result.FileSize);
            records = parser.ParseAll();
        }

        var (_, categoryIndex) = ObjectBoundsIndex.BuildCombined(records);
        AnsiConsole.MarkupLine($"[green]Categorized {categoryIndex.Count:N0} base object FormIDs[/]");
        AnsiConsole.WriteLine();

        // Collect placed objects per worldspace
        foreach (var ws in records.Worldspaces)
        {
            if (wsFilter != null &&
                !string.Equals(ws.EditorId, wsFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var wsName = ws.FullName ?? ws.EditorId ?? $"0x{ws.FormId:X8}";
            var allRefs = new List<PlacedReference>();
            foreach (var cell in ws.Cells)
            {
                allRefs.AddRange(cell.PlacedObjects);
            }

            if (allRefs.Count == 0)
            {
                continue;
            }

            OutputWorldspaceStats(wsName, allRefs, categoryIndex);
        }

        // Also show unlinked cells if no filter or no worldspace matched
        if (wsFilter == null)
        {
            var unlinkedRefs = new List<PlacedReference>();
            var linkedCellFormIds = new HashSet<uint>();
            foreach (var ws in records.Worldspaces)
            {
                foreach (var cell in ws.Cells)
                {
                    linkedCellFormIds.Add(cell.FormId);
                }
            }

            foreach (var cell in records.Cells)
            {
                if (!cell.IsInterior && !linkedCellFormIds.Contains(cell.FormId))
                {
                    unlinkedRefs.AddRange(cell.PlacedObjects);
                }
            }

            if (unlinkedRefs.Count > 0)
            {
                OutputWorldspaceStats("Unlinked Exterior", unlinkedRefs, categoryIndex);
            }

            // Interior cells
            var interiorRefs = new List<PlacedReference>();
            foreach (var cell in records.Cells)
            {
                if (cell.IsInterior)
                {
                    interiorRefs.AddRange(cell.PlacedObjects);
                }
            }

            if (interiorRefs.Count > 0)
            {
                OutputWorldspaceStats("Interiors (all)", interiorRefs, categoryIndex);
            }
        }
    }

    private static void OutputWorldspaceStats(
        string label,
        List<PlacedReference> allRefs,
        Dictionary<uint, PlacedObjectCategory> categoryIndex)
    {
        var counts = new Dictionary<PlacedObjectCategory, int>();
        foreach (var refr in allRefs)
        {
            var category = GetObjectCategory(refr, categoryIndex);
            counts.TryGetValue(category, out var count);
            counts[category] = count + 1;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[bold]{Markup.Escape(label)}[/] — {allRefs.Count:N0} placed objects")
            .AddColumn(new TableColumn("[bold]Category[/]"))
            .AddColumn(new TableColumn("[bold]Count[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]%[/]").RightAligned());

        foreach (var (cat, cnt) in counts.OrderByDescending(kv => kv.Value))
        {
            var pct = 100.0 * cnt / allRefs.Count;
            var color = pct >= 10.0 ? "green" : pct >= 1.0 ? "cyan" : "grey";
            _ = table.AddRow($"[{color}]{cat}[/]", cnt.ToString("N0"), $"{pct:F1}%");
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static PlacedObjectCategory GetObjectCategory(
        PlacedReference refr,
        Dictionary<uint, PlacedObjectCategory> categoryIndex)
    {
        if (refr.IsMapMarker)
        {
            return PlacedObjectCategory.MapMarker;
        }

        return refr.RecordType switch
        {
            "ACHR" => PlacedObjectCategory.Npc,
            "ACRE" => PlacedObjectCategory.Creature,
            _ => categoryIndex.GetValueOrDefault(refr.BaseFormId, PlacedObjectCategory.Unknown)
        };
    }
}
