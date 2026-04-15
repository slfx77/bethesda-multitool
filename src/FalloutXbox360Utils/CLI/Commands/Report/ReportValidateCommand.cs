using System.CommandLine;
using System.Text.Json;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Semantic;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Report;

/// <summary>
///     Per-build accuracy check. Loads an ESM/DMP, builds a structured RecordReport
///     for every record, and runs every field through <see cref="ReportFieldDomain" />.
///     Surfaces value violations (out-of-range damage, NaN floats, unresolvable FormIDs)
///     and unknown (RecordType, Section, Field) tuples that should be added to the rule table.
/// </summary>
internal static class ReportValidateCommand
{
    internal static Command Create()
    {
        var inputArg = new Argument<string>("input")
        {
            Description = "ESM, ESP, or DMP file to validate"
        };
        var outputOpt = new Option<string?>("-o", "--output")
        {
            Description = "Optional output directory for full violation/unknown-key dumps"
        };
        var formatOpt = new Option<string>("--format")
        {
            Description = "Output format: text (default), json",
            DefaultValueFactory = _ => "text"
        };
        var maxOpt = new Option<int>("--max-print")
        {
            Description = "Max violations and unknown keys to print to console",
            DefaultValueFactory = _ => 25
        };
        var strictOpt = new Option<bool>("--strict")
        {
            Description = "Treat unknown keys as failures (exit code 2). " +
                          "Intended for CI gating once the rule corpus is clean."
        };

        var command = new Command("validate",
            "Check that every reported field value sits inside its declared domain");
        command.Arguments.Add(inputArg);
        command.Options.Add(outputOpt);
        command.Options.Add(formatOpt);
        command.Options.Add(maxOpt);
        command.Options.Add(strictOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputOpt);
            var format = parseResult.GetValue(formatOpt) ?? "text";
            var maxPrint = parseResult.GetValue(maxOpt);
            var strict = parseResult.GetValue(strictOpt);
            await RunAsync(input, output, format, maxPrint, strict, cancellationToken);
        });

        return command;
    }

    private static async Task RunAsync(
        string inputPath,
        string? outputDir,
        string format,
        int maxPrint,
        bool strict,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(inputPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] File not found: {Markup.Escape(inputPath)}");
            Environment.ExitCode = 1;
            return;
        }

        AnsiConsole.MarkupLine($"[blue]Validating reports for:[/] {Markup.Escape(Path.GetFileName(inputPath))}");
        AnsiConsole.MarkupLine($"[dim]Domain rules registered: {ReportFieldDomain.RuleCount}[/]");
        AnsiConsole.WriteLine();

        var fileType = SemanticFileLoader.ResolveSemanticFileType(inputPath);
        using var loaded = await SemanticFileLoader.LoadAsync(
            inputPath,
            new SemanticFileLoadOptions
            {
                FileType = fileType,
                IncludeMetadata = fileType == AnalysisFileType.Minidump
            },
            cancellationToken);

        var resolver = loaded.Resolver;
        var records = loaded.Records;

        var allViolations = new List<ReportFieldDomain.DomainViolation>();
        var unknownKeys = new Dictionary<(string, string, string), ReportFieldDomain.UnknownKey>();
        var perTypeStats = new Dictionary<string, (int recordsChecked, int violations)>(StringComparer.Ordinal);
        var totalRecords = 0;

        var factionMembers = records.BuildFactionMembersIndex();
        var keyLockedDoors = records.BuildKeyToLockedDoorsMap();
        var modToWeapon = records.BuildModToWeaponMap();

        foreach (var (typeName, _, _, _, record) in RecordTextFormatter.EnumerateAll(records))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var report = RecordTextFormatter.BuildReport(record, resolver,
                factionMembers, keyLockedDoors, modToWeapon);
            if (report == null) continue;

            totalRecords++;
            var eval = ReportFieldDomain.Evaluate(report, resolver);

            if (!perTypeStats.TryGetValue(typeName, out var stats))
                stats = (0, 0);
            stats.recordsChecked++;
            stats.violations += eval.Violations.Count;
            perTypeStats[typeName] = stats;

            allViolations.AddRange(eval.Violations);
            foreach (var unk in eval.UnknownKeys)
            {
                unknownKeys.TryAdd((unk.RecordType, unk.Section, unk.Field), unk);
            }
        }

        PrintSummaryTable(perTypeStats);
        PrintViolations(allViolations, maxPrint);
        PrintUnknownKeys(unknownKeys.Values, maxPrint);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[bold]Records checked:[/] {totalRecords:N0}   " +
            $"[bold]Violations:[/] [{(allViolations.Count == 0 ? "green" : "red")}]{allViolations.Count:N0}[/]   " +
            $"[bold]Unknown keys:[/] {unknownKeys.Count:N0}");

        if (outputDir != null)
        {
            Directory.CreateDirectory(outputDir);
            var jsonPath = Path.Combine(outputDir, "validate_report.json");
            await using (var fs = File.Create(jsonPath))
            {
                await using var writer = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
                WriteValidateJson(writer, inputPath, totalRecords, allViolations, unknownKeys.Values);
            }

            AnsiConsole.MarkupLine($"[green]Wrote:[/] {Markup.Escape(jsonPath)}");
        }

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WriteNumber("totalRecords", totalRecords);
                writer.WriteNumber("violationCount", allViolations.Count);
                writer.WriteNumber("unknownKeyCount", unknownKeys.Count);
                writer.WriteEndObject();
            }

            Console.WriteLine(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
        }

        if (allViolations.Count > 0 || (strict && unknownKeys.Count > 0))
        {
            Environment.ExitCode = 2;
        }
    }

    private static void WriteValidateJson(
        Utf8JsonWriter writer,
        string inputPath,
        int totalRecords,
        List<ReportFieldDomain.DomainViolation> violations,
        IEnumerable<ReportFieldDomain.UnknownKey> unknownKeys)
    {
        writer.WriteStartObject();
        writer.WriteString("input", inputPath);
        writer.WriteNumber("totalRecords", totalRecords);
        writer.WriteNumber("violationCount", violations.Count);

        writer.WritePropertyName("violations");
        writer.WriteStartArray();
        foreach (var v in violations)
        {
            writer.WriteStartObject();
            writer.WriteString("recordType", v.RecordType);
            writer.WriteString("formId", $"0x{v.FormId:X8}");
            if (v.EditorId != null) writer.WriteString("editorId", v.EditorId);
            writer.WriteString("section", v.Section);
            writer.WriteString("field", v.Field);
            writer.WriteString("reason", v.Reason);
            writer.WriteString("displayValue", v.DisplayValue);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WritePropertyName("unknownKeys");
        writer.WriteStartArray();
        foreach (var u in unknownKeys)
        {
            writer.WriteStartObject();
            writer.WriteString("recordType", u.RecordType);
            writer.WriteString("section", u.Section);
            writer.WriteString("field", u.Field);
            writer.WriteString("sampleValue", u.SampleValue);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private static void PrintSummaryTable(Dictionary<string, (int Records, int Violations)> stats)
    {
        if (stats.Count == 0) return;

        var table = new Table().Border(TableBorder.Rounded).Title("[bold]Validation Summary[/]");
        table.AddColumn("[bold]Record Type[/]");
        table.AddColumn(new TableColumn("[bold]Records[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Violations[/]").RightAligned());

        foreach (var (typeName, s) in stats.OrderBy(kv => kv.Key))
        {
            var color = s.Violations == 0 ? "green" : "red";
            table.AddRow(typeName, s.Records.ToString("N0"), $"[{color}]{s.Violations:N0}[/]");
        }

        AnsiConsole.Write(table);
    }

    private static void PrintViolations(List<ReportFieldDomain.DomainViolation> violations, int maxPrint)
    {
        if (violations.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No domain violations.[/]");
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold red]Violations ({violations.Count:N0}, showing {Math.Min(maxPrint, violations.Count)}):[/]");
        var table = new Table().Border(TableBorder.Minimal);
        table.AddColumn("Record");
        table.AddColumn("FormID");
        table.AddColumn("Section");
        table.AddColumn("Field");
        table.AddColumn("Value");
        table.AddColumn("Reason");

        foreach (var v in violations.Take(maxPrint))
        {
            table.AddRow(
                Markup.Escape($"{v.RecordType} {v.EditorId ?? ""}".TrimEnd()),
                $"0x{v.FormId:X8}",
                Markup.Escape(v.Section),
                Markup.Escape(v.Field),
                Markup.Escape(v.DisplayValue),
                Markup.Escape(v.Reason));
        }

        AnsiConsole.Write(table);
    }

    private static void PrintUnknownKeys(
        IEnumerable<ReportFieldDomain.UnknownKey> unknown,
        int maxPrint)
    {
        var list = unknown.ToList();
        if (list.Count == 0) return;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[bold yellow]Unknown field keys ({list.Count:N0}, showing {Math.Min(maxPrint, list.Count)}):[/]");
        AnsiConsole.MarkupLine(
            "[dim]These (RecordType, Section, Field) tuples have no domain rule. " +
            "Add to ReportFieldDomain.BuildRules() to extend coverage.[/]");
        var table = new Table().Border(TableBorder.Minimal);
        table.AddColumn("Record Type");
        table.AddColumn("Section");
        table.AddColumn("Field");
        table.AddColumn("Sample");

        foreach (var u in list
                     .OrderBy(x => x.RecordType)
                     .ThenBy(x => x.Section)
                     .ThenBy(x => x.Field)
                     .Take(maxPrint))
        {
            table.AddRow(
                Markup.Escape(u.RecordType),
                Markup.Escape(u.Section),
                Markup.Escape(u.Field),
                Markup.Escape(u.SampleValue));
        }

        AnsiConsole.Write(table);
    }
}
