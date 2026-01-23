using System.CommandLine;
using EsmAnalyzer.Conversion;
using EsmAnalyzer.Helpers;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Commands for converting Xbox 360 ESM files to PC format.
/// </summary>
public static class ConvertCommands
{
    /// <summary>
    ///     Creates the 'convert' command for converting Xbox 360 ESM to PC format.
    /// </summary>
    public static Command CreateConvertCommand()
    {
        var inputArg = new Argument<string>("input") { Description = "Path to the Xbox 360 ESM file" };
        var outputOpt = new Option<string?>("-o", "--output") { Description = "Output path for converted PC ESM file" };
        var verboseOpt = new Option<bool>("-v", "--verbose") { Description = "Show detailed conversion progress" };
        var skipLandOpt = new Option<string[]>("--skip-land")
        {
            Description = "Skip LAND records by FormID (hex). Example: --skip-land 0x000E23E0",
            AllowMultipleArgumentsPerToken = true
        };
        var skipTypeOpt = new Option<string[]>("--skip-type")
        {
            Description = "Skip record types by signature (e.g., PACK, DIAL). Example: --skip-type PACK",
            AllowMultipleArgumentsPerToken = true
        };

        var command = new Command("convert", "Convert Xbox 360 ESM file to PC format");
        command.Arguments.Add(inputArg);
        command.Options.Add(outputOpt);
        command.Options.Add(verboseOpt);
        command.Options.Add(skipLandOpt);
        command.Options.Add(skipTypeOpt);

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputOpt);
            var verbose = parseResult.GetValue(verboseOpt);
            var skipLandIds = parseResult.GetValue(skipLandOpt) ?? Array.Empty<string>();
            var skipTypes = parseResult.GetValue(skipTypeOpt) ?? Array.Empty<string>();

            if (string.IsNullOrEmpty(output)) output = Environment.GetEnvironmentVariable("ESM_OUTPUT_PATH");
            if (string.IsNullOrEmpty(output)) output = Path.ChangeExtension(input, ".pc.esm");

            Convert(input, output, verbose);
        });

        return command;
    }

    /// <summary>
    ///     Main conversion method.
    /// </summary>
    private static void Convert(string inputPath, string outputPath, bool verbose)
    {
        if (!File.Exists(inputPath))
        {
            AnsiConsole.MarkupLine($"[red]Error: Input file not found: {inputPath}[/]");
            Environment.Exit(1);
            return;
        }

        AnsiConsole.MarkupLine($"[cyan]Converting:[/] {Path.GetFileName(inputPath)}");
        AnsiConsole.MarkupLine($"[cyan]Output:[/] {outputPath}");
        AnsiConsole.MarkupLine("[yellow]Using schema-driven conversion[/]");
        AnsiConsole.WriteLine();

        try
        {
            // Ensure output directory exists
            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);

            var inputData = File.ReadAllBytes(inputPath);

            // Verify it's an Xbox 360 ESM (big-endian)
            var header = EsmParser.ParseFileHeader(inputData);
            if (header == null || !header.IsBigEndian)
            {
                AnsiConsole.MarkupLine("[red]Error: Input file is not a valid Xbox 360 ESM (expected big-endian)[/]");
                Environment.Exit(1);
                return;
            }

            using var converter = new EsmConverter(inputData, verbose);
            var outputData = converter.ConvertToLittleEndian();

            File.WriteAllBytes(outputPath, outputData);

            AnsiConsole.MarkupLine("[green]✓ Conversion complete![/]");
            AnsiConsole.MarkupLine($"  Input size:  {inputData.Length:N0} bytes");
            AnsiConsole.MarkupLine($"  Output size: {outputData.Length:N0} bytes");

            // Show stats
            converter.PrintStats();
        }
        catch (NotSupportedException ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Conversion failed:[/] {ex.Message}");
            Environment.Exit(1);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ Conversion failed:[/] {ex.Message}");
            if (verbose) AnsiConsole.WriteException(ex);
            Environment.Exit(1);
        }
    }
}
