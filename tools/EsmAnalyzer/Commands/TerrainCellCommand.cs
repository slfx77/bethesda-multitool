using System.CommandLine;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Diagnostic harness for a single exterior terrain cell: runs the PRODUCTION semantic parse and
///     reports, for one worldspace + grid coordinate, every candidate CELL at that grid (duplicates,
///     cross-worldspace contamination), which one the viewer's spatial index would select, its LAND
///     provenance (VHGT base/delta stats, decoded corner heights), and the decoded 33-sample edge
///     arrays shared with all four neighbors including per-edge max/mean mismatch. Built for the
///     AnvilWorld (-45,-8) "wrong height map / disconnected terrain" report — separates record
///     selection, override merging, and per-cell decode from a genuine authored discontinuity.
/// </summary>
internal static class TerrainCellCommand
{
    internal static Command Create()
    {
        var command = new Command("terrain-cell",
            "Production-parse an ESM and dump one exterior cell's terrain selection + neighbor edge continuity");
        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var worldspaceOpt = new Option<string>("--worldspace", "-w")
        {
            Description = "Worldspace EditorID (e.g. AnvilWorld)",
            Required = true
        };
        var gridXOpt = new Option<int>("--grid-x", "-x") { Description = "Cell grid X", Required = true };
        var gridYOpt = new Option<int>("--grid-y", "-y") { Description = "Cell grid Y", Required = true };
        command.Arguments.Add(fileArg);
        command.Options.Add(worldspaceOpt);
        command.Options.Add(gridXOpt);
        command.Options.Add(gridYOpt);
        command.SetAction(parseResult => Execute(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(worldspaceOpt)!,
            parseResult.GetValue(gridXOpt),
            parseResult.GetValue(gridYOpt)));
        return command;
    }

    private static int Execute(string filePath, string worldspaceEditorId, int gridX, int gridY)
    {
        using var result = UnifiedAnalyzer.AnalyzeAsync(filePath).GetAwaiter().GetResult();
        var worldspaces = result.Records.Worldspaces;
        var ws = worldspaces.FirstOrDefault(w =>
            string.Equals(w.EditorId, worldspaceEditorId, StringComparison.OrdinalIgnoreCase));
        if (ws == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] worldspace '{worldspaceEditorId}' not found");
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"[cyan]File:[/] {Path.GetFileName(filePath)}  [cyan]WRLD:[/] 0x{ws.FormId:X8} " +
            $"{Markup.Escape(ws.EditorId ?? "?")}  [cyan]target grid:[/] ({gridX},{gridY})");
        AnsiConsole.WriteLine();

        // 1. Every cell in the FILE at this grid — cross-worldspace contamination is visible here.
        var fileWide = result.Records.Cells
            .Where(c => c.GridX == gridX && c.GridY == gridY)
            .ToList();
        AnsiConsole.MarkupLine($"[green]Cells at ({gridX},{gridY}) file-wide:[/] {fileWide.Count}");
        foreach (var c in fileWide)
        {
            var owner = worldspaces.FirstOrDefault(w => w.FormId == c.WorldspaceFormId);
            AnsiConsole.MarkupLine(
                $"  0x{c.FormId:X8} {Markup.Escape(c.EditorId ?? "(no EDID)")}  " +
                $"ws={(owner is null ? $"0x{c.WorldspaceFormId ?? 0:X8}?" : Markup.Escape(owner.EditorId ?? "?"))}  " +
                $"refs={c.PlacedObjects.Count} persistent={c.IsPersistentCell} virtual={c.IsVirtual} " +
                $"land={(c.Heightmap is null ? "none" : $"base={c.Heightmap.HeightOffset:F2}")}");
        }

        AnsiConsole.WriteLine();

        // 2. Candidates INSIDE the target worldspace + the spatial-index winner (viewer selection).
        var candidates = ws.Cells.Where(c => c.GridX == gridX && c.GridY == gridY).ToList();
        if (candidates.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No cell at ({gridX},{gridY}) in {worldspaceEditorId}.[/]");
            return 1;
        }

        var winner = candidates[0];
        for (var i = 1; i < candidates.Count; i++)
        {
            if (global::BethesdaMultitool.WorldSpatialIndex.PreferGridLookupCell(candidates[i], winner))
            {
                winner = candidates[i];
            }
        }

        AnsiConsole.MarkupLine(
            $"[green]In-worldspace candidates:[/] {candidates.Count}  " +
            $"[green]spatial-index winner:[/] 0x{winner.FormId:X8} {Markup.Escape(winner.EditorId ?? "(no EDID)")}");
        DumpCellTerrain("winner", winner);

        // Raw extracted-LAND provenance for the winner cell — shows the subrecord census the
        // relic gate keys on (VTEX-only pre-release relics are skipped at attach time).
        foreach (var land in result.RawResult.EsmRecords.LandRecords
                     .Where(l => l.ParentCellFormId == winner.FormId))
        {
            AnsiConsole.MarkupLine(
                $"  [[raw-land]] 0x{land.Header.FormId:X8} vtex={land.VtexCount} btxt={land.BtxtCount} " +
                $"atxt={land.AtxtCount} vtxt={land.VtxtCount} heightmap={(land.Heightmap != null ? "yes" : "no")}");
        }

        // 3. Edge continuity against the four neighbors (as the viewer selects them).
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]Neighbor edge continuity (decoded heights, 33 shared samples):[/]");
        DumpEdge(ws, winner, gridX, gridY, gridX, gridY + 1, "N");
        DumpEdge(ws, winner, gridX, gridY, gridX, gridY - 1, "S");
        DumpEdge(ws, winner, gridX, gridY, gridX + 1, gridY, "E");
        DumpEdge(ws, winner, gridX, gridY, gridX - 1, gridY, "W");
        return 0;
    }

    private static void DumpCellTerrain(string label, CellRecord cell)
    {
        if (cell.Heightmap is not { } hm)
        {
            AnsiConsole.MarkupLine($"  [[{label}]] NO heightmap attached");
            return;
        }

        var heights = hm.CalculateHeights();
        var min = float.MaxValue;
        var max = float.MinValue;
        for (var y = 0; y < 33; y++)
        {
            for (var x = 0; x < 33; x++)
            {
                min = Math.Min(min, heights[y, x]);
                max = Math.Max(max, heights[y, x]);
            }
        }

        AnsiConsole.MarkupLine(
            $"  [[{label}]] VHGT base={hm.HeightOffset:F2} exact={(hm.ExactHeights != null ? "yes" : "no")} " +
            $"vhgtOffset=0x{hm.Offset:X} sourceCell={(hm.SourceParentCellFormId is { } src ? $"0x{src:X8}" : "-")} " +
            $"decoded min={min:F1} max={max:F1}  corners SW={heights[0, 0]:F1} SE={heights[0, 32]:F1} " +
            $"NW={heights[32, 0]:F1} NE={heights[32, 32]:F1}");
    }

    private static void DumpEdge(
        WorldspaceRecord ws, CellRecord cell, int gx, int gy, int ngx, int ngy, string direction)
    {
        var neighborCandidates = ws.Cells.Where(c => c.GridX == ngx && c.GridY == ngy).ToList();
        if (neighborCandidates.Count == 0)
        {
            AnsiConsole.MarkupLine($"  {direction} ({ngx},{ngy}): no neighbor cell");
            return;
        }

        var neighbor = neighborCandidates[0];
        for (var i = 1; i < neighborCandidates.Count; i++)
        {
            if (global::BethesdaMultitool.WorldSpatialIndex.PreferGridLookupCell(neighborCandidates[i], neighbor))
            {
                neighbor = neighborCandidates[i];
            }
        }

        if (cell.Heightmap is not { } ownHm || neighbor.Heightmap is not { } nbHm)
        {
            AnsiConsole.MarkupLine(
                $"  {direction} ({ngx},{ngy}) 0x{neighbor.FormId:X8}: " +
                $"{(cell.Heightmap is null ? "own" : "neighbor")} heightmap missing");
            return;
        }

        var own = ownHm.CalculateHeights();
        var other = nbHm.CalculateHeights();

        // VHGT rows run south→north (y=0 south edge), columns west→east (x=0 west edge). The shared
        // boundary is our outermost row/column vs the neighbor's opposite outermost row/column.
        var maxDiff = 0f;
        var sumDiff = 0f;
        for (var i = 0; i < 33; i++)
        {
            var (a, b) = direction switch
            {
                "N" => (own[32, i], other[0, i]),
                "S" => (own[0, i], other[32, i]),
                "E" => (own[i, 32], other[i, 0]),
                _ => (own[i, 0], other[i, 32]),
            };
            var diff = Math.Abs(a - b);
            maxDiff = Math.Max(maxDiff, diff);
            sumDiff += diff;
        }

        var verdict = maxDiff <= 0.01f ? "[green]CONTINUOUS[/]"
            : maxDiff <= 16f ? "[yellow]NEAR[/]"
            : "[red]DISCONTINUOUS[/]";
        AnsiConsole.MarkupLine(
            $"  {direction} ({ngx},{ngy}) 0x{neighbor.FormId:X8} {Markup.Escape(neighbor.EditorId ?? "")}: " +
            $"max|Δ|={maxDiff:F1} mean|Δ|={sumDiff / 33f:F1} {verdict} " +
            $"(neighbor base={nbHm.HeightOffset:F2})");
    }
}
