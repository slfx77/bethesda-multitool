using System.CommandLine;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Diagnostic harness for per-game water layout (Oblivion vs FO3/FNV). Runs the PRODUCTION semantic
///     parse (<see cref="UnifiedAnalyzer" />, the same path the viewer uses) and reports the water data
///     the renderer actually receives: each worldspace's DefaultLandHeight / DefaultWaterHeight (DNAM) +
///     WaterFormId (NAM2), and a per-worldspace breakdown of how many exterior cells carry an in-range
///     XCLW water height vs the no-water sentinel vs nothing. This tells us whether Oblivion's water
///     reaches the viewer where FO3/FNV's does, or whether the parser is missing a differently-laid-out
///     subrecord — so we fix the real layout instead of synthesizing a fake worldspace default.
/// </summary>
internal static class WaterLayoutCommand
{
    internal static Command Create()
    {
        var command = new Command("water-layout",
            "Production-parse an ESM and report the water data the viewer receives (worldspace DNAM default + per-cell XCLW)");
        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var sampleOpt = new Option<int>("--samples", "-n")
        {
            Description = "How many water-bearing exterior cells to dump in detail per worldspace",
            DefaultValueFactory = _ => 6
        };
        command.Arguments.Add(fileArg);
        command.Options.Add(sampleOpt);
        command.SetAction(parseResult => Execute(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(sampleOpt)));
        return command;
    }

    private static int Execute(string filePath, int samples)
    {
        using var result = UnifiedAnalyzer.AnalyzeAsync(filePath).GetAwaiter().GetResult();
        var worldspaces = result.Records.Worldspaces;
        var cells = result.Records.Cells;

        AnsiConsole.MarkupLine($"[cyan]File:[/] {Path.GetFileName(filePath)}  " +
                               $"[cyan]WRLD:[/] {worldspaces.Count}  [cyan]CELL:[/] {cells.Count}");
        AnsiConsole.WriteLine();

        // Group cells by their worldspace so the per-WRLD breakdown matches what the viewer renders
        // for a selected worldspace.
        var cellsByWorld = cells
            .Where(c => c.WorldspaceFormId is > 0)
            .GroupBy(c => c.WorldspaceFormId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var withDnamWater = 0;
        var withWaterFid = 0;
        foreach (var ws in worldspaces)
        {
            var hasDnamWater = ws.DefaultWaterHeight.HasValue &&
                               !WorldHeightNormalizer.IsNoWaterSentinel(ws.DefaultWaterHeight);
            if (hasDnamWater)
            {
                withDnamWater++;
            }

            if (ws.WaterFormId is > 0)
            {
                withWaterFid++;
            }
        }

        AnsiConsole.MarkupLine(
            $"[green]Worldspaces with a usable DefaultWaterHeight (DNAM):[/] {withDnamWater}/{worldspaces.Count}   " +
            $"[green]with WaterFormId (NAM2):[/] {withWaterFid}/{worldspaces.Count}");
        AnsiConsole.WriteLine();

        // Detail the largest worldspaces (by cell count) — that's where the user is looking (Tamriel, etc.).
        foreach (var ws in worldspaces
                     .OrderByDescending(w => cellsByWorld.GetValueOrDefault(w.FormId)?.Count ?? 0)
                     .Take(6))
        {
            DumpWorldspace(ws, cellsByWorld.GetValueOrDefault(ws.FormId) ?? [], samples);
            AnsiConsole.WriteLine();
        }

        return 0;
    }

    private static void DumpWorldspace(WorldspaceRecord ws, List<CellRecord> cells, int samples)
    {
        var dWater = ws.DefaultWaterHeight;
        var dWaterStr = !dWater.HasValue
            ? "[red](null — no DNAM water)[/]"
            : WorldHeightNormalizer.IsNoWaterSentinel(dWater)
                ? "[yellow](no-water sentinel)[/]"
                : $"{dWater.Value:0.##}";

        AnsiConsole.MarkupLine($"[green]WRLD 0x{ws.FormId:X8}[/] {Markup.Escape(ws.EditorId ?? "?")}  " +
                               $"cells={cells.Count}");
        AnsiConsole.MarkupLine(
            $"  DefaultLandHeight={(ws.DefaultLandHeight is { } l ? l.ToString("0.##") : "-")}  " +
            $"DefaultWaterHeight={dWaterStr}  " +
            $"WaterFormId={(ws.WaterFormId is { } w ? $"0x{w:X8}" : "-")}");

        var exterior = cells.Where(c => c.GridX.HasValue && c.GridY.HasValue).ToList();
        var inRange = 0;
        var sentinel = 0;
        var nullWater = 0;
        foreach (var c in exterior)
        {
            if (c.WaterHeight is not { } h)
            {
                nullWater++;
            }
            else if (WorldHeightNormalizer.IsNoWaterSentinel(h) || Math.Abs(h) >= 1e6f)
            {
                sentinel++;
            }
            else
            {
                inRange++;
            }
        }

        AnsiConsole.MarkupLine(
            $"  exterior cells={exterior.Count}  [cyan]XCLW in-range={inRange}[/]  " +
            $"sentinel/no-water={sentinel}  null(no XCLW)={nullWater}");

        var shown = 0;
        foreach (var c in exterior.Where(c => c.WaterHeight is { } h && Math.Abs(h) < 1e6f))
        {
            if (shown >= samples)
            {
                break;
            }

            AnsiConsole.MarkupLine(
                $"    ext ({c.GridX},{c.GridY}) 0x{c.FormId:X8} {Markup.Escape(c.EditorId ?? "")}  " +
                $"WaterHeight={c.WaterHeight!.Value:0.##}");
            shown++;
        }
    }
}
