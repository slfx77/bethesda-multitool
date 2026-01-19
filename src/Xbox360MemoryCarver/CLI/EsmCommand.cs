using System.CommandLine;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;

namespace Xbox360MemoryCarver.CLI;

/// <summary>
///     CLI command for analyzing ESM/ESP plugin files.
/// </summary>
public static class EsmCommand
{
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
        var recordTypeOpt = new Option<string?>("-t", "--type") { Description = "Filter by record type (e.g., WEAP, NPC_, CELL)" };
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
            await ExecuteAsync(input, verbose, format, output, recordType, limit);
        });

        return command;
    }

    private static async Task ExecuteAsync(
        string input,
        bool verbose,
        string format,
        string? output,
        string? recordType,
        int? limit)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return;
        }

        AnsiConsole.MarkupLine("[blue]Loading:[/] {0}", Path.GetFileName(input));

        var fileInfo = new FileInfo(input);
        AnsiConsole.MarkupLine("[dim]Size:[/] {0:N0} bytes ({1:N2} MB)", fileInfo.Length, fileInfo.Length / 1024.0 / 1024.0);

        // Load file into memory
        var data = await File.ReadAllBytesAsync(input);

        // Parse file header
        var header = EsmParser.ParseFileHeader(data);
        if (header == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Not a valid ESM/ESP file (missing TES4 header)");
            return;
        }

        // Scan records
        AnsiConsole.MarkupLine("[blue]Scanning records...[/]");
        var recordInfos = EsmParser.ScanRecords(data);
        var recordCounts = EsmParser.GetRecordTypeCounts(data);

        // Build FormID -> EditorID map
        AnsiConsole.MarkupLine("[blue]Building FormID index...[/]");
        var formIdMap = new Dictionary<uint, string>();
        foreach (var record in EsmParser.EnumerateRecords(data))
        {
            if (!string.IsNullOrEmpty(record.EditorId))
                formIdMap[record.Header.FormId] = record.EditorId;
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
            "md" or "markdown" => FormatMarkdown(result, Path.GetFileName(input), recordType, limit),
            "json" => FormatJson(result),
            _ => FormatText(result, verbose, recordType, limit)
        };

        if (!string.IsNullOrEmpty(output))
        {
            await File.WriteAllTextAsync(output, outputText);
            AnsiConsole.MarkupLine("[green]Output written to:[/] {0}", output);
        }
        else
        {
            Console.WriteLine(outputText);
        }
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
                    existing = 0;
                result[typeInfo.Category] = existing + count;
            }
        }

        return result;
    }

    private static string FormatText(EsmFileScanResult result, bool verbose, string? recordTypeFilter, int? limit)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("═══════════════════════════════════════════════════════════════════");
        sb.AppendLine("                        ESM File Analysis                          ");
        sb.AppendLine("═══════════════════════════════════════════════════════════════════");
        sb.AppendLine();

        // Header info
        sb.AppendLine("FILE HEADER");
        sb.AppendLine("───────────────────────────────────────────────────────────────────");
        sb.AppendLine($"  Platform:       {(result.Header?.IsBigEndian == true ? "Xbox 360 (Big-Endian)" : "PC (Little-Endian)")}");
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
                sb.AppendLine($"  • {master}");
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
                sb.AppendLine($"  {category,-20} {count,10:N0}");
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
                sb.AppendLine($"  0x{formId:X8} → {editorId}");
                shown++;
            }

            if (result.FormIdToEditorId.Count > toShow)
                sb.AppendLine($"  ... and {result.FormIdToEditorId.Count - shown:N0} more");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatMarkdown(EsmFileScanResult result, string fileName, string? recordTypeFilter, int? limit)
    {
        var sb = new System.Text.StringBuilder();

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
                sb.AppendLine($"- `{master}`");
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
                sb.AppendLine($"| {category} | {count:N0} |");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string FormatJson(EsmFileScanResult result)
    {
        var options = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        };

        // Create a serializable version
        var jsonObj = new
        {
            header = result.Header != null ? new
            {
                version = result.Header.Version,
                author = result.Header.Author,
                description = result.Header.Description,
                nextObjectId = result.Header.NextObjectId,
                masters = result.Header.Masters
            } : null,
            totalRecords = result.TotalRecords,
            uniqueEditorIds = result.FormIdToEditorId.Count,
            recordTypeCounts = result.RecordTypeCounts,
            recordsByCategory = result.RecordsByCategory.ToDictionary(kvp => kvp.Key.ToString(), kvp => kvp.Value)
        };

        return System.Text.Json.JsonSerializer.Serialize(jsonObj, options);
    }
}
