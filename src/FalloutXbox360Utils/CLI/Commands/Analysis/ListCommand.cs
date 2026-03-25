using System.CommandLine;
using FalloutXbox360Utils.CLI.Shared;
using FalloutXbox360Utils.Core;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Analysis;

/// <summary>
///     Format-agnostic record browsing. Works on ESM, DMP, and ESP files.
///     Equivalent to the GUI's Data Browser tab.
/// </summary>
public static class ListCommand
{
    public static Command Create()
    {
        var command = new Command("list", "Browse parsed records from any supported file");

        var fileArg = new Argument<string>("file") { Description = "ESM, ESP, or DMP file path" };
        var typeOpt = new Option<string?>("-t", "--type")
        {
            Description = "Record type filter (e.g., NPC_, QUST, DIAL, WEAP, FACT)"
        };
        var filterOpt = new Option<string?>("-f", "--filter")
        {
            Description = "Filter EditorID or DisplayName (case-insensitive contains)"
        };
        var limitOpt = new Option<int>("-l", "--limit")
        {
            Description = "Max records to show (default: 50)",
            DefaultValueFactory = _ => 50
        };

        command.Arguments.Add(fileArg);
        command.Options.Add(typeOpt);
        command.Options.Add(filterOpt);
        command.Options.Add(limitOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var filePath = parseResult.GetValue(fileArg)!;
            var type = parseResult.GetValue(typeOpt);
            var filter = parseResult.GetValue(filterOpt);
            var limit = parseResult.GetValue(limitOpt);

            return await RunListAsync(filePath, type, filter, limit, cancellationToken);
        });

        return command;
    }

    private static async Task<int> RunListAsync(string filePath, string? typeFilter,
        string? nameFilter, int limit, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {filePath}");
            return 1;
        }

        var fileType = FileTypeDetector.Detect(filePath);
        AnsiConsole.MarkupLine($"[bold]List:[/] [cyan]{Path.GetFileName(filePath)}[/] ({fileType})");

        try
        {
            using var result = await CliProgressRunner.RunWithProgressAsync(
                "Analyzing...",
                (progress, ct) => UnifiedAnalyzer.AnalyzeAsync(filePath, progress, ct),
                cancellationToken);

            var entries = RecordFlattener.Flatten(result.Records);

            // Apply filters
            if (!string.IsNullOrEmpty(typeFilter))
            {
                entries = entries.Where(e =>
                    e.Type.Equals(typeFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (!string.IsNullOrEmpty(nameFilter))
            {
                entries = entries.Where(e =>
                        (e.EditorId?.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (e.DisplayName?.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ?? false))
                    .ToList();
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold]{entries.Count} records[/] match filters");

            if (entries.Count == 0)
            {
                return 0;
            }

            // Display table
            CliTableBuilder.WriteRecordTable(entries, limit);

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }
}
