using System.CommandLine;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Analysis.FileAnalysis;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Export.Report;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Records;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Esm;

/// <summary>
///     CLI command for analyzing ESM/ESP plugin files.
/// </summary>
public static class EsmCommand
{
    /// <summary>The <c>schema</c> identifier of the <c>esm &lt;file&gt; -f json</c> document.</summary>
    internal const string JsonSchema = "bethesda-multitool/esm-summary";

    private static readonly JsonWriterOptions JsonOutputOptions = new() { Indented = true };

    public static Command Create()
    {
        var command = new Command("esm", "Analyze ESM/ESP plugin files");

        var inputArg = new Argument<string>("input") { Description = "Path to ESM/ESP file" };
        var verboseOpt = new Option<bool>("-v", "--verbose") { Description = "Show detailed output" };
        var formatOpt = new Option<string>("-f", "--format")
        {
            Description = "Output format: text, md, json",
            DefaultValueFactory = _ => "text"
        };
        var outputOpt = new Option<string?>("-o", "--output") { Description = "Output file path" };
        var recordTypeOpt = new Option<string?>("-t", "--type")
            { Description = "Filter by record type (e.g., WEAP, NPC_, CELL)" };
        var limitOpt = new Option<int?>("-l", "--limit") { Description = "Limit number of records shown" };

        command.Arguments.Add(inputArg);
        command.Options.Add(verboseOpt);
        command.Options.Add(formatOpt);
        command.Options.Add(outputOpt);
        command.Options.Add(recordTypeOpt);
        command.Options.Add(limitOpt);

        command.SetAction(async (parseResult, _) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var verbose = parseResult.GetValue(verboseOpt);
            var format = parseResult.GetValue(formatOpt)!;
            var output = parseResult.GetValue(outputOpt);
            var recordType = parseResult.GetValue(recordTypeOpt);
            var limit = parseResult.GetValue(limitOpt);
            return await ExecuteAsync(input, verbose, format, output, recordType, limit);
        });

        command.Subcommands.Add(PackagesCommand.Create());
        command.Subcommands.Add(NpcInventoryCommand.CreateEsmCommand());
        command.Subcommands.Add(EsmActorDetailsCommand.Create());
        command.Subcommands.Add(EsmTerminalGraphCommand.Create());
        command.Subcommands.Add(EsmReportsCommand.Create());
        command.Subcommands.Add(EsmStatsCommand.CreateStatsCommand());
        command.Subcommands.Add(EsmCoverageCommand.CreateCoverageCommand());
        command.Subcommands.Add(EsmDiagnoseScriptsCommand.CreateDiagnoseScriptsCommand());
        command.Subcommands.Add(EsmGameplayAuditCommand.CreateGameplayAuditCommand());
        command.Subcommands.Add(EsmDumpCommand.CreateDumpCommand());
        command.Subcommands.Add(EsmDumpCommand.CreateTraceCommand());
        command.Subcommands.Add(EsmConvertCommand.CreateConvertCommand());
        command.Subcommands.Add(EsmSemdiffCommand.CreateSemanticDiffCommand());
        command.Subcommands.Add(EsmDiffCommand.CreateUnifiedDiffCommand());
        command.Subcommands.Add(CreateCellGroup());

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string input,
        bool verbose,
        string format,
        string? output,
        string? recordType,
        int? limit)
    {
        // JSON owns stdout: every status line and log line goes to stderr so the document parses as-is.
        // Logger's AsyncLocal redirect is scoped to this call.
        var isJson = format.Equals("json", StringComparison.OrdinalIgnoreCase);
        if (isJson)
        {
            Logger.SetOutput(Console.Error);
        }

        var console = CliConsoles.ForStatus(isJson);

        if (!File.Exists(input))
        {
            console.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(input)}");
            return 1;
        }

        console.MarkupLine($"[blue]Loading:[/] {Markup.Escape(Path.GetFileName(input))}");

        var fileInfo = new FileInfo(input);
        console.MarkupLine("[dim]Size:[/] {0:N0} bytes ({1:N2} MB)", fileInfo.Length,
            fileInfo.Length / 1024.0 / 1024.0);

        // Load file into memory
        var data = await File.ReadAllBytesAsync(input);

        // Parse file header
        var header = EsmParser.ParseFileHeader(data);
        if (header == null)
        {
            console.MarkupLine("[red]Error:[/] Not a valid ESM/ESP file (missing TES4 header)");
            return 1;
        }

        // Scan records
        console.MarkupLine("[blue]Scanning records...[/]");
        var recordInfos = EsmParser.ScanRecords(data);
        var recordCounts = EsmParser.GetRecordTypeCounts(data);

        // Build FormID -> EditorID map
        console.MarkupLine("[blue]Building FormID index...[/]");
        var formIdMap = new Dictionary<uint, string>();
        foreach (var record in EsmParser.EnumerateRecords(data))
        {
            if (!string.IsNullOrEmpty(record.EditorId))
            {
                formIdMap[record.Header.FormId] = record.EditorId;
            }
        }

        // Generate output
        var result = new EsmFileScanResult
        {
            Header = header,
            RecordTypeCounts = recordCounts,
            TotalRecords = recordInfos.Count,
            RecordInfos = recordInfos,
            FormIdToEditorId = formIdMap,
            RecordsByCategory = GetRecordsByCategory(recordCounts)
        };

        var outputText = format.ToLowerInvariant() switch
        {
            "md" or "markdown" => FormatMarkdown(result, Path.GetFileName(input), recordType),
            "json" => FormatJson(result),
            _ => FormatText(result, verbose, recordType, limit)
        };

        if (!string.IsNullOrEmpty(output))
        {
            await File.WriteAllTextAsync(output, outputText);
            console.MarkupLine($"[green]Output written to:[/] {Markup.Escape(output)}");
        }
        else
        {
            Console.WriteLine(outputText);
        }

        return 0;
    }

    private static Dictionary<RecordCategory, int> GetRecordsByCategory(Dictionary<string, int> recordCounts)
    {
        var result = new Dictionary<RecordCategory, int>();

        foreach (var (sig, count) in recordCounts)
        {
            var typeInfo = EsmRecordTypes.MainRecordTypes.GetValueOrDefault(sig);
            if (typeInfo != null)
            {
                if (!result.TryGetValue(typeInfo.Category, out var existing))
                {
                    existing = 0;
                }

                result[typeInfo.Category] = existing + count;
            }
        }

        return result;
    }

    private static string FormatText(EsmFileScanResult result, bool verbose, string? recordTypeFilter, int? limit)
    {
        var sb = new StringBuilder();

        sb.AppendLine("═══════════════════════════════════════════════════════════════════");
        sb.AppendLine("                        ESM File Analysis                          ");
        sb.AppendLine("═══════════════════════════════════════════════════════════════════");
        sb.AppendLine();

        // Header info
        sb.AppendLine("FILE HEADER");
        sb.AppendLine("───────────────────────────────────────────────────────────────────");
        sb.AppendLine(
            $"  Platform:       {(result.Header?.IsBigEndian == true ? "Xbox 360 (Big-Endian)" : "PC (Little-Endian)")}");
        sb.AppendLine($"  Version:        {result.Header?.Version:F2}");
        sb.AppendLine($"  Author:         {result.Header?.Author ?? "(none)"}");
        sb.AppendLine($"  Description:    {result.Header?.Description ?? "(none)"}");
        sb.AppendLine($"  Next Object ID: 0x{result.Header?.NextObjectId:X8}");
        sb.AppendLine($"  Total Records:  {result.TotalRecords:N0}");
        sb.AppendLine();

        if (result.Header?.Masters.Count > 0)
        {
            sb.AppendLine("MASTER FILES");
            sb.AppendLine("───────────────────────────────────────────────────────────────────");
            foreach (var master in result.Header.Masters)
            {
                sb.AppendLine($"  • {master}");
            }

            sb.AppendLine();
        }

        // Record type summary
        sb.AppendLine("RECORD TYPES");
        sb.AppendLine("───────────────────────────────────────────────────────────────────");
        sb.AppendLine($"  {"Type",-8} {"Name",-30} {"Count",10}");
        sb.AppendLine($"  {new string('-', 8)} {new string('-', 30)} {new string('-', 10)}");

        var filteredCounts = recordTypeFilter != null
            ? result.RecordTypeCounts.Where(kvp => kvp.Key.Equals(recordTypeFilter, StringComparison.OrdinalIgnoreCase))
            : result.RecordTypeCounts;

        foreach (var (sig, count) in filteredCounts.OrderByDescending(kvp => kvp.Value))
        {
            var typeInfo = EsmRecordTypes.MainRecordTypes.GetValueOrDefault(sig);
            var name = typeInfo?.Name ?? "Unknown";
            sb.AppendLine($"  {sig,-8} {name,-30} {count,10:N0}");
        }

        sb.AppendLine();

        // Category summary
        if (verbose && result.RecordsByCategory.Count > 0)
        {
            sb.AppendLine("RECORDS BY CATEGORY");
            sb.AppendLine("───────────────────────────────────────────────────────────────────");
            foreach (var (category, count) in result.RecordsByCategory.OrderByDescending(kvp => kvp.Value))
            {
                sb.AppendLine($"  {category,-20} {count,10:N0}");
            }

            sb.AppendLine();
        }

        // FormID index sample
        if (verbose && result.FormIdToEditorId.Count > 0)
        {
            sb.AppendLine($"FORMID INDEX ({result.FormIdToEditorId.Count:N0} entries)");
            sb.AppendLine("───────────────────────────────────────────────────────────────────");

            var toShow = limit ?? 20;
            var shown = 0;
            foreach (var (formId, editorId) in result.FormIdToEditorId.Take(toShow))
            {
                sb.AppendLine($"  0x{formId:X8} -> {editorId}");
                shown++;
            }

            if (result.FormIdToEditorId.Count > toShow)
            {
                sb.AppendLine($"  ... and {result.FormIdToEditorId.Count - shown:N0} more");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatMarkdown(EsmFileScanResult result, string fileName, string? recordTypeFilter)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# ESM Analysis: {fileName}");
        sb.AppendLine();

        sb.AppendLine("## File Header");
        sb.AppendLine();
        sb.AppendLine("| Property | Value |");
        sb.AppendLine("|----------|-------|");
        sb.AppendLine($"| Version | {result.Header?.Version:F2} |");
        sb.AppendLine($"| Author | {result.Header?.Author ?? "(none)"} |");
        sb.AppendLine($"| Description | {result.Header?.Description ?? "(none)"} |");
        sb.AppendLine($"| Next Object ID | `0x{result.Header?.NextObjectId:X8}` |");
        sb.AppendLine($"| Total Records | {result.TotalRecords:N0} |");
        sb.AppendLine($"| Unique EditorIDs | {result.FormIdToEditorId.Count:N0} |");
        sb.AppendLine();

        if (result.Header?.Masters.Count > 0)
        {
            sb.AppendLine("## Master Files");
            sb.AppendLine();
            foreach (var master in result.Header.Masters)
            {
                sb.AppendLine($"- `{master}`");
            }

            sb.AppendLine();
        }

        sb.AppendLine("## Record Types");
        sb.AppendLine();
        sb.AppendLine("| Type | Name | Count | Category |");
        sb.AppendLine("|------|------|------:|----------|");

        var filteredCounts = recordTypeFilter != null
            ? result.RecordTypeCounts.Where(kvp => kvp.Key.Equals(recordTypeFilter, StringComparison.OrdinalIgnoreCase))
            : result.RecordTypeCounts;

        foreach (var (sig, count) in filteredCounts.OrderByDescending(kvp => kvp.Value))
        {
            var typeInfo = EsmRecordTypes.MainRecordTypes.GetValueOrDefault(sig);
            var name = typeInfo?.Name ?? "Unknown";
            var category = typeInfo?.Category.ToString() ?? "Unknown";
            sb.AppendLine($"| `{sig}` | {name} | {count:N0} | {category} |");
        }

        sb.AppendLine();

        if (result.RecordsByCategory.Count > 0)
        {
            sb.AppendLine("## Records by Category");
            sb.AppendLine();
            sb.AppendLine("| Category | Count |");
            sb.AppendLine("|----------|------:|");
            foreach (var (category, count) in result.RecordsByCategory.OrderByDescending(kvp => kvp.Value))
            {
                sb.AppendLine($"| {category} | {count:N0} |");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Renders the scan summary as one JSON object with <see cref="Utf8JsonWriter" /> (the
    ///     <see cref="ReportJsonFormatter" /> pattern). The reflection-based
    ///     <see cref="JsonSerializer" /> must not be used here: the shipped CLI runs with
    ///     <c>IsReflectionEnabledByDefault=false</c>, under which serializing an anonymous type throws.
    ///     Field names match the earlier anonymous-type form; <c>schema</c>, <c>schemaVersion</c>,
    ///     <c>toolVersion</c> and <c>header.versionRawBits</c> are additions, dictionary keys are written in
    ///     ordinal order, and a non-finite header version is written as null beside its raw bits.
    /// </summary>
    internal static string FormatJson(EsmFileScanResult result)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, JsonOutputOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", JsonSchema);
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("toolVersion", CliConsoles.ToolVersion);

            if (result.Header is { } header)
            {
                writer.WriteStartObject("header");
                if (float.IsFinite(header.Version))
                {
                    writer.WriteNumber("version", header.Version);
                }
                else
                {
                    writer.WriteNull("version");
                }

                writer.WriteString("versionRawBits", $"0x{BitConverter.SingleToUInt32Bits(header.Version):X8}");
                writer.WriteString("author", header.Author);
                writer.WriteString("description", header.Description);
                writer.WriteNumber("nextObjectId", header.NextObjectId);
                writer.WriteStartArray("masters");
                foreach (var master in header.Masters)
                {
                    writer.WriteStringValue(master);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("header");
            }

            writer.WriteNumber("totalRecords", result.TotalRecords);
            writer.WriteNumber("uniqueEditorIds", result.FormIdToEditorId.Count);

            writer.WriteStartObject("recordTypeCounts");
            foreach (var (signature, count) in result.RecordTypeCounts.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            {
                writer.WriteNumber(signature, count);
            }

            writer.WriteEndObject();

            writer.WriteStartObject("recordsByCategory");
            foreach (var (category, count) in result.RecordsByCategory
                         .Select(kvp => (Name: kvp.Key.ToString(), kvp.Value))
                         .OrderBy(entry => entry.Name, StringComparer.Ordinal))
            {
                writer.WriteNumber(category, count);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static Command CreateCellGroup()
    {
        var group = new Command("cell", "Cell inspection commands");
        group.Subcommands.Add(EsmCellCommand.CreateObjectsCommand());
        group.Subcommands.Add(EsmCellCommand.CreateNpcTraceCommand());
        return group;
    }
}
