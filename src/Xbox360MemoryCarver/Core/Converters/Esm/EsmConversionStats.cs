using System.Globalization;
using System.Text;
using static Xbox360MemoryCarver.Core.Converters.Esm.EsmEndianHelpers;

namespace Xbox360MemoryCarver.Core.Converters.Esm;

/// <summary>
///     Statistics tracking and reporting for ESM conversion.
/// </summary>
public sealed class EsmConversionStats
{
    public int RecordsConverted { get; set; }
    public int GrupsConverted { get; set; }
    public int SubrecordsConverted { get; set; }
    public int TopLevelRecordsSkipped { get; set; }
    public int TopLevelGrupsSkipped { get; set; }
    public int ToftTrailingBytesSkipped { get; set; }
    public int OfstStripped { get; set; }
    public long OfstBytesStripped { get; set; }

    public Dictionary<string, int> RecordTypeCounts { get; } = [];
    public Dictionary<string, int> SubrecordTypeCounts { get; } = [];
    public Dictionary<string, int> SkippedRecordTypeCounts { get; } = [];
    public Dictionary<int, int> SkippedGrupTypeCounts { get; } = [];

    /// <summary>
    ///     Increments the record type count.
    /// </summary>
    public void IncrementRecordType(string signature)
    {
        if (!RecordTypeCounts.TryGetValue(signature, out var count))
        {
            count = 0;
        }

        RecordTypeCounts[signature] = count + 1;
    }

    /// <summary>
    ///     Increments the subrecord type count.
    /// </summary>
    public void IncrementSubrecordType(string recordType, string signature)
    {
        var key = $"{recordType}.{signature}";
        if (!SubrecordTypeCounts.TryGetValue(key, out var count))
        {
            count = 0;
        }

        SubrecordTypeCounts[key] = count + 1;
    }

    /// <summary>
    ///     Increments the skipped record type count.
    /// </summary>
    public void IncrementSkippedRecordType(string signature)
    {
        if (!SkippedRecordTypeCounts.TryGetValue(signature, out var count))
        {
            count = 0;
        }

        SkippedRecordTypeCounts[signature] = count + 1;
    }

    /// <summary>
    ///     Increments the skipped GRUP type count.
    /// </summary>
    public void IncrementSkippedGrupType(int grupType)
    {
        if (!SkippedGrupTypeCounts.TryGetValue(grupType, out var count))
        {
            count = 0;
        }

        SkippedGrupTypeCounts[grupType] = count + 1;
    }

    /// <summary>
    ///     Gets a summary of conversion statistics as a string.
    /// </summary>
    public string GetStatsSummary(bool verbose = false)
    {
        var sb = new StringBuilder();

        sb.AppendLine();
        sb.AppendLine("Conversion Statistics:");
        sb.AppendLine($"  Records converted:    {RecordsConverted:N0}");
        sb.AppendLine($"  GRUPs converted:      {GrupsConverted:N0}");
        sb.AppendLine($"  Subrecords converted: {SubrecordsConverted:N0}");

        AppendToftStats(sb);
        AppendOfstStats(sb);
        AppendSkippedStats(sb);

        if (verbose)
        {
            AppendRecordTypeStats(sb);
        }

        return sb.ToString();
    }

    private void AppendToftStats(StringBuilder sb)
    {
        if (ToftTrailingBytesSkipped <= 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("Xbox 360 streaming cache region skipped:");
        sb.AppendLine(
            $"  TOFT trailing data: {ToftTrailingBytesSkipped:N0} bytes ({ToftTrailingBytesSkipped / 1024.0 / 1024.0:F2} MB)");
        sb.AppendLine("  (TOFT + cached records used by the Xbox 360 streaming system)");
    }

    private void AppendOfstStats(StringBuilder sb)
    {
        if (OfstStripped <= 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("OFST subrecords stripped:");
        sb.AppendLine($"  WRLD offset tables: {OfstStripped:N0} subrecords ({OfstBytesStripped:N0} bytes)");
        sb.AppendLine(
            "  (File offsets to cells become invalid after conversion; game scans for cells instead)");
    }

    private void AppendSkippedStats(StringBuilder sb)
    {
        if (TopLevelRecordsSkipped <= 0 && TopLevelGrupsSkipped <= 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("Skipped (Xbox 360 streaming layout artifacts):");

        if (TopLevelGrupsSkipped > 0)
        {
            sb.AppendLine($"  Top-level GRUPs skipped: {TopLevelGrupsSkipped:N0}");

            if (SkippedGrupTypeCounts.Count > 0)
            {
                foreach (var kvp in SkippedGrupTypeCounts.OrderByDescending(x => x.Value))
                {
                    sb.AppendLine($"    {GetGrupTypeName(kvp.Key)}: {kvp.Value.ToString("N0", CultureInfo.InvariantCulture)}");
                }
            }
        }

        if (TopLevelRecordsSkipped > 0)
        {
            sb.AppendLine($"  Top-level records skipped: {TopLevelRecordsSkipped:N0}");
        }

        if (SkippedRecordTypeCounts.Count > 0)
        {
            foreach (var kvp in SkippedRecordTypeCounts.OrderByDescending(x => x.Value))
            {
                sb.AppendLine($"    {kvp.Key}: {kvp.Value.ToString("N0", CultureInfo.InvariantCulture)}");
            }
        }
    }

    private void AppendRecordTypeStats(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("Records by Type:");

        foreach (var kvp in RecordTypeCounts.OrderByDescending(x => x.Value).Take(20))
        {
            sb.AppendLine($"  {kvp.Key}: {kvp.Value:N0}");
        }
    }
}
