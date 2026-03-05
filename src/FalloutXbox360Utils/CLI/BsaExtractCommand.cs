// Copyright (c) 2026 FalloutXbox360Utils Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.IO.Enumeration;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Ddx;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI command for BSA file extraction (bsa extract).
/// </summary>
internal static class BsaExtractCommand
{
    public static Command CreateExtractCommand()
    {
        var command = new Command("extract", "Extract files from a BSA archive");

        var inputArg = new Argument<string>("input") { Description = "Path to BSA file" };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory",
            Required = true
        };
        var filterOption = new Option<string?>("-f", "--filter")
            { Description = "Filter by glob pattern (e.g., *.nif, *benny*.nif) or extension (.nif)" };
        var folderOption = new Option<string?>("-d", "--folder") { Description = "Filter by folder path" };
        var overwriteOption = new Option<bool>("--overwrite") { Description = "Overwrite existing files" };
        var convertOption = new Option<bool>("-c", "--convert")
            { Description = "Convert Xbox 360 formats to PC (DDX->DDS, XMA->WAV, NIF endian)" };
        var verboseOption = new Option<bool>("-v", "--verbose") { Description = "Verbose output" };

        command.Arguments.Add(inputArg);
        command.Options.Add(outputOption);
        command.Options.Add(filterOption);
        command.Options.Add(folderOption);
        command.Options.Add(overwriteOption);
        command.Options.Add(convertOption);
        command.Options.Add(verboseOption);

        command.SetAction(async (parseResult, _) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputOption)!;
            var filter = parseResult.GetValue(filterOption);
            var folder = parseResult.GetValue(folderOption);
            var overwrite = parseResult.GetValue(overwriteOption);
            var convert = parseResult.GetValue(convertOption);
            var verbose = parseResult.GetValue(verboseOption);
            await RunExtractAsync(input, output, filter, folder, overwrite, convert, verbose);
        });

        return command;
    }

    private static async Task RunExtractAsync(string input, string output, string? filter, string? folder,
        bool overwrite, bool convert = false, bool verbose = false)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return;
        }

        try
        {
            using var extractor = new BsaExtractor(input);
            var archive = extractor.Archive;

            AnsiConsole.MarkupLine("[cyan]BSA:[/] {0} ([yellow]{1}[/])", Path.GetFileName(input), archive.Platform);
            AnsiConsole.MarkupLine("[cyan]Output:[/] {0}", output);

            // Initialize converters if requested
            // DDX conversion is handled separately via batch after extraction (much faster)
            var ddxBatchAvailable = false;
            if (convert)
            {
                ddxBatchAvailable = true; // DDXConv is compiled-in
                var xmaAvailable = extractor.EnableXmaConversion(true);
                var nifAvailable = extractor.EnableNifConversion(true, verbose);

                AnsiConsole.MarkupLine("[cyan]Conversion:[/] DDX->DDS: {0}, XMA->WAV: {1}, NIF: {2}",
                    "[green]Yes (batch)[/]",
                    xmaAvailable ? "[green]Yes[/]" : "[yellow]No (FFmpeg not found)[/]",
                    nifAvailable ? "[green]Yes[/]" : "[red]No[/]");
            }

            // Build filter predicate
            Func<BsaFileRecord, bool> predicate = _ => true;

            if (!string.IsNullOrEmpty(filter) || !string.IsNullOrEmpty(folder))
            {
                // Detect glob pattern vs simple extension
                var isGlob = filter != null && (filter.Contains('*') || filter.Contains('?'));

                predicate = file =>
                {
                    if (!string.IsNullOrEmpty(filter))
                    {
                        if (isGlob)
                        {
                            // Glob: match against full path (normalize separators)
                            var path = file.FullPath.Replace('\\', '/');
                            var pattern = filter.Replace('\\', '/');
                            if (!FileSystemName.MatchesSimpleExpression(pattern, path, true))
                                return false;
                        }
                        else
                        {
                            // Extension: e.g., ".nif" or "nif"
                            var ext = filter.StartsWith('.') ? filter : $".{filter}";
                            if (file.Name?.EndsWith(ext, StringComparison.OrdinalIgnoreCase) != true)
                                return false;
                        }
                    }

                    if (!string.IsNullOrEmpty(folder)
                        && file.Folder?.Name?.Contains(folder, StringComparison.OrdinalIgnoreCase) != true)
                    {
                        return false;
                    }

                    return true;
                };
            }

            var filesToExtract = archive.AllFiles.Where(predicate).ToList();
            AnsiConsole.MarkupLine("[cyan]Files to extract:[/] {0:N0}", filesToExtract.Count);
            AnsiConsole.WriteLine();

            if (filesToExtract.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No files match the filter criteria.[/]");
                return;
            }

            Directory.CreateDirectory(output);

            var results = await AnsiConsole.Progress()
                .AutoClear(false)
                .HideCompleted(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("[green]Extracting files[/]", maxValue: filesToExtract.Count);

                    var progress = new Progress<(int current, int total, string fileName)>(p =>
                    {
                        task.Value = p.current;
                        task.Description = $"[green]Extracting:[/] {Path.GetFileName(p.fileName)}";
                    });

                    return await extractor.ExtractFilteredAsync(output, predicate, overwrite, progress);
                });

            // Batch DDX conversion (much faster than per-file subprocess spawning)
            var ddxBatchConverted = 0;
            var ddxBatchFailed = 0;
            if (convert && ddxBatchAvailable)
            {
                var ddxFiles = Directory.GetFiles(output, "*.ddx", SearchOption.AllDirectories);
                if (ddxFiles.Length > 0)
                {
                    AnsiConsole.MarkupLine("[cyan]Converting {0:N0} DDX textures (batch)...[/]", ddxFiles.Length);
                    var ddxOutputDir = Path.Combine(Path.GetTempPath(), $"ddx_batch_{Guid.NewGuid():N}");
                    try
                    {
                        var converter = new DdxConverter();
                        var batchResult = await converter.ConvertBatchAsync(
                            output, ddxOutputDir, pcFriendly: true);
                        ddxBatchConverted = batchResult.Converted;
                        ddxBatchFailed = batchResult.Failed;
                        DdxBatchHelper.MergeConversions(output, ddxOutputDir);
                    }
                    catch (FileNotFoundException)
                    {
                        AnsiConsole.MarkupLine("[yellow]DDXConv not available for batch conversion[/]");
                    }
                    finally
                    {
                        if (Directory.Exists(ddxOutputDir))
                        {
                            Directory.Delete(ddxOutputDir, true);
                        }
                    }
                }
            }

            // Summary
            var succeeded = results.Count(r => r.Success);
            var failed = results.Count(r => !r.Success);
            var converted = results.Count(r => r.WasConverted);
            var totalSize = results.Where(r => r.Success).Sum(r => r.ExtractedSize);

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[green]\u2713 Extracted:[/] {0:N0} files ({1})", succeeded,
                CliHelpers.FormatSize(totalSize));

            if (converted > 0 || ddxBatchConverted > 0)
            {
                var xmaConverted = results.Count(r => r.ConversionType == "XMA->WAV");
                var nifConverted = results.Count(r => r.ConversionType == "NIF BE->LE");

                var parts = new List<string>();
                if (ddxBatchConverted > 0)
                {
                    parts.Add($"{ddxBatchConverted} DDX -> DDS");
                }

                if (xmaConverted > 0)
                {
                    parts.Add($"{xmaConverted} XMA -> WAV");
                }

                if (nifConverted > 0)
                {
                    parts.Add($"{nifConverted} NIF");
                }

                AnsiConsole.MarkupLine("[blue]\u21bb Converted:[/] {0}", string.Join(", ", parts));

                if (ddxBatchFailed > 0)
                {
                    AnsiConsole.MarkupLine("[yellow]  DDX conversion failures:[/] {0:N0}", ddxBatchFailed);
                }
            }

            if (failed > 0)
            {
                AnsiConsole.MarkupLine("[red]\u2717 Failed:[/] {0:N0} files", failed);

                foreach (var failure in results.Where(r => !r.Success).Take(10))
                {
                    AnsiConsole.MarkupLine("  [red]\u2022[/] {0}: {1}", failure.SourcePath,
                        failure.Error ?? "Unknown error");
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] {0}", ex.Message);
        }
    }
}
