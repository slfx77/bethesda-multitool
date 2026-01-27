using System.CommandLine;
using EsmAnalyzer.Core;
using EsmAnalyzer.Helpers;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Record diff command - finds records that exist in one ESM file but not the other.
/// </summary>
public static class RecordDiffCommands
{
    /// <summary>
    ///     Creates the 'record-diff' command.
    /// </summary>
    public static Command CreateRecordDiffCommand()
    {
        var command = new Command("record-diff", "Find records that exist in one ESM file but not the other");

        var fileAArg = new Argument<string>("file-a") { Description = "First ESM file (e.g., converted Xbox)" };
        var fileBArg = new Argument<string>("file-b") { Description = "Second ESM file (e.g., PC reference)" };
        var typeOption = new Option<string?>("-t", "--type")
        { Description = "Record type to filter (e.g., REFR, NPC_, STAT)" };
        var limitOption = new Option<int>("-l", "--limit")
        { Description = "Max records to show per category (default: 100)", DefaultValueFactory = _ => 100 };
        var outputOption = new Option<string?>("-o", "--output")
        { Description = "Output TSV file for full results" };
        var showEdidOption = new Option<bool>("--edid")
        { Description = "Show EDID for records that have one" };
        var resolveOption = new Option<bool>("--resolve")
        { Description = "For REFR/ACHR/ACRE, resolve what base objects they place and show summary" };

        command.Arguments.Add(fileAArg);
        command.Arguments.Add(fileBArg);
        command.Options.Add(typeOption);
        command.Options.Add(limitOption);
        command.Options.Add(outputOption);
        command.Options.Add(showEdidOption);
        command.Options.Add(resolveOption);

        command.SetAction(parseResult =>
        {
            var fileAPath = parseResult.GetValue(fileAArg)!;
            var fileBPath = parseResult.GetValue(fileBArg)!;
            var recordType = parseResult.GetValue(typeOption);
            var limit = parseResult.GetValue(limitOption);
            var outputPath = parseResult.GetValue(outputOption);
            var showEdid = parseResult.GetValue(showEdidOption);
            var resolve = parseResult.GetValue(resolveOption);

            return RunRecordDiff(fileAPath, fileBPath, recordType, limit, outputPath, showEdid, resolve);
        });

        return command;
    }

    private static int RunRecordDiff(string fileAPath, string fileBPath, string? recordTypeFilter, int limit, string? outputPath, bool showEdid, bool resolve = false)
    {
        AnsiConsole.MarkupLine("[bold cyan]Record Diff[/]");
        AnsiConsole.MarkupLine($"[grey]File A:[/] {fileAPath}");
        AnsiConsole.MarkupLine($"[grey]File B:[/] {fileBPath}");
        if (resolve)
            AnsiConsole.MarkupLine("[yellow]Resolve mode:[/] Will identify base objects for placed references");
        AnsiConsole.WriteLine();

        // Load files
        var fileA = EsmFileLoader.Load(fileAPath, printStatus: false);
        var fileB = EsmFileLoader.Load(fileBPath, printStatus: false);

        if (fileA == null || fileB == null)
        {
            return 1;
        }

        // Build EDID maps - needed for --edid or --resolve
        Dictionary<uint, string>? edidMapA = null;
        Dictionary<uint, string>? edidMapB = null;

        if (showEdid || resolve)
        {
            AnsiConsole.MarkupLine("[grey]Building EDID maps...[/]");
            edidMapA = EsmHelpers.BuildFormIdToEdidMap(fileA.Data, fileA.IsBigEndian);
            edidMapB = EsmHelpers.BuildFormIdToEdidMap(fileB.Data, fileB.IsBigEndian);
        }

        // Build record type maps for resolve mode
        Dictionary<uint, string>? recordTypeMapA = null;
        Dictionary<uint, string>? recordTypeMapB = null;

        if (resolve)
        {
            AnsiConsole.MarkupLine("[grey]Building record type maps...[/]");
            recordTypeMapA = BuildRecordTypeMap(fileA.Data, fileA.IsBigEndian);
            recordTypeMapB = BuildRecordTypeMap(fileB.Data, fileB.IsBigEndian);
        }

        // Scan all records (with full info for resolve mode)
        AnsiConsole.MarkupLine("[grey]Scanning records...[/]");
        var recordsA = resolve
            ? ScanRecordsWithBaseObject(fileA.Data, fileA.IsBigEndian, recordTypeFilter)
            : ScanRecordsToDict(fileA.Data, fileA.IsBigEndian, recordTypeFilter)
                .ToDictionary(kvp => kvp.Key, kvp => new RecordFullInfo { Type = kvp.Value.Type, Size = kvp.Value.Size, BaseObjectFormId = null });
        var recordsB = resolve
            ? ScanRecordsWithBaseObject(fileB.Data, fileB.IsBigEndian, recordTypeFilter)
            : ScanRecordsToDict(fileB.Data, fileB.IsBigEndian, recordTypeFilter)
                .ToDictionary(kvp => kvp.Key, kvp => new RecordFullInfo { Type = kvp.Value.Type, Size = kvp.Value.Size, BaseObjectFormId = null });

        AnsiConsole.MarkupLine($"[grey]File A: {recordsA.Count:N0} records[/]");
        AnsiConsole.MarkupLine($"[grey]File B: {recordsB.Count:N0} records[/]");
        AnsiConsole.WriteLine();

        // Find differences
        var onlyInA = new List<RecordSummary>();
        var onlyInB = new List<RecordSummary>();

        foreach (var kvp in recordsA)
        {
            if (!recordsB.ContainsKey(kvp.Key))
            {
                var info = kvp.Value;
                var baseObjEdid = resolve && info.BaseObjectFormId.HasValue
                    ? edidMapA?.GetValueOrDefault(info.BaseObjectFormId.Value)
                    : null;
                var baseObjType = resolve && info.BaseObjectFormId.HasValue
                    ? recordTypeMapA?.GetValueOrDefault(info.BaseObjectFormId.Value)
                    : null;

                onlyInA.Add(new RecordSummary
                {
                    FormId = kvp.Key,
                    Type = info.Type,
                    Size = info.Size,
                    Edid = showEdid ? edidMapA?.GetValueOrDefault(kvp.Key) : null,
                    BaseObjectFormId = info.BaseObjectFormId,
                    BaseObjectEdid = baseObjEdid,
                    BaseObjectType = baseObjType
                });
            }
        }

        foreach (var kvp in recordsB)
        {
            if (!recordsA.ContainsKey(kvp.Key))
            {
                var info = kvp.Value;
                var baseObjEdid = resolve && info.BaseObjectFormId.HasValue
                    ? edidMapB?.GetValueOrDefault(info.BaseObjectFormId.Value)
                    : null;
                var baseObjType = resolve && info.BaseObjectFormId.HasValue
                    ? recordTypeMapB?.GetValueOrDefault(info.BaseObjectFormId.Value)
                    : null;

                onlyInB.Add(new RecordSummary
                {
                    FormId = kvp.Key,
                    Type = info.Type,
                    Size = info.Size,
                    Edid = showEdid ? edidMapB?.GetValueOrDefault(kvp.Key) : null,
                    BaseObjectFormId = info.BaseObjectFormId,
                    BaseObjectEdid = baseObjEdid,
                    BaseObjectType = baseObjType
                });
            }
        }

        // Display statistics
        AnsiConsole.MarkupLine("[bold]Record Diff Statistics:[/]");
        var statsTable = new Table();
        statsTable.AddColumn("Metric");
        statsTable.AddColumn("Count");
        statsTable.Border = TableBorder.Rounded;

        statsTable.AddRow("Records in File A", recordsA.Count.ToString("N0"));
        statsTable.AddRow("Records in File B", recordsB.Count.ToString("N0"));
        statsTable.AddRow("[yellow]Only in File A[/]", $"[yellow]{onlyInA.Count:N0}[/]");
        statsTable.AddRow("[cyan]Only in File B[/]", $"[cyan]{onlyInB.Count:N0}[/]");

        AnsiConsole.Write(statsTable);
        AnsiConsole.WriteLine();

        // Group by type for File A only
        if (onlyInA.Count > 0)
        {
            AnsiConsole.MarkupLine($"[bold yellow]Records only in File A ({onlyInA.Count:N0} total):[/]");
            DisplayRecordsByType(onlyInA, limit, showEdid, "[yellow]");

            if (resolve)
            {
                DisplayBaseObjectSummary(onlyInA, limit, "[yellow]", "File A");
            }
        }

        // Group by type for File B only
        if (onlyInB.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold cyan]Records only in File B ({onlyInB.Count:N0} total):[/]");
            DisplayRecordsByType(onlyInB, limit, showEdid, "[cyan]");

            if (resolve)
            {
                DisplayBaseObjectSummary(onlyInB, limit, "[cyan]", "File B");
            }
        }

        // Write TSV output if requested
        if (!string.IsNullOrEmpty(outputPath))
        {
            using var writer = new StreamWriter(outputPath);
            if (resolve)
            {
                writer.WriteLine("Source\tFormID\tType\tSize\tEDID\tBaseObjFormID\tBaseObjType\tBaseObjEDID");
                foreach (var r in onlyInA)
                {
                    writer.WriteLine($"A-only\t0x{r.FormId:X8}\t{r.Type}\t{r.Size}\t{r.Edid ?? ""}\t{(r.BaseObjectFormId.HasValue ? $"0x{r.BaseObjectFormId.Value:X8}" : "")}\t{r.BaseObjectType ?? ""}\t{r.BaseObjectEdid ?? ""}");
                }
                foreach (var r in onlyInB)
                {
                    writer.WriteLine($"B-only\t0x{r.FormId:X8}\t{r.Type}\t{r.Size}\t{r.Edid ?? ""}\t{(r.BaseObjectFormId.HasValue ? $"0x{r.BaseObjectFormId.Value:X8}" : "")}\t{r.BaseObjectType ?? ""}\t{r.BaseObjectEdid ?? ""}");
                }
            }
            else
            {
                writer.WriteLine("Source\tFormID\tType\tSize\tEDID");
                foreach (var r in onlyInA)
                {
                    writer.WriteLine($"A-only\t0x{r.FormId:X8}\t{r.Type}\t{r.Size}\t{r.Edid ?? ""}");
                }
                foreach (var r in onlyInB)
                {
                    writer.WriteLine($"B-only\t0x{r.FormId:X8}\t{r.Type}\t{r.Size}\t{r.Edid ?? ""}");
                }
            }

            AnsiConsole.MarkupLine($"[grey]Full results written to: {outputPath}[/]");
        }

        return 0;
    }

    private static void DisplayRecordsByType(List<RecordSummary> records, int limit, bool showEdid, string colorTag)
    {
        var byType = records.GroupBy(r => r.Type)
            .OrderByDescending(g => g.Count())
            .ToList();

        // Summary table
        var summaryTable = new Table();
        summaryTable.AddColumn("Type");
        summaryTable.AddColumn("Count");
        summaryTable.Border = TableBorder.Rounded;

        foreach (var group in byType)
        {
            summaryTable.AddRow($"{colorTag}{group.Key}[/]", group.Count().ToString("N0"));
        }

        AnsiConsole.Write(summaryTable);

        // Detail table (limited)
        if (showEdid)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Sample Records (with EDIDs):[/]");

            var detailTable = new Table();
            detailTable.AddColumn("FormID");
            detailTable.AddColumn("Type");
            detailTable.AddColumn("Size");
            detailTable.AddColumn("EDID");
            detailTable.Border = TableBorder.Rounded;

            // Show records with EDIDs first, then without
            var withEdid = records.Where(r => r.Edid != null).Take(limit / 2).ToList();
            var withoutEdid = records.Where(r => r.Edid == null).Take(limit / 2).ToList();

            foreach (var r in withEdid.Concat(withoutEdid).Take(limit))
            {
                detailTable.AddRow(
                    $"0x{r.FormId:X8}",
                    r.Type,
                    $"{r.Size:N0}",
                    r.Edid ?? "[grey](none)[/]");
            }

            AnsiConsole.Write(detailTable);
        }
    }

    private static void DisplayBaseObjectSummary(List<RecordSummary> records, int limit, string colorTag, string fileName)
    {
        // Filter to only placed references (REFR, ACHR, ACRE)
        var placedRefs = records.Where(r => r.Type is "REFR" or "ACHR" or "ACRE" && r.BaseObjectFormId.HasValue).ToList();

        if (placedRefs.Count == 0)
            return;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Base Objects Placed (unique in {fileName}):[/]");

        // Group by base object
        var byBaseObject = placedRefs
            .GroupBy(r => new { r.BaseObjectFormId, r.BaseObjectType, r.BaseObjectEdid })
            .Select(g => new
            {
                FormId = g.Key.BaseObjectFormId!.Value,
                Type = g.Key.BaseObjectType ?? "???",
                Edid = g.Key.BaseObjectEdid,
                Count = g.Count()
            })
            .OrderByDescending(x => x.Count)
            .ToList();

        var table = new Table();
        table.AddColumn("Base Object");
        table.AddColumn("Type");
        table.AddColumn("EDID");
        table.AddColumn("Count");
        table.Border = TableBorder.Rounded;

        foreach (var obj in byBaseObject.Take(limit))
        {
            table.AddRow(
                $"0x{obj.FormId:X8}",
                $"{colorTag}{obj.Type}[/]",
                obj.Edid ?? "[grey](none)[/]",
                obj.Count.ToString("N0"));
        }

        AnsiConsole.Write(table);

        if (byBaseObject.Count > limit)
        {
            AnsiConsole.MarkupLine($"[grey]... and {byBaseObject.Count - limit:N0} more base objects[/]");
        }

        AnsiConsole.MarkupLine($"[grey]Total unique base objects: {byBaseObject.Count:N0}, Total placements: {placedRefs.Count:N0}[/]");
    }

    private static Dictionary<uint, string> BuildRecordTypeMap(byte[] data, bool bigEndian)
    {
        var dict = new Dictionary<uint, string>();
        var records = EsmHelpers.ScanAllRecords(data, bigEndian);

        foreach (var record in records)
        {
            if (record.Signature == "GRUP" || record.Signature == "TES4")
                continue;

            dict[record.FormId] = record.Signature;
        }

        return dict;
    }

    private static Dictionary<uint, RecordFullInfo> ScanRecordsWithBaseObject(byte[] data, bool bigEndian, string? typeFilter)
    {
        var dict = new Dictionary<uint, RecordFullInfo>();
        var records = EsmHelpers.ScanAllRecords(data, bigEndian);

        foreach (var record in records)
        {
            if (record.Signature == "GRUP" || record.Signature == "TES4")
                continue;

            if (typeFilter != null && record.Signature != typeFilter)
                continue;

            uint? baseObjectFormId = null;

            // For REFR, ACHR, ACRE - extract the NAME subrecord (base object FormID)
            if (record.Signature is "REFR" or "ACHR" or "ACRE")
            {
                baseObjectFormId = ExtractBaseObjectFormId(data, record, bigEndian);
            }

            dict[record.FormId] = new RecordFullInfo
            {
                Type = record.Signature,
                Size = record.DataSize,
                BaseObjectFormId = baseObjectFormId
            };
        }

        return dict;
    }

    private static uint? ExtractBaseObjectFormId(byte[] data, AnalyzerRecordInfo record, bool bigEndian)
    {
        var recordDataStart = (int)record.Offset + EsmParser.MainRecordHeaderSize;
        var recordDataEnd = recordDataStart + (int)record.DataSize;

        if (recordDataEnd > data.Length)
            return null;

        var recordData = data.AsSpan(recordDataStart, (int)record.DataSize).ToArray();

        // Handle compressed records
        if ((record.Flags & 0x00040000) != 0 && record.DataSize >= 4)
        {
            var decompressedSize = EsmBinary.ReadUInt32(recordData, 0, bigEndian);
            if (decompressedSize > 0 && decompressedSize < 100_000_000)
            {
                try
                {
                    recordData = EsmHelpers.DecompressZlib(recordData[4..], (int)decompressedSize);
                }
                catch
                {
                    return null;
                }
            }
        }

        // Parse subrecords to find NAME
        var subrecords = EsmHelpers.ParseSubrecords(recordData, bigEndian);
        foreach (var sub in subrecords)
        {
            if (sub.Signature == "NAME" && sub.Data.Length >= 4)
            {
                // NAME is the base object FormID (already converted to little-endian in converted file)
                return BitConverter.ToUInt32(sub.Data, 0);
            }
        }

        return null;
    }

    private static Dictionary<uint, (string Type, uint Size)> ScanRecordsToDict(byte[] data, bool bigEndian, string? typeFilter)
    {
        var dict = new Dictionary<uint, (string Type, uint Size)>();
        var records = EsmHelpers.ScanAllRecords(data, bigEndian);

        foreach (var record in records)
        {
            if (record.Signature == "GRUP" || record.Signature == "TES4")
                continue;

            if (typeFilter != null && record.Signature != typeFilter)
                continue;

            dict[record.FormId] = (record.Signature, record.DataSize);
        }

        return dict;
    }

    private sealed class RecordSummary
    {
        public uint FormId { get; init; }
        public required string Type { get; init; }
        public uint Size { get; init; }
        public string? Edid { get; init; }
        public uint? BaseObjectFormId { get; init; }
        public string? BaseObjectEdid { get; init; }
        public string? BaseObjectType { get; init; }
    }

    private sealed class RecordFullInfo
    {
        public required string Type { get; init; }
        public uint Size { get; init; }
        public uint? BaseObjectFormId { get; init; }
    }
}
