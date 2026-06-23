using System.CommandLine;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using Spectre.Console;

namespace NifAnalyzer.Commands;

/// <summary>
///     Batch harness: runs the PRODUCTION parse + geometry extraction (the same path the 3D viewer uses,
///     via <see cref="NifGeometryExtractor.Extract" />) over every NIF under a directory and flags files
///     whose output looks broken — huge/NaN bounds, zero submeshes, a clipped block list, or a thrown
///     exception. For the legacy (Oblivion/Morrowind) measure-walk, a single mis-sized block schema
///     desyncs everything after it, so this surfaces the symptom ("holes" / "huge bounds on clutter")
///     across a whole folder at once, then aggregates which block TYPES are over-represented among the
///     broken files — pointing straight at the offending schema. Single process (fast over thousands).
/// </summary>
internal static class MeshScanCommands
{
    public static Command CreateMeshScanCommand()
    {
        var command = new Command("meshscan",
            "Batch production-extract every NIF under a directory and flag broken bounds/geometry (legacy-parse desync finder)");
        var dirArg = new Argument<string>("dir") { Description = "Directory to scan recursively for *.nif" };
        var thresholdOption = new Option<double>("--max-dim")
        {
            Description = "Flag files whose largest bound exceeds this (world units)",
            DefaultValueFactory = _ => 50000.0
        };
        var limitOption = new Option<int>("--list")
        {
            Description = "How many anomalous files to list individually",
            DefaultValueFactory = _ => 40
        };
        var verboseOption = new Option<bool>("--verbose", "-v")
        {
            Description = "Enable production Debug logging (surfaces the legacy measure-walk's 'cannot size block' line)"
        };

        command.Arguments.Add(dirArg);
        command.Options.Add(thresholdOption);
        command.Options.Add(limitOption);
        command.Options.Add(verboseOption);
        command.SetAction(parseResult => Scan(
            parseResult.GetValue(dirArg)!,
            parseResult.GetValue(thresholdOption),
            parseResult.GetValue(limitOption),
            parseResult.GetValue(verboseOption)));
        return command;
    }

    private enum Verdict { Ok, ParseNull, NoGeometry, HugeBounds, Exception }

    private readonly record struct FileResult(
        string Path, Verdict Verdict, int Blocks, int Measured, int Submeshes, float MaxDim,
        IReadOnlyList<string> BlockTypes, string? Detail, uint Version);

    private static string FmtVersion(uint v) =>
        $"{(v >> 24) & 0xFF}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";

    private static void Scan(string dir, double maxDim, int listLimit, bool verbose)
    {
        if (!Directory.Exists(dir))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Directory not found: {0}", Markup.Escape(dir));
            return;
        }

        if (verbose)
        {
            BethesdaMultitool.Core.Diagnostics.Logger.Instance.SetVerbose(true);
        }

        var files = Directory.EnumerateFiles(dir, "*.nif", SearchOption.AllDirectories).ToList();
        AnsiConsole.MarkupLine($"[cyan]Scanning[/] {files.Count} NIF files under {Markup.Escape(dir)} (max-dim flag: {maxDim:N0})");

        var results = new List<FileResult>(files.Count);
        foreach (var file in files)
        {
            results.Add(Analyze(file, (float)maxDim));
        }

        var broken = results.Where(r => r.Verdict != Verdict.Ok).ToList();

        // Broken-by-version breakdown (the legacy header/measure walk is version-gated, so this splits
        // the failures into actionable buckets — e.g. pre-20.0.0.3 header layout vs 20.0.0.4 desync).
        AnsiConsole.WriteLine();
        var verTable = new Table().Border(TableBorder.Rounded)
            .AddColumn("NIF Version").AddColumn(new TableColumn("Broken").RightAligned()).AddColumn(new TableColumn("OK").RightAligned());
        foreach (var grp in results.GroupBy(r => r.Version).OrderByDescending(g => g.Count(x => x.Verdict != Verdict.Ok)))
        {
            _ = verTable.AddRow(FmtVersion(grp.Key), grp.Count(x => x.Verdict != Verdict.Ok).ToString(), grp.Count(x => x.Verdict == Verdict.Ok).ToString());
        }
        AnsiConsole.Write(verTable);

        // Per-verdict tally.
        AnsiConsole.WriteLine();
        var tally = new Table().Border(TableBorder.Rounded).AddColumn("Verdict").AddColumn("Count");
        foreach (var grp in results.GroupBy(r => r.Verdict).OrderByDescending(g => g.Count()))
        {
            _ = tally.AddRow(grp.Key.ToString(), grp.Count().ToString());
        }
        AnsiConsole.Write(tally);

        if (broken.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No anomalies.[/]");
            return;
        }

        // Block-type over-representation: for each block type, how often it appears in broken vs healthy
        // files. A type present in (nearly) every broken file but rare in healthy ones is the prime suspect.
        var healthy = results.Where(r => r.Verdict == Verdict.Ok).ToList();
        var brokenTypeCounts = CountTypes(broken);
        var healthyTypeCounts = CountTypes(healthy);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Block types over-represented in broken files[/] (type : brokenHits/{0} broken vs healthyHits/{1} healthy):", broken.Count, healthy.Count);
        var suspectTable = new Table().Border(TableBorder.Simple)
            .AddColumn("Block Type")
            .AddColumn(new TableColumn("Broken %").RightAligned())
            .AddColumn(new TableColumn("Healthy %").RightAligned())
            .AddColumn(new TableColumn("Broken").RightAligned())
            .AddColumn(new TableColumn("Healthy").RightAligned());
        foreach (var kv in brokenTypeCounts
                     .Select(kv => (Type: kv.Key, Broken: kv.Value,
                         Healthy: healthyTypeCounts.GetValueOrDefault(kv.Key, 0)))
                     .Select(t => (t.Type, t.Broken, t.Healthy,
                         BrokenPct: 100.0 * t.Broken / Math.Max(1, broken.Count),
                         HealthyPct: 100.0 * t.Healthy / Math.Max(1, healthy.Count)))
                     .OrderByDescending(t => t.BrokenPct - t.HealthyPct)
                     .Take(20))
        {
            _ = suspectTable.AddRow(
                Markup.Escape(kv.Type),
                $"{kv.BrokenPct:F0}%",
                $"{kv.HealthyPct:F0}%",
                kv.Broken.ToString(),
                kv.Healthy.ToString());
        }
        AnsiConsole.Write(suspectTable);

        // Individual anomalies.
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Anomalous files[/] (first {0}):", Math.Min(listLimit, broken.Count));
        foreach (var r in broken.Take(listLimit))
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]{r.Verdict}[/] v{FmtVersion(r.Version)} hdrBlocks={r.Blocks} measured={r.Measured} sub={r.Submeshes} maxDim={r.MaxDim:G6} " +
                $"{Markup.Escape(Path.GetFileName(r.Path))}{(r.Detail is null ? "" : "  [dim]" + Markup.Escape(r.Detail) + "[/]")}");
        }
    }

    private static Dictionary<string, int> CountTypes(IEnumerable<FileResult> set)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in set)
        {
            foreach (var t in r.BlockTypes.Distinct(StringComparer.Ordinal))
            {
                counts[t] = counts.GetValueOrDefault(t, 0) + 1;
            }
        }
        return counts;
    }

    private static FileResult Analyze(string path, float maxDimThreshold)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            var nif = NifParser.Parse(data);
            if (nif == null)
            {
                return new FileResult(path, Verdict.ParseNull, 0, 0, 0, 0, [], null, 0);
            }

            var types = nif.BlockTypeNames?.ToList() ?? [];
            var measured = nif.Blocks?.Count ?? 0;
            var model = NifGeometryExtractor.Extract(data, nif, textureResolver: null,
                skipSkinning: true, treatRootsAsIdentity: true, collectBillboards: true, dropBoneAttachedShapes: true);

            if (model == null || !model.HasGeometry)
            {
                return new FileResult(path, Verdict.NoGeometry, nif.BlockCount, measured, 0, 0, types, null, nif.BinaryVersion);
            }

            var max = Math.Max(model.Width, Math.Max(model.Height, model.Depth));
            var verdict = float.IsNaN(max) || float.IsInfinity(max) || max > maxDimThreshold
                ? Verdict.HugeBounds
                : Verdict.Ok;
            return new FileResult(path, verdict, nif.BlockCount, measured, model.Submeshes.Count, max, types, null, nif.BinaryVersion);
        }
        catch (Exception ex)
        {
            return new FileResult(path, Verdict.Exception, 0, 0, 0, 0, [], ex.GetType().Name + ": " + ex.Message, 0);
        }
    }
}
