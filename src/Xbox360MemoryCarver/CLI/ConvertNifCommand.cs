using System.CommandLine;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.Nif;

namespace Xbox360MemoryCarver.CLI;

/// <summary>
///     CLI command for converting Xbox 360 NIF files (big-endian) to PC format (little-endian).
/// </summary>
public static class ConvertNifCommand
{
    public static Command Create()
    {
        var command = new Command("convert-nif",
            "Convert Xbox 360 NIF files (big-endian) to PC format (little-endian)");

        var inputArgument = new Argument<string>("input", "Path to NIF file or directory containing NIF files");
        var outputOption = new Option<string>(["-o", "--output"],
            "Output directory (default: input directory with '_converted' suffix)");
        var recursiveOption = new Option<bool>(["-r", "--recursive"], "Process directories recursively");
        var verboseOption = new Option<bool>(["-v", "--verbose"], "Enable verbose output");
        var overwriteOption = new Option<bool>(["--overwrite"], "Overwrite existing files");

        command.AddArgument(inputArgument);
        command.AddOption(outputOption);
        command.AddOption(recursiveOption);
        command.AddOption(verboseOption);
        command.AddOption(overwriteOption);

        command.SetHandler(
            async (input, output, recursive, verbose, overwrite) =>
            {
                await ExecuteAsync(input, output, recursive, verbose, overwrite);
            }, inputArgument, outputOption, recursiveOption, verboseOption, overwriteOption);

        return command;
    }

    private static async Task ExecuteAsync(string input, string? output, bool recursive, bool verbose, bool overwrite)
    {
        var files = new List<string>();
        string? inputBaseDir = null;

        if (File.Exists(input))
        {
            files.Add(input);
            output ??= Path.GetDirectoryName(input) ?? ".";
        }
        else if (Directory.Exists(input))
        {
            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            files.AddRange(Directory.GetFiles(input, "*.nif", searchOption));
            output ??= input + "_converted";
            inputBaseDir = Path.GetFullPath(input);
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Input path not found: {input}");
            return;
        }

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No NIF files found.[/]");
            return;
        }

        Directory.CreateDirectory(output);

        AnsiConsole.MarkupLine($"[blue]Found[/] {files.Count} NIF file(s) to process");
        AnsiConsole.MarkupLine($"[blue]Output:[/] {output}");
        AnsiConsole.WriteLine();

        var converter = new NifConverter(verbose);
        var converted = 0;
        var skipped = 0;
        var failed = 0;

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[yellow]Converting NIF files[/]", maxValue: files.Count);

                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    task.Description = $"[yellow]{fileName}[/]";

                    try
                    {
                        // Preserve directory structure for recursive operations
                        string outputPath;
                        if (inputBaseDir != null)
                        {
                            var fullFilePath = Path.GetFullPath(file);
                            var relativePath = Path.GetRelativePath(inputBaseDir, fullFilePath);
                            outputPath = Path.Combine(output, relativePath);
                            var outputDir = Path.GetDirectoryName(outputPath);
                            if (!string.IsNullOrEmpty(outputDir))
                                Directory.CreateDirectory(outputDir);
                        }
                        else
                        {
                            outputPath = Path.Combine(output, fileName);
                        }

                        if (File.Exists(outputPath) && !overwrite)
                        {
                            if (verbose) AnsiConsole.MarkupLine($"[grey]Skipping (exists):[/] {fileName}");
                            skipped++;
                            task.Increment(1);
                            continue;
                        }

                        var data = await File.ReadAllBytesAsync(file);
                        var result = converter.Convert(data);

                        if (result.Success && result.OutputData != null)
                        {
                            await File.WriteAllBytesAsync(outputPath, result.OutputData);
                            converted++;

                            if (verbose)
                            {
                                AnsiConsole.MarkupLine($"[green]Converted:[/] {fileName}");
                                if (!string.IsNullOrEmpty(result.ErrorMessage))
                                    AnsiConsole.MarkupLine($"[dim]  {result.ErrorMessage}[/]");
                            }
                        }
                        else
                        {
                            // File might already be little-endian or invalid
                            if (verbose)
                                AnsiConsole.MarkupLine(
                                    $"[yellow]Skipped:[/] {fileName} - {result.ErrorMessage ?? "already LE or invalid"}");
                            skipped++;
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        if (verbose) AnsiConsole.MarkupLine($"[red]Failed:[/] {fileName} - {ex.Message}");
                    }

                    task.Increment(1);
                }

                task.Description = "[green]Complete[/]";
            });

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]Converted:[/] {converted}");

        if (skipped > 0) AnsiConsole.MarkupLine($"[yellow]Skipped:[/] {skipped}");

        if (failed > 0) AnsiConsole.MarkupLine($"[red]Failed:[/] {failed}");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Xbox 360 NIFs have been converted with geometry unpacking.[/]");
        AnsiConsole.MarkupLine("[dim]For best results, verify output with NifSkope.[/]");
    }
}
