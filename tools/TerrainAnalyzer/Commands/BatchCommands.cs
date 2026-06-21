using System.CommandLine;
using BethesdaMultitool.Core.Formats.Esm.Models;
using Spectre.Console;

namespace TerrainAnalyzer.Commands;

internal static class BatchCommands
{
    internal static Command CreateBatchCommand()
    {
        var command = new Command("batch", "Run terrain diagnostic across all DMP files in a directory");

        var dirArg = new Argument<string>("directory") { Description = "Directory containing .dmp files" };
        var csvOpt = new Option<string?>("--csv") { Description = "Export combined diagnostics to CSV file" };
        var verboseOpt = new Option<bool>("-v", "--verbose") { Description = "Show detailed progress" };

        command.Arguments.Add(dirArg);
        command.Options.Add(csvOpt);
        command.Options.Add(verboseOpt);

        command.SetAction(async (parseResult, _) =>
        {
            var dir = parseResult.GetValue(dirArg)!;
            var csv = parseResult.GetValue(csvOpt);
            var verbose = parseResult.GetValue(verboseOpt);
            await ExecuteAsync(dir, csv, verbose);
        });

        return command;
    }

    private static async Task ExecuteAsync(string directory, string? csvPath, bool verbose)
    {
        if (!Directory.Exists(directory))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Directory not found: {directory}");
            return;
        }

        var dmpFiles = Directory.GetFiles(directory, "*.dmp")
            .OrderBy(f => f)
            .ToArray();

        if (dmpFiles.Length == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No .dmp files found in:[/] {directory}");
            return;
        }

        AnsiConsole.MarkupLine($"[blue]Found {dmpFiles.Length} DMP files in:[/] {directory}");
        AnsiConsole.WriteLine();

        var allDiagnostics = new List<(string filename, List<TerrainMeshDiagnostic> diagnostics)>();
        var filesWithTerrain = 0;

        foreach (var dmpFile in dmpFiles)
        {
            var filename = Path.GetFileName(dmpFile);
            AnsiConsole.MarkupLine($"[blue]Processing:[/] {filename}");

            try
            {
                using var data = await DumpLoader.LoadAsync(dmpFile, verbose: verbose);

                var cellsWithMesh = data.ScanResult.LandRecords
                    .Where(l => l.RuntimeTerrainMesh != null && l.BestCellX.HasValue && l.BestCellY.HasValue)
                    .ToList();

                if (cellsWithMesh.Count == 0)
                {
                    AnsiConsole.MarkupLine($"  [grey]No terrain meshes[/]");
                    continue;
                }

                filesWithTerrain++;

                var diagnostics = cellsWithMesh
                    .Select(l => l.RuntimeTerrainMesh!.DiagnoseQuality(
                        l.BestCellX!.Value, l.BestCellY!.Value, l.Header.FormId))
                    .OrderBy(d => d.CellX)
                    .ThenBy(d => d.CellY)
                    .ToList();

                allDiagnostics.Add((filename, diagnostics));
                DiagCommands.RenderDiagnosticTable(diagnostics, filename);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red]Error:[/] {Markup.Escape(ex.Message)}");
            }

            AnsiConsole.WriteLine();
        }

        // Summary
        AnsiConsole.MarkupLine("[blue]Batch summary[/]");
        AnsiConsole.MarkupLine($"  Files processed: {dmpFiles.Length}");
        AnsiConsole.MarkupLine($"  Files with terrain: {filesWithTerrain}");

        var totalDiag = allDiagnostics.SelectMany(d => d.diagnostics).ToList();
        if (totalDiag.Count > 0)
        {
            var complete = totalDiag.Count(d => d.Classification == "Complete");
            var partial = totalDiag.Count(d => d.Classification == "Partial");
            var flat = totalDiag.Count(d => d.Classification == "Flat");
            var fewPixels = totalDiag.Count(d => d.Classification == "FewPixels");
            AnsiConsole.MarkupLine(
                $"  Total cells: {totalDiag.Count} — " +
                $"[green]Complete: {complete}[/]  [yellow]Partial: {partial}[/]  " +
                $"[red]Flat: {flat}  FewPixels: {fewPixels}[/]");
        }

        // Combined CSV
        if (!string.IsNullOrEmpty(csvPath) && allDiagnostics.Count > 0)
        {
            var combined = allDiagnostics
                .SelectMany(d => d.diagnostics)
                .ToList();
            DiagCommands.ExportCsv(combined, "batch", csvPath);
        }
    }
}
