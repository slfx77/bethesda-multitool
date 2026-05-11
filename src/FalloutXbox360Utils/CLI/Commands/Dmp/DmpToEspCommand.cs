using System.CommandLine;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Dmp;

/// <summary>
///     CLI command for running the DMP→ESP plugin conversion pipeline. Same engine as the
///     WinUI tab — reads an Xbox 360 memory dump, merges its records into a PC plugin
///     authored against the provided master ESM.
/// </summary>
public static class DmpToEspCommand
{
    public static Command Create()
    {
        var dmpArg = new Argument<string>("dmp") { Description = "Path to Xbox 360 memory dump (.dmp)" };
        var pcEsmOpt = new Option<string>("--pc-esm")
        {
            Description = "Path to PC FalloutNV.esm (master ESM)",
            Required = true
        };
        var outputOpt = new Option<string>("-o", "--output")
        {
            Description = "Output ESP path",
            Required = true
        };
        var authorOpt = new Option<string?>("--author") { Description = "Plugin author metadata" };
        var descriptionOpt = new Option<string?>("--description") { Description = "Plugin description" };
        var compressOpt = new Option<bool>("--compress") { Description = "Compress record bodies (zlib)" };
        var validateOpt = new Option<bool>("--validate")
        {
            Description = "Re-parse the output ESP to validate structure",
            DefaultValueFactory = _ => true
        };
        var verboseOpt = new Option<bool>("--verbose")
        {
            Description = "Emit per-record decision events (very chatty)"
        };

        var command = new Command("to-esp", "Convert a DMP to a PC plugin ESP overlay against a master ESM");
        command.Arguments.Add(dmpArg);
        command.Options.Add(pcEsmOpt);
        command.Options.Add(outputOpt);
        command.Options.Add(authorOpt);
        command.Options.Add(descriptionOpt);
        command.Options.Add(compressOpt);
        command.Options.Add(validateOpt);
        command.Options.Add(verboseOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var dmp = parseResult.GetValue(dmpArg)!;
            var pcEsm = parseResult.GetValue(pcEsmOpt)!;
            var output = parseResult.GetValue(outputOpt)!;
            var author = parseResult.GetValue(authorOpt);
            var description = parseResult.GetValue(descriptionOpt);
            var compress = parseResult.GetValue(compressOpt);
            var validate = parseResult.GetValue(validateOpt);
            var verbose = parseResult.GetValue(verboseOpt);

            await RunAsync(dmp, pcEsm, output, author, description, compress, validate, verbose, ct);
        });

        return command;
    }

    private static async Task RunAsync(
        string dmpPath,
        string pcEsmPath,
        string outputPath,
        string? author,
        string? description,
        bool compress,
        bool validate,
        bool verbose,
        CancellationToken ct)
    {
        if (!File.Exists(dmpPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] DMP file not found: {Markup.Escape(dmpPath)}");
            Environment.Exit(1);
            return;
        }

        if (!File.Exists(pcEsmPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] PC ESM not found: {Markup.Escape(pcEsmPath)}");
            Environment.Exit(1);
            return;
        }

        var pcEsmFileSize = new FileInfo(pcEsmPath).Length;
        var options = new PluginBuildOptions
        {
            MasterFileName = Path.GetFileName(pcEsmPath),
            MasterFileSize = pcEsmFileSize,
            Author = string.IsNullOrEmpty(author) ? null : author,
            Description = string.IsNullOrEmpty(description) ? null : description,
            CompressRecords = compress,
            ValidateOutput = validate,
            VerboseDecisions = verbose
        };

        var inputs = new DmpToEspInputs
        {
            DmpPath = dmpPath,
            PcEsmPath = pcEsmPath,
            OutputEspPath = outputPath,
            Options = options
        };

        AnsiConsole.MarkupLine($"[cyan]DMP:[/] {Markup.Escape(dmpPath)}");
        AnsiConsole.MarkupLine($"[cyan]Master ESM:[/] {Markup.Escape(pcEsmPath)} ({pcEsmFileSize:N0} bytes)");
        AnsiConsole.MarkupLine($"[cyan]Output:[/] {Markup.Escape(outputPath)}");
        AnsiConsole.WriteLine();

        var registry = RecordEncoderRegistry.CreateV11Default();
        var sink = new ConsoleProgressSink(verbose);
        var builder = new PluginBuilder(registry, sink);

        var result = await builder.BuildAsync(inputs, ct);

        AnsiConsole.WriteLine();
        if (result.Success)
        {
            var s = result.Stats;
            AnsiConsole.MarkupLine("[green]✓ Conversion succeeded.[/]");
            AnsiConsole.MarkupLine(
                $"  Records: considered={s.RecordsConsidered:N0}, emitted={s.RecordsEmitted:N0}, " +
                $"skipped={s.RecordsSkipped:N0}, failed={s.RecordsFailed:N0}");
            AnsiConsole.MarkupLine($"  Overrides={s.OverridesEmitted:N0}, new={s.NewRecordsEmitted:N0}, " +
                                   $"cells={s.CellsMerged:N0}");
            AnsiConsole.MarkupLine($"  Output: {s.OutputBytes:N0} bytes in {s.Elapsed.TotalSeconds:F2}s");

            if (!string.IsNullOrEmpty(result.ValidationReport))
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[bold]Validation:[/]");
                AnsiConsole.WriteLine(result.ValidationReport);
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]✗ Conversion failed:[/] {Markup.Escape(result.ErrorMessage ?? "(unknown)")}");
            Environment.Exit(1);
        }
    }

    /// <summary>
    ///     IConversionProgressSink that writes events to the console. Mirrors the WinUI
    ///     tab's channel-based sink but synchronous (no UI dispatcher).
    /// </summary>
    private sealed class ConsoleProgressSink(bool verbose) : IConversionProgressSink
    {
        public void OnPhaseStart(string phase, int? totalItems)
        {
            AnsiConsole.MarkupLine($"[bold cyan]▶ {Markup.Escape(phase)}[/]");
        }

        public void OnEvent(ConversionProgressEvent evt)
        {
            // Drop info events unless --verbose; warnings and errors always print.
            if (evt.Severity == ConversionEventSeverity.Info && !verbose)
            {
                return;
            }

            var label = evt.Severity switch
            {
                ConversionEventSeverity.Info => "[grey]INFO[/]",
                ConversionEventSeverity.Decision => "[blue]DEC[/]",
                ConversionEventSeverity.Warning => "[yellow]WARN[/]",
                ConversionEventSeverity.Error => "[red]ERR[/]",
                _ => Markup.Escape(evt.Severity.ToString())
            };

            var formId = evt.FormId.HasValue ? $" 0x{evt.FormId.Value:X8}" : "";
            var type = string.IsNullOrEmpty(evt.FormType) ? "" : $" {evt.FormType}";
            AnsiConsole.MarkupLine($"  {label}{type}{formId}: {Markup.Escape(evt.Message)}");
        }

        public void OnPhaseEnd(string phase, ConversionPipelineStats partialStats)
        {
            // Running stats are reflected in the final summary; nothing to print per phase.
        }

        public void OnComplete(ConversionPipelineStats stats)
        {
            // Final summary printed by caller.
        }
    }
}
