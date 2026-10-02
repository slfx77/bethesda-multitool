using System.CommandLine;
using System.IO.MemoryMappedFiles;
using System.Text;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.CLI.Shared;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Inspection;
using BethesdaMultitool.Core.Formats.Esm.Analysis.FileAnalysis;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Semantic.LoadOrder;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Esm;

/// <summary>
///     Semantic-level cell inspection commands that use the full parsing pipeline.
///     Unlike <c>cell-children</c> (which inspects raw GRUPs), these commands provide
///     enriched output with NPC names, categories, and persistent cell overlay.
/// </summary>
public static class EsmCellCommand
{
    private const float CellWorldSize = 4096f;

    public static Command CreateObjectsCommand()
    {
        var command = new Command("objects",
            "List placed objects in a cell (enriched with names, categories, persistent overlay)");

        var fileArg = new Argument<string>("file") { Description = "Path to ESM file" };
        var cellArg = new Argument<string>("cell")
        {
            Description = "Cell FormID (hex) or name (case-insensitive substring match)"
        };
        var persistentOption = new Option<bool>("-p", "--include-persistent")
        {
            Description = "Include other persistent refs in the same worldspace " +
                          "whose positions fall within this cell's grid bounds"
        };
        var typeOption = new Option<string?>("-t", "--type")
        {
            Description = "Filter by record type (ACHR, ACRE, REFR)"
        };
        var limitOption = new Option<int>("-l", "--limit")
        {
            Description = "Maximum rows to display (0 = unlimited)",
            DefaultValueFactory = _ => 50
        };

        var formatOption = new Option<string>("--format")
        {
            Description = "Output format: table, json, or csv (JSON/CSV preserve full float precision)",
            DefaultValueFactory = _ => "table"
        };
        var outputOption = new Option<string?>("--output") { Description = "Write to a new file; existing files are never overwritten" };
        var offsetOption = new Option<string?>("--offset") { Description = "Select a physical CELL occurrence by decimal or 0x hexadecimal file offset" };
        var loadOrderOption = LoadOrderOptions.CreateOption();
        var allowMissingOption = LoadOrderOptions.CreateAllowMissingMastersOption();
        command.Arguments.Add(fileArg);
        command.Arguments.Add(cellArg);
        command.Options.Add(persistentOption);
        command.Options.Add(typeOption);
        command.Options.Add(limitOption);
        command.Options.Add(formatOption);
        command.Options.Add(outputOption);
        command.Options.Add(offsetOption);
        command.Options.Add(loadOrderOption);
        command.Options.Add(allowMissingOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            return await RunObjectsAsync(
                parseResult.GetValue(fileArg)!,
                parseResult.GetValue(cellArg)!,
                parseResult.GetValue(persistentOption),
                parseResult.GetValue(typeOption),
                parseResult.GetValue(limitOption),
                parseResult.GetValue(formatOption)!,
                parseResult.GetValue(outputOption),
                parseResult.GetValue(offsetOption),
                cancellationToken, parseResult.GetValue(loadOrderOption), parseResult.GetValue(allowMissingOption));
        });

        return command;
    }

    public static Command CreateNpcTraceCommand()
    {
        var command = new Command("npc-trace",
            "Trace an NPC from base FormID through ACHR placement to cell, position, and visual cell");

        var fileArg = new Argument<string>("file") { Description = "Path to ESM file" };
        var formidArg = new Argument<string>("formid")
        {
            Description = "NPC_ base FormID or ACHR ref FormID (hex)"
        };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(formidArg);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await RunNpcTraceAsync(
                parseResult.GetValue(fileArg)!,
                parseResult.GetValue(formidArg)!,
                cancellationToken);
        });

        return command;
    }

    internal static async Task<int> RunObjectsAsync(
        string filePath, string cellQuery, bool includePersistent, string? typeFilter, int limit,
        string format, string? outputPath, string? offsetText, CancellationToken cancellationToken,
        string[]? loadOrderSpecifications = null, bool allowMissingMasters = false)
    {
        // Parent command actions do not run for subcommands. Establish this command's async
        // logging sink before semantic parsing so redirected stdout remains a document.
        Logger.SetOutput(Console.Error);
        format = format.ToLowerInvariant();
        typeFilter = string.IsNullOrWhiteSpace(typeFilter) ? null : typeFilter.ToUpperInvariant();
        if (format is not ("table" or "json" or "csv") || limit < 0 ||
            (typeFilter != null && !PlacementQuery.IsPlacement(typeFilter)))
        {
            Console.Error.WriteLine("Use --format table|json|csv, --limit >= 0, and --type REFR|ACHR|ACRE.");
            return 2;
        }
        long? offset = null;
        if (offsetText != null)
        {
            if (!PlacementQuery.TryParseOffset(offsetText, out var parsedOffset))
            {
                Console.Error.WriteLine("Invalid --offset: use a nonnegative decimal or 0x hexadecimal file offset.");
                return 2;
            }
            offset = parsedOffset;
        }
        if (string.IsNullOrWhiteSpace(cellQuery))
        {
            Console.Error.WriteLine("A cell FormID, EditorID or name is required.");
            return 2;
        }
        try
        {
            if (loadOrderSpecifications is { Length: > 0 })
            {
                if (offset.HasValue) throw new ArgumentException("--offset selects a single-file occurrence; omit --load-order for physical occurrence inspection.");
                var order = PluginLoadOrder.Open(LoadOrderOptions.ResolvePaths(filePath, loadOrderSpecifications), allowMissingMasters);
                var view = await LoadOrderSelectionView.LoadAsync(order, cancellationToken, retainPhysicalPlacements: true);
                var cells = PlacementQuery.CollectCells(view.Records);
                var qualifier = cellQuery.IndexOf(':');
                var qualified = qualifier > 0 && (cellQuery[..qualifier].EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ||
                    cellQuery[..qualifier].EndsWith(".esp", StringComparison.OrdinalIgnoreCase));
                var query = qualified
                    ? $"0x{view.Index.ResolveTarget(cellQuery, order):X8}" : cellQuery;
                var matches = PlacementQuery.ResolveCells(cells, query);
                if (matches.Count != 1) return Reject(matches);
                var selectedCell = matches[0];
                var (objects, issues) = LoadOrderPlacementQuery.InCell(view, selectedCell, includePersistent, typeFilter);
                var winner = view.Index.Records[selectedCell.FormId].Winner;
                return Emit(CellObjectsOutput.Create(filePath, cellQuery, selectedCell, includePersistent, typeFilter, limit, objects) with
                {
                    LoadOrder = order.Entries.Select(entry => entry.Path).ToArray(), MissingMasters = order.MissingMasters,
                    SelectionIssues = issues, CellSourcePath = winner.FilePath, FileLocalCellFormId = winner.FileLocalFormId
                });
            }
            if (allowMissingMasters) throw new ArgumentException("--allow-missing-masters requires --load-order.");
            // No progress UI on stdout: it must remain a standalone JSON/CSV document.
            using var source = await UnifiedAnalyzer.AnalyzeAsync(filePath, cancellationToken: cancellationToken);
            var candidates = PlacementQuery.ResolveCells(PlacementQuery.ReadCells(source), cellQuery, offset);
            if (candidates.Count != 1) return Reject(candidates);
            var selected = candidates[0];
            var rows = PlacementQuery.InCell(PlacementQuery.Read(source, cancellationToken: cancellationToken),
                selected, includePersistent, typeFilter);
            var report = CellObjectsOutput.Create(filePath, cellQuery, selected, includePersistent, typeFilter, limit, rows);
            return Emit(report);

            int Reject(IReadOnlyList<CellRecord> candidates)
            {
                Console.Error.WriteLine(candidates.Count == 0 ? $"No selected cell found matching: {cellQuery}" :
                    $"Ambiguous cell lookup ({candidates.Count} occurrences). Use an exact FormID/EditorID; single-file queries also support --offset:");
                foreach (var cell in candidates.Take(20))
                    Console.Error.WriteLine($"  0x{cell.FormId:X8} offset=0x{cell.Offset:X} {cell.EditorId} {cell.FullName}");
                return candidates.Count == 0 ? 1 : 2;
            }

            int Emit(CellObjectsReport report)
            {
                if (outputPath == null) { CellObjectsOutput.Write(Console.Out, format, report); }
                else
                {
                    using var stream = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                    CellObjectsOutput.Write(writer, format, report);
                }
                Console.Error.WriteLine($"Cell objects: {report.ReturnedCount} of {report.TotalCount} occurrences; truncated={report.Truncated}; excluded={report.ExcludedCount}.");
                return 0;
            }
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Cell objects failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task RunNpcTraceAsync(
        string filePath, string formidStr, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] File not found: {filePath}");
            return;
        }

        var targetFormId = EsmFileLoader.ParseFormId(formidStr);
        if (!targetFormId.HasValue)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid FormID: {formidStr}");
            return;
        }

        var (records, scanResult) = await LoadRecordsAsync(filePath, cancellationToken);
        if (records == null || scanResult == null)
        {
            return;
        }

        var allCells = CollectAllCells(records);

        // Determine if the FormID is a base NPC_ or an ACHR ref
        var npc = records.Npcs.FirstOrDefault(n => n.FormId == targetFormId.Value);
        uint baseFormId;
        if (npc != null)
        {
            baseFormId = npc.FormId;
        }
        else
        {
            // Check if it's an ACHR ref — find its base
            var achrRef = allCells.SelectMany(c => c.PlacedObjects)
                .FirstOrDefault(o => o.FormId == targetFormId.Value && o.RecordType == "ACHR");

            if (achrRef != null)
            {
                baseFormId = achrRef.BaseFormId;
                npc = records.Npcs.FirstOrDefault(n => n.FormId == baseFormId);

                AnsiConsole.MarkupLine(
                    $"[dim]Input 0x{targetFormId.Value:X8} is an ACHR ref → base NPC_ 0x{baseFormId:X8}[/]");
                AnsiConsole.WriteLine();
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]FormID 0x{targetFormId.Value:X8} is not a recognized NPC_ base or ACHR ref.[/]");
                return;
            }
        }

        // NPC identity
        AnsiConsole.MarkupLine("[bold cyan]NPC Identity[/]");
        AnsiConsole.MarkupLine($"  Base FormID:  0x{baseFormId:X8}");
        AnsiConsole.MarkupLine($"  Editor ID:    {Markup.Escape(npc?.EditorId ?? "(unknown)")}");
        AnsiConsole.MarkupLine($"  Display Name: {Markup.Escape(npc?.FullName ?? "(unknown)")}");
        AnsiConsole.WriteLine();

        // Find all ACHR refs that reference this NPC
        var placements = new List<(PlacedReference Ref, CellRecord Cell)>();
        foreach (var cell in allCells)
        {
            foreach (var obj in cell.PlacedObjects)
            {
                if (obj.RecordType == "ACHR" && obj.BaseFormId == baseFormId)
                {
                    placements.Add((obj, cell));
                }
            }
        }

        if (placements.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No ACHR placements found for this NPC.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[bold cyan]Placements[/] ({placements.Count} ACHR refs)");
        AnsiConsole.WriteLine();

        // Build worldspace lookup
        var worldspaceById = records.Worldspaces.ToDictionary(ws => ws.FormId);

        // Build cell grid lookup for visual cell resolution
        var cellByGrid = new Dictionary<(uint wsFormId, int gx, int gy), CellRecord>();
        foreach (var cell in allCells)
        {
            if (cell.GridX.HasValue && cell.GridY.HasValue && cell.WorldspaceFormId is > 0)
            {
                cellByGrid.TryAdd((cell.WorldspaceFormId.Value, cell.GridX.Value, cell.GridY.Value), cell);
            }
        }

        foreach (var (refr, cell) in placements)
        {
            // Cell info
            var cellLabel = cell.EditorId ?? cell.FullName ?? $"0x{cell.FormId:X8}";
            var isGridless = !cell.GridX.HasValue || !cell.GridY.HasValue;
            var cellType = isGridless ? "persistent cell" : $"grid ({cell.GridX}, {cell.GridY})";

            // Worldspace info
            string? wsName = null;
            if (cell.WorldspaceFormId is > 0 && worldspaceById.TryGetValue(cell.WorldspaceFormId.Value, out var ws))
            {
                wsName = ws.EditorId ?? ws.FullName ?? $"0x{ws.FormId:X8}";
            }

            // Visual cell (computed from position)
            var visualGridX = (int)MathF.Floor(refr.X / CellWorldSize);
            var visualGridY = (int)MathF.Floor(refr.Y / CellWorldSize);
            CellRecord? visualCell = null;
            if (cell.WorldspaceFormId is > 0)
            {
                cellByGrid.TryGetValue(
                    (cell.WorldspaceFormId.Value, visualGridX, visualGridY), out visualCell);
            }

            var visualLabel = visualCell != null
                ? $"{visualCell.EditorId ?? visualCell.FullName ?? $"0x{visualCell.FormId:X8}"} " +
                  $"({visualGridX}, {visualGridY})"
                : $"({visualGridX}, {visualGridY})";

            // Output
            var table = new Table()
                .Border(TableBorder.Rounded)
                .HideHeaders()
                .AddColumn(new TableColumn("Key").Width(18))
                .AddColumn("Value");

            _ = table.AddRow("[cyan]ACHR Ref[/]", $"0x{refr.FormId:X8}");
            _ = table.AddRow("[cyan]Position[/]", $"({refr.X:F1}, {refr.Y:F1}, {refr.Z:F1})");
            _ = table.AddRow("[cyan]Parent Cell[/]",
                $"0x{cell.FormId:X8} ({Markup.Escape(cellLabel)}) — {cellType}");
            if (wsName != null)
            {
                _ = table.AddRow("[cyan]Worldspace[/]",
                    $"0x{cell.WorldspaceFormId!.Value:X8} ({Markup.Escape(wsName)})");
            }

            _ = table.AddRow("[cyan]Visual Cell[/]", Markup.Escape(visualLabel));
            _ = table.AddRow("[cyan]Persistent[/]", refr.IsPersistent ? "[yellow]Yes[/]" : "No");
            _ = table.AddRow("[cyan]Disabled[/]", refr.IsInitiallyDisabled ? "[yellow]Yes[/]" : "No");

            if (refr.AssignmentSource != null)
            {
                _ = table.AddRow("[cyan]Assigned Via[/]", Markup.Escape(refr.AssignmentSource));
            }

            if (refr.EnableParentFormId is > 0)
            {
                var parentEditorId =
                    records.FormIdToEditorId.GetValueOrDefault(refr.EnableParentFormId.Value);
                _ = table.AddRow("[cyan]Enable Parent[/]",
                    $"0x{refr.EnableParentFormId.Value:X8}" +
                    (parentEditorId != null ? $" ({Markup.Escape(parentEditorId)})" : "") +
                    (refr.EnableParentFlags.HasValue
                        ? $" flags=0x{refr.EnableParentFlags.Value:X2}"
                        : ""));
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
        }
    }

    #region Pipeline

    private static async Task<(RecordCollection? Records, EsmRecordScanResult? ScanResult)> LoadRecordsAsync(
        string filePath, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine($"[blue]Analyzing:[/] {Path.GetFileName(filePath)}");
        var result = await EsmFileAnalyzer.AnalyzeAsync(filePath, cancellationToken: cancellationToken);

        if (result.EsmRecords == null)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Failed to parse ESM records");
            return (null, null);
        }

        RecordCollection records;
        AnsiConsole.MarkupLine("[blue]Parsing records...[/]");
        using (var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0,
                   MemoryMappedFileAccess.Read))
        using (var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
        {
            var parser = new RecordParser(result.EsmRecords, result.FormIdMap, accessor, result.FileSize);
            records = parser.ParseAll(cancellationToken: cancellationToken);
        }

        AnsiConsole.MarkupLine(
            $"[green]Loaded {records.Cells.Count:N0} cells, " +
            $"{records.Worldspaces.Count:N0} worldspaces, " +
            $"{records.Npcs.Count:N0} NPCs[/]");
        AnsiConsole.WriteLine();

        return (records, result.EsmRecords);
    }

    #endregion

    #region Helpers

    private static List<CellRecord> CollectAllCells(RecordCollection records) => PlacementQuery.CollectCells(records);

    #endregion
}
