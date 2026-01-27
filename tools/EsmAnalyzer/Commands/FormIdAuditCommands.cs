using System.CommandLine;
using System.Security.Cryptography;
using EsmAnalyzer.Conversion.Schema;
using EsmAnalyzer.Core;
using EsmAnalyzer.Helpers;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;

namespace EsmAnalyzer.Commands;

/// <summary>
///     FormID audit command - finds FormID references that resolve differently between two ESM files.
/// </summary>
public static class FormIdAuditCommands
{
    /// <summary>
    ///     Creates the 'formid-audit' command.
    /// </summary>
    public static Command CreateFormIdAuditCommand()
    {
        var command = new Command("formid-audit", "Audit FormID references - find references that resolve differently between two ESM files");

        var convertedArg = new Argument<string>("converted") { Description = "Converted ESM file" };
        var pcArg = new Argument<string>("pc") { Description = "PC reference ESM file" };
        var typeOption = new Option<string?>("-t", "--type")
        { Description = "Record type to filter (e.g., REFR, QUST, NPC_)" };
        var limitOption = new Option<int>("-l", "--limit")
        { Description = "Max mismatches to show (default: 100)", DefaultValueFactory = _ => 100 };
        var outputOption = new Option<string?>("-o", "--output")
        { Description = "Output TSV file for full results" };
        var deepOption = new Option<bool>("--deep")
        { Description = "Deep audit: also check records without EDIDs by comparing type/content" };

        command.Arguments.Add(convertedArg);
        command.Arguments.Add(pcArg);
        command.Options.Add(typeOption);
        command.Options.Add(limitOption);
        command.Options.Add(outputOption);
        command.Options.Add(deepOption);

        command.SetAction(parseResult =>
        {
            var convertedPath = parseResult.GetValue(convertedArg)!;
            var pcPath = parseResult.GetValue(pcArg)!;
            var recordType = parseResult.GetValue(typeOption);
            var limit = parseResult.GetValue(limitOption);
            var outputPath = parseResult.GetValue(outputOption);
            var deep = parseResult.GetValue(deepOption);

            return RunFormIdAudit(convertedPath, pcPath, recordType, limit, outputPath, deep);
        });

        return command;
    }

    private static int RunFormIdAudit(string convertedPath, string pcPath, string? recordTypeFilter, int limit, string? outputPath, bool deep = false)
    {
        AnsiConsole.MarkupLine("[bold cyan]FormID Reference Audit[/]");
        AnsiConsole.MarkupLine($"[grey]Converted:[/] {convertedPath}");
        AnsiConsole.MarkupLine($"[grey]PC Reference:[/] {pcPath}");
        if (deep)
            AnsiConsole.MarkupLine("[yellow]Deep mode:[/] Checking records without EDIDs");
        AnsiConsole.WriteLine();

        // Load files using the standard loader
        var converted = EsmFileLoader.Load(convertedPath, printStatus: false);
        var pc = EsmFileLoader.Load(pcPath, printStatus: false);

        if (converted == null || pc == null)
        {
            return 1;
        }

        var convertedData = converted.Data;
        var pcData = pc.Data;
        var convertedBigEndian = converted.IsBigEndian;
        var pcBigEndian = pc.IsBigEndian;

        AnsiConsole.MarkupLine("[grey]Building FormID resolution maps...[/]");
        var convertedEdidMap = EsmHelpers.BuildFormIdToEdidMap(convertedData, convertedBigEndian);
        var pcEdidMap = EsmHelpers.BuildFormIdToEdidMap(pcData, pcBigEndian);
        AnsiConsole.MarkupLine($"[grey]Loaded {convertedEdidMap.Count} Converted, {pcEdidMap.Count} PC EDIDs[/]");

        // Build record info maps for deep audit
        Dictionary<uint, RecordInfo>? convertedRecordMap = null;
        Dictionary<uint, RecordInfo>? pcRecordMap = null;

        if (deep)
        {
            AnsiConsole.MarkupLine("[grey]Building record info maps for deep audit...[/]");
            convertedRecordMap = BuildRecordInfoMap(convertedData, convertedBigEndian);
            pcRecordMap = BuildRecordInfoMap(pcData, pcBigEndian);
            AnsiConsole.MarkupLine($"[grey]Loaded {convertedRecordMap.Count} Converted, {pcRecordMap.Count} PC records[/]");
        }

        AnsiConsole.WriteLine();

        // Scan records and collect FormID references
        var mismatches = new List<FormIdMismatch>();
        var deepMismatches = new List<DeepMismatch>();
        var stats = new AuditStats();

        AnsiConsole.MarkupLine("[grey]Scanning records for FormID references...[/]");

        var convertedRecords = EsmHelpers.ScanAllRecords(convertedData, convertedBigEndian);

        foreach (var record in convertedRecords)
        {
            if (record.Signature == "GRUP" || record.Signature == "TES4")
                continue;

            // Apply record type filter
            if (recordTypeFilter != null && record.Signature != recordTypeFilter)
                continue;

            stats.RecordsScanned++;

            // Get record data
            var recordDataStart = (int)record.Offset + EsmParser.MainRecordHeaderSize;
            var recordDataEnd = recordDataStart + (int)record.DataSize;

            if (recordDataEnd > convertedData.Length)
                continue;

            var recordData = convertedData.AsSpan(recordDataStart, (int)record.DataSize).ToArray();

            // Handle compressed records
            if ((record.Flags & 0x00040000) != 0 && record.DataSize >= 4)
            {
                var decompressedSize = EsmBinary.ReadUInt32(recordData, 0, convertedBigEndian);
                if (decompressedSize > 0 && decompressedSize < 100_000_000)
                {
                    try
                    {
                        recordData = EsmHelpers.DecompressZlib(recordData[4..], (int)decompressedSize);
                    }
                    catch
                    {
                        continue;
                    }
                }
            }

            // Parse subrecords
            var subrecords = EsmHelpers.ParseSubrecords(recordData, convertedBigEndian);

            foreach (var sub in subrecords)
            {
                // Get schema for this subrecord
                var schema = SubrecordSchemaRegistry.GetSchema(sub.Signature, record.Signature, sub.Data.Length);
                if (schema == null)
                    continue;

                // Check each field for FormID types
                var offset = 0;
                foreach (var field in schema.Fields)
                {
                    if (offset >= sub.Data.Length)
                        break;

                    var fieldSize = field.EffectiveSize;
                    if (fieldSize <= 0)
                        fieldSize = sub.Data.Length - offset;

                    if (field.Type == SubrecordFieldType.FormId || field.Type == SubrecordFieldType.FormIdLittleEndian)
                    {
                        if (offset + 4 <= sub.Data.Length)
                        {
                            stats.FormIdFieldsChecked++;

                            // Read FormID (already little-endian in converted file)
                            var formId = BitConverter.ToUInt32(sub.Data, offset);

                            if (formId != 0) // Skip null references
                            {
                                // Look up in both EDID maps
                                var convertedEdid = convertedEdidMap.GetValueOrDefault(formId, null);
                                var pcEdid = pcEdidMap.GetValueOrDefault(formId, null);

                                // Check for mismatch
                                if (convertedEdid != pcEdid)
                                {
                                    stats.Mismatches++;
                                    if (mismatches.Count < limit)
                                    {
                                        mismatches.Add(new FormIdMismatch
                                        {
                                            RecordType = record.Signature,
                                            RecordFormId = record.FormId,
                                            SubrecordType = sub.Signature,
                                            FieldName = field.Name,
                                            ReferencedFormId = formId,
                                            ConvertedEdid = convertedEdid,
                                            PcEdid = pcEdid
                                        });
                                    }
                                }
                                else if (convertedEdid != null)
                                {
                                    stats.Matches++;
                                }
                                else
                                {
                                    // Both unresolved - do deep check if enabled
                                    if (deep && convertedRecordMap != null && pcRecordMap != null)
                                    {
                                        var deepResult = CheckDeepMismatch(formId, convertedRecordMap, pcRecordMap);
                                        if (deepResult != null)
                                        {
                                            stats.DeepMismatches++;
                                            if (deepMismatches.Count < limit)
                                            {
                                                deepResult.SourceRecordType = record.Signature;
                                                deepResult.SourceRecordFormId = record.FormId;
                                                deepResult.SubrecordType = sub.Signature;
                                                deepResult.FieldName = field.Name;
                                                deepMismatches.Add(deepResult);
                                            }
                                        }
                                        else
                                        {
                                            stats.DeepMatches++;
                                        }
                                    }
                                    else
                                    {
                                        stats.UnresolvedBoth++;
                                    }
                                }
                            }
                        }
                    }

                    offset += fieldSize;
                }
            }
        }

        // Display results
        AnsiConsole.MarkupLine("[bold]Audit Statistics:[/]");
        var statsTable = new Table();
        statsTable.AddColumn("Metric");
        statsTable.AddColumn("Count");
        statsTable.Border = TableBorder.Rounded;

        statsTable.AddRow("Records scanned", stats.RecordsScanned.ToString("N0"));
        statsTable.AddRow("FormID fields checked", stats.FormIdFieldsChecked.ToString("N0"));
        statsTable.AddRow("[green]Matching references (EDID)[/]", $"[green]{stats.Matches:N0}[/]");
        statsTable.AddRow("[yellow]Mismatched references (EDID)[/]", $"[yellow]{stats.Mismatches:N0}[/]");

        if (deep)
        {
            statsTable.AddRow("[green]Matching references (deep)[/]", $"[green]{stats.DeepMatches:N0}[/]");
            statsTable.AddRow("[red]Mismatched references (deep)[/]", $"[red]{stats.DeepMismatches:N0}[/]");
        }
        else
        {
            statsTable.AddRow("[grey]Unresolved in both[/]", $"[grey]{stats.UnresolvedBoth:N0}[/]");
        }

        AnsiConsole.Write(statsTable);
        AnsiConsole.WriteLine();

        if (mismatches.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No FormID resolution mismatches found![/]");
            return 0;
        }

        // Group mismatches by type
        var byType = mismatches.GroupBy(m => m.RecordType)
            .OrderByDescending(g => g.Count())
            .ToList();

        AnsiConsole.MarkupLine($"[bold yellow]Found {stats.Mismatches:N0} mismatches[/]");
        if (stats.Mismatches > limit)
            AnsiConsole.MarkupLine($"[grey](showing first {limit})[/]");
        AnsiConsole.WriteLine();

        // Summary by record type
        AnsiConsole.MarkupLine("[bold]Mismatches by Record Type:[/]");
        var summaryTable = new Table();
        summaryTable.AddColumn("Record Type");
        summaryTable.AddColumn("Count");
        summaryTable.Border = TableBorder.Rounded;

        foreach (var group in byType.Take(20))
        {
            summaryTable.AddRow(group.Key, group.Count().ToString("N0"));
        }

        AnsiConsole.Write(summaryTable);
        AnsiConsole.WriteLine();

        // Detailed mismatches
        AnsiConsole.MarkupLine("[bold]Mismatch Details:[/]");
        var detailTable = new Table();
        detailTable.AddColumn("Record");
        detailTable.AddColumn("Subrecord");
        detailTable.AddColumn("Field");
        detailTable.AddColumn("FormID");
        detailTable.AddColumn("Converted EDID");
        detailTable.AddColumn("PC EDID");
        detailTable.Border = TableBorder.Rounded;

        foreach (var m in mismatches.Take(50))
        {
            var convertedEdid = m.ConvertedEdid ?? "[grey](not found)[/]";
            var pcEdid = m.PcEdid ?? "[grey](not found)[/]";

            detailTable.AddRow(
                $"{m.RecordType} {m.RecordFormId:X8}",
                m.SubrecordType,
                m.FieldName,
                $"0x{m.ReferencedFormId:X8}",
                convertedEdid,
                pcEdid);
        }

        AnsiConsole.Write(detailTable);

        // Display deep mismatches if any
        if (deep && deepMismatches.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold red]Deep Audit: Found {stats.DeepMismatches:N0} mismatches in records without EDIDs[/]");
            if (stats.DeepMismatches > limit)
                AnsiConsole.MarkupLine($"[grey](showing first {limit})[/]");
            AnsiConsole.WriteLine();

            // Group by mismatch type
            var deepByType = deepMismatches.GroupBy(m => m.MismatchType).OrderByDescending(g => g.Count()).ToList();

            AnsiConsole.MarkupLine("[bold]Deep Mismatches by Type:[/]");
            var deepSummaryTable = new Table();
            deepSummaryTable.AddColumn("Mismatch Type");
            deepSummaryTable.AddColumn("Count");
            deepSummaryTable.Border = TableBorder.Rounded;

            foreach (var group in deepByType)
            {
                deepSummaryTable.AddRow(group.Key, group.Count().ToString("N0"));
            }

            AnsiConsole.Write(deepSummaryTable);
            AnsiConsole.WriteLine();

            // Detail table
            AnsiConsole.MarkupLine("[bold]Deep Mismatch Details:[/]");
            var deepDetailTable = new Table();
            deepDetailTable.AddColumn("FormID");
            deepDetailTable.AddColumn("Type");
            deepDetailTable.AddColumn("Converted");
            deepDetailTable.AddColumn("PC");
            deepDetailTable.AddColumn("Referenced By");
            deepDetailTable.Border = TableBorder.Rounded;

            foreach (var m in deepMismatches.Take(50))
            {
                var convertedInfo = m.ConvertedType != null
                    ? $"{m.ConvertedType} ({m.ConvertedSize:N0}b)"
                    : "[grey](not found)[/]";
                var pcInfo = m.PcType != null
                    ? $"{m.PcType} ({m.PcSize:N0}b)"
                    : "[grey](not found)[/]";

                deepDetailTable.AddRow(
                    $"0x{m.FormId:X8}",
                    m.MismatchType,
                    convertedInfo,
                    pcInfo,
                    $"{m.SourceRecordType} {m.SourceRecordFormId:X8}.{m.SubrecordType}");
            }

            AnsiConsole.Write(deepDetailTable);
        }

        // Write TSV output if requested
        if (!string.IsNullOrEmpty(outputPath))
        {
            using var writer = new StreamWriter(outputPath);
            writer.WriteLine("Type\tRecordType\tRecordFormId\tSubrecord\tField\tReferencedFormId\tConvertedInfo\tPcInfo");

            foreach (var m in mismatches)
            {
                writer.WriteLine($"EDID\t{m.RecordType}\t0x{m.RecordFormId:X8}\t{m.SubrecordType}\t{m.FieldName}\t0x{m.ReferencedFormId:X8}\t{m.ConvertedEdid ?? "(null)"}\t{m.PcEdid ?? "(null)"}");
            }

            foreach (var m in deepMismatches)
            {
                var convertedInfo = m.ConvertedType != null ? $"{m.ConvertedType}:{m.ConvertedSize}" : "(null)";
                var pcInfo = m.PcType != null ? $"{m.PcType}:{m.PcSize}" : "(null)";
                writer.WriteLine($"DEEP:{m.MismatchType}\t{m.SourceRecordType}\t0x{m.SourceRecordFormId:X8}\t{m.SubrecordType}\t{m.FieldName}\t0x{m.FormId:X8}\t{convertedInfo}\t{pcInfo}");
            }

            AnsiConsole.MarkupLine($"[grey]Full results written to: {outputPath}[/]");
        }

        return 0;
    }

    /// <summary>
    ///     Builds a map of FormID → RecordInfo for all records in the file.
    /// </summary>
    private static Dictionary<uint, RecordInfo> BuildRecordInfoMap(byte[] data, bool bigEndian)
    {
        var map = new Dictionary<uint, RecordInfo>();
        var records = EsmHelpers.ScanAllRecords(data, bigEndian);

        foreach (var record in records)
        {
            if (record.Signature == "GRUP" || record.Signature == "TES4")
                continue;

            // Get record data for hashing
            var recordDataStart = (int)record.Offset + EsmParser.MainRecordHeaderSize;
            var recordDataEnd = recordDataStart + (int)record.DataSize;

            if (recordDataEnd > data.Length)
                continue;

            var recordData = data.AsSpan(recordDataStart, (int)record.DataSize).ToArray();
            var actualSize = record.DataSize;

            // Handle compressed records
            if ((record.Flags & 0x00040000) != 0 && record.DataSize >= 4)
            {
                var decompressedSize = EsmBinary.ReadUInt32(recordData, 0, bigEndian);
                if (decompressedSize > 0 && decompressedSize < 100_000_000)
                {
                    try
                    {
                        recordData = EsmHelpers.DecompressZlib(recordData[4..], (int)decompressedSize);
                        actualSize = decompressedSize;
                    }
                    catch
                    {
                        // Use compressed data for hash
                    }
                }
            }

            // Compute content hash
            var hash = Convert.ToHexString(MD5.HashData(recordData));

            map[record.FormId] = new RecordInfo
            {
                Type = record.Signature,
                Size = actualSize,
                ContentHash = hash
            };
        }

        return map;
    }

    /// <summary>
    ///     Checks if an unresolved FormID has mismatched record info between the two files.
    /// </summary>
    private static DeepMismatch? CheckDeepMismatch(
        uint formId,
        Dictionary<uint, RecordInfo> convertedMap,
        Dictionary<uint, RecordInfo> pcMap)
    {
        var inConverted = convertedMap.TryGetValue(formId, out var convertedInfo);
        var inPc = pcMap.TryGetValue(formId, out var pcInfo);

        // Both missing - can't check
        if (!inConverted && !inPc)
            return null;

        // One exists, one doesn't
        if (inConverted != inPc)
        {
            return new DeepMismatch
            {
                FormId = formId,
                MismatchType = inConverted ? "Converted-only" : "PC-only",
                ConvertedType = convertedInfo?.Type,
                ConvertedSize = convertedInfo?.Size ?? 0,
                PcType = pcInfo?.Type,
                PcSize = pcInfo?.Size ?? 0
            };
        }

        // Both exist - check type
        if (convertedInfo!.Type != pcInfo!.Type)
        {
            return new DeepMismatch
            {
                FormId = formId,
                MismatchType = "Type-mismatch",
                ConvertedType = convertedInfo.Type,
                ConvertedSize = convertedInfo.Size,
                PcType = pcInfo.Type,
                PcSize = pcInfo.Size
            };
        }

        // Same type - check size (content hash would be different due to endianness)
        if (convertedInfo.Size != pcInfo.Size)
        {
            return new DeepMismatch
            {
                FormId = formId,
                MismatchType = "Size-mismatch",
                ConvertedType = convertedInfo.Type,
                ConvertedSize = convertedInfo.Size,
                PcType = pcInfo.Type,
                PcSize = pcInfo.Size
            };
        }

        // Same type and size - consider it a match (can't compare content due to endianness)
        return null;
    }

    private sealed class AuditStats
    {
        public int RecordsScanned { get; set; }
        public int FormIdFieldsChecked { get; set; }
        public int Matches { get; set; }
        public int Mismatches { get; set; }
        public int UnresolvedBoth { get; set; }
        public int DeepMatches { get; set; }
        public int DeepMismatches { get; set; }
    }

    private sealed class FormIdMismatch
    {
        public required string RecordType { get; init; }
        public uint RecordFormId { get; init; }
        public required string SubrecordType { get; init; }
        public required string FieldName { get; init; }
        public uint ReferencedFormId { get; init; }
        public string? ConvertedEdid { get; init; }
        public string? PcEdid { get; init; }
    }

    private sealed class RecordInfo
    {
        public required string Type { get; init; }
        public uint Size { get; init; }
        public required string ContentHash { get; init; }
    }

    private sealed class DeepMismatch
    {
        public uint FormId { get; init; }
        public required string MismatchType { get; init; }
        public string? ConvertedType { get; init; }
        public uint ConvertedSize { get; init; }
        public string? PcType { get; init; }
        public uint PcSize { get; init; }

        // Source of the reference
        public string? SourceRecordType { get; set; }
        public uint SourceRecordFormId { get; set; }
        public string? SubrecordType { get; set; }
        public string? FieldName { get; set; }
    }
}
