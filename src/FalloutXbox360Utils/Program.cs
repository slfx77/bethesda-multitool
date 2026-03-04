using System.CommandLine;
using System.Text;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core;
using Spectre.Console;

namespace FalloutXbox360Utils;

/// <summary>
///     Cross-platform CLI entry point for Fallout Xbox 360 Utils.
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
        // Detect plain output mode BEFORE any AnsiConsole usage.
        // Triggers: --plain flag, --no-ansi (compat), piped output, NO_COLOR env var.
        var plainMode = args.Contains("--plain", StringComparer.OrdinalIgnoreCase)
                        || args.Contains("--no-ansi", StringComparer.OrdinalIgnoreCase)
                        || Console.IsOutputRedirected
                        || Environment.GetEnvironmentVariable("NO_COLOR") != null;

        if (plainMode)
        {
            AnsiConsole.Profile.Capabilities.Ansi = false;
            AnsiConsole.Profile.Capabilities.Unicode = false;
            AnsiConsole.Profile.Capabilities.Links = false;
            Logger.Instance.UseSpectre = false;
        }

        // Strip flags before System.CommandLine sees them
        args = args.Where(a => !a.Equals("--plain", StringComparison.OrdinalIgnoreCase)
                            && !a.Equals("--no-ansi", StringComparison.OrdinalIgnoreCase))
                   .ToArray();

        Console.OutputEncoding = Encoding.UTF8;

        AnsiConsole.Write(
            new FigletText("Fallout 360 Utils")
                .Color(Color.Green));
        AnsiConsole.MarkupLine("[grey]Xbox 360 to PC Conversion Utilities - CLI Mode[/]");
        AnsiConsole.WriteLine();

        var rootCommand = BuildRootCommand();

        // Format-agnostic analysis commands (auto-detect file type)
        rootCommand.Subcommands.Add(SearchCommand.Create());
        rootCommand.Subcommands.Add(StatsCommand.Create());
        rootCommand.Subcommands.Add(ListCommand.Create());
        rootCommand.Subcommands.Add(ShowCommand.Create());
        rootCommand.Subcommands.Add(DiffCommand.Create());

        // Format-specific diagnostic commands
        rootCommand.Subcommands.Add(ConvertNifCommand.Create());
        rootCommand.Subcommands.Add(ConvertDdxCommand.Create());
        rootCommand.Subcommands.Add(EsmCommand.Create());
        rootCommand.Subcommands.Add(BsaCommand.Create());
        rootCommand.Subcommands.Add(DialogueCommand.Create());
        rootCommand.Subcommands.Add(WorldCommand.Create());
        rootCommand.Subcommands.Add(RepackCommand.Create());
        rootCommand.Subcommands.Add(RttiCommand.Create());
        rootCommand.Subcommands.Add(SaveCommand.Create());
        rootCommand.Subcommands.Add(DmpCommand.Create());
        rootCommand.Subcommands.Add(RenderCommand.Create());

        return rootCommand.Parse(args).Invoke();
    }

    private static RootCommand BuildRootCommand()
    {
        var rootCommand = new RootCommand("Fallout Xbox 360 to PC Conversion Utilities");

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
        var convertDdxOption = new Option<bool>("--convert-ddx", "--convert")
        {
            Description = "Enable format conversions (DDX -> DDS textures, XUR -> XUI interfaces)",
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
        var pcFriendlyOption = new Option<bool>("--pc-friendly", "-pc")
        {
            Description = "Enable PC-friendly normal map conversion (merges normal + specular maps)",
            DefaultValueFactory = _ => true
        };

        rootCommand.Arguments.Add(inputArgument);
        rootCommand.Options.Add(outputOption);
        rootCommand.Options.Add(noGuiOption);
        rootCommand.Options.Add(convertDdxOption);
        rootCommand.Options.Add(typesOption);
        rootCommand.Options.Add(verboseOption);
        rootCommand.Options.Add(maxFilesOption);
        rootCommand.Options.Add(pcFriendlyOption);

        rootCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            _ = cancellationToken; // Reserved for future use
            var input = parseResult.GetValue(inputArgument);
            var output = parseResult.GetValue(outputOption)!;
            var convertDdx = parseResult.GetValue(convertDdxOption);
            var types = parseResult.GetValue(typesOption);
            var verbose = parseResult.GetValue(verboseOption);
            var maxFiles = parseResult.GetValue(maxFilesOption);
            var pcFriendly = parseResult.GetValue(pcFriendlyOption);

            if (string.IsNullOrEmpty(input))
            {
                new System.CommandLine.Help.HelpAction().Invoke(parseResult);
                return 0;
            }

            if (!File.Exists(input) && !Directory.Exists(input))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Input path not found: {input}");
                return 1;
            }

            try
            {
                await CarveCommand.ExecuteAsync(input, output, types?.ToList(), convertDdx, verbose, maxFiles,
                    pcFriendly);
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
