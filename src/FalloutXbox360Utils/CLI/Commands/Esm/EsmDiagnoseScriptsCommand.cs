using System.CommandLine;
using System.Globalization;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Semantic;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Esm;

public static class EsmDiagnoseScriptsCommand
{
    public static Command CreateDiagnoseScriptsCommand()
    {
        var command = new Command("diagnose-scripts", "Generate targeted dialogue/package script diagnostics");
        var inputArg = new Argument<string>("esm-input") { Description = "Path to ESM/ESP file" };
        var actorOpt = new Option<string[]>("--actor")
        {
            Description = "Actor FormID, EditorID fragment, or display-name fragment to diagnose",
            AllowMultipleArgumentsPerToken = false
        };
        var outputOpt = new Option<string>("--output")
        {
            Description = "Output directory for targeted diagnostics",
            DefaultValueFactory = _ => "script_diagnostics"
        };
        var sourceDmpOpt = new Option<string?>("--source-dmp")
        {
            Description = "Optional source DMP used to compare runtime script refs/result scripts against the generated ESP"
        };
        var pcEsmOpt = new Option<string?>("--pc-esm")
        {
            Description = "Optional PC FalloutNV.esm used as master label/source fallback for provenance diagnostics"
        };
        var recordOpt = new Option<string[]>("--record")
        {
            Description = "Explicit record FormID to include in diagnostics (hex, repeatable)",
            AllowMultipleArgumentsPerToken = false
        };

        command.Arguments.Add(inputArg);
        command.Options.Add(actorOpt);
        command.Options.Add(outputOpt);
        command.Options.Add(sourceDmpOpt);
        command.Options.Add(pcEsmOpt);
        command.Options.Add(recordOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var actors = parseResult.GetValue(actorOpt) ?? [];
            var output = parseResult.GetValue(outputOpt)!;
            var sourceDmp = parseResult.GetValue(sourceDmpOpt);
            var pcEsm = parseResult.GetValue(pcEsmOpt);
            var records = ParseFormIdSet(parseResult.GetValue(recordOpt) ?? []);
            return await RunAsync(input, actors, records, sourceDmp, pcEsm, output, cancellationToken);
        });

        return command;
    }

    private static async Task<int> RunAsync(
        string input,
        IReadOnlyList<string> actors,
        IReadOnlySet<uint> explicitRecordFormIds,
        string? sourceDmp,
        string? pcEsm,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] File not found: {input}");
            return 1;
        }

        if (!string.IsNullOrWhiteSpace(sourceDmp) && !File.Exists(sourceDmp))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Source DMP not found: {sourceDmp}");
            return 1;
        }

        if (!string.IsNullOrWhiteSpace(pcEsm) && !File.Exists(pcEsm))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] PC ESM not found: {pcEsm}");
            return 1;
        }

        var targets = actors.Count > 0
            ? actors.Where(a => !string.IsNullOrWhiteSpace(a)).ToList()
            : ["Ulysses", "Chomps Lewis"];

        AnsiConsole.MarkupLine(
            $"[blue]Generating script diagnostics:[/] {Path.GetFileName(input)} " +
            $"for {Markup.Escape(string.Join(", ", targets))}");

        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeFile(input, targets, explicitRecordFormIds);
        EsmScriptDiagnosticsAnalyzer.WriteReport(result, outputDirectory);

        UnifiedAnalysisResult? source = null;
        UnifiedAnalysisResult? master = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(sourceDmp) || !string.IsNullOrWhiteSpace(pcEsm))
            {
                if (!string.IsNullOrWhiteSpace(sourceDmp))
                {
                    AnsiConsole.MarkupLine($"[blue]Loading source DMP:[/] {Markup.Escape(Path.GetFileName(sourceDmp))}");
                    source = await SemanticFileLoader.LoadAsync(sourceDmp, cancellationToken: cancellationToken);
                }

                if (!string.IsNullOrWhiteSpace(pcEsm))
                {
                    AnsiConsole.MarkupLine($"[blue]Loading master ESM:[/] {Markup.Escape(Path.GetFileName(pcEsm))}");
                    master = await SemanticFileLoader.LoadAsync(pcEsm, cancellationToken: cancellationToken);
                }

                var provenance = EsmScriptProvenanceAnalyzer.AnalyzeFile(
                    input,
                    result,
                    source?.Records,
                    master?.Records);
                EsmScriptProvenanceAnalyzer.WriteReport(provenance, outputDirectory);

                AnsiConsole.MarkupLine(
                    $"[green]Wrote provenance diagnostics:[/] " +
                    $"[cyan]{provenance.SourceVsEmittedRefs.Count:N0}[/] ref comparison row(s), " +
                    $"[cyan]{provenance.ResultScripts.Count:N0}[/] result-script row(s), " +
                    $"[cyan]{provenance.BytecodeEndianProbes.Count:N0}[/] endian probe row(s), " +
                    $"[cyan]{provenance.StateTrace.Count:N0}[/] state trace row(s)");
            }
        }
        finally
        {
            source?.Dispose();
            master?.Dispose();
        }

        var structuralFailures = result.ScriptBlocks.Count(r =>
            !r.CompiledSizeMatches || !r.RefCountMatches || !r.WalkedToEnd || r.HasDiagnostics);
        var missingRefs = result.ScriptReferences.Count(r => r.Status is "Null" or "Missing");

        AnsiConsole.MarkupLine(
            $"[green]Wrote script diagnostics:[/] {outputDirectory} " +
            $"([cyan]{result.TargetMatches.Count:N0}[/] target match(es), " +
            $"[cyan]{result.ScriptBlocks.Count:N0}[/] script block(s), " +
            $"[cyan]{structuralFailures:N0}[/] structural failure(s), " +
            $"[cyan]{missingRefs:N0}[/] null/missing ref(s))");
        return 0;
    }

    private static HashSet<uint> ParseFormIdSet(IEnumerable<string> values)
    {
        var result = new HashSet<uint>();
        foreach (var value in values)
        {
            var text = value.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                text = text[2..];
            }

            if (uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var formId))
            {
                result.Add(formId);
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]Skipping invalid FormID:[/] {Markup.Escape(value)}");
            }
        }

        return result;
    }
}
