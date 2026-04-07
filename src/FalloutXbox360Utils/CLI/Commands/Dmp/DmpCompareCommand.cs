using System.CommandLine;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Semantic;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Dmp;

/// <summary>
///     Cross-dump comparison command: processes all DMP/ESM files in a directory and generates
///     per-record-type comparison output.
/// </summary>
internal static class DmpCompareCommand
{
    internal static Command Create()
    {
        var dirArg = new Argument<string>("directory") { Description = "Directory containing .dmp files" };
        var outputOpt = new Option<string>("-o", "--output")
        {
            Description = "Output directory for comparison output",
            Required = true
        };
        var typesOpt = new Option<string?>("--types")
        {
            Description = "Comma-separated record types to include (e.g., Weapon,NPC,Armor). Default: all"
        };
        var verboseOpt = new Option<bool>("--verbose")
        {
            Description = "Show detailed progress"
        };
        var baseOpt = new Option<string?>("--base")
        {
            Description = "Directory of ESM files to use as the base build (e.g., Fallout 3 + DLCs). " +
                          "DLC load order is auto-detected from MAST subrecords."
        };
        var formatOpt = new Option<string?>("--format")
        {
            Description = "Output format: html (default), json, csv"
        };

        var command = new Command("compare",
            "Cross-build comparison: generates per-record-type HTML pages showing changes across builds");
        command.Arguments.Add(dirArg);
        command.Options.Add(outputOpt);
        command.Options.Add(typesOpt);
        command.Options.Add(verboseOpt);
        command.Options.Add(baseOpt);
        command.Options.Add(formatOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var dir = parseResult.GetValue(dirArg)!;
            var output = parseResult.GetValue(outputOpt)!;
            var types = parseResult.GetValue(typesOpt);
            var verbose = parseResult.GetValue(verboseOpt);
            var basePath = parseResult.GetValue(baseOpt);
            var format = parseResult.GetValue(formatOpt);
            await RunAsync(dir, output, types, verbose, basePath, format, cancellationToken);
        });

        return command;
    }

    private static async Task RunAsync(
        string dirPath,
        string outputPath,
        string? typeFilter,
        bool verbose,
        string? basePath,
        string? format,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(dirPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Directory not found: {dirPath}");
            return;
        }

        var sourceFiles = Directory.GetFiles(dirPath, "*.*")
            .Where(path =>
                path.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".esm", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetFileName(path).Contains("hangdump", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => new FileInfo(path).LastWriteTimeUtc)
            .ToList();

        if (sourceFiles.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No .dmp or .esm files found in:[/] {dirPath}");
            return;
        }

        var dmpCount = sourceFiles.Count(path => path.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase));
        var esmCount = sourceFiles.Count - dmpCount;

        AnsiConsole.MarkupLine(
            $"[blue]Cross-build comparison: {dmpCount} DMP files, {esmCount} ESM files" +
            (basePath != null ? $", base: {Path.GetFileName(basePath.TrimEnd(Path.DirectorySeparatorChar))}" : "") +
            "[/]");
        AnsiConsole.WriteLine();

        CrossDumpComparisonResult? comparisonResult = null;
        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var progressTask = ctx.AddTask("Loading semantic sources", maxValue: sourceFiles.Count + 1);
                var processed = 0;

                comparisonResult = await CrossDumpComparisonPipeline.BuildAsync(
                    new CrossDumpComparisonRequest
                    {
                        SourceFiles = sourceFiles,
                        OutputPath = outputPath,
                        BaseDirectoryPath = basePath,
                        TypeFilter = typeFilter,
                        OutputFormat = format ?? "html",
                        Verbose = verbose
                    },
                    status =>
                    {
                        progressTask.Description = status;
                        progressTask.Value = Math.Min(++processed, progressTask.MaxValue);
                    },
                    cancellationToken);

                progressTask.Value = progressTask.MaxValue;
            });

        if (comparisonResult == null || comparisonResult.Index.StructuredRecords.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No sources produced parseable records.[/]");
            return;
        }

        if (verbose)
        {
            foreach (var source in comparisonResult.Sources)
            {
                AnsiConsole.MarkupLine(
                    $"  [green]{Markup.Escape(Path.GetFileName(source.FilePath))}[/]: " +
                    $"{source.Records.Weapons.Count} weapons, " +
                    $"{source.Records.Npcs.Count} NPCs, " +
                    $"{source.Records.Cells.Count} cells");
            }
        }

        var writtenFiles = await CrossDumpOutputWriter.WriteAsync(
            comparisonResult.Index,
            outputPath,
            format,
            cancellationToken);

        PrintSummaryTable(comparisonResult.Index);
        PrintSkillEraSummary(comparisonResult.Sources);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Output files:[/]");
        foreach (var file in writtenFiles.OrderBy(path => path))
        {
            AnsiConsole.MarkupLine($"  {Markup.Escape(file)}");
        }
    }

    private static void PrintSummaryTable(CrossDumpRecordIndex index)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Cross-Dump Comparison Summary[/]");

        table.AddColumn("[bold]Record Type[/]");
        table.AddColumn(new TableColumn("[bold]FormIDs[/]").RightAligned());

        foreach (var dump in index.Dumps)
        {
            table.AddColumn(
                new TableColumn($"[bold]{Markup.Escape(dump.ShortName)}[/]\n[dim]{dump.FileDate:yyyy-MM-dd}[/]")
                    .RightAligned());
        }

        foreach (var (recordType, formIdMap) in index.StructuredRecords.OrderBy(r => r.Key))
        {
            var row = new List<string>
            {
                recordType,
                formIdMap.Count.ToString("N0")
            };

            for (var dumpIndex = 0; dumpIndex < index.Dumps.Count; dumpIndex++)
            {
                var count = formIdMap.Values.Count(dumpMap => dumpMap.ContainsKey(dumpIndex));
                row.Add(count.ToString("N0"));
            }

            table.AddRow(row.Select(Markup.Escape).ToArray());
        }

        var totalRow = new List<string>
        {
            "[bold]TOTAL[/]",
            $"[bold]{index.StructuredRecords.Values.Sum(map => map.Count):N0}[/]"
        };

        for (var dumpIndex = 0; dumpIndex < index.Dumps.Count; dumpIndex++)
        {
            var total = index.StructuredRecords.Values
                .Sum(formIdMap => formIdMap.Values.Count(dumpMap => dumpMap.ContainsKey(dumpIndex)));
            totalRow.Add($"[bold]{total:N0}[/]");
        }

        table.AddRow(totalRow.ToArray());
        AnsiConsole.Write(table);
    }

    private static void PrintSkillEraSummary(IEnumerable<SemanticSource> sources)
    {
        var sourceList = sources.ToList();
        if (!sourceList.Any(source => source.Resolver.SkillEra != null))
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Skill Era Detection:[/]");
        foreach (var source in sourceList)
        {
            var era = source.Resolver.SkillEra;
            var name = Markup.Escape(Path.GetFileName(source.FilePath));
            if (era != null)
            {
                AnsiConsole.MarkupLine($"  [cyan]{name}[/]: {Markup.Escape(era.Summary)}");
            }
            else
            {
                AnsiConsole.MarkupLine($"  [cyan]{name}[/]: [dim](no AVIF/weapon data)[/]");
            }
        }
    }
}
