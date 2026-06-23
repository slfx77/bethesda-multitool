using System.CommandLine;
using BethesdaMultitool.Core.Formats.Esm.Export.Heightmap;
using BethesdaMultitool.Core.Formats.Esm.Export;
using Spectre.Console;

namespace TerrainAnalyzer.Commands;

internal static class ExportCommands
{
    internal static Command CreateExportCommand()
    {
        var command = new Command("export", "Export heightmap PNGs (individual cells + composite worldmap)");

        var dmpArg = new Argument<string>("dmp") { Description = "Path to memory dump file (.dmp)" };
        var outputOpt = new Option<string>("-o", "--output")
        {
            Description = "Output directory for PNGs",
            DefaultValueFactory = _ => "heightmaps"
        };
        var grayscaleOpt = new Option<bool>("--grayscale") { Description = "Use grayscale instead of color gradient" };
        var verboseOpt = new Option<bool>("-v", "--verbose") { Description = "Show detailed progress" };

        command.Arguments.Add(dmpArg);
        command.Options.Add(outputOpt);
        command.Options.Add(grayscaleOpt);
        command.Options.Add(verboseOpt);

        command.SetAction(async (parseResult, _) =>
        {
            var dmp = parseResult.GetValue(dmpArg)!;
            var output = parseResult.GetValue(outputOpt)!;
            var grayscale = parseResult.GetValue(grayscaleOpt);
            var verbose = parseResult.GetValue(verboseOpt);
            await ExecuteAsync(dmp, output, !grayscale, verbose);
        });

        return command;
    }

    private static async Task ExecuteAsync(string dmpPath, string outputDir, bool useColor, bool verbose)
    {
        using var data = await DumpLoader.LoadAsync(dmpPath, verbose: verbose);

        var landsWithHeightmap = data.ScanResult.LandRecords
            .Where(l => l.Heightmap != null)
            .ToList();

        if (landsWithHeightmap.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No LAND records with heightmaps found.[/]");
            return;
        }

        // Stats
        var exactCount = landsWithHeightmap.Count(l => l.Heightmap?.ExactHeights != null);
        var deltaCount = landsWithHeightmap.Count(l => l.Heightmap!.ExactHeights == null);
        AnsiConsole.MarkupLine(
            $"[yellow]Heights:[/] {exactCount} exact (runtime), {deltaCount} delta-decoded (VHGT)");

        var withCoords = landsWithHeightmap
            .Where(l => l.BestCellX.HasValue && l.BestCellY.HasValue)
            .ToList();
        if (withCoords.Count > 0)
        {
            var minX = withCoords.Min(l => l.BestCellX!.Value);
            var maxX = withCoords.Max(l => l.BestCellX!.Value);
            var minY = withCoords.Min(l => l.BestCellY!.Value);
            var maxY = withCoords.Max(l => l.BestCellY!.Value);
            AnsiConsole.MarkupLine($"[yellow]Cell range:[/] X=[[{minX}..{maxX}]] Y=[[{minY}..{maxY}]]");
        }

        // Clean output directory
        if (Directory.Exists(outputDir))
        {
            Directory.Delete(outputDir, true);
        }

        Directory.CreateDirectory(outputDir);

        // Export individual cell PNGs
        AnsiConsole.MarkupLine($"[blue]Exporting {landsWithHeightmap.Count} individual cell PNGs...[/]");
        await HeightmapPngExporter.ExportLandRecordsAsync(
            data.ScanResult.LandRecords, outputDir, useColor);
        AnsiConsole.MarkupLine($"[green]Individual PNGs exported to:[/] {outputDir}");

        // Export composite worldmap
        var compositePath = Path.Combine(outputDir, "worldmap_composite.png");
        AnsiConsole.MarkupLine("[blue]Generating composite worldmap...[/]");
        await HeightmapPngExporter.ExportCompositeWorldmapAsync(
            data.ScanResult.Heightmaps, data.ScanResult.CellGrids,
            data.ScanResult.LandRecords, compositePath, useColor);

        if (File.Exists(compositePath))
        {
            AnsiConsole.MarkupLine($"[green]Composite worldmap:[/] {compositePath}");
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Composite worldmap not generated (insufficient correlated data).[/]");
        }
    }
}

