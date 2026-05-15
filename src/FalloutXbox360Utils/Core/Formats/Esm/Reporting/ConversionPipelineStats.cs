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

    /// <summary>
    ///     Per-reason counts of drops/skips/decisions, keyed by decision code
    ///     (e.g., "refr.dangling-base", "scol.override-delta-observed").
    /// </summary>
    public Dictionary<string, int> DropReasonCounts { get; } = new(StringComparer.Ordinal);

    /// <summary>SCOL census collected during the conversion pipeline.</summary>
    public ScolCensusStats Scols { get; } = new();

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

    /// <summary>
    ///     Record a decision-coded reason (e.g., a drop code, a remap code, an
    ///     instrumentation-only code). Does not affect <see cref="RecordsSkipped"/>;
    ///     call <see cref="IncrementSkipped"/> separately when the decision is a drop.
    /// </summary>
    public void IncrementDropReason(string code)
    {
        DropReasonCounts[code] = DropReasonCounts.GetValueOrDefault(code) + 1;
    }
}

/// <summary>
///     SCOL-specific census: parsed/in-master/new-emitted counts plus per-part-drop
///     and override-delta diagnostics. Populated by PluginBuilder during a conversion run.
/// </summary>
public sealed class ScolCensusStats
{
    /// <summary>Total SCOLs seen in the DMP.</summary>
    public int TotalParsed { get; set; }

    /// <summary>SCOLs whose FormID matched a master ESM record (override path).</summary>
    public int InMaster { get; set; }

    /// <summary>SCOLs emitted as new records (FormID not in master).</summary>
    public int NewEmitted { get; set; }

    /// <summary>New SCOLs dropped because every ONAM part was unreachable.</summary>
    public int DroppedAllPartsUnreachable { get; set; }

    /// <summary>Total ONAM parts dropped across all new SCOL emissions (unreachable bases).</summary>
    public int PartsDroppedTotal { get; set; }

    /// <summary>
    ///     Number of master-FormID SCOLs whose DMP-side content differs from the master
    ///     (different part count, different ONAM list, or different placement count per part).
    ///     When this is &gt; 0 across an active corpus, SCOL override emission becomes worth
    ///     implementing. Logged via decision code "scol.override-delta-observed".
    /// </summary>
    public int OverrideDeltaObserved { get; set; }

    /// <summary>For each emitted SCOL FormID, how many REFRs anchored to it during cell-children emission.</summary>
    public Dictionary<uint, int> PlacementsPerScol { get; } = new();
}
