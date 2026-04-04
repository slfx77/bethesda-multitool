using System.CommandLine;
using System.Globalization;
using FalloutXbox360Utils.Core;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Analysis;

/// <summary>
///     Format-agnostic record comparison. Works on any combination of ESM, DMP, ESP files.
///     Compares parsed records by FormID across two files.
/// </summary>
public static class DiffCommand
{
    public static Command Create()
    {
        var command = new Command("diff", "Compare records between any two supported files (ESM, DMP, ESP)");

        var fileAArg = new Argument<string>("fileA") { Description = "First file (ESM, ESP, or DMP)" };
        var fileBArg = new Argument<string>("fileB") { Description = "Second file (ESM, ESP, or DMP)" };
        var typeOpt = new Option<string?>("-t", "--type")
        {
            Description = "Record type filter (e.g., NPC_, QUST, DIAL)"
        };
        var filterOpt = new Option<string?>("-f", "--filter")
        {
            Description = "Filter EditorID or DisplayName (case-insensitive contains)"
        };
        var formIdOpt = new Option<string?>("--formid")
        {
            Description = "Specific FormID to compare (hex, e.g., 0x000F0629)"
        };
        var limitOpt = new Option<int>("-l", "--limit")
        {
            Description = "Max records to show (default: 20)",
            DefaultValueFactory = _ => 20
        };

        command.Arguments.Add(fileAArg);
        command.Arguments.Add(fileBArg);
        command.Options.Add(typeOpt);
        command.Options.Add(filterOpt);
        command.Options.Add(formIdOpt);
        command.Options.Add(limitOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var fileA = parseResult.GetValue(fileAArg)!;
            var fileB = parseResult.GetValue(fileBArg)!;
            var type = parseResult.GetValue(typeOpt);
            var filter = parseResult.GetValue(filterOpt);
            var formIdStr = parseResult.GetValue(formIdOpt);
            var limit = parseResult.GetValue(limitOpt);

            return await RunDiffAsync(fileA, fileB, type, filter, formIdStr, limit, cancellationToken);
        });

        return command;
    }

    private static async Task<int> RunDiffAsync(string fileA, string fileB,
        string? typeFilter, string? nameFilter, string? formIdStr, int limit,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(fileA))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {fileA}");
            return 1;
        }

        if (!File.Exists(fileB))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {fileB}");
            return 1;
        }

        uint? targetFormId = null;
        if (!string.IsNullOrEmpty(formIdStr))
        {
            targetFormId = formIdStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToUInt32(formIdStr, 16)
                : uint.Parse(formIdStr, NumberStyles.HexNumber);
        }

        var fileTypeA = FileTypeDetector.Detect(fileA);
        var fileTypeB = FileTypeDetector.Detect(fileB);

        AnsiConsole.MarkupLine(
            $"[bold]Diff:[/] [cyan]{Path.GetFileName(fileA)}[/] ({fileTypeA}) vs [cyan]{Path.GetFileName(fileB)}[/] ({fileTypeB})");

        try
        {
            // Analyze both files
            UnifiedAnalysisResult resultA = null!, resultB = null!;

            await AnsiConsole.Progress()
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var taskA = ctx.AddTask($"Analyzing {Path.GetFileName(fileA)}...", maxValue: 100);
                    var progressA = new Progress<AnalysisProgress>(p =>
                    {
                        taskA.Description = $"A: {p.Phase}";
                        taskA.Value = p.PercentComplete;
                    });

                    resultA = await UnifiedAnalyzer.AnalyzeAsync(fileA, progressA, cancellationToken);

                    var taskB = ctx.AddTask($"Analyzing {Path.GetFileName(fileB)}...", maxValue: 100);
                    var progressB = new Progress<AnalysisProgress>(p =>
                    {
                        taskB.Description = $"B: {p.Phase}";
                        taskB.Value = p.PercentComplete;
                    });

                    resultB = await UnifiedAnalyzer.AnalyzeAsync(fileB, progressB, cancellationToken);
                });

            using (resultA)
            using (resultB)
            {
                var flatA = RecordFlattener.Flatten(resultA.Records);
                var flatB = RecordFlattener.Flatten(resultB.Records);

                // Apply filters
                if (!string.IsNullOrEmpty(typeFilter))
                {
                    flatA = flatA.Where(r => r.Type.Equals(typeFilter, StringComparison.OrdinalIgnoreCase)).ToList();
                    flatB = flatB.Where(r => r.Type.Equals(typeFilter, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                if (!string.IsNullOrEmpty(nameFilter))
                {
                    flatA = flatA.Where(r =>
                            (r.EditorId?.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                            (r.DisplayName?.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ?? false))
                        .ToList();
                    flatB = flatB.Where(r =>
                            (r.EditorId?.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                            (r.DisplayName?.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ?? false))
                        .ToList();
                }

                if (targetFormId.HasValue)
                {
                    flatA = flatA.Where(r => r.FormId == targetFormId.Value).ToList();
                    flatB = flatB.Where(r => r.FormId == targetFormId.Value).ToList();
                }

                // Build lookups
                var lookupA = flatA.GroupBy(r => r.FormId).ToDictionary(g => g.Key, g => g.First());
                var lookupB = flatB.GroupBy(r => r.FormId).ToDictionary(g => g.Key, g => g.First());

                var allFormIds = lookupA.Keys.Union(lookupB.Keys).OrderBy(x => x).ToList();

                // Classify differences
                var onlyInA = new List<RecordFlattener.FlatRecord>();
                var onlyInB = new List<RecordFlattener.FlatRecord>();
                var changed = new List<(RecordFlattener.FlatRecord A, RecordFlattener.FlatRecord B)>();
                var same = 0;

                foreach (var fid in allFormIds)
                {
                    var hasA = lookupA.TryGetValue(fid, out var recA);
                    var hasB = lookupB.TryGetValue(fid, out var recB);

                    if (hasA && !hasB)
                    {
                        onlyInA.Add(recA!);
                    }
                    else if (!hasA && hasB)
                    {
                        onlyInB.Add(recB!);
                    }
                    else if (hasA && hasB)
                    {
                        // Compare key fields
                        if (recA!.EditorId != recB!.EditorId ||
                            recA.DisplayName != recB.DisplayName ||
                            recA.Type != recB.Type)
                        {
                            changed.Add((recA, recB));
                        }
                        else
                        {
                            same++;
                        }
                    }
                }

                // Display summary
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[bold]Summary:[/]");
                AnsiConsole.MarkupLine($"  File A records (filtered): {flatA.Count}");
                AnsiConsole.MarkupLine($"  File B records (filtered): {flatB.Count}");
                AnsiConsole.MarkupLine($"  [green]Identical:[/] {same}");
                AnsiConsole.MarkupLine($"  [yellow]Only in A:[/] {onlyInA.Count}");
                AnsiConsole.MarkupLine($"  [cyan]Only in B:[/] {onlyInB.Count}");
                AnsiConsole.MarkupLine($"  [red]Changed:[/] {changed.Count}");

                // Show details
                if (onlyInA.Count > 0)
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine($"[yellow bold]Only in {Path.GetFileName(fileA)}:[/]");
                    CliTableBuilder.WriteRecordTable(onlyInA, limit);
                }

                if (onlyInB.Count > 0)
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine($"[cyan bold]Only in {Path.GetFileName(fileB)}:[/]");
                    CliTableBuilder.WriteRecordTable(onlyInB, limit);
                }

                if (changed.Count > 0)
                {
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[red bold]Changed records:[/]");
                    var table = new Table();
                    table.AddColumn("FormID");
                    table.AddColumn("Type");
                    table.AddColumn("EditorID (A)");
                    table.AddColumn("EditorID (B)");
                    table.AddColumn("Name (A)");
                    table.AddColumn("Name (B)");

                    foreach (var (a, b) in changed.Take(limit))
                    {
                        table.AddRow(
                            $"0x{a.FormId:X8}",
                            Markup.Escape(a.Type),
                            Markup.Escape(a.EditorId ?? ""),
                            Markup.Escape(b.EditorId ?? ""),
                            Markup.Escape(a.DisplayName ?? ""),
                            Markup.Escape(b.DisplayName ?? ""));
                    }

                    AnsiConsole.Write(table);
                    if (changed.Count > limit)
                    {
                        AnsiConsole.MarkupLine($"[grey]... {changed.Count - limit} more[/]");
                    }
                }

                // Type breakdown
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[bold]Type breakdown:[/]");
                var typeSummary = new Dictionary<string, (int countA, int countB)>();

                foreach (var r in flatA)
                {
                    if (!typeSummary.TryGetValue(r.Type, out var existing))
                    {
                        existing = (0, 0);
                    }

                    typeSummary[r.Type] = (existing.countA + 1, existing.countB);
                }

                foreach (var r in flatB)
                {
                    if (!typeSummary.TryGetValue(r.Type, out var existing))
                    {
                        existing = (0, 0);
                    }

                    typeSummary[r.Type] = (existing.countA, existing.countB + 1);
                }

                var typeTable = new Table();
                typeTable.AddColumn("Type");
                typeTable.AddColumn(new TableColumn("File A").RightAligned());
                typeTable.AddColumn(new TableColumn("File B").RightAligned());
                typeTable.AddColumn(new TableColumn("Delta").RightAligned());

                foreach (var (type, (countA, countB)) in typeSummary.OrderByDescending(x =>
                             Math.Abs(x.Value.countA - x.Value.countB)))
                {
                    var delta = countB - countA;
                    var deltaStr = delta switch
                    {
                        > 0 => $"[green]+{delta}[/]",
                        < 0 => $"[red]{delta}[/]",
                        _ => "[grey]0[/]"
                    };

                    typeTable.AddRow(Markup.Escape(type), countA.ToString(), countB.ToString(), deltaStr);
                }

                AnsiConsole.Write(typeTable);

                return 0;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }
}
