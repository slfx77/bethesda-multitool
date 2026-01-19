using System.CommandLine;
using System.Text.Json;
using Spectre.Console;
using Xbox360MemoryCarver.Core;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;
using Xbox360MemoryCarver.Core.Formats.Scda;
using Xbox360MemoryCarver.Core.Json;
using static Xbox360MemoryCarver.Core.LogLevel;

namespace Xbox360MemoryCarver.CLI;

/// <summary>
///     CLI command for analyzing memory dump structure and extracting metadata.
/// </summary>
public static class AnalyzeCommand
{
    public static Command Create()
    {
        var command = new Command("analyze", "Analyze memory dump structure and extract metadata");

        var inputArg = new Argument<string>("input") { Description = "Path to memory dump file (.dmp)" };
        var outputOpt = new Option<string?>("-o", "--output") { Description = "Output path for analysis report" };
        var formatOpt = new Option<string>("-f", "--format")
        {
            Description = "Output format: text, md, json",
            DefaultValueFactory = _ => "text"
        };
        var extractEsmOpt = new Option<string?>("-e", "--extract-esm")
        {
            Description = "Extract ESM records (EDID, GMST, SCTX, FormIDs) to directory"
        };
        var verboseOpt = new Option<bool>("-v", "--verbose") { Description = "Show detailed progress" };

        command.Arguments.Add(inputArg);
        command.Options.Add(outputOpt);
        command.Options.Add(formatOpt);
        command.Options.Add(extractEsmOpt);
        command.Options.Add(verboseOpt);

        command.SetAction(async (parseResult, _) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputOpt);
            var format = parseResult.GetValue(formatOpt)!;
            var extractEsm = parseResult.GetValue(extractEsmOpt);
            var verbose = parseResult.GetValue(verboseOpt);
            await ExecuteAsync(input, output, format, extractEsm, verbose);
        });

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

                result = await analyzer.AnalyzeAsync(input, progress);
                task.Value = 100;
                task.Description = $"[green]Complete[/] [grey]({result.CarvedFiles.Count} files)[/]";
            });

        AnsiConsole.WriteLine();

        var report = format.ToLowerInvariant() switch
        {
            "md" or "markdown" => MemoryDumpAnalyzer.GenerateReport(result),
            "json" => SerializeResultToJson(result),
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
        {
            await ExtractEsmRecordsAsync(input, extractEsm, result, verbose);
        }
    }

    /// <summary>
    ///     Serialize analysis result to JSON using source-generated serializer.
    /// </summary>
    private static string SerializeResultToJson(AnalysisResult result)
    {
        // Convert to the trim-compatible JSON types
        var jsonResult = new JsonAnalysisResult
        {
            FilePath = result.FilePath,
            FileSize = result.FileSize,
            BuildType = result.BuildType,
            IsXbox360 = result.MinidumpInfo?.IsXbox360 ?? false,
            ModuleCount = result.MinidumpInfo?.Modules.Count ?? 0,
            MemoryRegionCount = result.MinidumpInfo?.MemoryRegions.Count ?? 0,
            CarvedFiles = result.CarvedFiles.Select(cf => new JsonCarvedFileInfo
            {
                FileType = cf.FileType,
                Offset = cf.Offset,
                Length = cf.Length,
                FileName = cf.FileName
            }).ToList(),
            EsmRecords = result.EsmRecords != null
                ? new JsonEsmRecordSummary
                {
                    EdidCount = result.EsmRecords.EditorIds.Count,
                    GmstCount = result.EsmRecords.GameSettings.Count,
                    SctxCount = result.EsmRecords.ScriptSources.Count,
                    ScroCount = result.EsmRecords.FormIdReferences.Count
                }
                : null,
            ScdaRecords = result.ScdaRecords.Select(sr => new JsonScdaRecordInfo
            {
                Offset = sr.Offset,
                BytecodeLength = sr.BytecodeLength,
                ScriptName = sr.ScriptName
            }).ToList(),
            FormIdMap = result.FormIdMap
        };

        return JsonSerializer.Serialize(jsonResult, CarverJsonContext.Default.JsonAnalysisResult);
    }

    private static async Task ExtractEsmRecordsAsync(string input, string extractEsm,
        AnalysisResult result, bool verbose)
    {
        // Set logger level based on verbose flag
        if (verbose)
        {
            Logger.Instance.Level = Debug;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[blue]Exporting ESM records to:[/] {extractEsm}");
        await EsmRecordFormat.ExportRecordsAsync(
            result.EsmRecords!,
            result.FormIdMap,
            extractEsm);
        AnsiConsole.MarkupLine("[green]ESM export complete.[/]");

        if (result.ScdaRecords.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[blue]Extracting compiled scripts (SCDA)...[/]");
            var dumpData = await File.ReadAllBytesAsync(input);
            var scriptsDir = Path.Combine(extractEsm, "scripts");
            var scriptProgress =
                verbose ? new Progress<string>(msg => AnsiConsole.MarkupLine($"  [grey]{msg}[/]")) : null;
            var scriptResult = await ScdaExtractor.ExtractGroupedAsync(dumpData, scriptsDir, scriptProgress);
            AnsiConsole.MarkupLine(
                $"[green]Scripts extracted:[/] {scriptResult.TotalRecords} records ({scriptResult.GroupedQuests} quests, {scriptResult.UngroupedScripts} ungrouped)");
        }
    }
}
