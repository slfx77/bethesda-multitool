using System.CommandLine;
using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using Spectre.Console;

namespace TerrainAnalyzer.Commands;

internal static class DiagCommands
{
    internal static Command CreateDiagCommand()
    {
        var command = new Command("diag", "Run terrain mesh data quality diagnostic");

        var dmpArg = new Argument<string>("dmp") { Description = "Path to memory dump file (.dmp)" };
        var csvOpt = new Option<string?>("--csv") { Description = "Export diagnostic table to CSV file" };
        var verboseOpt = new Option<bool>("-v", "--verbose") { Description = "Show detailed progress" };

        command.Arguments.Add(dmpArg);
        command.Options.Add(csvOpt);
        command.Options.Add(verboseOpt);

        command.SetAction(async (parseResult, _) =>
        {
            var dmp = parseResult.GetValue(dmpArg)!;
            var csv = parseResult.GetValue(csvOpt);
            var verbose = parseResult.GetValue(verboseOpt);
            await ExecuteAsync(dmp, csv, verbose);
        });

        return command;
    }

    private static async Task ExecuteAsync(string dmpPath, string? csvPath, bool verbose)
    {
        using var data = await DumpLoader.LoadAsync(dmpPath, verbose: verbose);

        var cellsWithMesh = data.ScanResult.LandRecords
            .Where(l => l.RuntimeTerrainMesh != null && l.BestCellX.HasValue && l.BestCellY.HasValue)
            .ToList();

        if (cellsWithMesh.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No cells with runtime terrain meshes found.[/]");
            return;
        }

        var diagnostics = cellsWithMesh
            .Select(l => l.RuntimeTerrainMesh!.DiagnoseQuality(
                l.BestCellX!.Value, l.BestCellY!.Value, l.Header.FormId))
            .OrderBy(d => d.CellX)
            .ThenBy(d => d.CellY)
            .ToList();

        RenderDiagnosticTable(diagnostics, Path.GetFileName(dmpPath));

        if (!string.IsNullOrEmpty(csvPath))
        {
            ExportCsv(diagnostics, Path.GetFileName(dmpPath), csvPath);
        }
    }

    internal static void RenderDiagnosticTable(List<TerrainMeshDiagnostic> diagnostics, string dumpFilename)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[blue]Terrain mesh data quality diagnostic[/] — {Markup.Escape(dumpFilename)}");

        var table = new Table();
        table.AddColumn("Cell");
        table.AddColumn("FormID");
        table.AddColumn(new TableColumn("ZRange").RightAligned());
        table.AddColumn(new TableColumn("UniqueZ").RightAligned());
        table.AddColumn(new TableColumn("ZeroZ%").RightAligned());
        table.AddColumn(new TableColumn("GarbZ").RightAligned());
        table.AddColumn(new TableColumn("DomZ%").RightAligned());
        table.AddColumn(new TableColumn("LastRow").RightAligned());
        table.AddColumn(new TableColumn("Discont").RightAligned());
        table.AddColumn("Class");

        foreach (var d in diagnostics)
        {
            var classColor = d.Classification switch
            {
                "Complete" => "green",
                "Partial" => "yellow",
                "Flat" => "red",
                "FewPixels" => "red",
                _ => "grey"
            };

            var garbColor = d.GarbageZCount > 0 ? "red" : "green";

            table.AddRow(
                $"{d.CellX},{d.CellY}",
                $"0x{d.FormId:X8}",
                $"{d.ZRange:F1}",
                $"{d.UniqueZCount}",
                $"{d.ZeroZCount * 100.0f / RuntimeTerrainMesh.VertexCount:F1}",
                $"[{garbColor}]{d.GarbageZCount}[/]",
                $"{d.DominantZPercent:F1}",
                $"{d.LastActiveRow}",
                $"{d.RowDiscontinuities}",
                $"[{classColor}]{d.Classification}[/]");
        }

        AnsiConsole.Write(table);

        var complete = diagnostics.Count(d => d.Classification == "Complete");
        var partial = diagnostics.Count(d => d.Classification == "Partial");
        var flat = diagnostics.Count(d => d.Classification == "Flat");
        var fewPixels = diagnostics.Count(d => d.Classification == "FewPixels");
        AnsiConsole.MarkupLine(
            $"  [green]Complete: {complete}[/]  [yellow]Partial: {partial}[/]  " +
            $"[red]Flat: {flat}  FewPixels: {fewPixels}[/]  Total: {diagnostics.Count}");
    }

    internal static void ExportCsv(
        List<TerrainMeshDiagnostic> diagnostics, string dumpFilename, string csvPath)
    {
        var csv = new StringBuilder();
        csv.AppendLine("DumpFile,CellX,CellY,FormID,MinZ,MaxZ,ZRange,ZStdDev," +
                        "UniqueZCount,ZeroZCount,ZeroZPct,GarbageZCount,DominantZPct," +
                        "LastActiveRow,RowDiscontinuities,Classification");

        foreach (var d in diagnostics)
        {
            csv.AppendLine(CultureInfo.InvariantCulture,
                $"{dumpFilename},{d.CellX},{d.CellY},0x{d.FormId:X8}," +
                $"{d.MinZ:F2},{d.MaxZ:F2},{d.ZRange:F2},{d.ZStdDev:F2}," +
                $"{d.UniqueZCount},{d.ZeroZCount}," +
                $"{d.ZeroZCount * 100.0f / RuntimeTerrainMesh.VertexCount:F1}," +
                $"{d.GarbageZCount},{d.DominantZPercent:F1},{d.LastActiveRow}," +
                $"{d.RowDiscontinuities},{d.Classification}");
        }

        var dir = Path.GetDirectoryName(csvPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(csvPath, csv.ToString());
        AnsiConsole.MarkupLine($"  CSV exported: {csvPath}");
    }
}
