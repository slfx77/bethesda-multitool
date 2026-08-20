using System.CommandLine;
using System.Globalization;
using Spectre.Console;

namespace EsmAnalyzer.Commands.Dmp;

/// <summary>
///     Reads a dangling-REFR sweep CSV (produced by `dmp sweep-refrs`) and applies
///     a grid-attribution heuristic against the cell_worldspace_authority.json:
///     for each (gridX, gridY) carrying dangling REFRs, find candidate cells (any
///     worldspace), rank by (has editor_id desc, owning-worldspace cell count desc),
///     and pick the winner.
///     Emits a new `dangling_refs` section into the authority JSON (schema_version
///     bumps 2 -&gt; 3). All other top-level sections are preserved verbatim.
/// </summary>
internal static class DmpAttributeDanglingCommand
{
    public static Command CreateAttributeDanglingCommand()
    {
        var command = new Command(
            "attribute-dangling",
            "Apply grid-attribution heuristic to a sweep-refrs CSV and inject a dangling_refs section " +
            "into cell_worldspace_authority.json (schema_version 2 -> 3).");

        var sweepOpt = new Option<string>("--sweep")
        {
            Description = "Path to dangling_refr_sweep_all.csv (from `dmp sweep-refrs`)",
            Required = true
        };
        var authOpt = new Option<string?>("--authority")
        {
            Description = "Path to existing authority JSON (default: data/cell_worldspace_authority.json)"
        };
        var outOpt = new Option<string?>("-o", "--output")
        {
            Description = "Output JSON path (default: overwrite input)"
        };
        var minRefsOpt = new Option<int>("--min-refs")
        {
            Description = "Skip grid attributions with fewer than this many NULL-parent REFRs",
            DefaultValueFactory = _ => 1
        };
        var excludeNearOriginOpt = new Option<int>("--exclude-near-origin")
        {
            Description = "Skip grids with (gx² + gy²) below this threshold (filters interior/uninitialized noise)",
            DefaultValueFactory = _ => 25
        };
        var positionsOpt = new Option<string?>("--positions")
        {
            Description =
                "Optional path to per-REFR positions CSV (from `dmp sweep-refrs --per-refr-out`). " +
                "When supplied, the dangling_refs section also includes a `positions` array " +
                "with FormID, world position, scale, and attributed cell per REFR."
        };
        var esmOpt = new Option<string[]?>("--esm")
        {
            Description =
                "Path to a Sample ESM/ESP used to enrich positions with editor IDs, base names, " +
                "model paths, and map-marker metadata. Repeat to ingest multiple builds; later " +
                "ESMs only fill gaps left by earlier ones.",
            AllowMultipleArgumentsPerToken = true
        };

        command.Options.Add(sweepOpt);
        command.Options.Add(authOpt);
        command.Options.Add(outOpt);
        command.Options.Add(minRefsOpt);
        command.Options.Add(excludeNearOriginOpt);
        command.Options.Add(positionsOpt);
        command.Options.Add(esmOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var sweep = parseResult.GetValue(sweepOpt)!;
            var authority = parseResult.GetValue(authOpt)
                            ?? Path.Combine("data", "cell_worldspace_authority.json");
            var output = parseResult.GetValue(outOpt) ?? authority;
            var minRefs = parseResult.GetValue(minRefsOpt);
            var nearOrigin = parseResult.GetValue(excludeNearOriginOpt);
            var positions = parseResult.GetValue(positionsOpt);
            var esms = parseResult.GetValue(esmOpt) ?? [];
            await RunAsync(sweep, authority, output, minRefs, nearOrigin, positions, esms, ct);
        });

        return command;
    }

    private static async Task<int> RunAsync(string sweepCsv, string authorityIn, string authorityOut, int minRefs,
        int nearOriginCutoff, string? positionsCsv, string[] esmPaths, CancellationToken ct)
    {
        if (!File.Exists(sweepCsv))
        {
            AnsiConsole.MarkupLine($"[red]Sweep CSV not found:[/] {Markup.Escape(sweepCsv)}");
            return 1;
        }

        if (!File.Exists(authorityIn))
        {
            AnsiConsole.MarkupLine($"[red]Authority JSON not found:[/] {Markup.Escape(authorityIn)}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[blue]Loading authority:[/] {Markup.Escape(authorityIn)}");
        var auth = DmpDanglingReaders.LoadAuthority(authorityIn);
        AnsiConsole.MarkupLine(
            $"  worldspaces: [cyan]{auth.WorldspaceNames.Count}[/]  cells: [cyan]{auth.Cells.Count:N0}[/]  " +
            $"grids-with-cells: [cyan]{auth.CellsByGrid.Count:N0}[/]");

        AnsiConsole.MarkupLine($"[blue]Reading sweep:[/] {Markup.Escape(sweepCsv)}");
        var sweepRows = DmpDanglingReaders.LoadSweep(sweepCsv, minRefs, nearOriginCutoff);
        AnsiConsole.MarkupLine($"  (dump, cell) buckets after filter: [cyan]{sweepRows.Count:N0}[/]");

        AnsiConsole.MarkupLine("[blue]Applying heuristic...[/]");
        var attributions = DmpDanglingAttributor.Attribute(auth, sweepRows);

        if (!string.IsNullOrEmpty(positionsCsv) && File.Exists(positionsCsv))
        {
            AnsiConsole.MarkupLine($"[blue]Reading positions:[/] {Markup.Escape(positionsCsv)}");
            var positions = DmpDanglingReaders.LoadPositions(positionsCsv);
            AnsiConsole.MarkupLine($"  unique REFRs: [cyan]{positions.Count:N0}[/]");

            var enrichment = await DmpDanglingEnrichment.BuildEnrichmentIndexAsync(esmPaths, ct);
            DmpDanglingAttributor.AttributePositions(auth, attributions, positions, enrichment);

            var byPosConfidence = attributions.Positions.GroupBy(p => p.Confidence)
                .ToDictionary(g => g.Key, g => g.Count());
            AnsiConsole.MarkupLine($"  attributed positions: [green]{attributions.Positions.Count:N0}[/]");
            foreach (var k in new[] { "ESM", "HIGH", "STRONG", "MEDIUM", "LOW", "CUT" })
            {
                var n = byPosConfidence.GetValueOrDefault(k, 0);
                if (n > 0)
                {
                    AnsiConsole.MarkupLine($"    {k,-8}: [cyan]{n:N0}[/]");
                }
            }

            if (enrichment.Count > 0)
            {
                var enrichedCount = attributions.Positions.Count(p => p.HasEnrichment);
                AnsiConsole.MarkupLine($"  enriched from ESMs: [green]{enrichedCount:N0}[/]");
            }
        }
        else if (!string.IsNullOrEmpty(positionsCsv))
        {
            AnsiConsole.MarkupLine($"[yellow]Positions CSV not found:[/] {Markup.Escape(positionsCsv)} (skipping)");
        }

        AnsiConsole.MarkupLine($"  grid attributions: [green]{attributions.GridAttributions.Count:N0}[/]");
        AnsiConsole.MarkupLine($"  cut regions     : [yellow]{attributions.CutRegions.Count:N0}[/]");

        var byConfidence = attributions.GridAttributions
            .GroupBy(kv => kv.Value.Confidence)
            .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value.RefCount));
        var byWs = attributions.GridAttributions
            .GroupBy(kv => kv.Value.WorldspaceFormId)
            .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value.RefCount));

        var total = attributions.GridAttributions.Sum(kv => kv.Value.RefCount);
        var totalCut = attributions.CutRegions.Sum(c => c.RefCount);
        AnsiConsole.MarkupLine($"  total attributed REFRs: [green]{total:N0}[/]  cut: [yellow]{totalCut:N0}[/]");

        AnsiConsole.WriteLine();
        var conf = new Table().Border(TableBorder.Rounded).AddColumn("Confidence")
            .AddColumn("REFRs", c => c.RightAligned());
        foreach (var k in new[] { "HIGH", "STRONG", "MEDIUM", "LOW" })
        {
            conf.AddRow(k, byConfidence.GetValueOrDefault(k, 0).ToString("N0", CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(conf);

        AnsiConsole.WriteLine();
        var ws = new Table().Border(TableBorder.Rounded).AddColumn("Worldspace")
            .AddColumn("REFRs", c => c.RightAligned());
        foreach (var (id, n) in byWs.OrderByDescending(kv => kv.Value).Take(10))
        {
            var name = auth.WorldspaceNames.GetValueOrDefault(id, "?");
            ws.AddRow($"0x{id:X8} {Markup.Escape(name)}", n.ToString("N0", CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(ws);

        AnsiConsole.MarkupLine($"\n[blue]Writing:[/] {Markup.Escape(authorityOut)}");
        DmpDanglingWriter.RewriteAuthority(authorityIn, authorityOut, attributions);
        AnsiConsole.MarkupLine("[green]Done.[/] schema_version bumped to 3.");
        return 0;
    }
}