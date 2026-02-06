using System.Diagnostics;
using System.Globalization;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Extraction;
using FalloutXbox360Utils.Core.Formats;
using FalloutXbox360Utils.Core.Minidump;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI logic for carving files from memory dumps.
/// </summary>
public static class CarveCommand
{
    public static async Task ExecuteAsync(
        string inputPath,
        string outputDir,
        List<string>? fileTypes,
        bool convertDdx,
        bool verbose,
        int maxFiles,
        bool pcFriendly = true)
    {
        var files = new List<string>();

        if (File.Exists(inputPath))
        {
            files.Add(inputPath);
        }
        else if (Directory.Exists(inputPath))
        {
            files.AddRange(Directory.GetFiles(inputPath, "*.dmp", SearchOption.TopDirectoryOnly));
        }

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No dump files found.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[blue]Found[/] {files.Count} file(s) to process");

        foreach (var file in files)
        {
            await ProcessFileAsync(file, outputDir, fileTypes, convertDdx, verbose, maxFiles, pcFriendly);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]Done![/]");
    }

    private static async Task ProcessFileAsync(
        string file,
        string outputDir,
        List<string>? fileTypes,
        bool convertDdx,
        bool verbose,
        int maxFiles,
        bool pcFriendly)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[blue]{Path.GetFileName(file)}[/]").LeftJustified());

        Logger.Instance.SetVerbose(verbose);

        var stopwatch = Stopwatch.StartNew();

        var options = new ExtractionOptions
        {
            OutputPath = outputDir,
            ConvertDdx = convertDdx,
            FileTypes = fileTypes,
            Verbose = verbose,
            MaxFilesPerType = maxFiles,
            PcFriendly = pcFriendly,
            GenerateEsmReports = true, // Run analysis first to enable ESM reports
            ExtractScripts = fileTypes == null ||
                             fileTypes.Count == 0 ||
                             fileTypes.Any(t => t.Contains("scda", StringComparison.OrdinalIgnoreCase) ||
                                                t.Contains("script", StringComparison.OrdinalIgnoreCase))
        };

        ExtractionSummary summary;

        try
        {
            summary = await ExtractWithProgressAsync(file, options, verbose);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return;
        }

        stopwatch.Stop();

        AnsiConsole.MarkupLine(
            $"[green]Extracted[/] {summary.TotalExtracted} files in [blue]{stopwatch.Elapsed.TotalSeconds:F2}s[/]");

        PrintSummary(summary, convertDdx);
    }

    private static async Task<ExtractionSummary> ExtractWithProgressAsync(string file, ExtractionOptions options,
        bool verbose)
    {
        ExtractionSummary? summary = null;
        AnalysisResult? analysisResult = null;

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                // Phase 1: Run analysis to get ESM records for report generation
                if (options.GenerateEsmReports)
                {
                    var analysisTask = ctx.AddTask("[yellow]Analyzing dump[/]", maxValue: 100);
                    var analysisProgress = new Progress<AnalysisProgress>(p =>
                    {
                        analysisTask.Value = p.PercentComplete;
                        analysisTask.Description = $"[yellow]{p.Phase}[/]";
                    });

                    var analyzer = new MinidumpAnalyzer();
                    analysisResult = await analyzer.AnalyzeAsync(file, analysisProgress, true, verbose);
                    analysisTask.Value = 100;
                    analysisTask.Description = "[green]Analysis complete[/]";
                }

                // Phase 2: Extract files
                var extractTask = ctx.AddTask("[yellow]Extracting files[/]", maxValue: 100);
                var extractProgress = new Progress<ExtractionProgress>(p =>
                {
                    extractTask.Value = p.PercentComplete;
                    extractTask.Description = $"[yellow]{p.CurrentOperation}[/]";
                });

                summary = await MinidumpExtractor.Extract(file, options, extractProgress, analysisResult);
                extractTask.Value = 100;
                extractTask.Description = "[green]Complete[/]";
            });

        // Summary will always be non-null after successful completion
        return summary!;
    }

    private static void PrintSummary(ExtractionSummary summary, bool convertDdx)
    {
        PrintCategoryTable(summary);
        PrintConversionStats(summary, convertDdx);
        PrintScriptStats(summary);
        PrintEsmStats(summary);
    }

    private static void PrintCategoryTable(ExtractionSummary summary)
    {
        if (summary.TypeCounts.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();

        var categorized = CategorizeTypeCounts(summary.TypeCounts);
        var table = BuildCategoryTable(categorized, summary.ModulesExtracted);

        AnsiConsole.Write(table);
    }

    private static Dictionary<string, int> CategorizeTypeCounts(IReadOnlyDictionary<string, int> typeCounts)
    {
        var categorized = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (signatureId, count) in typeCounts)
        {
            var groupLabel = FormatRegistry.GetGroupLabel(signatureId);
            categorized[groupLabel] = categorized.GetValueOrDefault(groupLabel) + count;
        }

        return categorized;
    }

    private static Table BuildCategoryTable(Dictionary<string, int> categorized, int modulesExtracted)
    {
        var table = new Table();
        table.Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("[bold]Category[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold]Count[/]").RightAligned());

        foreach (var (category, count) in categorized.OrderByDescending(x => x.Value))
        {
            if (count > 0)
            {
                table.AddRow(category, count.ToString(CultureInfo.InvariantCulture));
            }
        }

        if (modulesExtracted > 0)
        {
            table.AddRow("[grey]Modules (from header)[/]", modulesExtracted.ToString(CultureInfo.InvariantCulture));
        }

        return table;
    }

    private static void PrintConversionStats(ExtractionSummary summary, bool convertDdx)
    {
        if (!convertDdx)
        {
            return;
        }

        PrintDdxConversionStats(summary);
        PrintXurConversionStats(summary);
    }

    private static void PrintDdxConversionStats(ExtractionSummary summary)
    {
        if (summary is { DdxConverted: 0, DdxFailed: 0 })
        {
            return;
        }

        AnsiConsole.WriteLine();
        var converted = FormatSuccessCount(summary.DdxConverted);
        var failed = FormatFailedCount(summary.DdxFailed);
        AnsiConsole.MarkupLine($"DDX -> DDS conversions: {converted}, {failed}");
    }

    private static void PrintXurConversionStats(ExtractionSummary summary)
    {
        if (summary is { XurConverted: 0, XurFailed: 0 })
        {
            return;
        }

        var converted = FormatSuccessCount(summary.XurConverted);
        var failed = FormatFailedCount(summary.XurFailed);
        AnsiConsole.MarkupLine($"XUR -> XUI conversions: {converted}, {failed}");
    }

    private static string FormatSuccessCount(int count)
    {
        return count > 0 ? $"[green]{count} successful[/]" : "0 successful";
    }

    private static string FormatFailedCount(int count)
    {
        return count > 0 ? $"[red]{count} failed[/]" : "0 failed";
    }

    private static void PrintScriptStats(ExtractionSummary summary)
    {
        if (summary.ScriptsExtracted == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[yellow]Scripts:[/] {summary.ScriptsExtracted} records ({summary.ScriptQuestsGrouped} quests grouped)");
    }

    private static void PrintEsmStats(ExtractionSummary summary)
    {
        if (!summary.EsmReportGenerated && summary.HeightmapsExported == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        if (summary.EsmReportGenerated)
        {
            AnsiConsole.MarkupLine("[cyan]ESM:[/] Semantic report generated");
        }

        if (summary.HeightmapsExported > 0)
        {
            AnsiConsole.MarkupLine($"[cyan]Heightmaps:[/] {summary.HeightmapsExported} exported");
        }
    }
}
