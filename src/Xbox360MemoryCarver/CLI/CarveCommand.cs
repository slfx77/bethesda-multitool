using System.Diagnostics;
using Xbox360MemoryCarver.Core;

namespace Xbox360MemoryCarver.CLI;

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
        int maxFiles)
    {
        var files = new List<string>();

        if (File.Exists(inputPath))
            files.Add(inputPath);
        else if (Directory.Exists(inputPath))
            files.AddRange(Directory.GetFiles(inputPath, "*.dmp", SearchOption.TopDirectoryOnly));

        if (files.Count == 0)
        {
            Console.WriteLine("No dump files found.");
            return;
        }

        Console.WriteLine($"Found {files.Count} file(s) to process");

        foreach (var file in files) await ProcessFileAsync(file, outputDir, fileTypes, convertDdx, verbose, maxFiles);

        Console.WriteLine();
        Console.WriteLine("Done!");
    }

    private static async Task ProcessFileAsync(
        string file,
        string outputDir,
        List<string>? fileTypes,
        bool convertDdx,
        bool verbose,
        int maxFiles)
    {
        Console.WriteLine();
        Console.WriteLine($"Processing: {Path.GetFileName(file)}");
        Console.WriteLine(new string('-', 50));

        var stopwatch = Stopwatch.StartNew();

        var options = new ExtractionOptions
        {
            OutputPath = outputDir,
            ConvertDdx = convertDdx,
            FileTypes = fileTypes,
            Verbose = verbose,
            MaxFilesPerType = maxFiles,
            ExtractScripts = fileTypes == null ||
                             fileTypes.Count == 0 ||
                             fileTypes.Any(t => t.Contains("scda", StringComparison.OrdinalIgnoreCase) ||
                                                t.Contains("script", StringComparison.OrdinalIgnoreCase))
        };

        var progress = new Progress<ExtractionProgress>(p =>
        {
            if (verbose) Console.Write($"\rProgress: {p.PercentComplete:F1}% - {p.CurrentOperation}");
        });

        var summary = await MemoryDumpExtractor.Extract(file, options, progress);

        stopwatch.Stop();

        if (verbose) Console.WriteLine(); // Clear progress line

        Console.WriteLine($"Extracted {summary.TotalExtracted} files in {stopwatch.Elapsed.TotalSeconds:F2}s");

        PrintSummary(summary, convertDdx);
    }

    private static void PrintSummary(ExtractionSummary summary, bool convertDdx)
    {
        if (summary.TypeCounts.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("File type summary:");
            foreach (var (type, count) in summary.TypeCounts.OrderByDescending(x => x.Value))
                if (count > 0)
                    Console.WriteLine($"  {type}: {count}");
        }

        if (summary.ModulesExtracted > 0) Console.WriteLine($"  modules: {summary.ModulesExtracted}");

        if (convertDdx && (summary.DdxConverted > 0 || summary.DdxFailed > 0))
        {
            Console.WriteLine();
            Console.WriteLine($"DDX conversions: {summary.DdxConverted} successful, {summary.DdxFailed} failed");
        }

        if (summary.ScriptsExtracted > 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"Scripts: {summary.ScriptsExtracted} records ({summary.ScriptQuestsGrouped} quests grouped)");
        }
    }
}
