using System.Buffers.Binary;
using System.Text;
using EsmAnalyzer.Conversion.Schema;
using EsmAnalyzer.Helpers;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

public static partial class DumpCommands
{
    private static int ValidateRefs(string filePath, string? filterType, int limit, string? outputPath)
    {
        var esm = EsmFileLoader.Load(filePath);
        if (esm == null) return 1;

        var typeFilter = string.IsNullOrWhiteSpace(filterType)
            ? null
            : filterType.Trim().ToUpperInvariant();

        var records = EsmHelpers.ScanAllRecords(esm.Data, esm.IsBigEndian)
            .Where(r => r.Signature != "GRUP")
            .ToList();

        var formIds = new HashSet<uint>(records.Select(r => r.FormId));
        var result = ValidateRefsInternal(esm, records, typeFilter, limit, formIds);

        AnsiConsole.MarkupLine(
            $"[cyan]Reference validation[/] Checked {result.CheckedRefs:N0} refs, missing {result.Missing:N0}, compressed skipped {result.CompressedSkipped:N0}");

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            WriteRefValidationResults(outputPath, result);
            AnsiConsole.MarkupLine($"[green]Saved[/] {result.Findings.Count:N0} missing references to {outputPath}");
        }
        else if (result.Missing > 0)
        {
            AnsiConsole.Write(result.Table);
        }

        return result.Missing == 0 ? 0 : 1;
    }

    private static RefValidationResult ValidateRefsInternal(
        EsmFileLoadResult esm,
        List<AnalyzerRecordInfo> records,
        string? typeFilter,
        int limit,
        HashSet<uint> formIds)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Record")
            .AddColumn("Subrecord")
            .AddColumn("FormID")
            .AddColumn("Issue");

        var stats = new RefValidationStats();
        var findings = new List<RefValidationFinding>();

        var context = new RefValidationContext(esm, formIds, limit, stats, table, findings);

        foreach (var record in records)
        {
            if (!RecordMatchesFilter(record, typeFilter))
                continue;

            if (IsCompressed(record))
            {
                context.Stats.CompressedSkipped++;
                continue;
            }

            if (ProcessRecordRefs(record, context))
                break;
        }

        return new RefValidationResult(stats.CheckedRefs, stats.Missing, stats.CompressedSkipped, table, findings);
    }

    private static bool ProcessRecordRefs(
        AnalyzerRecordInfo record,
        RefValidationContext context)
    {
        var recordData = EsmHelpers.GetRecordData(context.Esm.Data, record, context.Esm.IsBigEndian);
        var subrecords = EsmHelpers.ParseSubrecords(recordData, context.Esm.IsBigEndian);

        foreach (var sub in subrecords)
        {
            if (context.Limit > 0 && context.Stats.Missing >= context.Limit)
                return true;

            if (ProcessSubrecordRefs(sub, record, context))
                return true;
        }

        return false;
    }

    private static bool ProcessSubrecordRefs(
        AnalyzerSubrecordInfo sub,
        AnalyzerRecordInfo record,
        RefValidationContext context)
    {
        var schema = SubrecordSchemaRegistry.FindSchema(sub.Signature, record.Signature, sub.Data.Length);
        if (schema == null || (!schema.IsFormId && !schema.IsFormIdArray))
            return false;

        foreach (var value in EnumerateFormIds(schema, sub, context.Esm.IsBigEndian))
        {
            context.Stats.CheckedRefs++;

            if (!IsMissingFormId(value, context.FormIds))
                continue;

            context.Table.AddRow(
                $"{record.Signature} 0x{record.FormId:X8}",
                sub.Signature,
                $"0x{value:X8}",
                "Missing target");
            context.Findings.Add(new RefValidationFinding(record.Signature, record.FormId, sub.Signature, value,
                "Missing target"));
            context.Stats.Missing++;

            if (context.Limit > 0 && context.Stats.Missing >= context.Limit)
                return true;
        }

        return false;
    }

    private static void WriteRefValidationResults(string outputPath, RefValidationResult result)
    {
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");

        using var writer = new StreamWriter(fullPath, false, Encoding.UTF8);
        writer.WriteLine("RecordType\tRecordFormId\tSubrecord\tTargetFormId\tIssue");

        foreach (var finding in result.Findings)
        {
            writer.Write(finding.RecordType);
            writer.Write('\t');
            writer.Write($"0x{finding.RecordFormId:X8}");
            writer.Write('\t');
            writer.Write(finding.Subrecord);
            writer.Write('\t');
            writer.Write($"0x{finding.TargetFormId:X8}");
            writer.Write('\t');
            writer.WriteLine(finding.Issue);
        }
    }

    private static bool RecordMatchesFilter(AnalyzerRecordInfo record, string? typeFilter)
    {
        return typeFilter == null || record.Signature == typeFilter;
    }

    private static bool IsCompressed(AnalyzerRecordInfo record)
    {
        return (record.Flags & 0x00040000) != 0;
    }

    private static bool IsMissingFormId(uint value, HashSet<uint> formIds)
    {
        return value != 0 && !formIds.Contains(value);
    }

    private static IEnumerable<uint> EnumerateFormIds(SubrecordSchema schema, AnalyzerSubrecordInfo sub, bool bigEndian)
    {
        if (schema.Fields.Length == 0)
            yield break;

        var field = schema.Fields[0];
        var offset = Math.Max(0, field.Offset);

        if (schema.IsFormId)
        {
            if (offset + 4 <= sub.Data.Length)
                yield return ReadUInt32(sub.Data, offset, bigEndian);
            yield break;
        }

        if (!schema.IsFormIdArray)
            yield break;

        if (offset >= sub.Data.Length)
            yield break;

        var count = field.Count < 0
            ? (sub.Data.Length - offset) / 4
            : field.Count;

        for (var i = 0; i < count; i++)
        {
            var valueOffset = offset + i * 4;
            if (valueOffset + 4 > sub.Data.Length)
                yield break;

            yield return ReadUInt32(sub.Data, valueOffset, bigEndian);
        }
    }

    private static uint ReadUInt32(byte[] data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    }

    private sealed record RefValidationFinding(
        string RecordType,
        uint RecordFormId,
        string Subrecord,
        uint TargetFormId,
        string Issue);

    private sealed record RefValidationContext(
        EsmFileLoadResult Esm,
        HashSet<uint> FormIds,
        int Limit,
        RefValidationStats Stats,
        Table Table,
        List<RefValidationFinding> Findings);

    private sealed record RefValidationResult(
        int CheckedRefs,
        int Missing,
        int CompressedSkipped,
        Table Table,
        List<RefValidationFinding> Findings);

    private sealed class RefValidationStats
    {
        public int CheckedRefs { get; set; }
        public int Missing { get; set; }
        public int CompressedSkipped { get; set; }
    }
}