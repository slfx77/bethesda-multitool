using System.CommandLine;
using Spectre.Console;
using FalloutXbox360Utils.Repack;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI command for repacking Xbox 360 Fallout: New Vegas to PC format.
/// </summary>
public static class RepackCommand
{
    public static Command Create()
    {
        var command = new Command("repack", "Convert Xbox 360 Fallout: New Vegas installation to PC format");

        var sourceArg = new Argument<string>("source") { Description = "Path to Xbox 360 game folder" };
        var outputArg = new Argument<string>("output") { Description = "Path to output folder" };

        var videoOpt = new Option<bool>("--video")
        {
            Description = "Process video files (BIK)",
            DefaultValueFactory = _ => true
        };
        var musicOpt = new Option<bool>("--music")
        {
            Description = "Process music files (XMA to MP3)",
            DefaultValueFactory = _ => true
        };
        var bsaOpt = new Option<bool>("--bsa")
        {
            Description = "Process BSA archives",
            DefaultValueFactory = _ => true
        };
        var esmOpt = new Option<bool>("--esm")
        {
            Description = "Process ESM master files",
            DefaultValueFactory = _ => true
        };
        var espOpt = new Option<bool>("--esp")
        {
            Description = "Process ESP plugin files",
            DefaultValueFactory = _ => true
        };
        var verboseOpt = new Option<bool>("-v", "--verbose") { Description = "Show detailed progress" };

        command.Arguments.Add(sourceArg);
        command.Arguments.Add(outputArg);
        command.Options.Add(videoOpt);
        command.Options.Add(musicOpt);
        command.Options.Add(bsaOpt);
        command.Options.Add(esmOpt);
        command.Options.Add(espOpt);
        command.Options.Add(verboseOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var source = parseResult.GetValue(sourceArg)!;
            var output = parseResult.GetValue(outputArg)!;
            var processVideo = parseResult.GetValue(videoOpt);
            var processMusic = parseResult.GetValue(musicOpt);
            var processBsa = parseResult.GetValue(bsaOpt);
            var processEsm = parseResult.GetValue(esmOpt);
            var processEsp = parseResult.GetValue(espOpt);
            var verbose = parseResult.GetValue(verboseOpt);

            await ExecuteAsync(source, output, processVideo, processMusic, processBsa, processEsm, processEsp, verbose,
                cancellationToken);
        });

        return command;
    }

    private static async Task ExecuteAsync(
        string source,
        string output,
        bool processVideo,
        bool processMusic,
        bool processBsa,
        bool processEsm,
        bool processEsp,
        bool verbose,
        CancellationToken cancellationToken)
    {
        // Validate source folder
        AnsiConsole.MarkupLine("[blue]Validating source folder...[/]");
        var validation = RepackerService.ValidateSourceFolder(source);

        if (!validation.IsValid)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] {0}", validation.Message);
            return;
        }

        AnsiConsole.MarkupLine("[green]✓[/] {0}", validation.Message);

        // Show source info
        var sourceInfo = RepackerService.GetSourceInfo(source);
        AnsiConsole.MarkupLine("");
        AnsiConsole.MarkupLine("[blue]Source contents:[/]");
        AnsiConsole.MarkupLine("  Video files:  {0}", sourceInfo.VideoFiles);
        AnsiConsole.MarkupLine("  Music files:  {0}", sourceInfo.MusicFiles);
        AnsiConsole.MarkupLine("  BSA files:    {0}", sourceInfo.BsaFiles);
        AnsiConsole.MarkupLine("  ESM files:    {0}", sourceInfo.EsmFiles);
        AnsiConsole.MarkupLine("  ESP files:    {0}", sourceInfo.EspFiles);
        AnsiConsole.MarkupLine("  [bold]Total:        {0}[/]", sourceInfo.TotalFiles);
        AnsiConsole.MarkupLine("");

        // Build options
        var options = new RepackerOptions
        {
            SourceFolder = source,
            OutputFolder = output,
            ProcessVideo = processVideo,
            ProcessMusic = processMusic,
            ProcessBsa = processBsa,
            ProcessEsm = processEsm,
            ProcessEsp = processEsp
        };

        // Run conversion with progress
        var repackerService = new RepackerService();

        await AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var currentTask = ctx.AddTask("[blue]Starting...[/]", maxValue: 100);
                var lastPhase = RepackPhase.Validating;

                var progress = new Progress<RepackerProgress>(p =>
                {
                    // Update task description when phase changes
                    if (p.Phase != lastPhase)
                    {
                        lastPhase = p.Phase;
                        currentTask.Description = p.Phase switch
                        {
                            RepackPhase.Validating => "[blue]Validating...[/]",
                            RepackPhase.Video => "[yellow]Processing video files...[/]",
                            RepackPhase.Music => "[yellow]Processing music files...[/]",
                            RepackPhase.Bsa => "[yellow]Processing BSA archives...[/]",
                            RepackPhase.Esm => "[yellow]Processing ESM files...[/]",
                            RepackPhase.Esp => "[yellow]Processing ESP files...[/]",
                            RepackPhase.Complete => p.Success ? "[green]Complete[/]" : "[red]Failed[/]",
                            _ => "[blue]Processing...[/]"
                        };
                    }

                    // Update progress
                    if (p.TotalItems > 0)
                    {
                        currentTask.Value = (double)p.ItemsProcessed / p.TotalItems * 100;
                    }

                    // Verbose output
                    if (verbose && !string.IsNullOrEmpty(p.CurrentItem))
                    {
                        AnsiConsole.MarkupLine("[dim]{0}[/]", Markup.Escape(p.CurrentItem));
                    }
                });

                var result = await repackerService.RepackAsync(options, progress, cancellationToken);

                currentTask.Value = 100;

                // Show result
                AnsiConsole.MarkupLine("");
                if (result.Success)
                {
                    AnsiConsole.MarkupLine("[green]═══════════════════════════════════════════════════════════════[/]");
                    AnsiConsole.MarkupLine("[green]                    Conversion Complete                        [/]");
                    AnsiConsole.MarkupLine("[green]═══════════════════════════════════════════════════════════════[/]");
                    AnsiConsole.MarkupLine("");
                    AnsiConsole.MarkupLine("  Video files processed:  {0}", result.VideoFilesProcessed);
                    AnsiConsole.MarkupLine("  Music files processed:  {0}", result.MusicFilesProcessed);
                    AnsiConsole.MarkupLine("  BSA files processed:    {0}", result.BsaFilesProcessed);
                    AnsiConsole.MarkupLine("  ESM files processed:    {0}", result.EsmFilesProcessed);
                    AnsiConsole.MarkupLine("  ESP files processed:    {0}", result.EspFilesProcessed);
                    AnsiConsole.MarkupLine("  [bold green]Total:                  {0}[/]", result.TotalFilesProcessed);
                    AnsiConsole.MarkupLine("");
                    AnsiConsole.MarkupLine("[green]Output folder:[/] {0}", output);
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]═══════════════════════════════════════════════════════════════[/]");
                    AnsiConsole.MarkupLine("[red]                    Conversion Failed                          [/]");
                    AnsiConsole.MarkupLine("[red]═══════════════════════════════════════════════════════════════[/]");
                    AnsiConsole.MarkupLine("");
                    AnsiConsole.MarkupLine("[red]Error:[/] {0}", result.Error ?? "Unknown error");
                }
            });
    }
}
