using System.CommandLine;
using System.Globalization;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;
using BethesdaMultitool.Core.Semantic;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Esm;

public static class EsmDiagnoseScriptsCommand
{
    /// <summary>The targets used when neither <c>--actor</c> nor <c>--record</c> is given.</summary>
    private static readonly string[] LegacyDefaultTargets = ["Ulysses", "Chomps Lewis"];

    public static Command CreateDiagnoseScriptsCommand()
    {
        var command = new Command("diagnose-scripts", "Generate targeted dialogue/package script diagnostics");
        var inputArg = new Argument<string>("esm-input") { Description = "Path to ESM/ESM file" };
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
            Description =
                "Optional source DMP used to compare runtime script refs/result scripts against the generated ESM"
        };
        var pcEsmOpt = new Option<string?>("--pc-esm")
        {
            Description = "Optional PC FalloutNV.esm used as master label/source fallback for provenance diagnostics"
        };
        var recordOpt = new Option<string[]>("--record")
        {
            Description =
                "Explicit record FormID to include in diagnostics (hex, repeatable). Without --actor, only the " +
                "explicit records are diagnosed",
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
            var recordTokens = parseResult.GetValue(recordOpt) ?? [];
            return await RunAsync(input, actors, recordTokens, sourceDmp, pcEsm, output, AnsiConsole.Console,
                cancellationToken);
        });

        return command;
    }

    /// <summary>
    ///     Picks the diagnostic targets. Non-blank <c>--actor</c> values win. With none, any explicit
    ///     record makes the run explicit-only: no implicit actors, so an explicit record is never
    ///     reported under an actor it has nothing to do with. With neither option the legacy default
    ///     actors are used and <paramref name="usedLegacyDefaults" /> is set so the caller can say so.
    /// </summary>
    internal static IReadOnlyList<string> ResolveTargets(
        IReadOnlyList<string> actors,
        IReadOnlySet<uint> explicitRecords,
        out bool usedLegacyDefaults)
    {
        var named = actors
            .Where(actor => !string.IsNullOrWhiteSpace(actor))
            .Select(actor => actor.Trim())
            .ToList();
        usedLegacyDefaults = false;
        if (named.Count > 0)
        {
            return named;
        }

        if (explicitRecords.Count > 0 || actors.Count > 0)
        {
            return [];
        }

        usedLegacyDefaults = true;
        return LegacyDefaultTargets;
    }

    internal static async Task<int> RunAsync(
        string input,
        IReadOnlyList<string> actors,
        IReadOnlyList<string> recordTokens,
        string? sourceDmp,
        string? pcEsm,
        string outputDirectory,
        IAnsiConsole console,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(input))
        {
            console.MarkupLine($"[red]ERROR:[/] File not found: {Markup.Escape(input)}");
            return 1;
        }

        if (!string.IsNullOrWhiteSpace(sourceDmp) && !File.Exists(sourceDmp))
        {
            console.MarkupLine($"[red]ERROR:[/] Source DMP not found: {Markup.Escape(sourceDmp)}");
            return 1;
        }

        if (!string.IsNullOrWhiteSpace(pcEsm) && !File.Exists(pcEsm))
        {
            console.MarkupLine($"[red]ERROR:[/] PC ESM not found: {Markup.Escape(pcEsm)}");
            return 1;
        }

        var explicitRecordFormIds = ParseFormIdSet(recordTokens, console);
        if (recordTokens.Count > 0 && explicitRecordFormIds.Count == 0)
        {
            // Falling back to the legacy actors here would diagnose something nobody asked for.
            console.MarkupLine("[red]ERROR:[/] --record was given but none of its values is a hex FormID");
            return 1;
        }

        if (actors.Count > 0 && actors.All(string.IsNullOrWhiteSpace) && explicitRecordFormIds.Count == 0)
        {
            console.MarkupLine("[red]ERROR:[/] --actor was given but every value is blank");
            return 1;
        }

        var targets = ResolveTargets(actors, explicitRecordFormIds, out var usedLegacyDefaults);
        if (usedLegacyDefaults)
        {
            console.MarkupLine(
                "[yellow]Note:[/] neither --actor nor --record was given; using the legacy default targets " +
                $"{Markup.Escape(string.Join(", ", targets))}");
        }

        console.MarkupLine(
            $"[blue]Generating script diagnostics:[/] {Markup.Escape(Path.GetFileName(input))} " +
            $"for {Markup.Escape(DescribeScope(targets, explicitRecordFormIds))}");

        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeFile(input, targets, explicitRecordFormIds);
        foreach (var missing in result.MissingExplicitRecordFormIds)
        {
            console.MarkupLine($"[yellow]Explicit record not found:[/] 0x{missing:X8}");
        }

        EsmScriptDiagnosticsAnalyzer.WriteReport(result, outputDirectory);

        UnifiedAnalysisResult? source = null;
        UnifiedAnalysisResult? master = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(sourceDmp) || !string.IsNullOrWhiteSpace(pcEsm))
            {
                if (!string.IsNullOrWhiteSpace(sourceDmp))
                {
                    console.MarkupLine(
                        $"[blue]Loading source DMP:[/] {Markup.Escape(Path.GetFileName(sourceDmp))}");
                    source = await SemanticFileLoader.LoadAsync(sourceDmp, cancellationToken: cancellationToken);
                }

                if (!string.IsNullOrWhiteSpace(pcEsm))
                {
                    console.MarkupLine($"[blue]Loading master ESM:[/] {Markup.Escape(Path.GetFileName(pcEsm))}");
                    master = await SemanticFileLoader.LoadAsync(pcEsm, cancellationToken: cancellationToken);
                }

                var provenance = EsmScriptProvenanceAnalyzer.AnalyzeFile(
                    input,
                    result,
                    source?.Records,
                    master?.Records);
                EsmScriptProvenanceAnalyzer.WriteReport(provenance, outputDirectory);

                console.MarkupLine(
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
        var explicitFound = result.ExplicitRecordFormIds.Count - result.MissingExplicitRecordFormIds.Count;

        console.MarkupLine(
            $"[green]Wrote script diagnostics:[/] {Markup.Escape(outputDirectory)} " +
            $"([cyan]{result.TargetMatches.Count:N0}[/] target match(es), " +
            $"[cyan]{explicitFound:N0}[/] explicit record(s), " +
            $"[cyan]{result.ScriptBlocks.Count:N0}[/] script block(s), " +
            $"[cyan]{structuralFailures:N0}[/] structural failure(s), " +
            $"[cyan]{missingRefs:N0}[/] null/missing ref(s))");
        return 0;
    }

    private static string DescribeScope(IReadOnlyList<string> targets, IReadOnlySet<uint> explicitRecordFormIds)
    {
        var explicitText = explicitRecordFormIds.Count == 0
            ? string.Empty
            : "explicit record(s) " + string.Join(", ", explicitRecordFormIds.Order().Select(id => $"0x{id:X8}"));
        if (targets.Count == 0)
        {
            return explicitText;
        }

        var targetText = string.Join(", ", targets);
        return explicitText.Length == 0 ? targetText : $"{targetText} + {explicitText}";
    }

    private static HashSet<uint> ParseFormIdSet(IEnumerable<string> values, IAnsiConsole console)
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
                console.MarkupLine($"[yellow]Skipping invalid FormID:[/] {Markup.Escape(value)}");
            }
        }

        return result;
    }
}
