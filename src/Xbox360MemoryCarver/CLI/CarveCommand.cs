using System.Diagnostics;
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
            await ProcessFileAsync(file, outputDir, fileTypes, convertDdx, verbose, maxFiles);
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

        ExtractionSummary summary;

        try
        {
            summary = await ExtractWithProgressAsync(file, options);
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

    private static async Task<ExtractionSummary> ExtractWithProgressAsync(string file, ExtractionOptions options)
    {
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

        // Summary will always be non-null after successful completion
        return summary!;
    }

    private static void PrintSummary(ExtractionSummary summary, bool convertDdx)
    {
        PrintCategoryTable(summary);
        PrintConversionStats(summary, convertDdx);
        PrintScriptStats(summary);
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

        foreach (var (type, count) in typeCounts)
        {
            var category = CategoryMap.GetValueOrDefault(type, type);
            categorized[category] = categorized.GetValueOrDefault(category) + count;
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
}
