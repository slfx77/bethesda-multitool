using System.CommandLine;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Diagnostic for the TES4 PGRD → navigation-overlay pipeline: production-parses an ESM and
///     reports how many pathgrid-backed entries landed in the NavMeshes collection, how many resolved
///     a parent cell (structural GRUP map), and how many synthesize drawable geometry — plus a small
///     sample. The counts prove the overlay's data feed without needing a GUI session.
/// </summary>
internal static class PathgridSummaryCommand
{
    internal static Command Create()
    {
        var command = new Command("pathgrids",
            "Production-parse an ESM and summarize PGRD pathgrid → navigation overlay coverage");
        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var samplesOpt = new Option<int>("--samples", "-n")
        {
            Description = "How many pathgrids to detail",
            DefaultValueFactory = _ => 5
        };
        command.Arguments.Add(fileArg);
        command.Options.Add(samplesOpt);
        command.SetAction(parseResult => Execute(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(samplesOpt)));
        return command;
    }

    private static int Execute(string filePath, int samples)
    {
        using var result = UnifiedAnalyzer.AnalyzeAsync(filePath).GetAwaiter().GetResult();
        var navRecords = result.Records.NavMeshes;
        var pathgrids = navRecords
            .Where(n => n.RawSubrecords.Any(s => s.Signature == "PGRP"))
            .ToList();
        var withCell = pathgrids.Count(p => p.CellFormId != 0);
        var withGeometry = pathgrids.Count(p => NavMeshGeometry.TryParse(p) is not null);

        AnsiConsole.MarkupLine(
            $"[cyan]File:[/] {Path.GetFileName(filePath)}  [cyan]NavMeshes total:[/] {navRecords.Count}  " +
            $"[green]PGRD-backed:[/] {pathgrids.Count}  [green]with parent cell:[/] {withCell}  " +
            $"[green]with drawable geometry:[/] {withGeometry}");

        foreach (var pg in pathgrids.Take(samples))
        {
            var geom = NavMeshGeometry.TryParse(pg);
            AnsiConsole.MarkupLine(
                $"  0x{pg.FormId:X8} cell=0x{pg.CellFormId:X8} points={pg.VertexCount} links={pg.TriangleCount} " +
                $"synthTris={geom?.Triangles.Length ?? 0}");
        }

        return 0;
    }
}
