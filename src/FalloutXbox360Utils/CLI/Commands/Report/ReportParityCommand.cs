using System.CommandLine;
using System.Text.Json;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Semantic;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Report;

/// <summary>
///     Cross-format parity audit. Loads an ESM file and a DMP file from the
///     same build via the standalone <see cref="SemanticFileLoader" /> path,
///     diffs the two resulting <see cref="Core.Formats.Esm.Models.RecordCollection" />
///     instances field-by-field, and reports which fields one format can
///     populate but the other cannot.
/// </summary>
internal static class ReportParityCommand
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    internal static Command Create()
    {
        var esmOpt = new Option<string>("--esm")
        {
            Description = "ESM file to load via the standalone ESM pipeline.",
            Required = true
        };
        var dmpOpt = new Option<string>("--dmp")
        {
            Description = "DMP file to load via the standalone DMP pipeline.",
            Required = true
        };
        var outputOpt = new Option<string?>("-o", "--output")
        {
            Description = "Output directory for parity_report.json."
        };
        var formatOpt = new Option<string>("--format")
        {
            Description = "Output format: text (default), json, or both.",
            DefaultValueFactory = _ => "text"
        };
        var examplesOpt = new Option<int>("--examples")
        {
            Description = "Concrete (formId, esmValue, dmpValue) tuples retained per gap.",
            DefaultValueFactory = _ => ParityAuditCore.DefaultExamplesPerField
        };

        var command = new Command("parity",
            "Compare an ESM and DMP from the same build; report per-field gaps in each format.");
        command.Options.Add(esmOpt);
        command.Options.Add(dmpOpt);
        command.Options.Add(outputOpt);
        command.Options.Add(formatOpt);
        command.Options.Add(examplesOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var esmPath = parseResult.GetValue(esmOpt);
            var dmpPath = parseResult.GetValue(dmpOpt);
            var output = parseResult.GetValue(outputOpt);
            var format = parseResult.GetValue(formatOpt) ?? "text";
            var examples = parseResult.GetValue(examplesOpt);
            await RunAsync(esmPath!, dmpPath!, output, format, examples, cancellationToken);
        });

        return command;
    }

    private static async Task RunAsync(
        string esmPath,
        string dmpPath,
        string? outputDir,
        string format,
        int examplesPerField,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(esmPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] ESM not found: {Markup.Escape(esmPath)}");
            Environment.ExitCode = 1;
            return;
        }

        if (!File.Exists(dmpPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] DMP not found: {Markup.Escape(dmpPath)}");
            Environment.ExitCode = 1;
            return;
        }

        var esmType = SemanticFileLoader.ResolveSemanticFileType(esmPath);
        var dmpType = SemanticFileLoader.ResolveSemanticFileType(dmpPath);
        if (esmType != AnalysisFileType.EsmFile)
        {
            AnsiConsole.MarkupLine(
                $"[red]ERROR:[/] --esm must be an ESM/ESP file (got {esmType}): {Markup.Escape(esmPath)}");
            Environment.ExitCode = 1;
            return;
        }

        if (dmpType != AnalysisFileType.Minidump)
        {
            AnsiConsole.MarkupLine(
                $"[red]ERROR:[/] --dmp must be a memory dump (got {dmpType}): {Markup.Escape(dmpPath)}");
            Environment.ExitCode = 1;
            return;
        }

        AnsiConsole.MarkupLine($"[dim]Loading ESM:[/] {Markup.Escape(esmPath)}");
        using var esm = await SemanticFileLoader.LoadAsync(
            esmPath, new SemanticFileLoadOptions(), cancellationToken);
        AnsiConsole.MarkupLine($"[dim]Loading DMP:[/] {Markup.Escape(dmpPath)}");
        using var dmp = await SemanticFileLoader.LoadAsync(
            dmpPath, new SemanticFileLoadOptions { IncludeMetadata = true }, cancellationToken);

        var esmLabel = Path.GetFileName(esmPath);
        var dmpLabel = Path.GetFileName(dmpPath);

        var result = ParityAuditCore.Compare(
            esmLabel, esm.Records, esm.Resolver,
            dmpLabel, dmp.Records, dmp.Resolver,
            examplesPerField);

        var emitText = format.Equals("text", StringComparison.OrdinalIgnoreCase) ||
                       format.Equals("both", StringComparison.OrdinalIgnoreCase);
        var emitJson = format.Equals("json", StringComparison.OrdinalIgnoreCase) ||
                       format.Equals("both", StringComparison.OrdinalIgnoreCase);

        if (emitText)
        {
            PrintTextReport(result);
        }

        if (outputDir != null)
        {
            Directory.CreateDirectory(outputDir);
            var jsonPath = Path.Combine(outputDir, "parity_report.json");
            await using var fs = File.Create(jsonPath);
            await JsonSerializer.SerializeAsync(fs, result, IndentedJsonOptions, cancellationToken);
            AnsiConsole.MarkupLine($"[green]Wrote:[/] {Markup.Escape(jsonPath)}");
        }
        else if (emitJson)
        {
            var json = JsonSerializer.Serialize(result, IndentedJsonOptions);
            Console.WriteLine(json);
        }
    }

    private static void PrintTextReport(ParityAuditResult result)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[bold]Parity audit:[/] [cyan]{Markup.Escape(result.EsmLabel)}[/] " +
            $"vs [cyan]{Markup.Escape(result.DmpLabel)}[/]");
        AnsiConsole.WriteLine();

        // Per-record-type tables.
        foreach (var typeParity in result.RecordTypes.OrderBy(t => t.TypeName, StringComparer.Ordinal))
        {
            var visibleFields = typeParity.Fields
                .Where(f => f.EsmOnly + f.DmpOnly + f.Disagree + f.Agree > 0)
                .OrderByDescending(f => f.EsmOnly + f.DmpOnly + f.Disagree)
                .ThenBy(f => f.FieldName, StringComparer.Ordinal)
                .ToList();

            if (visibleFields.Count == 0 && typeParity.MatchedRecordCount == 0)
            {
                continue;
            }

            AnsiConsole.MarkupLine(
                $"[bold]== {Markup.Escape(typeParity.TypeName)} ==[/] " +
                $"[dim](ESM: {typeParity.EsmRecordCount:N0}, DMP: {typeParity.DmpRecordCount:N0}, " +
                $"matched: {typeParity.MatchedRecordCount:N0}, " +
                $"esmOnly: {typeParity.EsmOnlyRecordCount:N0}, dmpOnly: {typeParity.DmpOnlyRecordCount:N0})[/]");

            if (visibleFields.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]  (no matched records to diff)[/]");
                AnsiConsole.WriteLine();
                continue;
            }

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Field");
            table.AddColumn(new TableColumn("ESM-only").RightAligned());
            table.AddColumn(new TableColumn("DMP-only").RightAligned());
            table.AddColumn(new TableColumn("Disagree").RightAligned());
            table.AddColumn(new TableColumn("Agree").RightAligned());

            foreach (var field in visibleFields)
            {
                table.AddRow(
                    Markup.Escape(field.FieldName),
                    field.EsmOnly == 0 ? "-" : $"[yellow]{field.EsmOnly:N0}[/]",
                    field.DmpOnly == 0 ? "-" : $"[yellow]{field.DmpOnly:N0}[/]",
                    field.Disagree == 0 ? "-" : $"[red]{field.Disagree:N0}[/]",
                    field.Agree == 0 ? "-" : $"[green]{field.Agree:N0}[/]");
            }

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
        }

        // Top-N actionable shortlist.
        var allGaps = result.RecordTypes
            .SelectMany(t => t.Fields.Select(f => (Type: t.TypeName, Field: f)))
            .ToList();

        PrintShortlist("Top ESM-only gaps (DMP fills, ESM doesn't)",
            allGaps.OrderByDescending(g => g.Field.EsmOnly).Take(10), g => g.Field.EsmOnly);

        PrintShortlist("Top DMP-only gaps (ESM fills, DMP doesn't)",
            allGaps.OrderByDescending(g => g.Field.DmpOnly).Take(10), g => g.Field.DmpOnly);

        PrintShortlist("Top disagreements (both fill, values differ)",
            allGaps.OrderByDescending(g => g.Field.Disagree).Take(10), g => g.Field.Disagree);
    }

    private static void PrintShortlist(
        string title,
        IEnumerable<(string Type, FieldParity Field)> entries,
        Func<(string Type, FieldParity Field), int> selector)
    {
        var ordered = entries.Where(g => selector(g) > 0).ToList();
        if (ordered.Count == 0)
        {
            return;
        }

        AnsiConsole.MarkupLine($"[bold]{Markup.Escape(title)}[/]");
        var table = new Table().Border(TableBorder.Minimal);
        table.AddColumn("Type");
        table.AddColumn("Field");
        table.AddColumn(new TableColumn("Count").RightAligned());

        foreach (var (typeName, field) in ordered)
        {
            table.AddRow(
                Markup.Escape(typeName),
                Markup.Escape(field.FieldName),
                selector((typeName, field)).ToString("N0"));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }
}
