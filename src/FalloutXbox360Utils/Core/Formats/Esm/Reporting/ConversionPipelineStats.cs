namespace FalloutXbox360Utils.Core.Formats.Esm.Reporting;

/// <summary>
///     Running totals for a DMP→ESP conversion pipeline run.
/// </summary>
public sealed class ConversionPipelineStats
{
    public int RecordsConsidered { get; set; }
    public int RecordsEmitted { get; set; }
    public int RecordsSkipped { get; set; }
    public int RecordsFailed { get; set; }
    public int OverridesEmitted { get; set; }
    public int NewRecordsEmitted { get; set; }
    public int CellsMerged { get; set; }
    public int Warnings { get; set; }
    public int Errors { get; set; }

    /// <summary>Per-record-type counts of records emitted to the output ESP.</summary>
    public Dictionary<string, int> EmittedByType { get; } = new(StringComparer.Ordinal);

    /// <summary>Per-record-type counts of records skipped (not yet supported in v1).</summary>
    public Dictionary<string, int> SkippedByType { get; } = new(StringComparer.Ordinal);

    /// <summary>Output bytes written.</summary>
    public long OutputBytes { get; set; }

    public TimeSpan Elapsed { get; set; }

    public void IncrementEmitted(string recordType)
    {
        RecordsEmitted++;
        EmittedByType[recordType] = EmittedByType.GetValueOrDefault(recordType) + 1;
    }

    public void IncrementSkipped(string recordType)
    {
        RecordsSkipped++;
        SkippedByType[recordType] = SkippedByType.GetValueOrDefault(recordType) + 1;
    }
}
