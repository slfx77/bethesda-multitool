using System.CommandLine;
using System.Text.Json;
using Xbox360MemoryCarver.Core.Analysis;
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
            Console.WriteLine($"Error: File not found: {input}");
            return;
        }

        var progress = verbose ? new Progress<string>(msg => Console.WriteLine($"  {msg}")) : null;

        Console.WriteLine($"Analyzing: {Path.GetFileName(input)}");
        Console.WriteLine();

        var result = await DumpAnalyzer.AnalyzeAsync(input, progress: progress);

        var report = format.ToLowerInvariant() switch
        {
            "md" or "markdown" => DumpAnalyzer.GenerateReport(result),
            "json" => JsonSerializer.Serialize(result, JsonOptions),
            _ => DumpAnalyzer.GenerateSummary(result)
        };

        if (!string.IsNullOrEmpty(output))
        {
            await File.WriteAllTextAsync(output, report);
            Console.WriteLine($"Report saved to: {output}");
        }
        else
        {
            Console.WriteLine(report);
        }

        if (!string.IsNullOrEmpty(extractEsm) && result.EsmRecords != null)
            await ExtractEsmRecordsAsync(input, extractEsm, result, verbose);
    }

    private static async Task ExtractEsmRecordsAsync(string input, string extractEsm,
        DumpAnalyzer.AnalysisResult result, bool verbose)
    {
        Console.WriteLine();
        Console.WriteLine($"Exporting ESM records to: {extractEsm}");
        await EsmRecordFormat.ExportRecordsAsync(
            result.EsmRecords!,
            result.FormIdMap,
            extractEsm,
            verbose);
        Console.WriteLine("ESM export complete.");

        if (result.ScdaRecords.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Extracting compiled scripts (SCDA)...");
            var dumpData = await File.ReadAllBytesAsync(input);
            var scriptsDir = Path.Combine(extractEsm, "scripts");
            var scriptProgress = verbose ? new Progress<string>(msg => Console.WriteLine($"  {msg}")) : null;
            var scriptResult = await ScdaExtractor.ExtractGroupedAsync(dumpData, scriptsDir, scriptProgress, verbose);
            Console.WriteLine(
                $"Scripts extracted: {scriptResult.TotalRecords} records ({scriptResult.GroupedQuests} quests, {scriptResult.UngroupedScripts} ungrouped)");
        }
    }
}
