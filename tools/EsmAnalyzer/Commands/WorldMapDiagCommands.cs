using System.CommandLine;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Diagnoses world map category distribution and identifies sources of "Unknown" categorization.
/// </summary>
public static class WorldMapDiagCommands
{
    public static Command CreateWorldMapDiagCommand()
    {
        var command = new Command("worldmap-diag",
            "Diagnose world map category distribution and Unknown sources");

        var fileArg = new Argument<string>("file") { Description = "Path to ESM or DMP file" };
        var limitOption = new Option<int>("-l", "--limit")
        {
            Description = "Maximum entries to show per table",
            DefaultValueFactory = _ => 30
        };

        command.Arguments.Add(fileArg);
        command.Options.Add(limitOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var limit = parseResult.GetValue(limitOption);
            await RunDiagAsync(file, limit, cancellationToken);
        });

        return command;
    }

    private static async Task RunDiagAsync(string filePath, int limit, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] File not found: {filePath}");
            return;
        }

        // Phase 1: Analyze ESM
        AnsiConsole.MarkupLine($"[blue]Analyzing:[/] {Path.GetFileName(filePath)}");
        var result = await EsmFileAnalyzer.AnalyzeAsync(filePath, cancellationToken: cancellationToken);

        if (result.EsmRecords == null)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Failed to parse ESM records");
            return;
        }

        // Phase 2: Semantic reconstruction
        RecordCollection records;
        AnsiConsole.MarkupLine("[blue]Reconstructing records...[/]");
        using (var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0,
                   MemoryMappedFileAccess.Read))
        using (var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
        {
            var parser = new RecordParser(result.EsmRecords, result.FormIdMap, accessor, result.FileSize);
            records = parser.ReconstructAll();
        }

        // Phase 3: Build category index using authoritative logic (no duplication)
        var (_, categoryIndex) = ObjectBoundsIndex.BuildCombined(records);
        AnsiConsole.MarkupLine($"[green]Categorized {categoryIndex.Count:N0} base object FormIDs[/]");

        // Phase 3b: Build raw FormID → record type from scan data (covers ALL records)
        var rawFormIdToType = result.EsmRecords.MainRecords
            .GroupBy(r => r.FormId)
            .Where(g => g.Key != 0)
            .ToDictionary(g => g.Key, g => g.First().RecordType);
        AnsiConsole.MarkupLine($"[green]Indexed {rawFormIdToType.Count:N0} raw FormIDs from scan[/]");

        // Phase 4: Collect all placed references
        var allRefs = new List<PlacedReference>();
        foreach (var cell in records.Cells)
        {
            allRefs.AddRange(cell.PlacedObjects);
        }

        foreach (var ws in records.Worldspaces)
        {
            foreach (var cell in ws.Cells)
            {
                allRefs.AddRange(cell.PlacedObjects);
            }
        }

        AnsiConsole.MarkupLine($"[green]Found {allRefs.Count:N0} placed references[/]");
        AnsiConsole.WriteLine();

        // Phase 5: Categorize and report
        var categorized = new Dictionary<string, int>();
        var unknownByBaseType = new Dictionary<string, List<(uint BaseFormId, string? EditorId, string? ModelPath)>>();
        var staticByFolder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var refr in allRefs)
        {
            PlacedObjectCategory category;
            if (refr.IsMapMarker)
            {
                category = PlacedObjectCategory.MapMarker;
            }
            else
            {
                category = refr.RecordType switch
                {
                    "ACHR" => PlacedObjectCategory.Npc,
                    "ACRE" => PlacedObjectCategory.Creature,
                    _ => categoryIndex.GetValueOrDefault(refr.BaseFormId, PlacedObjectCategory.Unknown)
                };
            }

            var categoryStr = category.ToString();
            categorized.TryGetValue(categoryStr, out var count);
            categorized[categoryStr] = count + 1;

            // Track unknowns
            if (category == PlacedObjectCategory.Unknown)
            {
                var baseTypeStr = rawFormIdToType.GetValueOrDefault(refr.BaseFormId, "???");
                if (!unknownByBaseType.TryGetValue(baseTypeStr, out var unkList))
                {
                    unkList = [];
                    unknownByBaseType[baseTypeStr] = unkList;
                }

                var editorId = records.FormIdToEditorId.GetValueOrDefault(refr.BaseFormId);
                unkList.Add((refr.BaseFormId, editorId, refr.ModelPath));
            }

            // Track static model path folders
            if (category == PlacedObjectCategory.Static && refr.ModelPath != null)
            {
                var folder = ExtractTopFolder(refr.ModelPath);
                if (folder != null)
                {
                    staticByFolder.TryGetValue(folder, out var fCount);
                    staticByFolder[folder] = fCount + 1;
                }
            }
        }

        // === Report: Category Distribution ===
        var catTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Category Distribution[/]")
            .AddColumn(new TableColumn("[bold]Category[/]"))
            .AddColumn(new TableColumn("[bold]Count[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]%[/]").RightAligned());

        foreach (var (cat, cnt) in categorized.OrderByDescending(kv => kv.Value))
        {
            var pct = 100.0 * cnt / allRefs.Count;
            var color = cat == "Unknown" ? "red" : "cyan";
            _ = catTable.AddRow($"[{color}]{Markup.Escape(cat)}[/]", cnt.ToString("N0"), $"{pct:F1}%");
        }

        AnsiConsole.Write(catTable);
        AnsiConsole.WriteLine();

        // === Report: Unknown Breakdown ===
        if (unknownByBaseType.Count > 0)
        {
            var unkTable = new Table()
                .Border(TableBorder.Rounded)
                .Title("[bold red]Unknown Breakdown by Base Record Type[/]")
                .AddColumn(new TableColumn("[bold]Base Type[/]"))
                .AddColumn(new TableColumn("[bold]Count[/]").RightAligned())
                .AddColumn(new TableColumn("[bold]Sample FormIDs[/]"));

            foreach (var (baseType, refs) in unknownByBaseType.OrderByDescending(kv => kv.Value.Count).Take(limit))
            {
                var samples = refs.Take(5)
                    .Select(r =>
                    {
                        var label = $"0x{r.BaseFormId:X8}";
                        if (r.EditorId != null)
                        {
                            label += $" ({r.EditorId})";
                        }

                        return label;
                    });
                _ = unkTable.AddRow(
                    $"[yellow]{Markup.Escape(baseType)}[/]",
                    refs.Count.ToString("N0"),
                    Markup.Escape(string.Join(", ", samples)));
            }

            AnsiConsole.Write(unkTable);
            AnsiConsole.WriteLine();

            // Show unknown model paths if available
            var unknownWithPaths = unknownByBaseType.Values
                .SelectMany(refs => refs)
                .Where(r => r.ModelPath != null)
                .GroupBy(r => ExtractTopFolder(r.ModelPath!) ?? "(no folder)")
                .OrderByDescending(g => g.Count())
                .Take(limit)
                .ToList();

            if (unknownWithPaths.Count > 0)
            {
                var pathTable = new Table()
                    .Border(TableBorder.Rounded)
                    .Title("[bold red]Unknown Objects by Model Path Folder[/]")
                    .AddColumn(new TableColumn("[bold]Folder[/]"))
                    .AddColumn(new TableColumn("[bold]Count[/]").RightAligned())
                    .AddColumn(new TableColumn("[bold]Sample Path[/]"));

                foreach (var g in unknownWithPaths)
                {
                    var sample = g.First().ModelPath ?? "";
                    _ = pathTable.AddRow(
                        $"[yellow]{Markup.Escape(g.Key)}[/]",
                        g.Count().ToString("N0"),
                        Markup.Escape(sample.Length > 60 ? sample[..57] + "..." : sample));
                }

                AnsiConsole.Write(pathTable);
                AnsiConsole.WriteLine();
            }
        }
        else
        {
            AnsiConsole.MarkupLine("[green]No Unknown references found![/]");
            AnsiConsole.WriteLine();
        }

        // === Report: Static Model Path Folder Distribution ===
        if (staticByFolder.Count > 0)
        {
            var folderTable = new Table()
                .Border(TableBorder.Rounded)
                .Title("[bold]Static/MSTT Objects by Model Path Folder (not yet sub-categorized)[/]")
                .AddColumn(new TableColumn("[bold]Folder[/]"))
                .AddColumn(new TableColumn("[bold]Count[/]").RightAligned());

            foreach (var (folder, cnt) in staticByFolder.OrderByDescending(kv => kv.Value).Take(limit))
            {
                _ = folderTable.AddRow($"[cyan]{Markup.Escape(folder)}[/]", cnt.ToString("N0"));
            }

            AnsiConsole.Write(folderTable);
            AnsiConsole.WriteLine();
        }

        // Summary
        var totalUnknown = categorized.GetValueOrDefault("Unknown", 0);
        var unknownPct = allRefs.Count > 0 ? 100.0 * totalUnknown / allRefs.Count : 0;
        AnsiConsole.MarkupLine($"[bold]Summary:[/] {totalUnknown:N0} Unknown out of {allRefs.Count:N0} total ({unknownPct:F1}%)");
    }

    /// <summary>
    ///     Extracts the top-level folder from a model path, stripping meshes\ and DLCxx\ prefixes.
    /// </summary>
    private static string? ExtractTopFolder(string modelPath)
    {
        var path = modelPath.AsSpan();

        // Strip "meshes\" or "meshes/" prefix
        if (path.Length > 7 &&
            (path[..7].Equals("meshes\\", StringComparison.OrdinalIgnoreCase) ||
             path[..7].Equals("meshes/", StringComparison.OrdinalIgnoreCase)))
        {
            path = path[7..];
        }

        // Strip DLC directory prefix (numeric: DLC01\, DLC02\, etc.)
        if (path.Length > 6 &&
            path[..3].Equals("dlc", StringComparison.OrdinalIgnoreCase) &&
            path[3] >= '0' && path[3] <= '9' &&
            path[4] >= '0' && path[4] <= '9' &&
            (path[5] == '\\' || path[5] == '/'))
        {
            path = path[6..];
        }

        // Strip named DLC folder prefixes (dlcanch\, DLCPitt\, etc.)
        if (path.Length >= 5 && path[..3].Equals("dlc", StringComparison.OrdinalIgnoreCase))
        {
            var dlcSep = path.IndexOfAny('\\', '/');
            if (dlcSep > 3)
            {
                path = path[(dlcSep + 1)..];
            }
        }

        var sepIndex = path.IndexOfAny('\\', '/');
        return sepIndex <= 0 ? null : path[..sepIndex].ToString();
    }

}
