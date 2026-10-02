using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using System.Text;
using BethesdaMultitool.CLI.Commands.Audio;
using BethesdaMultitool.CLI.Commands.Classic;
using BethesdaMultitool.CLI.Commands.Diagnostics;
using BethesdaMultitool.CLI.Commands.Sprite;
using BethesdaMultitool.CLI.Commands.Video;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Diagnostics;
using Spectre.Console;

namespace BethesdaMultitool;

/// <summary>
///     Cross-platform CLI entry point for Bethesda Multitool.
///     On Windows with GUI build, this delegates to the GUI app unless a registered root command
///     or the explicit <c>--no-gui</c> compatibility switch selects the command-line host.
/// </summary>
public static class Program
{
    private static readonly HashSet<string> CliRootCommandNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "search",
        "stats",
        "list",
        "show",
        "refs",
        "diff",
        "convert-nif",
        "convert-ddx",
        "esm",
        "archive",
        "bsa",
        "ba2",
        "btd",
        "dialogue",
        "assets",
        "papyrus",
        "pex",
        "world",
        "repack",
        "rtti",
        "runtime",
        "save",
        "dmp",
        "render",
        "capture-shadowkey-native",
        "sprite",
        "classic",
        "audio",
        "lip",
        "tri",
        "video",
        "export",
        "mesh",
        "analyze",
        "report",
        "version-track",
        "build-shader-bytecode-pack"
    };

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
    ///     Exact actor selector used with <c>--view actors</c> (set via <c>--actor</c>). Accepts a
    ///     hexadecimal/decimal FormID or an exact, unique Editor ID/full name. Null leaves the actor
    ///     list unselected, preserving the normal interactive startup behavior.
    /// </summary>
    public static string? AutoOpenActor { get; internal set; }

    /// <summary>
    ///     Folder, archive or classic game root to open in the Asset Browser at startup (set via
    ///     <c>--asset-source</c>). Every one of the Asset Browser's own open routes ends in a native
    ///     picker, which scripted verification must never drive: that means synthetic input into
    ///     whatever window happens to have focus. This is the picker-free equivalent of <c>--file</c>.
    /// </summary>
    public static string? AutoAssetSource { get; internal set; }

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

    /// <summary>
    ///     When <c>--rendered-models</c> is passed, turns on the top-down "Rendered models" overlay
    ///     once the world map is up (drives the top-down render path for repro/perf testing).
    /// </summary>
    public static bool AutoRenderedModels { get; internal set; }

    /// <summary>
    ///     When <c>--repro-layer-toggle</c> is passed, after the world map is set up the auto-open
    ///     switches to Heightmap then back to the requested layer (with state logging) — the repro for the
    ///     "textures work until you toggle layers and back" bug.
    /// </summary>
    public static bool AutoReproLayerToggle { get; internal set; }

    [STAThread]
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

#if WINDOWS_GUI
        if (!ShouldRunCli(args))
        {
            AutoLoadFile = GetAutoLoadFile(args);
            AutoOpenView = GetFlagValue(args, "--view");
            AutoOpenActor = GetFlagValue(args, "--actor");
            AutoAssetSource = GetFlagValue(args, "--asset-source");
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
        // Spectre captures Console.Out on its first use. Set its encoding before that writer exists.
        Console.OutputEncoding = Encoding.UTF8;
        var originalArgs = args;
        var resourceStats = args.Contains("--resource-stats", StringComparer.OrdinalIgnoreCase)
                            || EnvironmentVariables.IsEnabled(EnvironmentVariables.Diagnostics.ResourceStats);
        args = args.Where(a => !a.Equals("--plain", StringComparison.OrdinalIgnoreCase)
                               && !a.Equals("--no-ansi", StringComparison.OrdinalIgnoreCase)
                               && !a.Equals("--resource-stats", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var parseResult = CreateCliCommand().Parse(args);
        // Detect plain output mode BEFORE any AnsiConsole usage.
        // Triggers: --plain flag, --no-ansi (compat), piped output, NO_COLOR env var.
        // A JSON document owns stdout, so a JSON request is also plain: no banner may precede it even on an
        // interactive terminal.
        var jsonOutput = RequestsJsonOutput(parseResult);
        var plainMode = IsPlainMode(originalArgs, Console.IsOutputRedirected,
            Environment.GetEnvironmentVariable("NO_COLOR") != null, jsonOutput);

        if (plainMode)
        {
            AnsiConsole.Profile.Capabilities.Ansi = false;
            AnsiConsole.Profile.Capabilities.Unicode = false;
            AnsiConsole.Profile.Capabilities.Links = false;
            Logger.Instance.UseSpectre = false;
        }
        if (jsonOutput) Logger.SetOutput(Console.Error);

        if (!plainMode)
        {
            AnsiConsole.Write(
                new FigletText("Bethesda Multitool")
                    .Color(Color.Green));
            AnsiConsole.MarkupLine("[grey]Analyze and convert assets across Bethesda games - CLI Mode[/]");
            AnsiConsole.WriteLine();
        }

        // Parse errors include help and typo suggestions: in JSON mode those diagnostics belong on stderr.
        using var cancellation = new CancellationTokenSource();
        void CancelInvocation(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        }
        // Register before invoking: an async handler can perform substantial synchronous work
        // before its first await. Also handles Ctrl+Break on Windows, alongside Ctrl+C.
        Console.CancelKeyPress += CancelInvocation;
        int exitCode;
        try
        {
            var configuration = new InvocationConfiguration();
            if (jsonOutput && parseResult.Errors.Count > 0)
            {
                configuration.Output = Console.Error;
                configuration.Error = Console.Error;
            }
            exitCode = parseResult.InvokeAsync(configuration, cancellation.Token).GetAwaiter().GetResult();
            exitCode = CLI.Commands.Mesh.MeshCommand.MapUsageExitCode(parseResult, exitCode);
            if (cancellation.IsCancellationRequested) exitCode = 130;
        }
        finally
        {
            Console.CancelKeyPress -= CancelInvocation;
        }

        if (resourceStats)
        {
            var stdoutConsole = AnsiConsole.Console;
            if (jsonOutput)
            {
                AnsiConsole.Console = CliConsoles.Stderr;
            }

            try
            {
                CliResourceStatsReporter.WriteIfAnyRegistered(ResourceRegistry.Instance);
            }
            finally
            {
                AnsiConsole.Console = stdoutConsole;
            }
        }

        return exitCode;
    }

    internal static bool IsPlainMode(IReadOnlyList<string> args, bool outputRedirected, bool noColor,
        bool jsonOutput)
    {
        return args.Contains("--plain", StringComparer.OrdinalIgnoreCase)
               || args.Contains("--no-ansi", StringComparer.OrdinalIgnoreCase)
               || outputRedirected || noColor || jsonOutput;
    }

    private static RootCommand CreateCliCommand()
    {
        var rootCommand = BuildRootCommand();

        // Format-agnostic analysis commands (auto-detect file type)
        rootCommand.Subcommands.Add(SearchCommand.Create());
        rootCommand.Subcommands.Add(StatsCommand.Create());
        rootCommand.Subcommands.Add(ListCommand.Create());
        rootCommand.Subcommands.Add(ShowCommand.Create());
        rootCommand.Subcommands.Add(RefsCommand.Create());
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
        rootCommand.Subcommands.Add(CLI.Commands.Assets.AssetsCommand.Create());
        rootCommand.Subcommands.Add(PapyrusCommand.Create());
        rootCommand.Subcommands.Add(WorldCommand.Create());
        rootCommand.Subcommands.Add(RepackCommand.Create());
        rootCommand.Subcommands.Add(RttiCommand.Create());
        rootCommand.Subcommands.Add(CLI.Commands.Runtime.RuntimeCommand.Create());
        rootCommand.Subcommands.Add(SaveCommand.Create());
        rootCommand.Subcommands.Add(DmpCommand.Create());
        rootCommand.Subcommands.Add(RenderCommand.Create());
        rootCommand.Subcommands.Add(ShadowkeyNativeCaptureCommand.Create());
        rootCommand.Subcommands.Add(SpriteCommand.Create());
        rootCommand.Subcommands.Add(ClassicCommand.Create());
        rootCommand.Subcommands.Add(AudioCommand.Create());
        rootCommand.Subcommands.Add(LipCommand.Create());
        rootCommand.Subcommands.Add(BethesdaMultitool.CLI.Commands.FaceGen.TriCommand.Create());
        rootCommand.Subcommands.Add(VideoCommand.Create());
        rootCommand.Subcommands.Add(ExportCommand.Create());
        rootCommand.Subcommands.Add(CLI.Commands.Mesh.MeshCommand.Create());
        rootCommand.Subcommands.Add(AnalyzeCommand.Create());
        rootCommand.Subcommands.Add(ReportCommand.Create());
        rootCommand.Subcommands.Add(VersionTrackCommand.Create());
        rootCommand.Subcommands.Add(
            BuildShaderBytecodePackCommand.Create());

        return rootCommand;
    }

    /// <summary>
    ///     Uses the parser's option identity and value, including aliases and colon/equal delimiters.
    ///     A string-valued --json output path or a -f filter/FormID is not a stdout JSON request.
    /// </summary>
    internal static bool RequestsJsonOutput(IReadOnlyList<string> args)
    {
        return RequestsJsonOutput(CreateCliCommand().Parse(args.ToArray()));
    }

    private static bool RequestsJsonOutput(ParseResult parseResult)
    {
        // These document commands own stdout even without a format switch. terminal-graph's DOT
        // alternative has the same banner/diagnostic routing contract as its default JSON output.
        if (parseResult.CommandResult.Command.Name is "actor-details" or "terminal-graph" or "audio-catalog") { return true; }
        foreach (var result in parseResult.CommandResult.Children.OfType<OptionResult>())
        {
            if (result.Option is Option<string> format &&
                (format.Name == "--format" || format.Aliases.Contains("--format")) &&
                result.Tokens.Any(token => token.Value.Equals("json", StringComparison.OrdinalIgnoreCase) ||
                    parseResult.CommandResult.Command.Name == "objects" &&
                    token.Value.Equals("csv", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (result.Option is Option<bool> json &&
                (json.Name == "--json" || json.Aliases.Contains("--json") ||
                 json.Name == "--samples" && parseResult.CommandResult.Command.Name == "inspect") &&
                result.GetValueOrDefault<bool>())
            {
                return true;
            }
        }

        return false;
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
            Description = "File types to extract (e.g., dds ddx xma nif)",

            // Without this, only the first value after -t binds; the rest become unmatched tokens,
            // which RootCommand rejects outright. The space-separated form this Description
            // advertises (and that README documents) would fail to parse.
            AllowMultipleArgumentsPerToken = true
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


    /// <summary>
    ///     Selects the command-line host when the caller either opts out of the GUI explicitly or
    ///     supplies a registered root command. A packaged Windows executable must not turn a valid
    ///     invocation such as <c>archive list ...</c> into an apparently hung, empty GUI merely
    ///     because the caller omitted the historical <c>--no-gui</c> compatibility flag.
    /// </summary>
    internal static bool ShouldRunCli(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Any(static argument =>
                argument.Equals("--no-gui", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("-n", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // These no-value CLI presentation/diagnostic switches may legally precede a subcommand.
        // Stop at any other option so values belonging to GUI flags (for example
        // `--actor archive`) cannot accidentally select the CLI host.
        foreach (var argument in args)
        {
            if (argument.Equals("--plain", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--no-ansi", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--resource-stats", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("--verbose", StringComparison.OrdinalIgnoreCase) ||
                argument.Equals("-v", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return !argument.StartsWith('-') && CliRootCommandNames.Contains(argument);
        }

        return false;
    }

#if WINDOWS_GUI
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
