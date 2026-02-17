using System.CommandLine;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

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
        var exportObjOpt = new Option<string?>("--export-obj")
        {
            Description = "Export runtime terrain mesh to Wavefront OBJ file"
        };

        command.Arguments.Add(inputArg);
        command.Arguments.Add(formIdArg);
        command.Options.Add(exportObjOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var formIdStr = parseResult.GetValue(formIdArg)!;
            var exportObj = parseResult.GetValue(exportObjOpt);
            await RunCellAsync(input, formIdStr, exportObj, cancellationToken);
        });

        return command;
    }

    private static async Task<RecordCollection?> LoadAndReconstructAsync(
        string input, CancellationToken cancellationToken)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return null;
        }

        AnsiConsole.MarkupLine("[blue]Loading:[/] {0}", Path.GetFileName(input));

        var analysisResult = await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("Analyzing ESM file...", maxValue: 100);
                var progress = new Progress<AnalysisProgress>(p =>
                {
                    task.Description = p.Phase;
                    task.Value = p.PercentComplete;
                });

                return await EsmFileAnalyzer.AnalyzeAsync(input, progress, cancellationToken);
            });

        if (analysisResult.EsmRecords == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No ESM records found in file.");
            return null;
        }

        AnsiConsole.MarkupLine("[blue]Reconstructing world data...[/]");

        var fileInfo = new FileInfo(input);
        RecordCollection semanticResult;
        using (var mmf = MemoryMappedFile.CreateFromFile(input, FileMode.Open, null, 0,
                   MemoryMappedFileAccess.Read))
        using (var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read))
        {
            var parser = new RecordParser(
                analysisResult.EsmRecords, analysisResult.FormIdMap, accessor, fileInfo.Length,
                analysisResult.MinidumpInfo);
            semanticResult = parser.ReconstructAll();
        }

        return semanticResult;
    }

    private static async Task RunMarkersAsync(string input, CancellationToken cancellationToken)
    {
        var result = await LoadAndReconstructAsync(input, cancellationToken);
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
        string input, string formIdStr, string? exportObjPath, CancellationToken cancellationToken)
    {
        var formId = ParseFormId(formIdStr);
        if (formId == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Invalid FormID: {0}", formIdStr);
            return;
        }

        var result = await LoadAndReconstructAsync(input, cancellationToken);
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
        HandleTerrainMeshExport(cell, exportObjPath);
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
            detailTable.AddRow("Grid", $"[{cell.GridX.Value}, {cell.GridY.Value}]");
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

    private static void HandleTerrainMeshExport(CellRecord cell, string? exportObjPath)
    {
        if (exportObjPath == null)
        {
            return;
        }

        if (cell.RuntimeTerrainMesh != null)
        {
            TerrainObjExporter.Export(
                cell.RuntimeTerrainMesh,
                cell.GridX ?? 0, cell.GridY ?? 0,
                exportObjPath);
            AnsiConsole.MarkupLine(
                "[green]Terrain mesh exported to:[/] {0} ({1} vertices)",
                exportObjPath, RuntimeTerrainMesh.VertexCount);
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

    private static uint ParseFormId(string str)
    {
        if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            str = str[2..];
        }

        return uint.TryParse(str, NumberStyles.HexNumber, null, out var result)
            ? result
            : 0;
    }
}
