using System.CommandLine;
using System.Runtime;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Semantic;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Dmp;

/// <summary>
///     Cross-dump comparison command: processes DMP/ESM files from one or more inputs and generates
///     per-record-type comparison output.
/// </summary>
internal static class DmpCompareCommand
{
    internal static Command Create()
    {
        var inputsArg = new Argument<string[]>("inputs")
        {
            Description = "One or more .dmp/.esm files or directories containing sources",
            Arity = ArgumentArity.OneOrMore
        };
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
        var recursiveOpt = new Option<bool>("--recursive")
        {
            Description = "Recursively search input directories for .dmp and .esm files"
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
        command.Arguments.Add(inputsArg);
        command.Options.Add(outputOpt);
        command.Options.Add(typesOpt);
        command.Options.Add(verboseOpt);
        command.Options.Add(recursiveOpt);
        command.Options.Add(baseOpt);
        command.Options.Add(formatOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var inputs = parseResult.GetValue(inputsArg)!;
            var output = parseResult.GetValue(outputOpt)!;
            var types = parseResult.GetValue(typesOpt);
            var verbose = parseResult.GetValue(verboseOpt);
            var recursive = parseResult.GetValue(recursiveOpt);
            var basePath = parseResult.GetValue(baseOpt);
            var format = parseResult.GetValue(formatOpt);
            await RunAsync(inputs, output, types, verbose, recursive, basePath, format, cancellationToken);
        });

        return command;
    }

    private static async Task RunAsync(
        IReadOnlyList<string> inputPaths,
        string outputPath,
        string? typeFilter,
        bool verbose,
        bool recursive,
        string? basePath,
        string? format,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DmpCompareSourceDescriptor> sourceDescriptors;
        try
        {
            sourceDescriptors = DmpCompareSourceDiscovery.Discover(inputPaths, recursive);
        }
        catch (FileNotFoundException ex)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] {Markup.Escape(ex.Message)}");
            return;
        }

        var sourceFiles = sourceDescriptors.Select(source => source.FilePath).ToList();

        if (sourceFiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No .dmp or .esm files found in the supplied inputs.[/]");
            return;
        }

        var dmpCount = sourceDescriptors.Count(source => source.IsDmp);
        var esmCount = sourceDescriptors.Count(source => source.IsEsm);

        AnsiConsole.MarkupLine(
            $"[blue]Cross-build comparison: {dmpCount} DMP files, {esmCount} ESM files" +
            (basePath != null ? $", base: {Path.GetFileName(basePath.TrimEnd(Path.DirectorySeparatorChar))}" : "") +
            "[/]");
        AnsiConsole.WriteLine();

        var normalizedFormat = string.IsNullOrWhiteSpace(format) ? "html" : format;
        CrossDumpComparisonResult? comparisonResult = null;
        CrossDumpStreamingComparisonResult? streamingComparisonResult = null;
        var logger = Logger.Instance;
        var previousLogLevel = logger.Level;
        if (verbose)
        {
            if (previousLogLevel < LogLevel.Debug)
            {
                logger.Level = LogLevel.Debug;
            }
        }
        else if (previousLogLevel > LogLevel.Warn)
        {
            logger.Level = LogLevel.Warn;
        }

        try
        {
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

                    var request = new CrossDumpComparisonRequest
                    {
                        SourceFiles = sourceFiles,
                        OutputPath = outputPath,
                        BaseDirectoryPath = basePath,
                        TypeFilter = typeFilter,
                        OutputFormat = normalizedFormat,
                        Verbose = verbose
                    };

                    void UpdateProgress(string status)
                    {
                        progressTask.Description = status;
                        progressTask.Value = Math.Min(++processed, progressTask.MaxValue);
                    }

                    if (IsHtmlFormat(normalizedFormat))
                    {
                        streamingComparisonResult =
                            await CrossDumpComparisonPipeline.WriteHtmlByRecordTypeAsync(
                                request,
                                UpdateProgress,
                                cancellationToken);
                    }
                    else
                    {
                        comparisonResult = await CrossDumpComparisonPipeline.BuildAsync(
                            request,
                            UpdateProgress,
                            cancellationToken);
                    }

                    progressTask.Value = progressTask.MaxValue;
                });
        }
        finally
        {
            logger.Level = previousLogLevel;
        }

        if (streamingComparisonResult != null)
        {
            if (streamingComparisonResult.RecordTypes.Count == 0)
            {
                AnsiConsole.MarkupLine("[red]No sources produced parseable records.[/]");
                return;
            }

            if (verbose)
            {
                foreach (var source in streamingComparisonResult.SourceSummaries)
                {
                    AnsiConsole.MarkupLine(
                        $"  [green]{Markup.Escape(Path.GetFileName(source.FilePath))}[/]: " +
                        $"{source.WeaponCount} weapons, " +
                        $"{source.NpcCount} NPCs, " +
                        $"{source.CellCount} cells");
                }
            }

            PrintSummaryTable(streamingComparisonResult.Dumps, streamingComparisonResult.RecordTypes);
            PrintSkillEraSummary(streamingComparisonResult.SourceSummaries);

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Output files:[/]");
            foreach (var file in streamingComparisonResult.WrittenFiles.OrderBy(path => path))
            {
                AnsiConsole.MarkupLine($"  {Markup.Escape(file)}");
            }

            return;
        }

        if (comparisonResult == null || comparisonResult.Index.StructuredRecords.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No sources produced parseable records.[/]");
            return;
        }

        if (verbose)
        {
            foreach (var source in comparisonResult.SourceSummaries)
            {
                AnsiConsole.MarkupLine(
                    $"  [green]{Markup.Escape(Path.GetFileName(source.FilePath))}[/]: " +
                    $"{source.WeaponCount} weapons, " +
                    $"{source.NpcCount} NPCs, " +
                    $"{source.CellCount} cells");
            }
        }

        ReleaseTransientMemoryBeforeOutput();

        var writtenFiles = await CrossDumpOutputWriter.WriteAsync(
            comparisonResult.Index,
            outputPath,
            normalizedFormat,
            cancellationToken);

        PrintSummaryTable(comparisonResult.Index);
        PrintSkillEraSummary(comparisonResult.SourceSummaries);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Output files:[/]");
        foreach (var file in writtenFiles.OrderBy(path => path))
        {
            AnsiConsole.MarkupLine($"  {Markup.Escape(file)}");
        }
    }

    private static bool IsHtmlFormat(string? format)
    {
        return string.IsNullOrWhiteSpace(format) ||
               string.Equals(format, "html", StringComparison.OrdinalIgnoreCase);
    }

    private static void PrintSummaryTable(
        IReadOnlyList<DumpSnapshot> dumps,
        IReadOnlyList<CrossDumpRecordTypeSummary> recordTypes)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Cross-Dump Comparison Summary[/]");

        table.AddColumn("[bold]Record Type[/]");
        table.AddColumn(new TableColumn("[bold]FormIDs[/]").RightAligned());

        foreach (var dump in dumps)
        {
            table.AddColumn(
                new TableColumn($"[bold]{Markup.Escape(dump.ShortName)}[/]\n[dim]{dump.FileDate:yyyy-MM-dd}[/]")
                    .RightAligned());
        }

        foreach (var summary in recordTypes.OrderBy(summary => summary.RecordType, StringComparer.OrdinalIgnoreCase))
        {
            var row = new List<string>
            {
                summary.RecordType,
                summary.FormIdCount.ToString("N0")
            };

            for (var dumpIndex = 0; dumpIndex < dumps.Count; dumpIndex++)
            {
                var count = dumpIndex < summary.DumpCounts.Count ? summary.DumpCounts[dumpIndex] : 0;
                row.Add(count.ToString("N0"));
            }

            table.AddRow(row.Select(Markup.Escape).ToArray());
        }

        var totalRow = new List<string>
        {
            "[bold]TOTAL[/]",
            $"[bold]{recordTypes.Sum(summary => summary.FormIdCount):N0}[/]"
        };

        for (var dumpIndex = 0; dumpIndex < dumps.Count; dumpIndex++)
        {
            var total = recordTypes.Sum(summary =>
                dumpIndex < summary.DumpCounts.Count ? summary.DumpCounts[dumpIndex] : 0);
            totalRow.Add($"[bold]{total:N0}[/]");
        }

        table.AddRow(totalRow.ToArray());
        AnsiConsole.Write(table);
    }

    private static void PrintSummaryTable(CrossDumpRecordIndex index)
    {
        var summaries = index.StructuredRecords
            .Select(entry => CrossDumpJsonHtmlWriter.BuildRecordTypeSummary(
                entry.Key,
                entry.Value,
                index.Dumps.Count))
            .ToList();
        PrintSummaryTable(index.Dumps, summaries);
    }

    private static void PrintSkillEraSummary(IEnumerable<CrossDumpSourceSummary> sources)
    {
        var sourceList = sources.ToList();
        if (!sourceList.Any(source => source.SkillEraSummary != null))
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Skill Era Detection:[/]");
        foreach (var source in sourceList)
        {
            var name = Markup.Escape(Path.GetFileName(source.FilePath));
            if (source.SkillEraSummary != null)
            {
                AnsiConsole.MarkupLine($"  [cyan]{name}[/]: {Markup.Escape(source.SkillEraSummary)}");
            }
            else
            {
                AnsiConsole.MarkupLine($"  [cyan]{name}[/]: [dim](no AVIF/weapon data)[/]");
            }
        }
    }

    private static void ReleaseTransientMemoryBeforeOutput()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
#pragma warning disable S1215
        // This command intentionally runs a one-shot full comparison over many large parsed sources.
        // Compacting before writing output releases transient parser/source buffers before chunk serialization.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
#pragma warning restore S1215
    }
}
