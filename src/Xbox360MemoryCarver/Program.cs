using System.CommandLine;
using System.CommandLine.Invocation;
using Spectre.Console;
using Xbox360MemoryCarver.Carving;
using Xbox360MemoryCarver.Compression;
using Xbox360MemoryCarver.Converters;
using Xbox360MemoryCarver.Reporting;

namespace Xbox360MemoryCarver;

/// <summary>
/// Xbox 360 Memory Dump File Carver &amp; DDX Converter
/// 
/// A comprehensive tool for extracting usable data from Xbox 360 memory dumps
/// and converting DDX texture files to DDS format.
/// </summary>
public static class Program
{
    public const string Version = "2.0.0";

    public static async Task<int> Main(string[] args)
    {
        // Ensure console uses UTF-8 for proper box-drawing characters
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var rootCommand = new RootCommand("Xbox 360 Memory Dump File Carver & DDX Converter");

        // Input path argument
        var inputArgument = new Argument<string>(
            "input",
            "Path to memory dump file (.dmp) or directory containing dumps, or DDX file/directory");

        // Output directory option
        var outputOption = new Option<string>(
            ["-o", "--output"],
            () => "output",
            "Output directory for carved files");

        // DDX conversion mode
        var ddxOption = new Option<bool>(
            ["--ddx"],
            "Convert DDX textures to DDS format instead of carving");

        // Auto-convert DDX to DDS during carving
        var convertDdxOption = new Option<bool>(
            ["--convert-ddx"],
            () => true,
            "Automatically convert carved DDX textures to DDS format (default: on)");

        // File types to extract
        var typesOption = new Option<string[]>(
            ["-t", "--types"],
            "File types to extract (e.g., dds ddx xma nif)");

        // Verbose output
        var verboseOption = new Option<bool>(
            ["-v", "--verbose"],
            "Enable verbose output");

        // Skip endian swap when untile BC blocks
        var skipEndianOption = new Option<bool>(
            ["--skip-endian"],
            "Do not swap byte order during untile (useful for testing)"
        );

        // Save raw decompressed data before untiling (debugging aid)
        var saveRawOption = new Option<bool>(
            ["--save-raw"],
            "Save raw decompressed data before untiling (for debugging)"
        );

        // Chunk size for processing
        var chunkSizeOption = new Option<int>(
            ["--chunk-size"],
            () => 10 * 1024 * 1024,
            "Chunk size in bytes for processing large dumps");

        // Max files per type
        var maxFilesOption = new Option<int>(
            ["--max-files"],
            () => 10000,
            "Maximum files to extract per type");

        rootCommand.AddArgument(inputArgument);
        rootCommand.AddOption(outputOption);
        rootCommand.AddOption(ddxOption);
        rootCommand.AddOption(convertDdxOption);
        rootCommand.AddOption(typesOption);
        rootCommand.AddOption(verboseOption);
        rootCommand.AddOption(skipEndianOption);
        rootCommand.AddOption(saveRawOption);
        rootCommand.AddOption(chunkSizeOption);
        rootCommand.AddOption(maxFilesOption);

        rootCommand.SetHandler(async (context) =>
        {
            var input = context.ParseResult.GetValueForArgument(inputArgument);
            var output = context.ParseResult.GetValueForOption(outputOption)!;
            var ddxMode = context.ParseResult.GetValueForOption(ddxOption);
            var convertDdx = context.ParseResult.GetValueForOption(convertDdxOption);
            var types = context.ParseResult.GetValueForOption(typesOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var skipEndian = context.ParseResult.GetValueForOption(skipEndianOption);
            var saveRaw = context.ParseResult.GetValueForOption(saveRawOption);
            var chunkSize = context.ParseResult.GetValueForOption(chunkSizeOption);
            var maxFiles = context.ParseResult.GetValueForOption(maxFilesOption);

            PrintBanner();

            try
            {
                if (ddxMode)
                {
                    await RunDdxConversion(input, output, verbose, skipEndian, saveRaw);
                }
                else
                {
                    await RunCarving(input, output, types, chunkSize, maxFiles, convertDdx);
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.WriteException(ex);
                context.ExitCode = 1;
            }
        });

        return await rootCommand.InvokeAsync(args);
    }

    private static void PrintBanner()
    {
        AnsiConsole.Write(new FigletText("Xbox360 Carver")
            .LeftJustified()
            .Color(Color.Green));

        AnsiConsole.MarkupLine($"[grey]Version {Version}[/]");
        AnsiConsole.WriteLine();
    }

    private static async Task RunCarving(string input, string output, string[]? types, int chunkSize, int maxFiles, bool convertDdx)
    {
        var inputPath = Path.GetFullPath(input);

        if (!File.Exists(inputPath) && !Directory.Exists(inputPath))
        {
            AnsiConsole.MarkupLine($"[red]Error: Input path not found: {inputPath}[/]");
            return;
        }

        var outputPath = Path.GetFullPath(output);
        Directory.CreateDirectory(outputPath);

        var carver = new MemoryCarver(outputPath, chunkSize, maxFiles, convertDdx);
        var reportGenerator = new ReportGenerator(outputPath);

        // Get list of files to process
        var dumpFiles = GetDumpFiles(inputPath);

        if (dumpFiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No .dmp files found to process[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[blue]Found {dumpFiles.Count} dump file(s) to process[/]");
        if (convertDdx)
        {
            AnsiConsole.MarkupLine("[green]DDX→DDS auto-conversion enabled[/]");
        }
        AnsiConsole.WriteLine();

        foreach (var dumpFile in dumpFiles)
        {
            AnsiConsole.MarkupLine($"[green]Processing:[/] {Path.GetFileName(dumpFile)}");

            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new RemainingTimeColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask($"Carving {Path.GetFileName(dumpFile)}");

                    var progress = new Progress<double>(value => task.Value = value * 100);

                    var manifest = await carver.CarveDumpAsync(dumpFile, types?.ToList(), progress);

                    task.Value = 100;

                    // Generate report
                    reportGenerator.SetDumpInfo(dumpFile, new FileInfo(dumpFile).Length);
                    reportGenerator.AddCarvedFiles(manifest);
                    reportGenerator.CalculateCoverage(manifest);
                });

            // Print statistics
            carver.PrintStats();
            AnsiConsole.WriteLine();
        }

        // Save report
        var reportPath = Path.Combine(outputPath, "extraction_report.txt");
        await File.WriteAllTextAsync(reportPath, reportGenerator.GenerateTextReport());
        AnsiConsole.MarkupLine($"[blue]Report saved to:[/] {reportPath}");
    }

    private static async Task RunDdxConversion(string input, string output, bool verbose, bool skipEndian, bool saveRaw)
    {
        var inputPath = Path.GetFullPath(input);
        var outputPath = Path.GetFullPath(output);
        Directory.CreateDirectory(outputPath);

        var ddxFiles = GetDdxFiles(inputPath);
        if (ddxFiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No DDX files found to convert[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[blue]Found {ddxFiles.Count} DDX file(s) to convert[/]");

        var subprocessConverter = TryCreateSubprocessConverter(verbose);
        AnsiConsole.WriteLine();

        var basePath = File.Exists(inputPath) ? Path.GetDirectoryName(inputPath)! : inputPath;
        var conversionOptions = new ConversionOptions { Verbose = verbose, SkipEndianSwap = skipEndian, SaveRaw = saveRaw };

        var (successCount, failCount) = await ConvertDdxFilesWithProgressAsync(
            ddxFiles, basePath, outputPath, subprocessConverter, conversionOptions, verbose);

        PrintConversionSummary(successCount, failCount, subprocessConverter);
    }

    private static DdxSubprocessConverter? TryCreateSubprocessConverter(bool verbose)
    {
        try
        {
            var converter = new DdxSubprocessConverter(verbose);
            AnsiConsole.MarkupLine($"[green]Using DDXConv subprocess:[/] {converter.DdxConvPath}");
            return converter;
        }
        catch (FileNotFoundException)
        {
            AnsiConsole.MarkupLine("[yellow]DDXConv.exe not found, using managed converter (may have limited functionality)[/]");
            return null;
        }
    }

    private static async Task<(int success, int fail)> ConvertDdxFilesWithProgressAsync(
        List<string> ddxFiles,
        string basePath,
        string outputPath,
        DdxSubprocessConverter? subprocessConverter,
        ConversionOptions options,
        bool verbose)
    {
        var successCount = 0;
        var failCount = 0;

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("Converting DDX files");
                task.MaxValue = ddxFiles.Count;

                foreach (var ddxFile in ddxFiles)
                {
                    var success = await ConvertSingleDdxFileAsync(
                        ddxFile, basePath, outputPath, subprocessConverter, options, verbose);

                    if (success) successCount++;
                    else failCount++;

                    task.Increment(1);
                }
            });

        return (successCount, failCount);
    }

    private static async Task<bool> ConvertSingleDdxFileAsync(
        string ddxFile,
        string basePath,
        string outputPath,
        DdxSubprocessConverter? subprocessConverter,
        ConversionOptions options,
        bool verbose)
    {
        var relativePath = Path.GetRelativePath(basePath, ddxFile);
        var ddsPath = Path.Combine(outputPath, Path.ChangeExtension(relativePath, ".dds"));
        Directory.CreateDirectory(Path.GetDirectoryName(ddsPath)!);

        try
        {
            var success = subprocessConverter != null
                ? await subprocessConverter.ConvertFileAsync(ddxFile, ddsPath)
                : await new DdxConverter(options.Verbose, options).ConvertFileAsync(ddxFile, ddsPath);

            if (verbose)
                AnsiConsole.MarkupLine(success ? $"[green]✓[/] {relativePath}" : $"[red]✗[/] {relativePath}");

            return success;
        }
        catch (Exception ex)
        {
            if (verbose)
                AnsiConsole.MarkupLine($"[red]✗[/] {relativePath}: {ex.Message}");
            return false;
        }
    }

    private static void PrintConversionSummary(int successCount, int failCount, DdxSubprocessConverter? subprocessConverter)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]Success:[/] {successCount}  [red]Failed:[/] {failCount}");

        if (subprocessConverter != null)
            AnsiConsole.MarkupLine($"DDX conversion: {subprocessConverter.Succeeded} succeeded, {subprocessConverter.Failed} failed, {subprocessConverter.Processed} total");
    }

    private static List<string> GetDumpFiles(string path)
    {
        if (File.Exists(path))
        {
            return [path];
        }

        if (Directory.Exists(path))
        {
            return Directory.GetFiles(path, "*.dmp", SearchOption.AllDirectories).ToList();
        }

        return [];
    }

    private static List<string> GetDdxFiles(string path)
    {
        if (File.Exists(path) && path.EndsWith(".ddx", StringComparison.OrdinalIgnoreCase))
        {
            return [path];
        }

        if (Directory.Exists(path))
        {
            return Directory.GetFiles(path, "*.ddx", SearchOption.AllDirectories).ToList();
        }

        return [];
    }
}
