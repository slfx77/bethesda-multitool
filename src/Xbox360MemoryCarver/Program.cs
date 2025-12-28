using System.CommandLine;
using System.Diagnostics;
using System.Text;
using Xbox360MemoryCarver.Core.Carving;

namespace Xbox360MemoryCarver;

/// <summary>
///     Cross-platform CLI entry point for Xbox 360 Memory Carver.
///     On Windows with GUI build, this delegates to the GUI app unless --no-gui is specified.
/// </summary>
public static class Program
{
    /// <summary>
    ///     File path to auto-load when GUI starts (set via --file parameter).
    /// </summary>
    public static string? AutoLoadFile { get; private set; }

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

#if WINDOWS_GUI
        // On Windows GUI build, check if we should launch GUI or CLI
        var isCliMode = args.Length > 0 && (HasFlag(args, "--no-gui") || HasFlag(args, "-n"));

        if (!isCliMode)
        {
            // Check for --file parameter for GUI mode
            AutoLoadFile = GetFlagValue(args, "--file") ?? GetFlagValue(args, "-f");

            // Also check for a single positional argument that's a .dmp file
            if (string.IsNullOrEmpty(AutoLoadFile) && args.Length > 0 && !args[0].StartsWith('-'))
            {
                var potentialFile = args[0];
                if (File.Exists(potentialFile) && potentialFile.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
                {
                    AutoLoadFile = potentialFile;
                }
            }

            return App.GuiEntryPoint.Run(args);
        }
#endif

        // CLI mode
        return await RunCliAsync(args);
    }

    private static bool HasFlag(string[] args, string flag)
    {
        return args.Any(arg => arg.Equals(flag, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetFlagValue(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static async Task<int> RunCliAsync(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("Xbox 360 Memory Carver - CLI Mode");
        Console.WriteLine("=================================");

        var rootCommand = new RootCommand("Xbox 360 Memory Dump File Carver & DDX Converter");

        // Input path argument
        var inputArgument = new Argument<string?>(
            "input",
            () => null,
            "Path to memory dump file (.dmp) or DDX file/directory");

        // Output directory option
        var outputOption = new Option<string>(
            ["-o", "--output"],
            () => "output",
            "Output directory for carved files");

        // No GUI flag (required for CLI mode on Windows)
        var noGuiOption = new Option<bool>(
            ["-n", "--no-gui"],
            "Run in command-line mode without GUI (Windows only)");

        // DDX conversion mode
        var ddxOption = new Option<bool>(
            ["--ddx"],
            "Convert DDX textures to DDS format instead of carving");

        // Auto-convert DDX to DDS during carving
        var convertDdxOption = new Option<bool>(
            ["--convert-ddx"],
            () => true,
            "Automatically convert carved DDX textures to DDS format");

        // File types to extract
        var typesOption = new Option<string[]>(
            ["-t", "--types"],
            "File types to extract (e.g., dds ddx xma nif)");

        // Verbose output
        var verboseOption = new Option<bool>(
            ["-v", "--verbose"],
            "Enable verbose output");

        // Max files per type
        var maxFilesOption = new Option<int>(
            ["--max-files"],
            () => 10000,
            "Maximum files to extract per type");

        rootCommand.AddArgument(inputArgument);
        rootCommand.AddOption(outputOption);
        rootCommand.AddOption(noGuiOption);
        rootCommand.AddOption(ddxOption);
        rootCommand.AddOption(convertDdxOption);
        rootCommand.AddOption(typesOption);
        rootCommand.AddOption(verboseOption);
        rootCommand.AddOption(maxFilesOption);

        rootCommand.SetHandler(async context =>
        {
            var input = context.ParseResult.GetValueForArgument(inputArgument);
            var output = context.ParseResult.GetValueForOption(outputOption)!;
            var convertDdx = context.ParseResult.GetValueForOption(convertDdxOption);
            var types = context.ParseResult.GetValueForOption(typesOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var maxFiles = context.ParseResult.GetValueForOption(maxFilesOption);

            if (string.IsNullOrEmpty(input))
            {
                Console.WriteLine("Error: Input path is required.");
                Console.WriteLine();
                Console.WriteLine("Usage:");
                Console.WriteLine("  Xbox360MemoryCarver <input.dmp> -o <output_dir> [options]");
                Console.WriteLine();
                Console.WriteLine("Examples:");
                Console.WriteLine("  Xbox360MemoryCarver dump.dmp -o extracted");
                Console.WriteLine("  Xbox360MemoryCarver dump.dmp -o extracted -t ddx xma nif");
                Console.WriteLine("  Xbox360MemoryCarver dump.dmp -o extracted --convert-ddx -v");
#if WINDOWS_GUI
                Console.WriteLine();
                Console.WriteLine("For GUI mode, run without arguments or with --file:");
                Console.WriteLine("  Xbox360MemoryCarver");
                Console.WriteLine("  Xbox360MemoryCarver --file dump.dmp");
#endif
                context.ExitCode = 1;
                return;
            }

            if (!File.Exists(input) && !Directory.Exists(input))
            {
                Console.WriteLine($"Error: Input path not found: {input}");
                context.ExitCode = 1;
                return;
            }

            try
            {
                await CarveFilesAsync(input, output, types?.ToList(), convertDdx, verbose, maxFiles);
                context.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                if (verbose)
                {
                    Console.WriteLine(ex.StackTrace);
                }

                context.ExitCode = 1;
            }
        });

        return await rootCommand.InvokeAsync(args);
    }

    private static async Task CarveFilesAsync(
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
            Console.WriteLine("No dump files found.");
            return;
        }

        Console.WriteLine($"Found {files.Count} file(s) to process");

        foreach (var file in files)
        {
            Console.WriteLine();
            Console.WriteLine($"Processing: {Path.GetFileName(file)}");
            Console.WriteLine(new string('-', 50));

            var carver = new MemoryCarver(
                outputDir,
                maxFiles,
                convertDdx,
                fileTypes,
                verbose);

            var progress = new Progress<double>(p =>
            {
                if (verbose)
                {
                    Console.Write($"\rProgress: {p * 100:F1}%");
                }
            });

            var stopwatch = Stopwatch.StartNew();
            var results = await carver.CarveDumpAsync(file, progress);
            stopwatch.Stop();

            Console.WriteLine();
            Console.WriteLine($"Extracted {results.Count} files in {stopwatch.Elapsed.TotalSeconds:F2}s");

            // Print stats
            Console.WriteLine();
            Console.WriteLine("File type summary:");
            foreach (var (type, count) in carver.Stats.OrderByDescending(x => x.Value))
            {
                if (count > 0)
                {
                    Console.WriteLine($"  {type}: {count}");
                }
            }

            if (convertDdx && (carver.DdxConvertedCount > 0 || carver.DdxConvertFailedCount > 0))
            {
                Console.WriteLine();
                Console.WriteLine(
                    $"DDX conversions: {carver.DdxConvertedCount} successful, {carver.DdxConvertFailedCount} failed");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Done!");
    }
}
