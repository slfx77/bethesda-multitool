using System;
using System.CommandLine;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Xbox360MemoryCarver.Core.Carving;

namespace Xbox360MemoryCarver.App;

/// <summary>
/// Entry point that supports both GUI and CLI modes.
/// Use --no-gui for command-line carving without launching the UI.
/// </summary>
public static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    private const int ATTACH_PARENT_PROCESS = -1;

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        // Check if running in CLI mode
        if (args.Length > 0 && (HasFlag(args, "--no-gui") || HasFlag(args, "-n")))
        {
            return await RunCliAsync(args);
        }

        // GUI mode - launch WinUI app
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });

        return 0;
    }

    private static bool HasFlag(string[] args, string flag)
    {
        foreach (var arg in args)
        {
            if (arg.Equals(flag, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static async Task<int> RunCliAsync(string[] args)
    {
        // Attach to console for output
        if (!AttachConsole(ATTACH_PARENT_PROCESS))
        {
            AllocConsole();
        }

        Console.OutputEncoding = System.Text.Encoding.UTF8;
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

        // No GUI flag (required for CLI mode)
        var noGuiOption = new Option<bool>(
            ["-n", "--no-gui"],
            "Run in command-line mode without GUI");

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

        rootCommand.SetHandler(async (context) =>
        {
            var input = context.ParseResult.GetValueForArgument(inputArgument);
            var output = context.ParseResult.GetValueForOption(outputOption)!;
            var ddxMode = context.ParseResult.GetValueForOption(ddxOption);
            var convertDdx = context.ParseResult.GetValueForOption(convertDdxOption);
            var types = context.ParseResult.GetValueForOption(typesOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var maxFiles = context.ParseResult.GetValueForOption(maxFilesOption);

            if (string.IsNullOrEmpty(input))
            {
                Console.WriteLine("Error: Input path is required for CLI mode.");
                Console.WriteLine("Usage: Xbox360MemoryCarver.App --no-gui <input.dmp> -o <output_dir>");
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
                    Console.WriteLine(ex.StackTrace);
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
                maxFilesPerType: maxFiles,
                convertDdxToDds: convertDdx,
                fileTypes: fileTypes,
                verbose: verbose);

            var progress = new Progress<double>(p =>
            {
                if (verbose)
                    Console.Write($"\rProgress: {p * 100:F1}%");
            });

            var startTime = DateTime.Now;
            var results = await carver.CarveDumpAsync(file, progress);
            var elapsed = DateTime.Now - startTime;

            Console.WriteLine();
            Console.WriteLine($"Extracted {results.Count} files in {elapsed.TotalSeconds:F2}s");

            // Print stats
            Console.WriteLine();
            Console.WriteLine("File type summary:");
            foreach (var (type, count) in carver.Stats.OrderByDescending(x => x.Value))
            {
                if (count > 0)
                    Console.WriteLine($"  {type}: {count}");
            }

            if (convertDdx && carver.DdxConvertedCount > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"DDX conversions: {carver.DdxConvertedCount} successful, {carver.DdxConvertFailedCount} failed");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Done!");
    }
}
