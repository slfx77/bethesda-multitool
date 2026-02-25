// Copyright (c) 2026 FalloutXbox360Utils Contributors
// Licensed under the MIT License.

using System.CommandLine;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Ddx;
using FalloutXbox360Utils.Core.Formats.Xma;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI command for BSA Xbox 360 to PC conversion (bsa convert).
/// </summary>
internal static class BsaConvertCommand
{
    public static Command CreateConvertCommand()
    {
        var command = new Command("convert",
            "Convert Xbox 360 BSA to PC format (extract, convert contents, repack)");

        var inputArg = new Argument<string>("input") { Description = "Path to Xbox 360 BSA file" };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output BSA file path",
            Required = true
        };
        var noDdxOption = new Option<bool>("--no-ddx") { Description = "Skip DDX->DDS texture conversion" };
        var noXmaOption = new Option<bool>("--no-xma") { Description = "Skip XMA->WAV audio conversion" };
        var noNifOption = new Option<bool>("--no-nif") { Description = "Skip NIF endian conversion" };
        var noCompressOption = new Option<bool>("--no-compress") { Description = "Don't compress output BSA" };
        var verboseOption = new Option<bool>("-v", "--verbose") { Description = "Verbose output" };

        command.Arguments.Add(inputArg);
        command.Options.Add(outputOption);
        command.Options.Add(noDdxOption);
        command.Options.Add(noXmaOption);
        command.Options.Add(noNifOption);
        command.Options.Add(noCompressOption);
        command.Options.Add(verboseOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputOption)!;
            var skipDdx = parseResult.GetValue(noDdxOption);
            var skipXma = parseResult.GetValue(noXmaOption);
            var skipNif = parseResult.GetValue(noNifOption);
            var noCompress = parseResult.GetValue(noCompressOption);
            var verbose = parseResult.GetValue(verboseOption);
            await RunConvertAsync(input, output, !skipDdx, !skipXma, !skipNif, !noCompress, verbose,
                cancellationToken);
        });

        return command;
    }

    private static string FormatConversionStatus(bool requested, bool available, string? dependency)
    {
        if (!requested)
        {
            return "[dim]Disabled[/]";
        }

        if (available)
        {
            return "[green]Enabled[/]";
        }

        var reason = dependency != null ? $" ({dependency} not found)" : "";
        return $"[yellow]Unavailable{reason}[/]";
    }

    private static async Task RunConvertAsync(
        string input,
        string output,
        bool convertDdx,
        bool convertXma,
        bool convertNif,
        bool compress,
        bool verbose,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return;
        }

        // If output is a directory (or has no extension), auto-generate the BSA filename
        if (Directory.Exists(output) || string.IsNullOrEmpty(Path.GetExtension(output)))
        {
            Directory.CreateDirectory(output);
            output = Path.Combine(output, Path.GetFileName(input));
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"bsa_convert_{Guid.NewGuid():N}");
        var extractDir = Path.Combine(tempDir, "extracted");
        var ddxOutputDir = Path.Combine(tempDir, "ddx_converted");

        try
        {
            AnsiConsole.MarkupLine("[cyan]BSA Xbox 360 -> PC Conversion[/]");
            AnsiConsole.MarkupLine("[dim]Input:[/]  {0}", input);
            AnsiConsole.MarkupLine("[dim]Output:[/] {0}", output);
            AnsiConsole.WriteLine();

            // Step 1: Parse source BSA
            AnsiConsole.MarkupLine("[yellow]Step 1:[/] Parsing source BSA...");
            using var extractor = new BsaExtractor(input);
            var archive = extractor.Archive;

            AnsiConsole.MarkupLine("  Version: {0}", archive.Header.Version);
            AnsiConsole.MarkupLine("  Platform: {0}", archive.Platform);
            AnsiConsole.MarkupLine("  Folders: {0:N0}", archive.Header.FolderCount);
            AnsiConsole.MarkupLine("  Files: {0:N0}", archive.Header.FileCount);

            var extStats = extractor.GetExtensionStats();
            AnsiConsole.MarkupLine("  Extensions: {0}",
                string.Join(", ", extStats.Take(8).Select(kv => $"{kv.Key} ({kv.Value:N0})")));
            AnsiConsole.WriteLine();

            // Step 2: Check conversion availability
            var ddxAvailable = convertDdx; // DDXConv is compiled-in, always available
            var xmaAvailable = convertXma && XmaWavConverter.IsAvailable;
            var nifAvailable = convertNif;

            AnsiConsole.MarkupLine("[yellow]Step 2:[/] Conversion configuration:");
            AnsiConsole.MarkupLine("  DDX->DDS: {0}", FormatConversionStatus(convertDdx, ddxAvailable, "DDXConv"));
            AnsiConsole.MarkupLine("  XMA->WAV: {0}", FormatConversionStatus(convertXma, xmaAvailable, "FFmpeg"));
            AnsiConsole.MarkupLine("  NIF BE->LE: {0}", FormatConversionStatus(convertNif, nifAvailable, null));
            AnsiConsole.MarkupLine("  Compression: {0}", compress ? "[green]Yes[/]" : "[dim]No[/]");
            AnsiConsole.WriteLine();

            // Step 3: Extract raw files (NO conversion -- conversions happen in dedicated phases)
            AnsiConsole.MarkupLine("[yellow]Step 3:[/] Extracting raw files...");
            Directory.CreateDirectory(extractDir);

            var extractResults = await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("[green]Extracting[/]", maxValue: archive.TotalFiles);
                    var progress = new Progress<(int current, int total, string fileName)>(p =>
                    {
                        task.Value = p.current;
                        if (verbose)
                        {
                            task.Description = $"[green]{Path.GetFileName(p.fileName)}[/]";
                        }
                    });
                    return await extractor.ExtractAllAsync(extractDir, true, progress, cancellationToken);
                });

            var extractSucceeded = extractResults.Count(r => r.Success);
            var extractFailed = extractResults.Count(r => !r.Success);
            AnsiConsole.MarkupLine("  Extracted: {0:N0} files", extractSucceeded);
            if (extractFailed > 0)
            {
                AnsiConsole.MarkupLine("  [red]Failed: {0:N0} files[/]", extractFailed);
                foreach (var f in extractResults.Where(r => !r.Success).Take(5))
                {
                    AnsiConsole.MarkupLine("    [red]{0}: {1}[/]", f.SourcePath, f.Error ?? "Unknown");
                }
            }

            AnsiConsole.WriteLine();

            // Step 4: Post-extraction conversions (each format uses its optimal strategy)
            var ddxConverted = 0;
            var xmaConverted = 0;
            var nifConverted = 0;

            // 4a: DDX -> DDS via batch DDXConv (single subprocess, internal Parallel.ForEach)
            if (ddxAvailable)
            {
                ddxConverted = await ConvertDdxBatchAsync(extractDir, ddxOutputDir, verbose, cancellationToken);
            }

            // 4b: XMA -> WAV via FFmpeg (parallel)
            if (xmaAvailable)
            {
                xmaConverted = await ConvertXmaFilesAsync(extractDir, cancellationToken);
            }

            // 4c: NIF endian conversion (sequential, in-process)
            if (nifAvailable)
            {
                nifConverted = await ConvertNifFilesAsync(extractor, extractDir, verbose, cancellationToken);
            }

            // Step 5: Repack into PC BSA v104
            AnsiConsole.MarkupLine("[yellow]Step 5:[/] Repacking into PC BSA v104...");
            var allFiles = Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories);
            var relativePaths = allFiles.Select(f => Path.GetRelativePath(extractDir, f));
            using var writer = BsaWriter.CreateWithAutoFlags(relativePaths, compress);

            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("[green]Repacking[/]", maxValue: allFiles.Length);

                    foreach (var filePath in allFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relativePath = Path.GetRelativePath(extractDir, filePath);
                        var data = await File.ReadAllBytesAsync(filePath, cancellationToken);
                        writer.AddFile(relativePath, data);
                        task.Increment(1);
                    }
                });

            var outputDirPath = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(outputDirPath))
            {
                Directory.CreateDirectory(outputDirPath);
            }

            writer.Write(output);

            var outputSize = new FileInfo(output).Length;
            var inputSize = new FileInfo(input).Length;

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[green]Conversion complete[/]");
            AnsiConsole.MarkupLine("  Input:  {0} ({1})", Path.GetFileName(input), CliHelpers.FormatSize(inputSize));
            AnsiConsole.MarkupLine("  Output: {0} ({1})", Path.GetFileName(output), CliHelpers.FormatSize(outputSize));
            AnsiConsole.MarkupLine("  Files:  {0:N0}", allFiles.Length);

            var conversions = new List<string>();
            if (ddxConverted > 0)
            {
                conversions.Add($"{ddxConverted:N0} DDX->DDS");
            }

            if (xmaConverted > 0)
            {
                conversions.Add($"{xmaConverted:N0} XMA->WAV");
            }

            if (nifConverted > 0)
            {
                conversions.Add($"{nifConverted:N0} NIF");
            }

            if (conversions.Count > 0)
            {
                AnsiConsole.MarkupLine("  Converted: {0}", string.Join(", ", conversions));
            }
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Conversion cancelled.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]Error during conversion:[/] {0}", ex.Message);
            if (verbose)
            {
                AnsiConsole.WriteException(ex);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch
                {
                    AnsiConsole.MarkupLine("[yellow]Warning:[/] Could not clean up temp dir: {0}", tempDir);
                }
            }
        }
    }

    /// <summary>
    ///     Convert DDX files to DDS using DDXConv batch mode (single subprocess, internal parallelism).
    ///     Returns the number of successfully converted files.
    /// </summary>
    private static async Task<int> ConvertDdxBatchAsync(
        string extractDir, string ddxOutputDir, bool verbose, CancellationToken cancellationToken)
    {
        var ddxFiles = Directory.GetFiles(extractDir, "*.ddx", SearchOption.AllDirectories);
        if (ddxFiles.Length == 0)
        {
            return 0;
        }

        AnsiConsole.MarkupLine("[yellow]Step 4a:[/] Converting {0:N0} DDX -> DDS (batch mode)...", ddxFiles.Length);
        Directory.CreateDirectory(ddxOutputDir);

        var ddxConverter = new DdxConverter();
        var batchResult = await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]DDX -> DDS[/]", maxValue: ddxFiles.Length);

                return await ddxConverter.ConvertBatchAsync(
                    extractDir,
                    ddxOutputDir,
                    (inputPath, status, _) =>
                    {
                        task.Increment(1);
                        if (verbose)
                        {
                            var safeName = Markup.Escape(Path.GetFileName(inputPath));
                            var safeStatus = Markup.Escape(status);
                            task.Description = $"[green]{safeName}[/] {safeStatus}";
                        }
                    },
                    cancellationToken,
                    true);
            });

        // Merge converted DDS files into extractDir, delete original DDX files
        var merged = DdxBatchHelper.MergeConversions(extractDir, ddxOutputDir);

        AnsiConsole.MarkupLine("  Converted: {0:N0}, Failed: {1:N0}, Unsupported: {2:N0}",
            batchResult.Converted, batchResult.Failed, batchResult.Unsupported);
        if (merged != batchResult.Converted)
        {
            AnsiConsole.MarkupLine("  [dim]Merged {0:N0} files (specular maps merged into normals by --pc-friendly)[/]",
                merged);
        }

        if (batchResult.Errors.Count > 0)
        {
            foreach (var error in batchResult.Errors.Take(5))
            {
                AnsiConsole.MarkupLine("    [red]{0}[/]", error);
            }
        }

        AnsiConsole.WriteLine();
        return batchResult.Converted;
    }

    /// <summary>
    ///     Convert XMA files to WAV using FFmpeg (parallel).
    ///     Returns the number of successfully converted files.
    /// </summary>
    private static async Task<int> ConvertXmaFilesAsync(
        string extractDir, CancellationToken cancellationToken)
    {
        var xmaFiles = Directory.GetFiles(extractDir, "*.xma", SearchOption.AllDirectories);
        if (xmaFiles.Length == 0)
        {
            return 0;
        }

        AnsiConsole.MarkupLine("[yellow]Step 4b:[/] Converting {0:N0} XMA -> WAV...", xmaFiles.Length);

        var xmaConverted = 0;
        var xmaFailed = 0;

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]XMA -> WAV[/]", maxValue: xmaFiles.Length);

                await Parallel.ForEachAsync(xmaFiles, cancellationToken, async (xmaFile, ct) =>
                {
                    var xmaData = await File.ReadAllBytesAsync(xmaFile, ct);
                    var result = await XmaWavConverter.ConvertAsync(xmaData);

                    if (result is { Success: true, OutputData: not null })
                    {
                        var wavFile = Path.ChangeExtension(xmaFile, ".wav");
                        await File.WriteAllBytesAsync(wavFile, result.OutputData, ct);
                        File.Delete(xmaFile);
                        Interlocked.Increment(ref xmaConverted);
                    }
                    else
                    {
                        Interlocked.Increment(ref xmaFailed);
                    }

                    task.Increment(1);
                });
            });

        AnsiConsole.MarkupLine("  Converted: {0:N0}, Failed: {1:N0}", xmaConverted, xmaFailed);
        AnsiConsole.WriteLine();
        return xmaConverted;
    }

    /// <summary>
    ///     Convert NIF files from Xbox 360 big-endian to PC little-endian (sequential, in-process).
    ///     Returns the number of successfully converted files.
    /// </summary>
    private static async Task<int> ConvertNifFilesAsync(
        BsaExtractor extractor, string extractDir, bool verbose, CancellationToken cancellationToken)
    {
        var nifFiles = Directory.GetFiles(extractDir, "*.nif", SearchOption.AllDirectories);
        if (nifFiles.Length == 0)
        {
            return 0;
        }

        AnsiConsole.MarkupLine("[yellow]Step 4c:[/] Converting {0:N0} NIF files...", nifFiles.Length);
        extractor.EnableNifConversion(true, verbose);

        var nifConverted = 0;
        var nifSkipped = 0;

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]NIF BE -> LE[/]", maxValue: nifFiles.Length);

                foreach (var nifFile in nifFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var nifData = await File.ReadAllBytesAsync(nifFile, cancellationToken);
                    var result = await extractor.ConvertNifAsync(nifData);

                    if (result is { Success: true, OutputData: not null })
                    {
                        if (result.Notes?.Contains("already") != true)
                        {
                            await File.WriteAllBytesAsync(nifFile, result.OutputData, cancellationToken);
                            nifConverted++;
                        }
                        else
                        {
                            nifSkipped++;
                        }
                    }

                    task.Increment(1);
                }
            });

        AnsiConsole.MarkupLine("  Converted: {0:N0}, Already PC: {1:N0}", nifConverted, nifSkipped);
        AnsiConsole.WriteLine();
        return nifConverted;
    }
}
