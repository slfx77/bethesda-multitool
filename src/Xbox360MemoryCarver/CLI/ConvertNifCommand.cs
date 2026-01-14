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

        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to NIF file or directory containing NIF files"
        };
        var outputOption = new Option<string?>("-o", "--output")
        {
            Description = "Output directory (default: input directory with '_converted' suffix)"
        };
        var recursiveOption = new Option<bool>("-r", "--recursive")
        {
            Description = "Process directories recursively"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };
        var overwriteOption = new Option<bool>("--overwrite")
        {
            Description = "Overwrite existing files"
        };

        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(recursiveOption);
        command.Options.Add(verboseOption);
        command.Options.Add(overwriteOption);

        command.SetAction(async (parseResult, _) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption);
            var recursive = parseResult.GetValue(recursiveOption);
            var verbose = parseResult.GetValue(verboseOption);
            var overwrite = parseResult.GetValue(overwriteOption);
            await ExecuteAsync(input, output, recursive, verbose, overwrite);
        });

        return command;
    }

    private static async Task ExecuteAsync(string input, string? output, bool recursive, bool verbose, bool overwrite)
    {
        var context = BuildConversionContext(input, output, recursive, verbose, overwrite);
        if (context == null) return;

        PrintStartMessage(context);
        await ProcessFilesAsync(context);
        PrintSummary(context);
    }

    /// <summary>
    ///     Builds the conversion context from command arguments.
    /// </summary>
    private static ConversionContext? BuildConversionContext(
        string input,
        string? output,
        bool recursive,
        bool verbose,
        bool overwrite)
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
            return null;
        }

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No NIF files found.[/]");
            return null;
        }

        Directory.CreateDirectory(output);

        return new ConversionContext
        {
            Files = files,
            Output = output,
            InputBaseDir = inputBaseDir,
            Verbose = verbose,
            Overwrite = overwrite
        };
    }

    /// <summary>
    ///     Prints the start message.
    /// </summary>
    private static void PrintStartMessage(ConversionContext context)
    {
        AnsiConsole.MarkupLine($"[blue]Found[/] {context.Files.Count} NIF file(s) to process");
        AnsiConsole.MarkupLine($"[blue]Output:[/] {context.Output}");
        AnsiConsole.WriteLine();
    }

    /// <summary>
    ///     Processes all files with progress display.
    /// </summary>
    private static async Task ProcessFilesAsync(ConversionContext context)
    {
        var converter = new NifConverter(context.Verbose);

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[yellow]Converting NIF files[/]", maxValue: context.Files.Count);

                foreach (var file in context.Files)
                {
                    var fileName = Path.GetFileName(file);
                    task.Description = $"[yellow]{fileName}[/]";

                    await ProcessSingleFileAsync(context, converter, file, fileName);
                    task.Increment(1);
                }

                task.Description = "[green]Complete[/]";
            });
    }

    /// <summary>
    ///     Processes a single file.
    /// </summary>
    private static async Task ProcessSingleFileAsync(
        ConversionContext context,
        NifConverter converter,
        string file,
        string fileName)
    {
        try
        {
            var outputPath = GetOutputPath(context, file, fileName);

            if (ShouldSkipExistingFile(context, outputPath, fileName)) return;

            await ConvertFileAsync(context, converter, file, fileName, outputPath);
        }
        catch (Exception ex)
        {
            context.Failed++;
            if (context.Verbose) AnsiConsole.MarkupLine($"[red]Failed:[/] {fileName} - {ex.Message}");
        }
    }

    /// <summary>
    ///     Gets the output path for a file, preserving directory structure.
    /// </summary>
    private static string GetOutputPath(ConversionContext context, string file, string fileName)
    {
        if (context.InputBaseDir == null) return Path.Combine(context.Output, fileName);

        var fullFilePath = Path.GetFullPath(file);
        var relativePath = Path.GetRelativePath(context.InputBaseDir, fullFilePath);
        var outputPath = Path.Combine(context.Output, relativePath);
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir)) Directory.CreateDirectory(outputDir);

        return outputPath;
    }

    /// <summary>
    ///     Checks if an existing file should be skipped.
    /// </summary>
    private static bool ShouldSkipExistingFile(ConversionContext context, string outputPath, string fileName)
    {
        if (!File.Exists(outputPath) || context.Overwrite) return false;

        if (context.Verbose) AnsiConsole.MarkupLine($"[grey]Skipping (exists):[/] {fileName}");

        context.Skipped++;
        return true;
    }

    /// <summary>
    ///     Performs the actual file conversion.
    /// </summary>
    private static async Task ConvertFileAsync(
        ConversionContext context,
        NifConverter converter,
        string file,
        string fileName,
        string outputPath)
    {
        var data = await File.ReadAllBytesAsync(file);
        var result = converter.Convert(data);

        if (result is { Success: true, OutputData: not null })
        {
            await File.WriteAllBytesAsync(outputPath, result.OutputData);
            context.Converted++;

            if (context.Verbose)
            {
                AnsiConsole.MarkupLine($"[green]Converted:[/] {fileName}");
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                    AnsiConsole.MarkupLine($"[dim]  {result.ErrorMessage}[/]");
            }
        }
        else
        {
            if (context.Verbose)
                AnsiConsole.MarkupLine(
                    $"[yellow]Skipped:[/] {fileName} - {result.ErrorMessage ?? "already LE or invalid"}");

            context.Skipped++;
        }
    }

    /// <summary>
    ///     Prints the summary.
    /// </summary>
    private static void PrintSummary(ConversionContext context)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]Converted:[/] {context.Converted}");

        if (context.Skipped > 0) AnsiConsole.MarkupLine($"[yellow]Skipped:[/] {context.Skipped}");

        if (context.Failed > 0) AnsiConsole.MarkupLine($"[red]Failed:[/] {context.Failed}");

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Xbox 360 NIFs have been converted with geometry unpacking.[/]");
        AnsiConsole.MarkupLine("[dim]For best results, verify output with NifSkope.[/]");
    }

    /// <summary>
    ///     Context for conversion operations.
    /// </summary>
    private sealed class ConversionContext
    {
        public required List<string> Files { get; init; }
        public required string Output { get; init; }
        public required string? InputBaseDir { get; init; }
        public required bool Verbose { get; init; }
        public required bool Overwrite { get; init; }
        public int Converted { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
    }
}
