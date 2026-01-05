using System.CommandLine;
using System.Text;
using Xbox360MemoryCarver.CLI;

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
    public static string? AutoLoadFile { get; internal set; }

    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

#if WINDOWS_GUI
        if (!IsCliMode(args))
        {
            AutoLoadFile = GetAutoLoadFile(args);
            return App.GuiEntryPoint.Run(args);
        }
#endif

        return await RunCliAsync(args);
    }

#if WINDOWS_GUI
    private static bool IsCliMode(string[] args)
    {
        return args.Length > 0 && (
            args.Any(a => a.Equals("--no-gui", StringComparison.OrdinalIgnoreCase)) ||
            args.Any(a => a.Equals("-n", StringComparison.OrdinalIgnoreCase)));
    }

    private static string? GetAutoLoadFile(string[] args)
    {
        var fileArg = GetFlagValue(args, "--file") ?? GetFlagValue(args, "-f");

        if (string.IsNullOrEmpty(fileArg) && args.Length > 0 && !args[0].StartsWith('-'))
        {
            var potentialFile = args[0];
            if (File.Exists(potentialFile) && potentialFile.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
            {
                return potentialFile;
            }
        }

        return fileArg;
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
#endif

    private static async Task<int> RunCliAsync(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("Xbox 360 Memory Carver - CLI Mode");
        Console.WriteLine("=================================");

        var rootCommand = BuildRootCommand();

        rootCommand.AddCommand(AnalyzeCommand.Create());
        rootCommand.AddCommand(ModulesCommand.Create());

        return await rootCommand.InvokeAsync(args);
    }

    private static RootCommand BuildRootCommand()
    {
        var rootCommand = new RootCommand("Xbox 360 Memory Dump File Carver");

        var inputArgument =
            new Argument<string?>("input", () => null, "Path to memory dump file (.dmp) or DDX file/directory");
        var outputOption = new Option<string>(["-o", "--output"], () => "output", "Output directory for carved files");
        var noGuiOption = new Option<bool>(["-n", "--no-gui"], "Run in command-line mode without GUI (Windows only)");
        var ddxOption = new Option<bool>(["--ddx"], "Convert DDX textures to DDS format instead of carving");
        var convertDdxOption = new Option<bool>(["--convert-ddx"], () => true,
            "Automatically convert carved DDX textures to DDS format");
        var typesOption = new Option<string[]>(["-t", "--types"], "File types to extract (e.g., dds ddx xma nif)");
        var verboseOption = new Option<bool>(["-v", "--verbose"], "Enable verbose output");
        var maxFilesOption = new Option<int>(["--max-files"], () => 10000, "Maximum files to extract per type");

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
                PrintUsage();
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
                await CarveCommand.ExecuteAsync(input, output, types?.ToList(), convertDdx, verbose, maxFiles);
                context.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                if (verbose) Console.WriteLine(ex.StackTrace);

                context.ExitCode = 1;
            }
        });

        return rootCommand;
    }

    private static void PrintUsage()
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
    }
}
