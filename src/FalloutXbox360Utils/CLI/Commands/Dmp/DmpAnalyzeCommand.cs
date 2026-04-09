using System.CommandLine;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Semantic;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Dmp;

/// <summary>
///     Analyze one or more DMP files via the unified semantic pipeline and emit diagnostic
///     sections covering persistent references, map markers, per-cell breakdown, unresolved
///     buckets, and runtime worldspace cell maps. Replaces the legacy <c>dmp-diag</c> command,
///     which ran its own scanner pipeline and re-implemented cell grouping.
///     All data here is sourced from <see cref="RecordCollection" /> via
///     <see cref="SemanticFileLoader.LoadAsync" />, so any future cell-linkage fix
///     (persistent ref redistribution, unresolved buckets, etc.) automatically reaches the
///     diagnostic surface.
/// </summary>
internal static class DmpAnalyzeCommand
{
    public static Command Create()
    {
        var command = new Command("analyze",
            "Analyze DMP files via the unified semantic pipeline (cells, persistent refs, map markers, worldspaces)");

        var pathArg = new Argument<string>("path")
        {
            Description = "Path to a .dmp file or a directory containing .dmp files"
        };
        command.Arguments.Add(pathArg);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var path = parseResult.GetValue(pathArg)!;
            await RunAsync(path, cancellationToken);
        });

        return command;
    }

    private static async Task RunAsync(string path, CancellationToken cancellationToken)
    {
        List<string> dmpFiles;
        if (Directory.Exists(path))
        {
            dmpFiles = Directory.GetFiles(path, "*.dmp")
                .Where(f => !Path.GetFileName(f).Contains("hangdump", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }
        else if (File.Exists(path))
        {
            dmpFiles = [path];
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Path not found: {path}");
            return;
        }

        if (dmpFiles.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No .dmp files found in:[/] {path}");
            return;
        }

        AnsiConsole.MarkupLine($"[blue]Analyzing {dmpFiles.Count} DMP file(s) via unified semantic pipeline...[/]");
        AnsiConsole.WriteLine();

        var results = new List<AnalyzeRow>();
        foreach (var dmpFile in dmpFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(dmpFile);
            try
            {
                using var loaded = await SemanticFileLoader.LoadAsync(
                    dmpFile,
                    new SemanticFileLoadOptions { FileType = AnalysisFileType.Minidump },
                    cancellationToken);
                results.Add(BuildRow(fileName, loaded.Records));
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red]{Markup.Escape(fileName)}: {Markup.Escape(ex.Message)}[/]");
            }
        }

        if (results.Count == 0)
        {
            return;
        }

        PrintSummaryTable(results);
        PrintMapMarkers(results);
        PrintPersistentBreakdown(results);
        PrintPerCellBreakdown(results);
        PrintUnresolvedBuckets(results);
        PrintWorldspaceCellMaps(results);
    }

    private static AnalyzeRow BuildRow(string fileName, RecordCollection records)
    {
        var allPlaced = records.Cells.SelectMany(c => c.PlacedObjects).ToList();
        var persistentRefs = allPlaced.Where(p => p.IsPersistent).ToList();
        var markers = records.MapMarkers;

        var persistentCellsCount = records.Cells.Count(c => c.IsPersistentCell);
        var unresolvedBuckets = records.Cells.Where(c => c.IsUnresolvedBucket).ToList();
        var unresolvedRefCount = unresolvedBuckets.Sum(c => c.PlacedObjects.Count);

        return new AnalyzeRow(
            fileName,
            records.Cells.Count,
            records.Worldspaces.Count,
            allPlaced.Count,
            persistentRefs.Count,
            persistentCellsCount,
            markers.Count,
            unresolvedRefCount,
            persistentRefs,
            markers,
            records.Cells,
            unresolvedBuckets,
            records.RuntimeWorldspaceMaps);
    }

    private static void PrintSummaryTable(List<AnalyzeRow> results)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]DMP Analysis Summary[/]")
            .AddColumn(new TableColumn("[bold]File[/]"))
            .AddColumn(new TableColumn("[bold]Cells[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Worldspaces[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Placed[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Persistent[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]PCellStubs[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Markers[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Unresolved[/]").RightAligned());

        foreach (var r in results)
        {
            var name = r.FileName.Length > 35 ? r.FileName[..35] + "..." : r.FileName;
            var unresolvedColor = r.UnresolvedRefCount > 0 ? "yellow" : "grey";
            table.AddRow(
                Markup.Escape(name),
                r.CellCount.ToString("N0"),
                r.WorldspaceCount.ToString("N0"),
                r.PlacedCount.ToString("N0"),
                r.PersistentCount > 0 ? $"[green]{r.PersistentCount:N0}[/]" : "[grey]0[/]",
                r.PersistentCellStubs > 0 ? $"[cyan]{r.PersistentCellStubs}[/]" : "[grey]0[/]",
                r.MarkerCount > 0 ? $"[green]{r.MarkerCount:N0}[/]" : "[grey]0[/]",
                $"[{unresolvedColor}]{r.UnresolvedRefCount:N0}[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Summary:[/] {results.Count} file(s) scanned");
        AnsiConsole.MarkupLine(
            $"  Files with persistent refs:  [green]{results.Count(r => r.PersistentCount > 0)}[/] / {results.Count}");
        AnsiConsole.MarkupLine(
            $"  Files with map markers:      [green]{results.Count(r => r.MarkerCount > 0)}[/] / {results.Count}");
        AnsiConsole.MarkupLine(
            $"  Files with unresolved refs:  [yellow]{results.Count(r => r.UnresolvedRefCount > 0)}[/] / {results.Count}");
    }

    private static void PrintMapMarkers(List<AnalyzeRow> results)
    {
        var withMarkers = results.Where(r => r.MarkerCount > 0).ToList();
        if (withMarkers.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold yellow]Map Marker Details:[/]");
        foreach (var r in withMarkers)
        {
            AnsiConsole.MarkupLine($"\n  [cyan]{Markup.Escape(r.FileName)}[/] — {r.Markers.Count} markers:");
            foreach (var m in r.Markers.Take(30))
            {
                var markerName = m.MarkerName ?? "(no name)";
                var type = m.MarkerType.HasValue ? $"type={m.MarkerType.Value}" : "no type";
                var pos = $"({m.X:F0}, {m.Y:F0})";
                var persistent = m.IsPersistent ? " PERSISTENT" : "";
                AnsiConsole.WriteLine($"    0x{m.FormId:X8} [{type}] {markerName} {pos}{persistent}");
            }

            if (r.Markers.Count > 30)
            {
                AnsiConsole.MarkupLine($"    ... and {r.Markers.Count - 30} more");
            }
        }
    }

    private static void PrintPersistentBreakdown(List<AnalyzeRow> results)
    {
        var withPersistent = results.Where(r => r.PersistentCount > 0).ToList();
        if (withPersistent.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold yellow]Persistent Reference Details:[/]");
        foreach (var r in withPersistent)
        {
            var byType = r.PersistentRefs.GroupBy(x => x.RecordType)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}={g.Count()}")
                .ToList();

            var markerInPersistent = r.PersistentRefs.Count(x => x.IsMapMarker);
            var esmRefs = r.PersistentRefs.Count(x => x.FormId >> 24 == 0x00);
            var runtimeRefs = r.PersistentRefs.Count(x => x.FormId >> 24 == 0xFF);
            var otherRefs = r.PersistentRefs.Count - esmRefs - runtimeRefs;
            var redistributed = r.PersistentRefs.Count(x => x.OriginCellFormId.HasValue);

            AnsiConsole.MarkupLine(
                $"  [cyan]{Markup.Escape(r.FileName)}[/] — " +
                $"{r.PersistentCount} persistent ({string.Join(", ", byType)})" +
                (markerInPersistent > 0 ? $", [green]{markerInPersistent} are map markers[/]" : ""));
            AnsiConsole.MarkupLine(
                $"    FormID origin: [green]ESM (0x00)={esmRefs}[/], " +
                $"[yellow]Runtime (0xFF)={runtimeRefs}[/]" +
                (otherRefs > 0 ? $", Other={otherRefs}" : ""));
            if (redistributed > 0)
            {
                AnsiConsole.MarkupLine(
                    $"    [cyan]{redistributed}[/] refs redistributed from persistent containers " +
                    "to their real exterior tiles by world position");
            }
        }
    }

    private static void PrintPerCellBreakdown(List<AnalyzeRow> results)
    {
        var largeResults = results.Where(r => r.PlacedCount > 100).ToList();
        if (largeResults.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold yellow]Per-Cell Breakdown (top cells by object count):[/]");
        foreach (var r in largeResults)
        {
            AnsiConsole.MarkupLine($"\n  [cyan]{Markup.Escape(r.FileName)}[/]:");

            var topCells = r.Cells
                .Where(c => c.PlacedObjects.Count > 0 && !c.IsUnresolvedBucket)
                .OrderByDescending(c => c.PlacedObjects.Count)
                .Take(20)
                .ToList();

            foreach (var cell in topCells)
            {
                var modIndex = (byte)(cell.FormId >> 24);
                string sourceLabel;
                if (cell.IsVirtual)
                {
                    sourceLabel = "[yellow]VIRT[/]";
                }
                else if (modIndex == 0xFF)
                {
                    sourceLabel = "[yellow]RT[/]";
                }
                else
                {
                    sourceLabel = "[green]ESM[/]";
                }

                string kindTag;
                if (cell.IsPersistentCell)
                {
                    kindTag = " [magenta](Persistent)[/]";
                }
                else if (cell.IsInterior)
                {
                    kindTag = " [grey](Interior)[/]";
                }
                else if (cell.GridX.HasValue)
                {
                    kindTag = $" [grey]({cell.GridX},{cell.GridY})[/]";
                }
                else
                {
                    kindTag = "";
                }

                var refrCount = cell.PlacedObjects.Count(p => p.RecordType == "REFR");
                var achrCount = cell.PlacedObjects.Count(p => p.RecordType == "ACHR");
                var acreCount = cell.PlacedObjects.Count(p => p.RecordType == "ACRE");
                var typeParts = new List<string>();
                if (refrCount > 0) typeParts.Add($"REFR={refrCount}");
                if (achrCount > 0) typeParts.Add($"ACHR={achrCount}");
                if (acreCount > 0) typeParts.Add($"ACRE={acreCount}");

                var label = cell.EditorId ?? cell.FullName ?? "";
                var labelMarkup = string.IsNullOrEmpty(label) ? "" : $" {Markup.Escape(label)}";

                AnsiConsole.MarkupLine(
                    $"    {sourceLabel} 0x{cell.FormId:X8}{kindTag}{labelMarkup}: " +
                    $"{cell.PlacedObjects.Count,5} objects ({string.Join(", ", typeParts)})");
            }
        }
    }

    private static void PrintUnresolvedBuckets(List<AnalyzeRow> results)
    {
        var withUnresolved = results.Where(r => r.UnresolvedBuckets.Count > 0).ToList();
        if (withUnresolved.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            "[bold yellow]Unresolved Buckets[/] [grey](refs whose owning cell could not be inferred)[/]:");
        foreach (var r in withUnresolved)
        {
            AnsiConsole.MarkupLine($"\n  [cyan]{Markup.Escape(r.FileName)}[/]:");
            foreach (var bucket in r.UnresolvedBuckets.OrderByDescending(c => c.PlacedObjects.Count))
            {
                var label = bucket.EditorId ?? "[Unresolved]";
                var sample = bucket.PlacedObjects.Take(3)
                    .Select(p => $"0x{p.FormId:X8}({p.X:F0},{p.Y:F0})")
                    .ToList();

                AnsiConsole.MarkupLine(
                    $"    {Markup.Escape(label)}: [yellow]{bucket.PlacedObjects.Count}[/] refs" +
                    (sample.Count > 0 ? $" — sample: {string.Join(", ", sample)}" : ""));
            }
        }
    }

    private static void PrintWorldspaceCellMaps(List<AnalyzeRow> results)
    {
        var withMaps = results.Where(r => r.RuntimeWorldspaceMaps.Count > 0).ToList();
        if (withMaps.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold yellow]Worldspace Cell Maps (from runtime pCellMap hash tables):[/]");
        foreach (var r in withMaps)
        {
            AnsiConsole.MarkupLine(
                $"\n  [cyan]{Markup.Escape(r.FileName)}[/] — {r.RuntimeWorldspaceMaps.Count} worldspaces:");

            foreach (var (wsFormId, wsData) in r.RuntimeWorldspaceMaps.OrderByDescending(kv => kv.Value.Cells.Count))
            {
                var cellCount = wsData.Cells.Count;
                var persistCell = wsData.PersistentCellFormId.HasValue
                    ? $"persistent=0x{wsData.PersistentCellFormId.Value:X8}"
                    : "no persistent";
                var parent = wsData.ParentWorldFormId.HasValue
                    ? $", parent=0x{wsData.ParentWorldFormId.Value:X8}"
                    : "";

                if (cellCount > 0)
                {
                    var minX = wsData.Cells.Min(c => c.GridX);
                    var maxX = wsData.Cells.Max(c => c.GridX);
                    var minY = wsData.Cells.Min(c => c.GridY);
                    var maxY = wsData.Cells.Max(c => c.GridY);
                    var interiorCount = wsData.Cells.Count(c => c.IsInterior);

                    AnsiConsole.MarkupLine(
                        $"    0x{wsFormId:X8}: [green]{cellCount}[/] cells, " +
                        $"grid X={Markup.Escape($"[{minX}..{maxX}]")} Y={Markup.Escape($"[{minY}..{maxY}]")}, " +
                        $"{persistCell}{parent}" +
                        (interiorCount > 0 ? $", [yellow]{interiorCount} interior[/]" : ""));
                }
                else
                {
                    AnsiConsole.MarkupLine($"    0x{wsFormId:X8}: [grey]0 cells[/], {persistCell}{parent}");
                }
            }
        }
    }

    private sealed record AnalyzeRow(
        string FileName,
        int CellCount,
        int WorldspaceCount,
        int PlacedCount,
        int PersistentCount,
        int PersistentCellStubs,
        int MarkerCount,
        int UnresolvedRefCount,
        IReadOnlyList<PlacedReference> PersistentRefs,
        IReadOnlyList<PlacedReference> Markers,
        IReadOnlyList<CellRecord> Cells,
        IReadOnlyList<CellRecord> UnresolvedBuckets,
        IReadOnlyDictionary<uint, RuntimeWorldspaceData> RuntimeWorldspaceMaps);
}
