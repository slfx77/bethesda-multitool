using System.CommandLine;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Esm;

public static class EsmCoverageCommand
{
    public static Command CreateCoverageCommand()
    {
        var command = new Command("coverage", "Generate ESM semantic modeling coverage reports");
        var inputArg = new Argument<string>("esm-input") { Description = "Path to ESM/ESP file" };
        var outputOpt = new Option<string>("--output")
        {
            Description = "Output directory for coverage CSV/Markdown reports",
            DefaultValueFactory = _ => "esm_coverage"
        };

        command.Arguments.Add(inputArg);
        command.Options.Add(outputOpt);
        command.Subcommands.Add(CreateCompareCommand());

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputOpt)!;
            return Run(input, output);
        });

        return command;
    }

    private static Command CreateCompareCommand()
    {
        var command = new Command("compare", "Compare two esm coverage report directories");
        var baselineArg = new Argument<string>("baseline-dir")
            { Description = "Baseline coverage directory, usually vanilla FalloutNV.esm" };
        var candidateArg = new Argument<string>("candidate-dir")
            { Description = "Candidate coverage directory, usually generated ESP output" };
        var outputOpt = new Option<string>("--output")
        {
            Description = "Output directory for comparison reports",
            DefaultValueFactory = _ => "esm_coverage_compare"
        };

        command.Arguments.Add(baselineArg);
        command.Arguments.Add(candidateArg);
        command.Options.Add(outputOpt);

        command.SetAction(parseResult =>
        {
            var baseline = parseResult.GetValue(baselineArg)!;
            var candidate = parseResult.GetValue(candidateArg)!;
            var output = parseResult.GetValue(outputOpt)!;
            return RunCompare(baseline, candidate, output);
        });

        return command;
    }

    private static int Run(string input, string outputDirectory)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] File not found: {input}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[blue]Analyzing ESM coverage:[/] {Path.GetFileName(input)}");
        var result = EsmCoverageAnalyzer.AnalyzeFile(input);
        EsmCoverageAnalyzer.WriteReport(result, outputDirectory);

        var unparsed = result.Records
            .Where(r => r.Classification == EsmCoverageClassification.Unparsed)
            .Sum(r => r.Count);
        var rawGaps = result.Subrecords.Count(r => r.UsesRawByteArray && !r.IsIntentionalRaw);

        AnsiConsole.MarkupLine(
            $"[green]Wrote coverage reports:[/] {outputDirectory} " +
            $"([cyan]{result.TotalRecordTypes:N0}[/] record types, " +
            $"[cyan]{unparsed:N0}[/] unparsed records, " +
            $"[cyan]{rawGaps:N0}[/] raw subrecord shape gaps)");
        return 0;
    }

    private static int RunCompare(string baselineDirectory, string candidateDirectory, string outputDirectory)
    {
        if (!Directory.Exists(baselineDirectory))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Baseline coverage directory not found: {baselineDirectory}");
            return 1;
        }

        if (!Directory.Exists(candidateDirectory))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Candidate coverage directory not found: {candidateDirectory}");
            return 1;
        }

        try
        {
            var result = EsmCoverageComparison.CompareCoverageDirectories(baselineDirectory, candidateDirectory);
            EsmCoverageComparison.WriteReport(result, outputDirectory);

            var generatedOnly = result.CandidateIssues.Count(r => r.IsGeneratedOnlyFailure);
            AnsiConsole.MarkupLine(
                $"[green]Wrote coverage comparison:[/] {outputDirectory} " +
                $"([cyan]{result.CandidateIssues.Count:N0}[/] candidate issue block(s), " +
                $"[cyan]{generatedOnly:N0}[/] generated-only)");

            if (!result.HasCandidateStructuralFailures)
            {
                AnsiConsole.MarkupLine(
                    "[green]Candidate SCDA bytecode walks cleanly; raw bytecode structure is unlikely root cause.[/]");
            }

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }
}
