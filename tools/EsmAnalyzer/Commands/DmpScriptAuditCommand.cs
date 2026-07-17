using System.CommandLine;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Emits a deterministic, same-dump audit of every raw runtime Script object and every
///     merged SCPT model. The command deliberately accepts one dump so source, bytecode, and
///     tables can never be borrowed from another capture.
/// </summary>
internal static class DmpScriptAuditCommand
{
    internal static Command Create()
    {
        var dumpArg = new Argument<string>("dump")
        {
            Description = "Path to one Xbox 360 minidump"
        };
        var outputOpt = new Option<string>("--output", "-o")
        {
            Description = "Destination CSV path",
            Required = true
        };

        var command = new Command(
            "audit",
            "Audit raw and merged scripts from one dump; fails only on hard structural contradictions");
        command.Arguments.Add(dumpArg);
        command.Options.Add(outputOpt);
        command.SetAction((parseResult, cancellationToken) => RunAsync(
            parseResult.GetValue(dumpArg)!,
            parseResult.GetValue(outputOpt)!,
            cancellationToken));
        return command;
    }

    private static async Task<int> RunAsync(
        string dumpPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loaded = await DmpScriptCommands.LoadDumpAsync(dumpPath);
        if (loaded is null)
        {
            return 1;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var (collection, _) = loaded.Value;
        var report = DmpScriptAuditAnalyzer.Build(collection);
        DmpScriptAuditCsv.Write(outputPath, report.Rows);

        var classes = report.Rows
            .GroupBy(static row => row.ContentClassification, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group => $"{group.Key}={group.Count():N0}");
        AnsiConsole.MarkupLine(
            $"[cyan]Same-dump script audit:[/] {collection.RuntimeScripts.Count:N0} raw runtime object(s), " +
            $"{collection.Scripts.Count:N0} merged script(s), {report.Rows.Count:N0} CSV row(s)");
        AnsiConsole.MarkupLine($"[cyan]Content classes:[/] {Markup.Escape(string.Join(", ", classes))}");
        AnsiConsole.MarkupLine(
            $"[cyan]Comparer diagnostics:[/] {report.Rows.Count(static row => row.ComparisonMismatchCount > 0):N0} row(s)");
        AnsiConsole.MarkupLine($"[green]CSV written to:[/] {Markup.Escape(Path.GetFullPath(outputPath))}");

        if (report.HardContradictionCount == 0)
        {
            AnsiConsole.MarkupLine("[green]Hard contradictions:[/] 0");
            return 0;
        }

        AnsiConsole.MarkupLine(
            $"[red]Hard contradictions:[/] {report.HardContradictionCount:N0}; inspect hard_contradictions in the CSV");
        return 2;
    }
}
