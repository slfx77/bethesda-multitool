using System.CommandLine;
using System.CommandLine.Help;
using System.Text;
using BethesdaMultitool.CLI;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core;
using Spectre.Console;

namespace BethesdaMultitool;

/// <summary>
///     Cross-platform CLI entry point for Bethesda Multitool.
///     On Windows with GUI build, this delegates to the GUI app unless --no-gui is specified.
/// </summary>
public static class Program
{
    /// <summary>
    ///     File path to auto-load when GUI starts (set via --file parameter).
    /// </summary>
    public static string? AutoLoadFile { get; internal set; }

    /// <summary>
    ///     Sub-view to auto-open after the auto-loaded file finishes analyzing (set via --view).
    ///     E.g. <c>--view worldmap</c> boots straight into the 2D world map, so an automated /
    ///     instrumented run reaches the map without manual tab navigation. Null = stay on the
    ///     default tab.
    /// </summary>
    public static string? AutoOpenView { get; internal set; }

    /// <summary>
    ///     Worldspace name substring to auto-select on the world map (set via --worldspace, e.g.
    ///     <c>--worldspace WastelandNV</c>). When unset, the auto-open picks the densest worldspace
    ///     (most cells) so it never lands on an empty test worldspace.
    /// </summary>
    public static string? AutoOpenWorldspace { get; internal set; }

    /// <summary>
    ///     World-map layer to auto-select (set via --layer, e.g. <c>--layer textures</c>). Names:
    ///     textures, heightmap, vertexcolors, regions, slope. Null = leave the default layer.
    /// </summary>
    public static string? AutoOpenLayer { get; internal set; }

    /// <summary>When <c>--rendered-models</c> is passed, turns on the top-down "Rendered models" overlay
    /// once the world map is up (drives the top-down render path for repro/perf testing).</summary>
    public static bool AutoRenderedModels { get; internal set; }

    /// <summary>When <c>--repro-layer-toggle</c> is passed, after the world map is set up the auto-open
    /// switches to Heightmap then back to the requested layer (with state logging) — the repro for the
    /// "textures work until you toggle layers and back" bug.</summary>
    public static bool AutoReproLayerToggle { get; internal set; }

    [STAThread]
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

#if WINDOWS_GUI
        if (!IsCliMode(args))
        {
            AutoLoadFile = GetAutoLoadFile(args);
            AutoOpenView = GetFlagValue(args, "--view");
            AutoOpenWorldspace = GetFlagValue(args, "--worldspace");
            AutoOpenLayer = GetFlagValue(args, "--layer");
            AutoRenderedModels = args.Any(a => a.Equals("--rendered-models", StringComparison.OrdinalIgnoreCase));
            AutoReproLayerToggle = args.Any(a => a.Equals("--repro-layer-toggle", StringComparison.OrdinalIgnoreCase));
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

        // End-of-run resource statistics: --resource-stats flag or FALLOUT_RESOURCE_STATS=1.
        var resourceStats = args.Contains("--resource-stats", StringComparer.OrdinalIgnoreCase)
                            || EnvironmentVariables.IsEnabled(EnvironmentVariables.Diagnostics.ResourceStats);

        // Strip flags before System.CommandLine sees them
        args = args.Where(a => !a.Equals("--plain", StringComparison.OrdinalIgnoreCase)
                               && !a.Equals("--no-ansi", StringComparison.OrdinalIgnoreCase)
                               && !a.Equals("--resource-stats", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Console.OutputEncoding = Encoding.UTF8;

        if (!plainMode)
        {
            AnsiConsole.Write(
                new FigletText("Bethesda Multitool")
                    .Color(Color.Green));
            AnsiConsole.MarkupLine("[grey]Analyze and convert assets across Bethesda games - CLI Mode[/]");
            AnsiConsole.WriteLine();
        }

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
        rootCommand.Subcommands.Add(ArchiveCommand.Create());
        rootCommand.Subcommands.Add(BsaCommand.Create()); // deprecated alias of 'archive'
        rootCommand.Subcommands.Add(Ba2Command.Create()); // deprecated alias of 'archive'
        rootCommand.Subcommands.Add(BtdCommand.Create());
        rootCommand.Subcommands.Add(DialogueCommand.Create());
        rootCommand.Subcommands.Add(PapyrusCommand.Create());
        rootCommand.Subcommands.Add(WorldCommand.Create());
        rootCommand.Subcommands.Add(RepackCommand.Create());
        rootCommand.Subcommands.Add(RttiCommand.Create());
        rootCommand.Subcommands.Add(SaveCommand.Create());
        rootCommand.Subcommands.Add(DmpCommand.Create());
        rootCommand.Subcommands.Add(RenderCommand.Create());
        rootCommand.Subcommands.Add(ExportCommand.Create());
        rootCommand.Subcommands.Add(AnalyzeCommand.Create());
        rootCommand.Subcommands.Add(ReportCommand.Create());
        rootCommand.Subcommands.Add(CLI.Commands.Version.VersionTrackCommand.Create());

        var exitCode = rootCommand.Parse(args).Invoke();

        // Single hook covering every command: print the resource table after the command finishes,
        // when opted in and anything actually registered during the run.
        if (resourceStats)
        {
            CLI.Shared.CliResourceStatsReporter.WriteIfAnyRegistered(
                Core.Diagnostics.ResourceRegistry.Instance);
        }

        return exitCode;
    }

    private static RootCommand BuildRootCommand()
    {
        var rootCommand = new RootCommand(
            "Bethesda Multitool - analyze and convert game assets across Bethesda titles (Morrowind through Starfield)");

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
                new HelpAction().Invoke(parseResult);
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
