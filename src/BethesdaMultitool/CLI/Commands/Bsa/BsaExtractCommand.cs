// Copyright (c) 2026 BethesdaMultitool Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.IO.Enumeration;
using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Bsa.Models;
using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Formats.Ddx;
using Spectre.Console;
using ArchiveEntry = BethesdaMultitool.Core.Formats.Bsa.Index.ArchiveReader.ArchiveEntry;

namespace BethesdaMultitool.CLI.Commands.Bsa;

/// <summary>
///     CLI command for archive extraction (<c>archive extract</c>). Works for BSA and BA2: enumeration
///     and filtering are format-agnostic via <see cref="ArchiveReader" />. The Xbox 360→PC conversion
///     (<c>--convert</c>: DDX→DDS, XMA→WAV, NIF endian) is BSA-only — BA2 is already a PC format, so
///     <c>--convert</c> is ignored there.
/// </summary>
internal static class BsaExtractCommand
{
    public static Command CreateExtractCommand()
    {
        var command = new Command("extract", "Extract files from an archive (BSA or BA2)");

        var inputArg = new Argument<string>("input") { Description = "Path to BSA or BA2 file" };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory",
            Required = true
        };
        var filterOption = new Option<string?>("-f", "--filter")
            { Description = "Filter by glob pattern (e.g., *.nif, *benny*.nif) or extension (.nif)" };
        var folderOption = new Option<string?>("-d", "--folder") { Description = "Filter by folder / path substring" };
        var overwriteOption = new Option<bool>("--overwrite") { Description = "Overwrite existing files" };
        var convertOption = new Option<bool>("-c", "--convert")
            { Description = "Convert Xbox 360 formats to PC (DDX->DDS, XMA->WAV, NIF endian) — BSA only" };
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
            using var reader = ArchiveReader.Open(input);

            AnsiConsole.MarkupLine("[cyan]{0}:[/] {1} ([yellow]{2}[/])",
                reader.FormatName, Path.GetFileName(input), reader.PlatformLabel);
            AnsiConsole.MarkupLine("[cyan]Output:[/] {0}", output);

            var predicate = BuildPredicate(filter, folder);
            var entries = reader.ListFiles().Where(predicate).ToList();

            AnsiConsole.MarkupLine("[cyan]Files to extract:[/] {0:N0}", entries.Count);
            AnsiConsole.WriteLine();

            if (entries.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No files match the filter criteria.[/]");
                return;
            }

            Directory.CreateDirectory(output);

            if (reader.AsBsaExtractor is { } bsaExtractor)
            {
                await RunBsaExtractAsync(bsaExtractor, entries, output, overwrite, convert, verbose);
            }
            else
            {
                if (convert)
                {
                    AnsiConsole.MarkupLine(
                        "[yellow]Note:[/] --convert is ignored for BA2 (already a PC format); extracting as-is.");
                }

                await RunGenericExtractAsync(reader, entries, output, overwrite);
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] {0}", ex.Message);
        }
    }

    /// <summary>Builds a format-neutral predicate over archive entries (glob or extension + folder).</summary>
    private static Func<ArchiveEntry, bool> BuildPredicate(string? filter, string? folder)
    {
        if (string.IsNullOrEmpty(filter) && string.IsNullOrEmpty(folder))
        {
            return _ => true;
        }

        var isGlob = filter != null && (filter.Contains('*') || filter.Contains('?'));

        return entry =>
        {
            if (!string.IsNullOrEmpty(filter))
            {
                if (isGlob)
                {
                    var path = entry.FullPath.Replace('\\', '/');
                    var pattern = filter.Replace('\\', '/');
                    if (!FileSystemName.MatchesSimpleExpression(pattern, path))
                    {
                        return false;
                    }
                }
                else
                {
                    var ext = filter.StartsWith('.') ? filter : $".{filter}";
                    if (!entry.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }

            return string.IsNullOrEmpty(folder)
                   || entry.FolderPath.Contains(folder, StringComparison.OrdinalIgnoreCase);
        };
    }

    /// <summary>BSA extraction: preserves the tuned converter path (per-file XMA/NIF + batch DDX).</summary>
    private static async Task RunBsaExtractAsync(BsaExtractor extractor, List<ArchiveEntry> entries,
        string output, bool overwrite, bool convert, bool verbose)
    {
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
            AnsiConsole.WriteLine();
        }

        // Restrict the extractor's filtered pass to exactly the entries we selected (reference identity:
        // the records came from this extractor's archive).
        var selected = new HashSet<BsaFileRecord>(
            entries.Select(e => (BsaFileRecord)e.Record), ReferenceEqualityComparer.Instance);

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
                var task = ctx.AddTask("[green]Extracting files[/]", maxValue: selected.Count);
                var progress = new Progress<(int current, int total, string fileName)>(p =>
                {
                    task.Value = p.current;
                    task.Description = $"[green]Extracting:[/] {Path.GetFileName(p.fileName)}";
                });

                return await extractor.ExtractFilteredAsync(output, selected.Contains, overwrite, progress);
            });

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
                    var batchResult = await converter.ConvertBatchAsync(output, ddxOutputDir, pcFriendly: true);
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

        var succeeded = results.Count(r => r.Success);
        var failed = results.Count(r => !r.Success);
        var converted = results.Count(r => r.WasConverted);
        var totalSize = results.Where(r => r.Success).Sum(r => r.ExtractedSize);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]✓ Extracted:[/] {0:N0} files ({1})", succeeded,
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

            AnsiConsole.MarkupLine("[blue]↻ Converted:[/] {0}", string.Join(", ", parts));

            if (ddxBatchFailed > 0)
            {
                AnsiConsole.MarkupLine("[yellow]  DDX conversion failures:[/] {0:N0}", ddxBatchFailed);
            }
        }

        if (failed > 0)
        {
            AnsiConsole.MarkupLine("[red]✗ Failed:[/] {0:N0} files", failed);

            foreach (var failure in results.Where(r => !r.Success).Take(10))
            {
                AnsiConsole.MarkupLine("  [red]•[/] {0}: {1}", failure.SourcePath,
                    failure.Error ?? "Unknown error");
            }
        }
    }

    /// <summary>BA2 (and any non-converting) extraction via the unified reader.</summary>
    private static async Task RunGenericExtractAsync(ArchiveReader reader, List<ArchiveEntry> entries,
        string output, bool overwrite)
    {
        var extracted = 0;
        var failed = 0;
        long totalSize = 0;

        await AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Extracting files[/]", maxValue: entries.Count);
                foreach (var entry in entries)
                {
                    task.Description = $"[green]Extracting:[/] {Path.GetFileName(entry.FullPath)}";
                    try
                    {
                        if (await reader.ExtractToDiskAsync(entry, output, overwrite))
                        {
                            extracted++;
                            totalSize += entry.Size;
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        AnsiConsole.MarkupLine("[red]Failed:[/] {0} ({1})", entry.FullPath, ex.Message);
                    }

                    task.Increment(1);
                }
            });

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[green]✓ Extracted:[/] {0:N0} files ({1})", extracted,
            CliHelpers.FormatSize(totalSize));
        if (failed > 0)
        {
            AnsiConsole.MarkupLine("[red]✗ Failed:[/] {0:N0} files", failed);
        }
    }
}
