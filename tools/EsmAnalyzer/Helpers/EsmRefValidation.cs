using System.Buffers.Binary;
using EsmAnalyzer.Conversion.Schema;

namespace EsmAnalyzer.Helpers;

internal sealed record RefValidationFinding(
    string RecordType,
    uint RecordFormId,
    string Subrecord,
    uint TargetFormId,
    string Issue);

internal sealed class RefValidationStats
{
    public int CheckedRefs { get; set; }
    public int Missing { get; set; }
    public int CompressedSkipped { get; set; }
}

internal sealed record RefValidationResult(
    RefValidationStats Stats,
    List<RefValidationFinding> Findings,
    Dictionary<string, int> MissingByRecordType,
    Dictionary<string, int> MissingBySubrecord);

internal static class EsmRefValidation
{
    public static RefValidationResult Validate(byte[] data, bool bigEndian, string? typeFilter = null,
        int limit = 0)
    {
        var records = EsmHelpers.ScanAllRecords(data, bigEndian)
            .Where(r => r.Signature != "GRUP")
            .ToList();

        var formIds = new HashSet<uint>(records.Select(r => r.FormId));
        var stats = new RefValidationStats();
        var findings = new List<RefValidationFinding>();
        var missingByRecordType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var missingBySubrecord = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in records)
        {
            if (!string.IsNullOrWhiteSpace(typeFilter) &&
                !record.Signature.Equals(typeFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            if ((record.Flags & 0x00040000) != 0)
            {
                stats.CompressedSkipped++;
                continue;
            }

            var recordData = EsmHelpers.GetRecordData(data, record, bigEndian);
            var subrecords = EsmHelpers.ParseSubrecords(recordData, bigEndian);

            foreach (var sub in subrecords)
            {
                if (limit > 0 && stats.Missing >= limit)
                    return new RefValidationResult(stats, findings, missingByRecordType, missingBySubrecord);

                var schema = SubrecordSchemaRegistry.FindSchema(sub.Signature, record.Signature, sub.Data.Length);
                if (schema == null || (!schema.IsFormId && !schema.IsFormIdArray))
                    continue;

                foreach (var value in EnumerateFormIds(schema, sub, bigEndian))
                {
                    stats.CheckedRefs++;

                    if (value == 0 || formIds.Contains(value))
                        continue;

                    findings.Add(new RefValidationFinding(record.Signature, record.FormId, sub.Signature, value,
                        "Missing target"));
                    stats.Missing++;

                    if (!missingByRecordType.TryAdd(record.Signature, 1))
                        missingByRecordType[record.Signature]++;

                    if (!missingBySubrecord.TryAdd(sub.Signature, 1))
                        missingBySubrecord[sub.Signature]++;

                    if (limit > 0 && stats.Missing >= limit)
                        return new RefValidationResult(stats, findings, missingByRecordType, missingBySubrecord);
                }
            }
        }

        return new RefValidationResult(stats, findings, missingByRecordType, missingBySubrecord);
    }

    private static IEnumerable<uint> EnumerateFormIds(SubrecordSchema schema, AnalyzerSubrecordInfo sub,
        bool bigEndian)
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
}