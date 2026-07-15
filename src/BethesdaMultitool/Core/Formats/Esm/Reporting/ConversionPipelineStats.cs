namespace BethesdaMultitool.Core.Formats.Esm.Reporting;

/// <summary>
///     Planner-visible state of a captured XESP enable parent. This is diagnostic only:
///     the writer's remap/drop policy remains the authority for whether the XESP subrecord
///     is emitted.
/// </summary>
public enum PlannerXespParentStatus
{
    /// <summary>The parent is a placed reference supplied by the master ESM.</summary>
    LiveMaster,

    /// <summary>The parent has an emit verdict in a cell bundle the planner will write.</summary>
    LiveEmitted,

    /// <summary>The parent was captured, but its ref or containing cell has a drop verdict.</summary>
    CapturedDropped,

    /// <summary>The parent is an engine/runtime FormID rather than authored plugin content.</summary>
    RuntimeState,

    /// <summary>No matching captured, master, or emitted parent is known.</summary>
    Absent,
}

/// <summary>One captured XESP link and the evidence used to classify its parent.</summary>
public sealed record PlannerXespObservation(
    uint SourceParentFormId,
    uint FinalParentFormId,
    bool XespEmitted,
    PlannerXespParentStatus ParentStatus,
    string Reason);

/// <summary>
///     Running totals for a DMP→ESM conversion pipeline run.
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
    public int RecoverableGapCandidates { get; set; }
    public int PromotedGapRawRecords { get; set; }
    public int PromotedGapRuntimeDialogue { get; set; }
    public int PromotedGapPlacedRefs { get; set; }
    public int SkippedGapCandidates { get; set; }

    /// <summary>
    ///     Number of captured XESP enable-parent links omitted by the planner writer because
    ///     the parent could not be resolved against the final master/emitted FormID set.
    /// </summary>
    public int PlannerXespSkipped { get; private set; }

    /// <summary>Distinct source parent FormIDs behind <see cref="PlannerXespSkipped" />.</summary>
    public HashSet<uint> PlannerXespMissingParentFormIds { get; } = [];

    /// <summary>Total captured XESP links observed by the planner cell writer.</summary>
    public int PlannerXespObserved { get; private set; }

    /// <summary>Captured XESP links that survived sanitation and were written.</summary>
    public int PlannerXespEmitted { get; private set; }

    /// <summary>
    ///     Per-link XESP evidence. Consumers should group by <see cref="PlannerXespObservation.ParentStatus" />
    ///     and distinct source FormID; repeated links can legitimately share one parent.
    /// </summary>
    public List<PlannerXespObservation> PlannerXespObservations { get; } = [];

    /// <summary>Per-record-type counts of records emitted to the output ESM.</summary>
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
    ///     instrumentation-only code). Does not affect <see cref="RecordsSkipped" />;
    ///     call <see cref="IncrementSkipped" /> separately when the decision is a drop.
    /// </summary>
    public void IncrementDropReason(string code)
    {
        DropReasonCounts[code] = DropReasonCounts.GetValueOrDefault(code) + 1;
    }

    public void RecordPlannerXesp(
        uint sourceParentFormId,
        uint finalParentFormId,
        bool emitted,
        PlannerXespParentStatus parentStatus,
        string reason)
    {
        PlannerXespObserved++;
        PlannerXespObservations.Add(new PlannerXespObservation(
            sourceParentFormId, finalParentFormId, emitted, parentStatus, reason));

        if (emitted)
        {
            PlannerXespEmitted++;
            return;
        }

        PlannerXespSkipped++;
        PlannerXespMissingParentFormIds.Add(sourceParentFormId);
        IncrementDropReason("refr.xesp-dangling");
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
