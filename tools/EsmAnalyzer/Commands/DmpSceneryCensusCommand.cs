using System.CommandLine;
using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Semantic;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Per-DMP census of cells that contain non-persistent ("scenery"/temporary-children)
///     placement data. A cell counts when it holds at least one placed REFR/ACHR/ACRE whose
///     header lacks the persistent flag (0x0400) — i.e. it was streamed in as part of the loaded
///     scene at dump time, not a global persistent ref (map marker, enable-parent, door, …).
///
///     Loads each dump through the same semantic pipeline as <c>dmp cell-inventory</c> (default
///     cell→worldspace authority applied) and tallies, per dump, the distinct cells with
///     non-persistent content. Dumps are ordered by the internal PE compile date of the game
///     module captured inside the dump (<c>MinidumpAnalyzer.FindGameModule().TimeDateStamp</c>),
///     so the breakdown reads as a build timeline. Emits a per-cell TSV (with a BuildDate column),
///     a per-file markdown report, and a corpus-wide union summary.
///
///     The high-confidence dangling-ref contribution (cut/heap refs the cell traversal missed)
///     is a corpus-aggregate signal in <c>data/cell_worldspace_authority.json</c> (deduped by
///     FormID across the whole corpus), so it is not attributable to an individual dump and is
///     not included here.
/// </summary>
internal static class DmpSceneryCensusCommand
{
    private static readonly HashSet<string> DefaultSceneryTypes =
        new(StringComparer.Ordinal) { "STAT", "MSTT", "SCOL", "TREE", "FURN", "ACTI" };

    public static Command CreateSceneryCensusCommand()
    {
        var command = new Command(
            "scenery-census",
            "Per-DMP census of cells/interiors holding non-persistent scenery, ordered by binary compile date.");

        var pathArg = new Argument<string>("path")
        {
            Description = "Path to a .dmp file or a directory of .dmp files"
        };
        var outputOpt = new Option<string?>("-o", "--output")
        {
            Description = "TSV output path (default: TestOutput/scenery_census.tsv). A sibling .md report is also written."
        };
        var sceneryOpt = new Option<string?>("--scenery-types")
        {
            Description = "Comma-separated base record types counted as static scenery (default STAT,MSTT,SCOL,TREE,FURN,ACTI)"
        };

        command.Arguments.Add(pathArg);
        command.Options.Add(outputOpt);
        command.Options.Add(sceneryOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var path = parseResult.GetValue(pathArg)!;
            var output = parseResult.GetValue(outputOpt)
                         ?? Path.Combine("TestOutput", "scenery_census.tsv");
            var sceneryRaw = parseResult.GetValue(sceneryOpt);
            var sceneryTypes = sceneryRaw is null
                ? DefaultSceneryTypes
                : sceneryRaw
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(t => t.ToUpperInvariant())
                    .ToHashSet(StringComparer.Ordinal);
            await RunAsync(path, output, sceneryTypes, ct);
        });

        return command;
    }

    private static async Task RunAsync(
        string path,
        string outputPath,
        HashSet<string> sceneryTypes,
        CancellationToken ct)
    {
        List<string> dmpFiles;
        if (Directory.Exists(path))
        {
            dmpFiles = Directory.GetFiles(path, "*.dmp")
                .Where(f => !Path.GetFileName(f).Contains("hangdump", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        else if (File.Exists(path))
        {
            dmpFiles = [path];
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Path not found:[/] {Markup.Escape(path)}");
            return;
        }

        if (dmpFiles.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No .dmp files found in:[/] {Markup.Escape(path)}");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        // Order dumps by the game module's internal PE compile date (TimeDateStamp), so the
        // breakdown is a build timeline. Header-only minidump parse — cheap, no memory scan.
        // Unknown dates sort last (by file name) so they don't masquerade as a date.
        var ordered = dmpFiles
            .Select(f => (File: f, Build: GetBuildInfo(f)))
            .OrderBy(x => x.Build.Date ?? DateTimeOffset.MaxValue)
            .ThenBy(x => Path.GetFileName(x.File), StringComparer.OrdinalIgnoreCase)
            .ToList();

        AnsiConsole.MarkupLine("[blue]Compile-date order (game-module PE TimeDateStamp):[/]");
        foreach (var x in ordered)
        {
            var d = x.Build.Date?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "Unknown";
            // Clean machine-parseable line (prefix BUILDDATE) for tooling.
            Console.WriteLine($"BUILDDATE\t{Path.GetFileName(x.File)}\t{d}\t" +
                              $"{(x.Build.Ts is { } t ? $"0x{t:X8}" : "")}\t{x.Build.Type ?? ""}");
        }
        AnsiConsole.WriteLine();

        var tsv = new StringBuilder();
        tsv.AppendLine(string.Join('\t',
            "BuildDate", "PeTimestamp", "BuildType", "Dump",
            "CellFormId", "EditorId", "FullName", "Interior", "Virtual",
            "Worldspace", "GridX", "GridY",
            "NonPersistTotal", "NonPersistObjects", "NonPersistActors", "StaticScenery"));

        var union = new Dictionary<string, UnionCell>(StringComparer.Ordinal);
        var dumpReports = new List<DumpReport>();

        foreach (var (dmpFile, build) in ordered)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(dmpFile);
            var dateStr = build.Date?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "Unknown";
            AnsiConsole.MarkupLine($"[cyan]{Markup.Escape(name)}[/] [grey]({dateStr})[/]");

            var report = new DumpReport
            {
                Dump = name,
                Date = build.Date,
                PeTimestamp = build.Ts,
                BuildType = build.Type
            };

            try
            {
                using var loaded = await SemanticFileLoader.LoadAsync(
                    dmpFile,
                    new SemanticFileLoadOptions
                    {
                        FileType = AnalysisFileType.Minidump,
                        ApplyDefaultCellWorldspaceAuthority = true
                    },
                    ct);

                var records = loaded.Records;
                report.TotalCells = records.Cells.Count;

                var sceneryBase = BuildSceneryBaseMap(records, sceneryTypes);

                var wsName = new Dictionary<uint, string>();
                foreach (var ws in records.Worldspaces)
                {
                    wsName[ws.FormId] = ws.EditorId ?? ws.FullName ?? $"0x{ws.FormId:X8}";
                }

                foreach (var cell in records.Cells)
                {
                    var nonPersist = cell.PlacedObjects.Where(o => !o.IsPersistent).ToList();
                    if (nonPersist.Count == 0)
                    {
                        continue;
                    }

                    var actors = nonPersist.Count(o => o.RecordType is "ACHR" or "ACRE");
                    var objects = nonPersist.Count - actors;
                    var scenery = nonPersist.Count(o => sceneryBase.ContainsKey(o.BaseFormId));
                    var wsFid = cell.WorldspaceFormId ?? 0u;
                    var wsLabel = wsFid != 0
                        ? wsName.GetValueOrDefault(wsFid, $"0x{wsFid:X8}")
                        : (cell.IsInterior ? "(interior)" : "(unlinked)");

                    report.Cells.Add(new CellDetail
                    {
                        FormId = cell.FormId,
                        EditorId = cell.EditorId,
                        FullName = cell.FullName,
                        Interior = cell.IsInterior,
                        Virtual = cell.IsVirtual,
                        Worldspace = wsLabel,
                        GridX = cell.GridX,
                        GridY = cell.GridY,
                        NonPersist = nonPersist.Count,
                        Objects = objects,
                        Actors = actors,
                        Scenery = scenery
                    });

                    tsv.AppendLine(string.Join('\t',
                        dateStr,
                        build.Ts is { } t ? $"0x{t:X8}" : "",
                        build.Type ?? "",
                        name,
                        $"0x{cell.FormId:X8}",
                        cell.EditorId ?? "",
                        (cell.FullName ?? "").Replace('\t', ' '),
                        cell.IsInterior ? "Y" : "N",
                        cell.IsVirtual ? "Y" : "N",
                        wsLabel.Replace('\t', ' '),
                        cell.GridX?.ToString(CultureInfo.InvariantCulture) ?? "",
                        cell.GridY?.ToString(CultureInfo.InvariantCulture) ?? "",
                        nonPersist.Count.ToString(CultureInfo.InvariantCulture),
                        objects.ToString(CultureInfo.InvariantCulture),
                        actors.ToString(CultureInfo.InvariantCulture),
                        scenery.ToString(CultureInfo.InvariantCulture)));

                    AddToUnion(union, cell, wsLabel, name, nonPersist.Count);
                }

                AnsiConsole.MarkupLine(
                    $"  cells={report.TotalCells} withScenery={report.Cells.Count} " +
                    $"(int={report.InteriorCount} ext={report.ExteriorCount}) " +
                    $"placements={report.PlacementCount}");
            }
            catch (Exception ex)
            {
                report.Error = ex.Message;
                AnsiConsole.MarkupLine($"  [red]ERROR: {Markup.Escape(ex.Message)}[/]");
            }

            dumpReports.Add(report);
        }

        await File.WriteAllTextAsync(outputPath, tsv.ToString(), ct);

        var mdPath = Path.ChangeExtension(outputPath, ".md");
        await File.WriteAllTextAsync(mdPath, BuildMarkdown(dumpReports), ct);

        RenderSummary(dumpReports, union, outputPath, mdPath);
    }

    private static Dictionary<uint, string> BuildSceneryBaseMap(
        RecordCollection records,
        HashSet<string> sceneryTypes)
    {
        var map = new Dictionary<uint, string>();
        if (sceneryTypes.Contains("STAT"))
        {
            foreach (var s in records.Statics) map[s.FormId] = "STAT";
        }
        if (sceneryTypes.Contains("SCOL"))
        {
            foreach (var s in records.StaticCollections) map[s.FormId] = "SCOL";
        }
        foreach (var g in records.GenericRecords)
        {
            if (sceneryTypes.Contains(g.RecordType))
            {
                map[g.FormId] = g.RecordType;
            }
        }

        return map;
    }

    private static (DateTimeOffset? Date, uint? Ts, string? Type) GetBuildInfo(string dmpPath)
    {
        try
        {
            var info = MinidumpParser.Parse(dmpPath);
            var gm = MinidumpAnalyzer.FindGameModule(info);
            var ts = gm?.TimeDateStamp;
            var date = ts is > 0 ? DateTimeOffset.FromUnixTimeSeconds(ts.Value) : (DateTimeOffset?)null;
            return (date, ts, MinidumpAnalyzer.DetectBuildType(info));
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static void AddToUnion(
        Dictionary<string, UnionCell> union,
        CellRecord cell,
        string worldspaceLabel,
        string dump,
        int placements)
    {
        var key = cell.IsVirtual
            ? $"V:{cell.WorldspaceFormId ?? 0}:{cell.GridX}:{cell.GridY}"
            : $"F:0x{cell.FormId:X8}";

        if (!union.TryGetValue(key, out var u))
        {
            u = new UnionCell
            {
                EditorId = cell.EditorId,
                FullName = cell.FullName,
                IsInterior = cell.IsInterior,
                IsVirtual = cell.IsVirtual,
                Worldspace = worldspaceLabel
            };
            union[key] = u;
        }

        u.EditorId ??= cell.EditorId;
        u.FullName ??= cell.FullName;
        u.Dumps.Add(dump);
        u.TotalPlacements += placements;
    }

    private static string BuildMarkdown(List<DumpReport> reports)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Scenery-cell capture by dump");
        sb.AppendLine();
        sb.AppendLine("Cells with non-persistent (scenery / temporary-children) placement data, per memory dump, " +
                      "ordered by the game module's internal PE compile date.");
        sb.AppendLine();
        sb.AppendLine("| # | Compile date | Dump | Build | Cells | Int | Ext | Placements |");
        sb.AppendLine("|--:|---|---|---|--:|--:|--:|--:|");
        var n = 0;
        foreach (var r in reports)
        {
            n++;
            var date = r.Date?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "Unknown";
            var cells = r.Error is not null ? "ERR" : r.Cells.Count.ToString(CultureInfo.InvariantCulture);
            sb.AppendLine($"| {n} | {date} | {r.Dump} | {r.BuildType ?? ""} | {cells} | " +
                          $"{r.InteriorCount} | {r.ExteriorCount} | {r.PlacementCount} |");
        }
        sb.AppendLine();

        n = 0;
        foreach (var r in reports)
        {
            n++;
            var date = r.Date?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "Unknown";
            var ts = r.PeTimestamp is { } t ? $"PE 0x{t:X8}" : "";
            sb.AppendLine($"## {n}. {date} — {r.Dump}  ({r.BuildType} {ts})".TrimEnd());
            sb.AppendLine();

            if (r.Error is not null)
            {
                sb.AppendLine($"_Error: {r.Error}_");
                sb.AppendLine();
                continue;
            }

            if (r.Cells.Count == 0)
            {
                sb.AppendLine("_No cells with non-persistent scenery captured (partial dump)._");
                sb.AppendLine();
                continue;
            }

            sb.AppendLine($"{r.Cells.Count} cell(s) — interior={r.InteriorCount}, exterior={r.ExteriorCount}, " +
                          $"{r.PlacementCount} non-persistent placements.");
            sb.AppendLine();
            sb.AppendLine("| Cell | Type | Worldspace | Grid | NonPersist | Obj | Act | Scenery | FormID |");
            sb.AppendLine("|---|---|---|---|--:|--:|--:|--:|---|");
            foreach (var c in r.Cells
                         .OrderByDescending(c => c.NonPersist)
                         .ThenBy(c => c.EditorId ?? "", StringComparer.OrdinalIgnoreCase))
            {
                var label = c.EditorId ?? "(no EDID)";
                if (!string.IsNullOrEmpty(c.FullName))
                {
                    label += $" \"{c.FullName}\"";
                }
                var grid = c.GridX.HasValue && c.GridY.HasValue ? $"{c.GridX},{c.GridY}" : "";
                sb.AppendLine($"| {EscMd(label)} | {(c.Interior ? "INT" : "EXT")} | {EscMd(c.Worldspace)} | {grid} | " +
                              $"{c.NonPersist} | {c.Objects} | {c.Actors} | {c.Scenery} | `0x{c.FormId:X8}` |");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void RenderSummary(
        List<DumpReport> reports,
        Dictionary<string, UnionCell> union,
        string outputPath,
        string mdPath)
    {
        AnsiConsole.WriteLine();
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Date");
        table.AddColumn("Dump");
        table.AddColumn(new TableColumn("Cells").RightAligned());
        table.AddColumn(new TableColumn("Int").RightAligned());
        table.AddColumn(new TableColumn("Ext").RightAligned());
        table.AddColumn(new TableColumn("Placements").RightAligned());

        foreach (var r in reports)
        {
            var date = r.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "Unknown";
            if (r.Error is not null)
            {
                table.AddRow(date, Markup.Escape(r.Dump), "[red]ERR[/]", "", "", "");
                continue;
            }

            table.AddRow(
                date,
                Markup.Escape(r.Dump),
                r.Cells.Count.ToString("N0", CultureInfo.InvariantCulture),
                r.InteriorCount.ToString("N0", CultureInfo.InvariantCulture),
                r.ExteriorCount.ToString("N0", CultureInfo.InvariantCulture),
                r.PlacementCount.ToString("N0", CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);

        var nonEmpty = reports.Count(r => r.Error is null && r.Cells.Count > 0);
        var unionInterior = union.Values.Count(u => u.IsInterior);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[green]Corpus union:[/] {union.Count:N0} distinct cells with non-persistent placements " +
            $"across {nonEmpty:N0} dump(s) with content " +
            $"(interior={unionInterior:N0}, exterior={union.Count - unionInterior:N0}).");
        AnsiConsole.MarkupLine($"[green]Per-cell TSV:[/] {Markup.Escape(outputPath)}");
        AnsiConsole.MarkupLine($"[green]Per-file report:[/] {Markup.Escape(mdPath)}");
    }

    private static string EscMd(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    private sealed class DumpReport
    {
        public required string Dump { get; init; }
        public DateTimeOffset? Date { get; init; }
        public uint? PeTimestamp { get; init; }
        public string? BuildType { get; init; }
        public int TotalCells { get; set; }
        public List<CellDetail> Cells { get; } = [];
        public string? Error { get; set; }

        public int InteriorCount => Cells.Count(c => c.Interior);
        public int ExteriorCount => Cells.Count(c => !c.Interior);
        public int PlacementCount => Cells.Sum(c => c.NonPersist);
    }

    private sealed class CellDetail
    {
        public uint FormId { get; init; }
        public string? EditorId { get; init; }
        public string? FullName { get; init; }
        public bool Interior { get; init; }
        public bool Virtual { get; init; }
        public string Worldspace { get; init; } = "";
        public int? GridX { get; init; }
        public int? GridY { get; init; }
        public int NonPersist { get; init; }
        public int Objects { get; init; }
        public int Actors { get; init; }
        public int Scenery { get; init; }
    }

    private sealed class UnionCell
    {
        public string? EditorId { get; set; }
        public string? FullName { get; set; }
        public bool IsInterior { get; init; }
        public bool IsVirtual { get; init; }
        public string Worldspace { get; init; } = "";
        public HashSet<string> Dumps { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int TotalPlacements { get; set; }
    }
}
