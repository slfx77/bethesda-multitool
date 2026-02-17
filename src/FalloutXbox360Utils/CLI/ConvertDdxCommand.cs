using System.CommandLine;
using FalloutXbox360Utils.Core.Formats.Ddx;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI command for converting Xbox 360 DDX texture files to DDS format.
/// </summary>
public static class ConvertDdxCommand
{
    public static Command Create()
    {
        var command = new Command("convert-ddx",
            "Convert Xbox 360 DDX texture files to DDS format");

        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to DDX file or directory containing DDX files"
        };
        var outputOption = new Option<string?>("-o", "--output")
        {
            Description = "Output directory (default: 'converted_dds' subdirectory)"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };
        var overwriteOption = new Option<bool>("--overwrite")
        {
            Description = "Overwrite existing DDS files"
        };
        var pcFriendlyOption = new Option<bool>("--pc-friendly", "-pc")
        {
            Description = "Merge normal + specular maps for PC compatibility",
            DefaultValueFactory = _ => true
        };

        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);
        command.Options.Add(overwriteOption);
        command.Options.Add(pcFriendlyOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption);
            var verbose = parseResult.GetValue(verboseOption);
            var overwrite = parseResult.GetValue(overwriteOption);
            var pcFriendly = parseResult.GetValue(pcFriendlyOption);
            await ExecuteAsync(input, output, verbose, overwrite, pcFriendly, cancellationToken);
        });

        return command;
    }

    private static async Task ExecuteAsync(
        string input, string? output, bool verbose, bool overwrite, bool pcFriendly,
        CancellationToken cancellationToken)
    {
        if (File.Exists(input))
        {
            await ConvertSingleFileAsync(input, output, verbose, overwrite);
            return;
        }

        if (!Directory.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Input path not found: {input}");
            return;
        }

        await ConvertDirectoryAsync(input, output, verbose, overwrite, pcFriendly, cancellationToken);
    }

    private static async Task ConvertSingleFileAsync(
        string inputFile, string? output, bool verbose, bool overwrite)
    {
        var outputDir = output ?? Path.GetDirectoryName(inputFile) ?? ".";
        var outputPath = Path.Combine(outputDir, Path.ChangeExtension(Path.GetFileName(inputFile), ".dds"));

        if (File.Exists(outputPath) && !overwrite)
        {
            AnsiConsole.MarkupLine("[yellow]Skipped:[/] Output file already exists. Use --overwrite to replace.");
            return;
        }

        Directory.CreateDirectory(outputDir);

        var converter = new DdxSubprocessConverter(verbose);
        var success = converter.ConvertFile(inputFile, outputPath);

        if (success)
        {
            AnsiConsole.MarkupLine($"[green]Converted:[/] {Path.GetFileName(inputFile)} -> {outputPath}");
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Failed:[/] {Path.GetFileName(inputFile)}");
        }

        await Task.CompletedTask;
    }

    private static async Task ConvertDirectoryAsync(
        string inputDir, string? output, bool verbose, bool overwrite, bool pcFriendly,
        CancellationToken cancellationToken)
    {
        var outputDir = output ?? Path.Combine(inputDir, "converted_dds");

        var ddxFiles = Directory.GetFiles(inputDir, "*.ddx", SearchOption.AllDirectories);

        if (ddxFiles.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No DDX files found.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[blue]Found[/] {ddxFiles.Length} DDX file(s) to convert");
        AnsiConsole.MarkupLine($"[blue]Output:[/] {outputDir}");
        if (pcFriendly)
        {
            AnsiConsole.MarkupLine("[blue]PC-friendly:[/] Normal+specular map merge enabled");
        }

        AnsiConsole.WriteLine();

        if (!overwrite)
        {
            var existingCount = ddxFiles.Count(f =>
            {
                var rel = Path.GetRelativePath(inputDir, f);
                var outPath = Path.Combine(outputDir, Path.ChangeExtension(rel, ".dds"));
                return File.Exists(outPath);
            });

            if (existingCount > 0)
            {
                AnsiConsole.MarkupLine($"[yellow]{existingCount} file(s) already exist and will be skipped. Use --overwrite to replace.[/]");
            }
        }

        var converter = new DdxSubprocessConverter(verbose);
        var converted = 0;
        var failed = 0;
        var unsupported = 0;
        var skipped = 0;

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[yellow]Converting DDX files[/]", maxValue: ddxFiles.Length);

                void OnFileCompleted(string inputPath, string status, string? error)
                {
                    var fileName = Path.GetFileName(inputPath);
                    switch (status)
                    {
                        case "OK":
                            Interlocked.Increment(ref converted);
                            if (verbose)
                            {
                                AnsiConsole.MarkupLine($"[green]Converted:[/] {fileName}");
                            }

                            break;
                        case "UNSUPPORTED":
                            Interlocked.Increment(ref unsupported);
                            if (verbose)
                            {
                                AnsiConsole.MarkupLine($"[yellow]Unsupported:[/] {fileName}");
                            }

                            break;
                        case "FAIL":
                            Interlocked.Increment(ref failed);
                            if (verbose)
                            {
                                AnsiConsole.MarkupLine($"[red]Failed:[/] {fileName} - {error}");
                            }

                            break;
                    }

                    task.Increment(1);
                }

                var result = await converter.ConvertBatchAsync(
                    inputDir, outputDir, OnFileCompleted, cancellationToken, pcFriendly);

                skipped = ddxFiles.Length - result.Converted - result.Failed - result.Unsupported;
                task.Description = "[green]Complete[/]";
            });

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]Converted:[/] {converted}");

        if (unsupported > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Unsupported:[/] {unsupported}");
        }

        if (skipped > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]Skipped:[/] {skipped}");
        }

        if (failed > 0)
        {
            AnsiConsole.MarkupLine($"[red]Failed:[/] {failed}");
        }
    }
}
