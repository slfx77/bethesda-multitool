using System.CommandLine;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

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
            Description = "Include persistent refs from the worldspace persistent cell " +
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

        command.Arguments.Add(fileArg);
        command.Arguments.Add(cellArg);
        command.Options.Add(persistentOption);
        command.Options.Add(typeOption);
        command.Options.Add(limitOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await RunObjectsAsync(
                parseResult.GetValue(fileArg)!,
                parseResult.GetValue(cellArg)!,
                parseResult.GetValue(persistentOption),
                parseResult.GetValue(typeOption),
                parseResult.GetValue(limitOption),
                cancellationToken);
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

    private static async Task RunObjectsAsync(
        string filePath, string cellQuery, bool includePersistent,
        string? typeFilter, int limit, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] File not found: {filePath}");
            return;
        }

        var (records, scanResult) = await LoadRecordsAsync(filePath, cancellationToken);
        if (records == null || scanResult == null)
        {
            return;
        }

        // Find all cells (worldspace cells + top-level cells)
        var allCells = CollectAllCells(records);

        // Resolve the target cell
        var targetCell = FindCell(allCells, cellQuery);
        if (targetCell == null)
        {
            AnsiConsole.MarkupLine($"[yellow]No cell found matching:[/] {Markup.Escape(cellQuery)}");

            // Show close matches
            var candidates = allCells
                .Where(c => (c.EditorId != null &&
                             c.EditorId.Contains(cellQuery, StringComparison.OrdinalIgnoreCase)) ||
                            (c.FullName != null &&
                             c.FullName.Contains(cellQuery, StringComparison.OrdinalIgnoreCase)))
                .Take(10)
                .ToList();

            if (candidates.Count > 0)
            {
                AnsiConsole.MarkupLine("[dim]Did you mean:[/]");
                foreach (var c in candidates)
                {
                    AnsiConsole.MarkupLine(
                        $"  0x{c.FormId:X8}  {Markup.Escape(c.EditorId ?? "(no EDID)")}  " +
                        $"Grid=[{c.GridX?.ToString() ?? "?"},{c.GridY?.ToString() ?? "?"}]");
                }
            }

            return;
        }

        // Build category index
        var (_, categoryIndex) = ObjectBoundsIndex.BuildCombined(records);

        // Header
        AnsiConsole.MarkupLine($"[bold cyan]Cell:[/] 0x{targetCell.FormId:X8}  " +
                               $"EDID={Markup.Escape(targetCell.EditorId ?? "(none)")}  " +
                               $"Name={Markup.Escape(targetCell.FullName ?? "(none)")}");
        AnsiConsole.MarkupLine($"[bold cyan]Grid:[/] ({targetCell.GridX?.ToString() ?? "?"}, " +
                               $"{targetCell.GridY?.ToString() ?? "?"})  " +
                               $"Interior={targetCell.IsInterior}  " +
                               $"Worldspace=0x{targetCell.WorldspaceFormId ?? 0:X8}");
        AnsiConsole.WriteLine();

        // Collect objects to display
        var objects = FilterObjects(targetCell.PlacedObjects, typeFilter);
        var directCount = objects.Count;

        // Persistent overlay
        List<PlacedReference> persistentOverlay = [];
        if (includePersistent && targetCell.GridX.HasValue && targetCell.GridY.HasValue)
        {
            persistentOverlay = FindPersistentOverlay(
                allCells, targetCell, typeFilter);
            objects.AddRange(persistentOverlay);
        }

        AnsiConsole.MarkupLine($"[cyan]Direct objects:[/] {directCount}");
        if (persistentOverlay.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"[cyan]Persistent overlay:[/] {persistentOverlay.Count} (from worldspace persistent cell)");
        }

        AnsiConsole.MarkupLine($"[cyan]Total:[/] {objects.Count}");
        AnsiConsole.WriteLine();

        // Summary by type
        var typeCounts = objects.GroupBy(o => o.RecordType)
            .OrderByDescending(g => g.Count())
            .ToList();
        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Type")
            .AddColumn(new TableColumn("Count").RightAligned());

        foreach (var g in typeCounts)
        {
            _ = summaryTable.AddRow(g.Key, g.Count().ToString("N0", CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(summaryTable);
        AnsiConsole.WriteLine();

        // Detail table
        var displayLimit = limit <= 0 ? objects.Count : Math.Min(limit, objects.Count);
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Placed Objects[/]")
            .AddColumn("Type")
            .AddColumn(new TableColumn("FormID").RightAligned())
            .AddColumn(new TableColumn("Base").RightAligned())
            .AddColumn("EditorID")
            .AddColumn("Category")
            .AddColumn(new TableColumn("X").RightAligned())
            .AddColumn(new TableColumn("Y").RightAligned())
            .AddColumn(new TableColumn("Z").RightAligned())
            .AddColumn("Flags");

        for (var i = 0; i < displayLimit; i++)
        {
            var obj = objects[i];
            var category = GetCategory(obj, categoryIndex);
            var flags = BuildFlags(obj, persistentOverlay.Contains(obj));
            var editorId = obj.BaseEditorId ?? records.FormIdToEditorId.GetValueOrDefault(obj.BaseFormId);

            _ = table.AddRow(
                ColorType(obj.RecordType),
                $"0x{obj.FormId:X8}",
                $"0x{obj.BaseFormId:X8}",
                Markup.Escape(TruncateString(editorId, 28)),
                category,
                obj.X.ToString("F0"),
                obj.Y.ToString("F0"),
                obj.Z.ToString("F0"),
                flags);
        }

        AnsiConsole.Write(table);

        if (displayLimit < objects.Count)
        {
            AnsiConsole.MarkupLine(
                $"[dim]...{objects.Count - displayLimit} more (use -l 0 to show all)[/]");
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
            records = parser.ParseAll();
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

    private static List<CellRecord> CollectAllCells(RecordCollection records)
    {
        // Deduplicate: worldspace cells may overlap with top-level Cells list
        var seen = new HashSet<uint>();
        var allCells = new List<CellRecord>();

        foreach (var ws in records.Worldspaces)
        {
            foreach (var cell in ws.Cells)
            {
                if (seen.Add(cell.FormId))
                {
                    allCells.Add(cell);
                }
            }
        }

        foreach (var cell in records.Cells)
        {
            if (seen.Add(cell.FormId))
            {
                allCells.Add(cell);
            }
        }

        return allCells;
    }

    private static CellRecord? FindCell(List<CellRecord> cells, string query)
    {
        // Try parsing as FormID first
        var formId = EsmFileLoader.ParseFormId(query);
        if (formId.HasValue)
        {
            var byFormId = cells.FirstOrDefault(c => c.FormId == formId.Value);
            if (byFormId != null)
            {
                return byFormId;
            }
        }

        // Try exact EDID match
        var byEdid = cells.FirstOrDefault(c =>
            c.EditorId != null && c.EditorId.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (byEdid != null)
        {
            return byEdid;
        }

        // Try substring match on EDID
        var byEdidPartial = cells.FirstOrDefault(c =>
            c.EditorId != null && c.EditorId.Contains(query, StringComparison.OrdinalIgnoreCase));
        if (byEdidPartial != null)
        {
            return byEdidPartial;
        }

        // Try FULL name match
        return cells.FirstOrDefault(c =>
            c.FullName != null && c.FullName.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private static List<PlacedReference> FilterObjects(List<PlacedReference> objects, string? typeFilter)
    {
        if (string.IsNullOrEmpty(typeFilter))
        {
            return [..objects];
        }

        var filter = typeFilter.ToUpperInvariant();
        return objects.Where(o => o.RecordType == filter).ToList();
    }

    private static List<PlacedReference> FindPersistentOverlay(
        List<CellRecord> allCells,
        CellRecord targetCell, string? typeFilter)
    {
        if (!targetCell.GridX.HasValue || !targetCell.GridY.HasValue ||
            targetCell.WorldspaceFormId is null or 0)
        {
            return [];
        }

        var cellMinX = targetCell.GridX.Value * CellWorldSize;
        var cellMaxX = cellMinX + CellWorldSize;
        var cellMinY = targetCell.GridY.Value * CellWorldSize;
        var cellMaxY = cellMinY + CellWorldSize;

        var targetWs = targetCell.WorldspaceFormId.Value;

        // Find persistent refs from OTHER cells in the same worldspace whose
        // positions fall within this cell's grid bounds.  The worldspace persistent
        // cell typically has grid (0,0) — not null — so we scan all worldspace cells
        // and check for persistent references with matching positions.
        var directFormIds = new HashSet<uint>(
            targetCell.PlacedObjects.Select(o => o.FormId));

        var overlay = new List<PlacedReference>();
        foreach (var cell in allCells)
        {
            if (cell.FormId == targetCell.FormId ||
                cell.WorldspaceFormId != targetWs)
            {
                continue;
            }

            foreach (var obj in cell.PlacedObjects)
            {
                if (!obj.IsPersistent)
                {
                    continue;
                }

                if (directFormIds.Contains(obj.FormId))
                {
                    continue;
                }

                if (obj.X >= cellMinX && obj.X < cellMaxX &&
                    obj.Y >= cellMinY && obj.Y < cellMaxY &&
                    (string.IsNullOrEmpty(typeFilter) ||
                     obj.RecordType.Equals(typeFilter, StringComparison.OrdinalIgnoreCase)))
                {
                    overlay.Add(obj);
                }
            }
        }

        return overlay;
    }

    private static string GetCategory(PlacedReference obj,
        Dictionary<uint, PlacedObjectCategory> categoryIndex)
    {
        if (obj.IsMapMarker)
        {
            return "MapMarker";
        }

        if (obj.RecordType == "ACHR")
        {
            return "[bold green]Npc[/]";
        }

        if (obj.RecordType == "ACRE")
        {
            return "[bold yellow]Creature[/]";
        }

        var cat = categoryIndex.GetValueOrDefault(obj.BaseFormId, PlacedObjectCategory.Unknown);
        return cat.ToString();
    }

    private static string ColorType(string recordType)
    {
        return recordType switch
        {
            "ACHR" => "[bold green]ACHR[/]",
            "ACRE" => "[bold yellow]ACRE[/]",
            "REFR" => "[dim]REFR[/]",
            _ => recordType
        };
    }

    private static string BuildFlags(PlacedReference obj, bool isPersistentOverlay)
    {
        var parts = new List<string>();
        if (obj.IsPersistent)
        {
            parts.Add("[cyan]P[/]");
        }

        if (obj.IsInitiallyDisabled)
        {
            parts.Add("[red]D[/]");
        }

        if (isPersistentOverlay)
        {
            parts.Add("[yellow]OVR[/]");
        }

        return parts.Count > 0 ? string.Join(" ", parts) : "[dim]-[/]";
    }

    private static string TruncateString(string? value, int maxLen)
    {
        if (value == null)
        {
            return "(none)";
        }

        return value.Length <= maxLen ? value : value[..(maxLen - 3)] + "...";
    }

    #endregion
}
