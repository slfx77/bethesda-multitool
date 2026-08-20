using System.CommandLine;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Esm.Export.Csv;
using BethesdaMultitool.Core.Formats.Esm.Export.ModelExport;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Semantic;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Analysis;

/// <summary>
///     CLI command for world map diagnostics: markers, cells, and placed objects.
/// </summary>
public static class WorldCommand
{
    public static Command Create()
    {
        var command = new Command("world", "World map diagnostics");

        command.Subcommands.Add(CreateMarkersCommand());
        command.Subcommands.Add(CreateCellCommand());
        command.Subcommands.Add(CreatePersistentCommand());
        command.Subcommands.Add(CreateHeightmapCommand());

        return command;
    }

    private static Command CreateHeightmapCommand()
    {
        var command = new Command(
            "heightmap",
            "Render a worldspace terrain heightmap to a grayscale PNG from parsed cell heights "
            + "(includes Fallout 76 BTD-injected terrain)");

        var inputArg = new Argument<string>("input") { Description = "Path to ESM file" };
        var outputOpt = new Option<string>("-o", "--output") { Description = "Output PNG path", Required = true };
        var worldspaceOpt = new Option<string?>("-w", "--worldspace")
        {
            Description = "Worldspace editor ID, full name, or FormID (default: the one with the most terrain)"
        };
        var cellPxOpt = new Option<int>("--cell-px")
        {
            Description = "Pixels rendered per cell, 1..33 (default 8)", DefaultValueFactory = _ => 8
        };

        command.Arguments.Add(inputArg);
        command.Options.Add(outputOpt);
        command.Options.Add(worldspaceOpt);
        command.Options.Add(cellPxOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await RunHeightmapAsync(
                parseResult.GetValue(inputArg)!,
                parseResult.GetValue(outputOpt)!,
                parseResult.GetValue(worldspaceOpt),
                parseResult.GetValue(cellPxOpt),
                cancellationToken);
        });

        return command;
    }

    private static Command CreateMarkersCommand()
    {
        var command = new Command("markers", "List map markers and their worldspace assignments");

        var inputArg = new Argument<string>("input") { Description = "Path to ESM file" };

        command.Arguments.Add(inputArg);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            await RunMarkersAsync(input, cancellationToken);
        });

        return command;
    }

    private static Command CreateCellCommand()
    {
        var command = new Command("cell", "Show cell data including placed objects");

        var inputArg = new Argument<string>("input") { Description = "Path to ESM file" };
        var formIdArg = new Argument<string>("formid") { Description = "Cell FormID (hex, e.g. 0x00012345)" };
        var exportGlbOpt = new Option<string?>("--export-glb")
        {
            Description = "Export runtime terrain mesh to glTF Binary (.glb) file"
        };

        command.Arguments.Add(inputArg);
        command.Arguments.Add(formIdArg);
        command.Options.Add(exportGlbOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var formIdStr = parseResult.GetValue(formIdArg)!;
            var exportGlb = parseResult.GetValue(exportGlbOpt);
            await RunCellAsync(input, formIdStr, exportGlb, cancellationToken);
        });

        return command;
    }

    private static async Task<RecordCollection?> LoadAndParseAsync(
        string input, CancellationToken cancellationToken)
    {
        using var loaded = await CliSemanticLoader.TryLoadAsync(
            input,
            "Loading world data...",
            new SemanticFileLoadOptions { FileType = AnalysisFileType.EsmFile },
            cancellationToken);
        return loaded?.Records;
    }

    private static async Task RunMarkersAsync(string input, CancellationToken cancellationToken)
    {
        var result = await LoadAndParseAsync(input, cancellationToken);
        if (result == null)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[blue]Map Markers by Worldspace[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var totalMarkers = 0;
        var worldspaceCount = 0;

        foreach (var ws in result.Worldspaces)
        {
            var wsMarkers = new List<PlacedReference>();
            foreach (var cell in ws.Cells)
            {
                wsMarkers.AddRange(cell.PlacedObjects.Where(o => o.IsMapMarker));
            }

            if (wsMarkers.Count == 0)
            {
                continue;
            }

            worldspaceCount++;
            var wsName = ws.FullName ?? ws.EditorId ?? $"0x{ws.FormId:X8}";

            AnsiConsole.Write(new Rule(
                    $"[yellow]{Markup.Escape(wsName)} (0x{ws.FormId:X8}) \u2014 {wsMarkers.Count} markers[/]")
                .LeftJustified());
            AnsiConsole.WriteLine();

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("FormID");
            table.AddColumn("Name");
            table.AddColumn("Type");
            table.AddColumn(new TableColumn("Position").RightAligned());

            foreach (var marker in wsMarkers.OrderBy(m => m.MarkerName ?? ""))
            {
                var name = marker.MarkerName ?? "(unnamed)";
                var type = marker.MarkerType?.ToString() ?? "Unknown";
                var pos = $"({marker.X:F0}, {marker.Y:F0}, {marker.Z:F0})";
                table.AddRow($"0x{marker.FormId:X8}", Markup.Escape(name), type, pos);
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
            totalMarkers += wsMarkers.Count;
        }

        AnsiConsole.MarkupLine(
            $"[green]Total:[/] {totalMarkers:N0} markers across {worldspaceCount} worldspace(s)");
    }

    private static async Task RunCellAsync(
        string input, string formIdStr, string? exportGlbPath, CancellationToken cancellationToken)
    {
        var formId = CliHelpers.ParseFormId(formIdStr) ?? 0;
        if (formId == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Invalid FormID: {0}", formIdStr);
            return;
        }

        var result = await LoadAndParseAsync(input, cancellationToken);
        if (result == null)
        {
            return;
        }

        var (cell, worldspaceName) = FindCell(result, formId);
        if (cell == null)
        {
            AnsiConsole.MarkupLine("[yellow]Cell 0x{0:X8} not found.[/]", formId);
            return;
        }

        AnsiConsole.WriteLine();
        var cellName = cell.EditorId ?? cell.FullName ?? $"0x{cell.FormId:X8}";
        AnsiConsole.Write(new Rule($"[blue]Cell: {Markup.Escape(cellName)} (0x{formId:X8})[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var resolver = result.CreateResolver();

        RenderCellDetails(cell, worldspaceName);
        HandleTerrainMeshExport(cell, exportGlbPath);
        RenderPlacedObjects(cell, resolver);
    }

    private static (CellRecord? Cell, string? WorldspaceName) FindCell(RecordCollection result, uint formId)
    {
        foreach (var ws in result.Worldspaces)
        {
            var cell = ws.Cells.FirstOrDefault(c => c.FormId == formId);
            if (cell != null)
            {
                return (cell, ws.FullName ?? ws.EditorId ?? $"0x{ws.FormId:X8}");
            }
        }

        return (result.Cells.FirstOrDefault(c => c.FormId == formId), null);
    }

    private static async Task RunHeightmapAsync(
        string input, string output, string? worldspaceSel, int cellPx, CancellationToken cancellationToken)
    {
        var result = await LoadAndParseAsync(input, cancellationToken);
        if (result == null)
        {
            return;
        }

        cellPx = Math.Clamp(cellPx, 1, 33);
        var ws = SelectWorldspaceForHeightmap(result, worldspaceSel);
        if (ws == null)
        {
            AnsiConsole.MarkupLine("[red]No worldspace with terrain heightmap data found.[/]");
            return;
        }

        var cells = ws.Cells
            .Where(c => !c.IsInterior && c.GridX.HasValue && c.GridY.HasValue && c.Heightmap != null)
            .ToList();
        if (cells.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Worldspace has no exterior cells with heightmap data.[/]");
            return;
        }

        var minGx = cells.Min(c => c.GridX!.Value);
        var maxGx = cells.Max(c => c.GridX!.Value);
        var minGy = cells.Min(c => c.GridY!.Value);
        var maxGy = cells.Max(c => c.GridY!.Value);
        var width = (long)(maxGx - minGx + 1) * cellPx;
        var height = (long)(maxGy - minGy + 1) * cellPx;
        if (width * height > 400_000_000L)
        {
            AnsiConsole.MarkupLine("[red]Heightmap would be {0:N0}x{1:N0} px — lower --cell-px.[/]", width, height);
            return;
        }

        // Decode every cell once, tracking the global height range for normalization.
        var grids = new Dictionary<(int X, int Y), float[,]>(cells.Count);
        var gMin = float.MaxValue;
        var gMax = float.MinValue;
        foreach (var cell in cells)
        {
            var hm = cell.Heightmap!.CalculateHeights();
            grids[(cell.GridX!.Value, cell.GridY!.Value)] = hm;
            var edge = hm.GetLength(0);
            for (var y = 0; y < edge; y++)
            {
                for (var x = 0; x < edge; x++)
                {
                    var v = hm[y, x];
                    if (v < gMin)
                    {
                        gMin = v;
                    }

                    if (v > gMax)
                    {
                        gMax = v;
                    }
                }
            }
        }

        var scale = gMax > gMin ? 255.0f / (gMax - gMin) : 0f;
        var pixels = new byte[width * height];
        foreach (var ((gx, gy), hm) in grids)
        {
            var edge = hm.GetLength(0);
            var baseCol = (long)(gx - minGx) * cellPx;
            var cellTopRow = (long)(maxGy - gy) * cellPx; // north-up
            for (var py = 0; py < cellPx; py++)
            {
                var sy = cellPx == 1 ? 0 : (int)Math.Round(py * (double)(edge - 1) / (cellPx - 1));
                var row = cellTopRow + (cellPx - 1 - py); // sample row 0 is the south edge -> bottom
                var dst = row * width + baseCol;
                for (var px = 0; px < cellPx; px++)
                {
                    var sx = cellPx == 1 ? 0 : (int)Math.Round(px * (double)(edge - 1) / (cellPx - 1));
                    var g = (int)((hm[sy, sx] - gMin) * scale);
                    pixels[dst + px] = (byte)Math.Clamp(g, 0, 255);
                }
            }
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        PngWriter.SaveGrayscale(pixels, (int)width, (int)height, output);
        var label = ws.EditorId ?? ws.FullName ?? $"0x{ws.FormId:X8}";
        AnsiConsole.MarkupLine(
            "[green]Wrote[/] {0} ({1:N0}x{2:N0}) — worldspace [cyan]{3}[/], {4:N0} cells, height {5:F0}..{6:F0}",
            output, width, height, label, cells.Count, gMin, gMax);
    }

    private static WorldspaceRecord? SelectWorldspaceForHeightmap(RecordCollection result, string? sel)
    {
        if (!string.IsNullOrEmpty(sel))
        {
            var match = result.Worldspaces.FirstOrDefault(w =>
                string.Equals(w.EditorId, sel, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(w.FullName, sel, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match;
            }

            var formId = CliHelpers.ParseFormId(sel);
            return formId is { } id ? result.Worldspaces.FirstOrDefault(w => w.FormId == id) : null;
        }

        return result.Worldspaces
            .Where(w => w.Cells.Any(c => c.Heightmap != null))
            .OrderByDescending(w => w.Cells.Count(c => c.Heightmap != null))
            .FirstOrDefault();
    }

    private static void RenderCellDetails(CellRecord cell, string? worldspaceName)
    {
        var detailTable = new Table().Border(TableBorder.Rounded).HideHeaders();
        detailTable.AddColumn("Property");
        detailTable.AddColumn("Value");

        detailTable.AddRow("FormID", $"0x{cell.FormId:X8}");
        if (!string.IsNullOrEmpty(cell.EditorId))
        {
            detailTable.AddRow("Editor ID", cell.EditorId);
        }

        if (!string.IsNullOrEmpty(cell.FullName))
        {
            detailTable.AddRow("Full Name", cell.FullName);
        }

        if (cell.GridX.HasValue && cell.GridY.HasValue)
        {
            // Escape the brackets — Spectre treats '[' as a markup-tag opener.
            detailTable.AddRow("Grid", $"[[{cell.GridX.Value}, {cell.GridY.Value}]]");
        }

        if (worldspaceName != null)
        {
            detailTable.AddRow("Worldspace", worldspaceName);
        }

        detailTable.AddRow("Interior", cell.IsInterior ? "Yes" : "No");
        detailTable.AddRow("Has Heightmap", cell.Heightmap != null ? "Yes" : "No");
        detailTable.AddRow("Runtime Terrain Mesh", FormatTerrainMeshStatus(cell.RuntimeTerrainMesh));
        detailTable.AddRow("Has Water", cell.HasWater ? "Yes" : "No");
        detailTable.AddRow("Objects", $"{cell.PlacedObjects.Count:N0}");
        detailTable.AddRow("Endianness", cell.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)");

        AnsiConsole.Write(detailTable);
        AnsiConsole.WriteLine();
    }

    private static void HandleTerrainMeshExport(CellRecord cell, string? exportGlbPath)
    {
        if (exportGlbPath == null)
        {
            return;
        }

        if (cell.RuntimeTerrainMesh != null)
        {
            TerrainGlbExporter.Export(
                cell.RuntimeTerrainMesh,
                cell.GridX ?? 0, cell.GridY ?? 0,
                exportGlbPath);
            AnsiConsole.MarkupLine(
                "[green]Terrain mesh exported to:[/] {0} ({1} vertices)",
                exportGlbPath, RuntimeTerrainMesh.VertexCount);
        }
        else
        {
            AnsiConsole.MarkupLine(
                "[yellow]No runtime terrain mesh available for this cell.[/]");
        }

        AnsiConsole.WriteLine();
    }

    private static void RenderPlacedObjects(CellRecord cell, FormIdResolver resolver)
    {
        if (cell.PlacedObjects.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]No placed objects in this cell.[/]");
            return;
        }

        var grouped = cell.PlacedObjects
            .GroupBy(obj => GetCategoryName(obj))
            .OrderBy(g => GetCategorySortOrder(g.Key));

        foreach (var group in grouped)
        {
            AnsiConsole.Write(new Rule(
                $"[yellow]{Markup.Escape(group.Key)} ({group.Count()})[/]").LeftJustified());

            var table = new Table().Border(TableBorder.Simple);
            table.AddColumn("FormID");
            table.AddColumn("Base");
            table.AddColumn(new TableColumn("Position").RightAligned());

            foreach (var obj in group.OrderBy(o => o.BaseEditorId ?? $"0x{o.BaseFormId:X8}"))
            {
                var baseName = obj.BaseEditorId
                               ?? resolver.GetBestName(obj.BaseFormId)
                               ?? $"0x{obj.BaseFormId:X8}";
                var pos = $"({obj.X:F1}, {obj.Y:F1}, {obj.Z:F1})";
                table.AddRow($"0x{obj.FormId:X8}", Markup.Escape(baseName), pos);
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
        }
    }

    private static string GetCategoryName(PlacedReference obj)
    {
        if (obj.IsMapMarker)
        {
            return "Map Markers";
        }

        return obj.RecordType switch
        {
            "ACHR" => "NPCs",
            "ACRE" => "Creatures",
            _ => "Objects (REFR)"
        };
    }

    private static int GetCategorySortOrder(string category)
    {
        return category switch
        {
            "NPCs" => 0,
            "Creatures" => 1,
            "Map Markers" => 2,
            _ => 3
        };
    }

    private static string FormatTerrainMeshStatus(RuntimeTerrainMesh? mesh)
    {
        if (mesh == null)
        {
            return "No";
        }

        var parts = new List<string> { $"{RuntimeTerrainMesh.VertexCount} vertices" };
        if (mesh.HasNormals)
        {
            parts.Add("normals");
        }

        if (mesh.HasColors)
        {
            parts.Add("colors");
        }

        return $"Yes ({string.Join(", ", parts)})";
    }

    #region Persistent Objects

    private static Command CreatePersistentCommand()
    {
        var command = new Command("persistent", "List persistent references (NPCs, doors, quest objects)");

        var inputArg = new Argument<string>("input") { Description = "Path to ESM file" };
        var outputOpt = new Option<string?>("-o", "--output") { Description = "Export to CSV file" };
        var typeOpt = new Option<string?>("-t", "--type")
        {
            Description = "Filter by record type (ACHR, ACRE, REFR)"
        };

        command.Arguments.Add(inputArg);
        command.Options.Add(outputOpt);
        command.Options.Add(typeOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputOpt);
            var typeFilter = parseResult.GetValue(typeOpt);
            await RunPersistentAsync(input, output, typeFilter, cancellationToken);
        });

        return command;
    }

    private static async Task RunPersistentAsync(
        string input, string? outputPath, string? typeFilter, CancellationToken cancellationToken)
    {
        var result = await LoadAndParseAsync(input, cancellationToken);
        if (result == null)
        {
            return;
        }

        var resolver = result.CreateResolver();

        // Collect all persistent objects across all cells
        var persistent = result.Cells
            .SelectMany(c => c.PlacedObjects
                .Where(o => o.IsPersistent)
                .Select(o => (Cell: c, Obj: o)))
            .ToList();

        // Also check worldspace cells
        foreach (var ws in result.Worldspaces)
        {
            foreach (var cell in ws.Cells)
            {
                persistent.AddRange(cell.PlacedObjects
                    .Where(o => o.IsPersistent)
                    .Select(o => (Cell: cell, Obj: o)));
            }
        }

        // Apply type filter
        if (!string.IsNullOrEmpty(typeFilter))
        {
            var filter = typeFilter.ToUpperInvariant();
            persistent = persistent.Where(p => p.Obj.RecordType == filter).ToList();
        }

        if (persistent.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No persistent objects found.[/]");
            return;
        }

        // Export to CSV if requested
        if (outputPath != null)
        {
            var allCells = result.Cells
                .Concat(result.Worldspaces.SelectMany(ws => ws.Cells))
                .ToList();
            var csv = CsvSupplementalWriter.GeneratePersistentObjectsCsv(allCells, resolver);
            await File.WriteAllTextAsync(outputPath, csv, cancellationToken);
            AnsiConsole.MarkupLine(
                $"[green]Exported {persistent.Count:N0} persistent objects to:[/] {outputPath}");
            return;
        }

        // Console output
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[blue]Persistent Objects ({persistent.Count:N0})[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var grouped = persistent
            .GroupBy(p => p.Obj.RecordType)
            .OrderBy(g => g.Key switch { "ACHR" => 0, "ACRE" => 1, _ => 2 });

        foreach (var group in grouped)
        {
            var typeName = group.Key switch
            {
                "ACHR" => "NPCs (ACHR)",
                "ACRE" => "Creatures (ACRE)",
                _ => $"Objects ({group.Key})"
            };

            AnsiConsole.Write(new Rule(
                $"[yellow]{typeName} ({group.Count():N0})[/]").LeftJustified());

            var table = new Table().Border(TableBorder.Simple);
            table.AddColumn("FormID");
            table.AddColumn("Base");
            table.AddColumn(new TableColumn("Position").RightAligned());
            table.AddColumn(new TableColumn("Rotation").RightAligned());
            table.AddColumn("Cell");

            foreach (var (cell, obj) in group.OrderBy(p => p.Obj.BaseEditorId ?? $"0x{p.Obj.BaseFormId:X8}"))
            {
                var baseName = obj.BaseEditorId
                               ?? resolver.GetBestName(obj.BaseFormId)
                               ?? $"0x{obj.BaseFormId:X8}";
                var pos = $"({obj.X:F1}, {obj.Y:F1}, {obj.Z:F1})";
                var rot = $"({obj.RotX:F3}, {obj.RotY:F3}, {obj.RotZ:F3})";
                var cellName = cell.EditorId ?? $"0x{cell.FormId:X8}";
                var disabled = obj.IsInitiallyDisabled ? " [dim](disabled)[/]" : "";

                table.AddRow(
                    $"0x{obj.FormId:X8}",
                    Markup.Escape(baseName) + disabled,
                    pos,
                    rot,
                    Markup.Escape(cellName));
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
        }

        AnsiConsole.MarkupLine(
            $"[green]Total:[/] {persistent.Count:N0} persistent objects");
    }

    #endregion
}
