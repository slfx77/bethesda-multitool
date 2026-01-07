using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Spectre.Console;
using Xbox360MemoryCarver.Core;

namespace Xbox360MemoryCarver.CLI;

/// <summary>
///     CLI logic for carving files from memory dumps.
/// </summary>
public static class CarveCommand
{
    /// <summary>
    ///     Maps signature IDs to display categories for cleaner output.
    /// </summary>
    private const string UncompiledScriptsCategory = "Uncompiled Scripts";

    private static readonly Dictionary<string, string> CategoryMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Uncompiled scripts (debug builds only)
        ["script_scn"] = UncompiledScriptsCategory,
        ["script_scriptname"] = UncompiledScriptsCategory,
        ["script_scn_tab"] = UncompiledScriptsCategory,
        ["script_scriptname_lower"] = UncompiledScriptsCategory,

        // Compiled scripts
        ["scda"] = "Compiled Scripts",

        // Textures
        ["ddx_3xdo"] = "DDX Textures",
        ["ddx_3xdr"] = "DDX Textures",
        ["dds"] = "DDS Textures",
        ["png"] = "PNG Images",

        // UI
        ["xui_scene"] = "XUI Scenes",
        ["xui_binary"] = "XUI Binary",

        // Audio
        ["xma"] = "XMA Audio",
        ["lip"] = "LIP Sync",

        // Models
        ["nif"] = "NIF Models",

        // Executables
        ["xex"] = "XEX Modules",

        // Data
        ["esp"] = "ESP/ESM Plugins",
        ["xdbf"] = "XDBF Files"
    };

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
            AnsiConsole.MarkupLine("[red]No dump files found.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[blue]Found[/] {files.Count} file(s) to process");

        foreach (var file in files) await ProcessFileAsync(file, outputDir, fileTypes, convertDdx, verbose, maxFiles);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]Done![/]");
    }

    private static async Task ProcessFileAsync(
        string file,
        string outputDir,
        List<string>? fileTypes,
        bool convertDdx,
        bool verbose,
        int maxFiles)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[blue]{Path.GetFileName(file)}[/]").LeftJustified());

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

        ExtractionSummary? summary = null;

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[yellow]Extracting files[/]", maxValue: 100);

                var progress = new Progress<ExtractionProgress>(p =>
                {
                    task.Value = p.PercentComplete;
                    task.Description = $"[yellow]{p.CurrentOperation}[/]";
                });

                summary = await MemoryDumpExtractor.Extract(file, options, progress);
                task.Value = 100;
                task.Description = "[green]Complete[/]";
            });

        stopwatch.Stop();

        // Null check is defensive - summary is set inside async lambda which the analyzer can't verify
#pragma warning disable S2583 // Conditionally executed code should be reachable
        if (summary is null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Extraction failed");
            return;
        }
#pragma warning restore S2583

        AnsiConsole.MarkupLine(
            $"[green]Extracted[/] {summary.TotalExtracted} files in [blue]{stopwatch.Elapsed.TotalSeconds:F2}s[/]");

        PrintSummary(summary, convertDdx);
    }

    [SuppressMessage("Sonar", "S3776:Cognitive Complexity",
        Justification = "Summary output logic with multiple stats categories is inherently branched")]
    private static void PrintSummary(ExtractionSummary summary, bool convertDdx)
    {
        if (summary.TypeCounts.Count > 0)
        {
            AnsiConsole.WriteLine();

            // Group by category
            var categorized = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var (type, count) in summary.TypeCounts)
            {
                var category = CategoryMap.TryGetValue(type, out var cat) ? cat : type;
                categorized.TryGetValue(category, out var existing);
                categorized[category] = existing + count;
            }

            var table = new Table();
            table.Border(TableBorder.Rounded);
            table.AddColumn(new TableColumn("[bold]Category[/]").LeftAligned());
            table.AddColumn(new TableColumn("[bold]Count[/]").RightAligned());

            foreach (var (category, count) in categorized.OrderByDescending(x => x.Value))
                if (count > 0)
                    table.AddRow(category, count.ToString(CultureInfo.InvariantCulture));

            if (summary.ModulesExtracted > 0)
                table.AddRow("[grey]Modules (from header)[/]",
                    summary.ModulesExtracted.ToString(CultureInfo.InvariantCulture));

            AnsiConsole.Write(table);
        }

        if (convertDdx)
        {
            // DDX conversion stats
            if (summary.DdxConverted > 0 || summary.DdxFailed > 0)
            {
                AnsiConsole.WriteLine();
                var converted = summary.DdxConverted > 0
                    ? $"[green]{summary.DdxConverted} successful[/]"
                    : "0 successful";
                var failed = summary.DdxFailed > 0
                    ? $"[red]{summary.DdxFailed} failed[/]"
                    : "0 failed";
                AnsiConsole.MarkupLine($"DDX → DDS conversions: {converted}, {failed}");
            }

            // XUR conversion stats
            if (summary.XurConverted > 0 || summary.XurFailed > 0)
            {
                var xurConverted = summary.XurConverted > 0
                    ? $"[green]{summary.XurConverted} successful[/]"
                    : "0 successful";
                var xurFailed = summary.XurFailed > 0
                    ? $"[red]{summary.XurFailed} failed[/]"
                    : "0 failed";
                AnsiConsole.MarkupLine($"XUR → XUI conversions: {xurConverted}, {xurFailed}");
            }
        }

        if (summary.ScriptsExtracted > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"[yellow]Scripts:[/] {summary.ScriptsExtracted} records ({summary.ScriptQuestsGrouped} quests grouped)");
        }
    }
}
