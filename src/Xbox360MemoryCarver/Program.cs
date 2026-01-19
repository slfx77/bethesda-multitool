using System.CommandLine;
using System.Text;
using Spectre.Console;
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
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

#if WINDOWS_GUI
        if (!IsCliMode(args))
        {
            AutoLoadFile = GetAutoLoadFile(args);
            return GuiEntryPoint.Run(args);
        }
#endif

        return RunCli(args);
    }

    private static int RunCli(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        AnsiConsole.Write(
            new FigletText("Xbox360 Carver")
                .Color(Color.Green));
        AnsiConsole.MarkupLine("[grey]Memory Dump File Carver - CLI Mode[/]");
        AnsiConsole.WriteLine();

        var rootCommand = BuildRootCommand();

        rootCommand.Subcommands.Add(AnalyzeCommand.Create());
        rootCommand.Subcommands.Add(ModulesCommand.Create());
        rootCommand.Subcommands.Add(ConvertNifCommand.Create());
        rootCommand.Subcommands.Add(EsmCommand.Create());
        rootCommand.Subcommands.Add(BsaCommand.Create());

        return rootCommand.Parse(args).Invoke();
    }

    private static RootCommand BuildRootCommand()
    {
        var rootCommand = new RootCommand("Xbox 360 Memory Dump File Carver");

        var inputArgument = new Argument<string?>("input")
        {
            Description = "Path to memory dump file (.dmp) or DDX file/directory",
            DefaultValueFactory = _ => null
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for carved files",
            DefaultValueFactory = _ => "output"
        };
        var noGuiOption = new Option<bool>("-n", "--no-gui")
        {
            Description = "Run in command-line mode without GUI (Windows only)"
        };
        var ddxOption = new Option<bool>("--ddx")
        {
            Description = "Convert DDX textures to DDS format instead of carving"
        };
        var convertDdxOption = new Option<bool>("--convert-ddx", "--convert")
        {
            Description = "Enable format conversions (DDX→DDS textures, XUR→XUI interfaces)",
            DefaultValueFactory = _ => true
        };
        var typesOption = new Option<string[]>("-t", "--types")
        {
            Description = "File types to extract (e.g., dds ddx xma nif)"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };
        var maxFilesOption = new Option<int>("--max-files")
        {
            Description = "Maximum files to extract per type",
            DefaultValueFactory = _ => 10000
        };

        rootCommand.Arguments.Add(inputArgument);
        rootCommand.Options.Add(outputOption);
        rootCommand.Options.Add(noGuiOption);
        rootCommand.Options.Add(ddxOption);
        rootCommand.Options.Add(convertDdxOption);
        rootCommand.Options.Add(typesOption);
        rootCommand.Options.Add(verboseOption);
        rootCommand.Options.Add(maxFilesOption);

        rootCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            _ = cancellationToken; // Reserved for future use
            var input = parseResult.GetValue(inputArgument);
            var output = parseResult.GetValue(outputOption)!;
            var convertDdx = parseResult.GetValue(convertDdxOption);
            var types = parseResult.GetValue(typesOption);
            var verbose = parseResult.GetValue(verboseOption);
            var maxFiles = parseResult.GetValue(maxFilesOption);

            if (string.IsNullOrEmpty(input))
            {
                PrintUsage();
                return 1;
            }

            if (!File.Exists(input) && !Directory.Exists(input))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Input path not found: {input}");
                return 1;
            }

            try
            {
                await CarveCommand.ExecuteAsync(input, output, types?.ToList(), convertDdx, verbose, maxFiles);
                return 0;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
                if (verbose)
                {
                    AnsiConsole.WriteException(ex);
                }

                return 1;
            }
        });

        return rootCommand;
    }

    private static void PrintUsage()
    {
        AnsiConsole.MarkupLine("[red]Error:[/] Input path is required.");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Usage:[/]");
        AnsiConsole.MarkupLine("  Xbox360MemoryCarver [green]<input.dmp>[/] -o [blue]<output_dir>[/] [options]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Examples:[/]");
        AnsiConsole.MarkupLine("  Xbox360MemoryCarver [green]dump.dmp[/] -o [blue]extracted[/]");
        AnsiConsole.MarkupLine("  Xbox360MemoryCarver [green]dump.dmp[/] -o [blue]extracted[/] -t ddx xma nif");
        AnsiConsole.MarkupLine("  Xbox360MemoryCarver [green]dump.dmp[/] -o [blue]extracted[/] --convert-ddx -v");
#if WINDOWS_GUI
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]For GUI mode:[/]");
        AnsiConsole.MarkupLine("  Xbox360MemoryCarver");
        AnsiConsole.MarkupLine("  Xbox360MemoryCarver --file [green]dump.dmp[/]");
#endif
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
                return potentialFile;
        }

        return fileArg;
    }

    private static string? GetFlagValue(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];

        return null;
    }
#endif
}
