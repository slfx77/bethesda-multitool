using System.CommandLine;
using System.Globalization;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Dialogue;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Dialogue;

/// <summary>
///     CLI command for comparing DMP dialogue against ESM reference.
/// </summary>
internal static class DialogueVerifyCommand
{
    internal static Command CreateVerifyCommand()
    {
        var command = new Command("verify", "Compare DMP dialogue against ESM reference");

        var dmpArg = new Argument<string>("dmp") { Description = "Path to DMP (memory dump) file" };
        var esmArg = new Argument<string>("esm") { Description = "Path to ESM reference file" };
        var questOpt = new Option<string?>("--quest")
            { Description = "Filter by quest FormID (hex, e.g. 0x12345)" };
        var verboseOpt = new Option<bool>("--verbose") { Description = "Show per-topic differences" };
        var outputOpt = new Option<string?>("-o") { Description = "Export CSV report to path" };

        command.Arguments.Add(dmpArg);
        command.Arguments.Add(esmArg);
        command.Options.Add(questOpt);
        command.Options.Add(verboseOpt);
        command.Options.Add(outputOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var dmpPath = parseResult.GetValue(dmpArg)!;
            var esmPath = parseResult.GetValue(esmArg)!;
            var questFilter = parseResult.GetValue(questOpt);
            var verbose = parseResult.GetValue(verboseOpt);
            var outputPath = parseResult.GetValue(outputOpt);
            await RunVerifyAsync(dmpPath, esmPath, questFilter, verbose, outputPath, cancellationToken);
        });

        return command;
    }

    private static async Task RunVerifyAsync(
        string dmpPath, string esmPath,
        string? questFilter, bool verbose, string? outputPath,
        CancellationToken cancellationToken)
    {
        // Load DMP
        AnsiConsole.MarkupLine("[blue]Loading DMP:[/] {0}", Path.GetFileName(dmpPath));
        var dmpResult = await DialogueCommand.LoadAndParseAsync(dmpPath, cancellationToken);
        if (dmpResult == null)
        {
            return;
        }

        // Load ESM
        AnsiConsole.MarkupLine("[blue]Loading ESM:[/] {0}", Path.GetFileName(esmPath));
        var esmResult = await DialogueCommand.LoadAndParseAsync(esmPath, cancellationToken);
        if (esmResult == null)
        {
            return;
        }

        var dmpTree = dmpResult.Value.result.DialogueTree;
        var esmTree = esmResult.Value.result.DialogueTree;

        if (dmpTree == null || esmTree == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] One or both files have no dialogue tree.");
            return;
        }

        // Parse quest filter
        uint? questFormId = null;
        if (questFilter != null)
        {
            if (questFilter.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                if (uint.TryParse(questFilter.AsSpan(2), NumberStyles.HexNumber, null, out var parsed))
                {
                    questFormId = parsed;
                }
            }
            else if (uint.TryParse(questFilter, NumberStyles.HexNumber, null, out var parsed2))
            {
                questFormId = parsed2;
            }

            if (questFormId == null)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Invalid quest FormID: {0}", questFilter);
                return;
            }
        }

        // Compare
        AnsiConsole.MarkupLine("[blue]Comparing dialogue trees...[/]");
        var result = DialogueVerifier.Compare(dmpTree, esmTree, questFormId);

        // Display summary table
        var table = new Table();
        table.AddColumn("Metric");
        table.AddColumn(new TableColumn("Count").RightAligned());

        table.AddRow("[bold]Topics[/]", "");
        table.AddRow("  Matched", $"[green]{result.TopicsMatched:N0}[/]");
        table.AddRow("  Missing (ESM only)", result.TopicsMissing > 0
            ? $"[red]{result.TopicsMissing:N0}[/]"
            : $"[dim]{result.TopicsMissing:N0}[/]");
        table.AddRow("  Extra (DMP only)", result.TopicsExtra > 0
            ? $"[yellow]{result.TopicsExtra:N0}[/]"
            : $"[dim]{result.TopicsExtra:N0}[/]");

        table.AddRow("[bold]INFOs[/]", "");
        table.AddRow("  Matched", $"[green]{result.InfosMatched:N0}[/]");
        table.AddRow("  Missing (ESM only)", result.InfosMissing > 0
            ? $"[red]{result.InfosMissing:N0}[/]"
            : $"[dim]{result.InfosMissing:N0}[/]");
        table.AddRow("  Extra (DMP only)", result.InfosExtra > 0
            ? $"[yellow]{result.InfosExtra:N0}[/]"
            : $"[dim]{result.InfosExtra:N0}[/]");

        table.AddRow("[bold]Dialogue Flow (TCLT)[/]", "");
        table.AddRow("  Matches", $"[green]{result.FlowMatches:N0}[/]");
        table.AddRow("  Mismatches", result.FlowMismatches > 0
            ? $"[red]{result.FlowMismatches:N0}[/]"
            : $"[dim]{result.FlowMismatches:N0}[/]");

        table.AddRow("[bold]Response Text[/]", "");
        table.AddRow("  Both have text", $"[green]{result.ResponseTextMatches:N0}[/]");
        table.AddRow("  ESM has text, DMP missing", result.ResponseTextMissing > 0
            ? $"[yellow]{result.ResponseTextMissing:N0}[/]"
            : $"[dim]{result.ResponseTextMissing:N0}[/]");

        table.AddRow("[bold]AddTopics (NAME)[/]", "");
        table.AddRow("  Matches", $"[green]{result.AddTopicMatches:N0}[/]");
        table.AddRow("  Mismatches", result.AddTopicMismatches > 0
            ? $"[yellow]{result.AddTopicMismatches:N0}[/]"
            : $"[dim]{result.AddTopicMismatches:N0}[/]");

        table.AddRow("[bold]Runtime State[/]", "");
        table.AddRow("  SaidOnce (player spoke)", $"[cyan]{result.SaidOnceCount:N0}[/] / {result.TotalDmpInfos:N0}");

        AnsiConsole.Write(table);

        // Verbose: show per-topic diffs
        if (verbose && result.TopicDiffs.Count > 0)
        {
            AnsiConsole.MarkupLine("\n[bold]Per-topic differences:[/]");
            var diffTable = new Table();
            diffTable.AddColumn("FormID");
            diffTable.AddColumn("Topic");
            diffTable.AddColumn("Type");
            diffTable.AddColumn("Detail");

            foreach (var diff in result.TopicDiffs.Take(100))
            {
                var color = diff.DiffType switch
                {
                    "Missing" => "red",
                    "Extra" => "yellow",
                    "FlowMismatch" => "red",
                    _ => "dim"
                };
                diffTable.AddRow(
                    $"0x{diff.TopicFormId:X8}",
                    Markup.Escape(diff.TopicName ?? "\u2014"),
                    $"[{color}]{Markup.Escape(diff.DiffType)}[/]",
                    Markup.Escape(diff.Detail.Length > 80
                        ? diff.Detail[..77] + "..."
                        : diff.Detail));
            }

            if (result.TopicDiffs.Count > 100)
            {
                AnsiConsole.MarkupLine("[dim]... and {0} more differences[/]", result.TopicDiffs.Count - 100);
            }

            AnsiConsole.Write(diffTable);
        }

        // CSV export
        if (outputPath != null)
        {
            await ExportVerifyReportAsync(result, outputPath);
            AnsiConsole.MarkupLine("[green]Report saved:[/] {0}", outputPath);
        }
    }

    private static async Task ExportVerifyReportAsync(DialogueVerifier.VerificationResult result, string path)
    {
        var lines = new List<string>
        {
            "TopicFormID,TopicName,DiffType,Detail"
        };

        foreach (var diff in result.TopicDiffs)
        {
            lines.Add(string.Join(",",
                $"0x{diff.TopicFormId:X8}",
                CliHelpers.CsvEscape(diff.TopicName ?? ""),
                CliHelpers.CsvEscape(diff.DiffType),
                CliHelpers.CsvEscape(diff.Detail)));
        }

        await File.WriteAllLinesAsync(path, lines);
    }
}
