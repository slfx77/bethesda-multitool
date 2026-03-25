using System.CommandLine;
using FalloutXbox360Utils.Core.Formats.Esm;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Esm;

/// <summary>
///     Semantic diff command - shows human-readable field-by-field differences.
/// </summary>
public static class EsmSemdiffCommand
{
    /// <summary>
    ///     Creates the 'semdiff' command for semantic comparison.
    /// </summary>
    public static Command CreateSemanticDiffCommand()
    {
        var command = new Command("semdiff", "Semantic diff - shows human-readable field differences (like TES5Edit)");

        var fileAArg = new Argument<string>("fileA") { Description = "First ESM file" };
        var fileBArg = new Argument<string>("fileB") { Description = "Second ESM file" };
        var formIdOption = new Option<string?>("-f", "--formid")
            { Description = "Specific FormID to compare (hex, e.g., 0x0017B37C)" };
        var typeOption = new Option<string?>("-t", "--type")
            { Description = "Record type to filter (e.g., PROJ, WEAP, NPC_)" };
        var limitOption = new Option<int>("-l", "--limit")
            { Description = "Max records to show (default: 10)", DefaultValueFactory = _ => 10 };
        var showAllOption = new Option<bool>("--all")
            { Description = "Show all fields, not just differences" };
        var formatOption = new Option<string>("--format")
            { Description = "Output format: table (default), tree, json", DefaultValueFactory = _ => "table" };

        command.Arguments.Add(fileAArg);
        command.Arguments.Add(fileBArg);
        command.Options.Add(formIdOption);
        command.Options.Add(typeOption);
        command.Options.Add(limitOption);
        command.Options.Add(showAllOption);
        command.Options.Add(formatOption);

        command.SetAction(parseResult =>
        {
            var fileA = parseResult.GetValue(fileAArg)!;
            var fileB = parseResult.GetValue(fileBArg)!;
            var formIdStr = parseResult.GetValue(formIdOption);
            var recordType = parseResult.GetValue(typeOption);
            var limit = parseResult.GetValue(limitOption);
            var showAll = parseResult.GetValue(showAllOption);
            return RunSemanticDiff(fileA, fileB, formIdStr, recordType, limit, showAll);
        });

        return command;
    }

    /// <summary>
    ///     Public entry point for semantic diff with custom labels (called by unified diff command).
    /// </summary>
    public static int RunSemanticDiffLabeled(string fileAPath, string fileBPath, string labelA, string labelB,
        string? formIdStr, string? recordType, int limit, bool showAll,
        bool skipHeader = false)
    {
        return RunSemanticDiffCore(fileAPath, fileBPath, labelA, labelB, formIdStr, recordType, limit, showAll,
            skipHeader);
    }

    private static int RunSemanticDiff(string fileAPath, string fileBPath, string? formIdStr,
        string? recordType, int limit, bool showAll)
    {
        return RunSemanticDiffCore(fileAPath, fileBPath, "File A", "File B", formIdStr, recordType, limit, showAll,
            false);
    }

    private static int RunSemanticDiffCore(string fileAPath, string fileBPath, string labelA, string labelB,
        string? formIdStr, string? recordType, int limit, bool showAll, bool skipHeader)
    {
        if (!File.Exists(fileAPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {fileAPath}");
            return 1;
        }

        if (!File.Exists(fileBPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {fileBPath}");
            return 1;
        }

        var dataA = File.ReadAllBytes(fileAPath);
        var dataB = File.ReadAllBytes(fileBPath);
        var bigEndianA = EsmParser.IsBigEndian(dataA);
        var bigEndianB = EsmParser.IsBigEndian(dataB);

        if (!skipHeader)
        {
            AnsiConsole.MarkupLine("[bold]Semantic ESM Diff[/]");
            AnsiConsole.MarkupLine(
                $"{labelA}: [cyan]{Path.GetFileName(fileAPath)}[/] ({(bigEndianA ? "Big-endian" : "Little-endian")})");
            AnsiConsole.MarkupLine(
                $"{labelB}: [cyan]{Path.GetFileName(fileBPath)}[/] ({(bigEndianB ? "Big-endian" : "Little-endian")})");
            AnsiConsole.WriteLine();
        }

        // Parse specific FormID
        uint? targetFormId = null;
        if (!string.IsNullOrEmpty(formIdStr))
        {
            if (formIdStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                targetFormId = Convert.ToUInt32(formIdStr, 16);
            }
            else if (uint.TryParse(formIdStr, out var parsed))
            {
                targetFormId = parsed;
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Invalid FormID format: {formIdStr}");
                return 1;
            }
        }

        // Parse records from both files
        var recordsA = SemdiffRecordParser.ParseRecordsWithSubrecords(dataA, bigEndianA, recordType, targetFormId);
        var recordsB = SemdiffRecordParser.ParseRecordsWithSubrecords(dataB, bigEndianB, recordType, targetFormId);

        // Build lookup by FormID
        var lookupA = recordsA.ToDictionary(r => r.FormId);
        var lookupB = recordsB.ToDictionary(r => r.FormId);

        // Find differences
        var differences = new List<SemdiffTypes.RecordDiff>();
        var allFormIds = lookupA.Keys.Union(lookupB.Keys).OrderBy(x => x).ToList();

        foreach (var formId in allFormIds)
        {
            var hasA = lookupA.TryGetValue(formId, out var recA);
            var hasB = lookupB.TryGetValue(formId, out var recB);

            if (!hasA && hasB)
            {
                differences.Add(new SemdiffTypes.RecordDiff(formId, recB!.Type, SemdiffTypes.DiffType.OnlyInB, null,
                    recB));
            }
            else if (hasA && !hasB)
            {
                differences.Add(new SemdiffTypes.RecordDiff(formId, recA!.Type, SemdiffTypes.DiffType.OnlyInA, recA,
                    null));
            }
            else if (hasA && hasB)
            {
                var fieldDiffs =
                    SemdiffRecordParser.CompareRecordFields(recA!, recB!, bigEndianA, bigEndianB);
                if (fieldDiffs.Count > 0 || showAll)
                {
                    differences.Add(new SemdiffTypes.RecordDiff(formId, recA!.Type, SemdiffTypes.DiffType.Different,
                        recA, recB, fieldDiffs));
                }
            }
        }

        // Display results
        if (differences.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No differences found.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[yellow]Found {differences.Count} record(s) with differences[/]");
        AnsiConsole.WriteLine();

        var shown = 0;
        foreach (var diff in differences.Take(limit))
        {
            SemdiffFieldFormatter.DisplayRecordDiff(diff, labelA, labelB);
            shown++;
            if (shown < limit && shown < differences.Count)
            {
                AnsiConsole.WriteLine();
            }
        }

        if (differences.Count > limit)
        {
            AnsiConsole.MarkupLine($"[grey]... and {differences.Count - limit} more records[/]");
        }

        return 0;
    }
}
