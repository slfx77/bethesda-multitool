using System.CommandLine;
using System.Text.Json;
using Spectre.Console;
using Xbox360MemoryCarver.Core;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;
using Xbox360MemoryCarver.Core.Formats.Scda;

namespace Xbox360MemoryCarver.CLI;

/// <summary>
///     CLI command for analyzing memory dump structure and extracting metadata.
/// </summary>
public static class AnalyzeCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Command Create()
    {
        var command = new Command("analyze", "Analyze memory dump structure and extract metadata");

        var inputArg = new Argument<string>("input", "Path to memory dump file (.dmp)");
        var outputOpt = new Option<string?>(["-o", "--output"], "Output path for analysis report");
        var formatOpt = new Option<string>(["-f", "--format"], () => "text", "Output format: text, md, json");
        var extractEsmOpt = new Option<string?>(["-e", "--extract-esm"],
            "Extract ESM records (EDID, GMST, SCTX, FormIDs) to directory");
        var verboseOpt = new Option<bool>(["-v", "--verbose"], "Show detailed progress");

        command.AddArgument(inputArg);
        command.AddOption(outputOpt);
        command.AddOption(formatOpt);
        command.AddOption(extractEsmOpt);
        command.AddOption(verboseOpt);

        command.SetHandler(ExecuteAsync, inputArg, outputOpt, formatOpt, extractEsmOpt, verboseOpt);

        return command;
    }

    private static async Task ExecuteAsync(string input, string? output, string format, string? extractEsm,
        bool verbose)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {input}");
            return;
        }

        AnsiConsole.MarkupLine($"[blue]Analyzing:[/] {Path.GetFileName(input)}");
        AnsiConsole.WriteLine();

        var analyzer = new MemoryDumpAnalyzer();
        AnalysisResult result = null!;

        // Run analysis with progress bar
        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Scanning[/]", maxValue: 100);

                var progress = new Progress<AnalysisProgress>(p =>
                {
                    task.Value = p.PercentComplete;
                    var filesInfo = p.FilesFound > 0 ? $" ({p.FilesFound} files)" : "";
                    task.Description = $"[green]{p.Phase}[/][grey]{filesInfo}[/]";
                });

                result = await analyzer.AnalyzeAsync(input, progress, true);
                task.Value = 100;
                task.Description = $"[green]Complete[/] [grey]({result.CarvedFiles.Count} files)[/]";
            });

        AnsiConsole.WriteLine();

        var report = format.ToLowerInvariant() switch
        {
            "md" or "markdown" => MemoryDumpAnalyzer.GenerateReport(result),
            "json" => JsonSerializer.Serialize(result, JsonOptions),
            _ => MemoryDumpAnalyzer.GenerateSummary(result)
        };

        if (!string.IsNullOrEmpty(output))
        {
            await File.WriteAllTextAsync(output, report);
            AnsiConsole.MarkupLine($"[green]Report saved to:[/] {output}");
        }
        else
        {
            AnsiConsole.WriteLine(report);
        }

        if (!string.IsNullOrEmpty(extractEsm) && result.EsmRecords != null)
            await ExtractEsmRecordsAsync(input, extractEsm, result, verbose);
    }

    private static async Task ExtractEsmRecordsAsync(string input, string extractEsm,
        AnalysisResult result, bool verbose)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[blue]Exporting ESM records to:[/] {extractEsm}");
        await EsmRecordFormat.ExportRecordsAsync(
            result.EsmRecords!,
            result.FormIdMap,
            extractEsm,
            verbose);
        AnsiConsole.MarkupLine("[green]ESM export complete.[/]");

        if (result.ScdaRecords.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[blue]Extracting compiled scripts (SCDA)...[/]");
            var dumpData = await File.ReadAllBytesAsync(input);
            var scriptsDir = Path.Combine(extractEsm, "scripts");
            var scriptProgress =
                verbose ? new Progress<string>(msg => AnsiConsole.MarkupLine($"  [grey]{msg}[/]")) : null;
            var scriptResult = await ScdaExtractor.ExtractGroupedAsync(dumpData, scriptsDir, scriptProgress, verbose);
            AnsiConsole.MarkupLine(
                $"[green]Scripts extracted:[/] {scriptResult.TotalRecords} records ({scriptResult.GroupedQuests} quests, {scriptResult.UngroupedScripts} ungrouped)");
        }
    }
}
