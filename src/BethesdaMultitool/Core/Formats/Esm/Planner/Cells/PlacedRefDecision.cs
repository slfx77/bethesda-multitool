namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     The planner's emit-or-drop verdict for one placed reference (REFR/ACHR/ACRE) inside a cell.
///     Produced by <c>CellSectionPlanner</c> so the writer never re-derives base resolution,
///     leveled-spawn recovery, marker drops, or bucketing — it just serializes the verdict.
/// </summary>
public enum PlacedRefEmitVerdict
{
    /// <summary>Emit this ref. <see cref="PlacedRefDecision.FinalBaseFormId" /> and
    /// <see cref="PlacedRefDecision.TargetGroupType" /> carry the resolved base + bucket.</summary>
    Emit,

    /// <summary>Do not emit this ref. <see cref="PlacedRefDecision.DropReason" /> records why.</summary>
    Drop,
}

/// <summary>
///     Per-placed-ref decision the planner settles upfront: whether the ref emits, its final NAME
///     base FormID (after SourceToEmittedFormId remap, leveled-spawn recovery, and EditorID-stem
///     rescue), and the child GRUP bucket it routes into (8 persistent / 9 temporary / 10 VWD).
///     Keyed by the child <c>RecordPlan.FormId</c> in <c>CellPlan.RefDecisions</c>.
/// </summary>
public sealed record PlacedRefDecision
{
    /// <summary>Emit or drop.</summary>
    public required PlacedRefEmitVerdict Verdict { get; init; }

    /// <summary>
    ///     Drop-reason stat code (e.g. <c>refr.dangling-base</c>); null when emitted. A null
    ///     reason on a Drop verdict means "silently not emitted" (e.g. override without a
    ///     master record) — the writer skips the stats counters for those, matching legacy.
    /// </summary>
    public string? DropReason { get; init; }

    /// <summary>
    ///     Additional stat code incremented alongside the verdict: a recovery marker on Emit
    ///     (<c>refr.leveled-recovered</c> / <c>refr.editorid-remap</c>) or the ambiguity
    ///     marker on Drop (<c>refr.editorid-remap-ambiguous</c>).
    /// </summary>
    public string? AuxStatCode { get; init; }

    /// <summary>
    ///     Final NAME base FormID for an emitted NEW ref, after remap/recovery/rescue. 0 when the
    ///     ref is an Override (its base rides the merged master record) or dropped.
    /// </summary>
    public uint FinalBaseFormId { get; init; }

    /// <summary>Child GRUP group-type the ref routes into: 8 persistent, 9 temporary, 10 VWD.</summary>
    public int TargetGroupType { get; init; }

    /// <summary>
    ///     Record-header Initially-Disabled bit (0x800) selected for an emitted NEW ref.
    ///     The planner owns this decision so compatibility policy is applied once and the
    ///     encoder only serializes the settled state. Null is reserved for transitional
    ///     callers that predate verdict planning and means "use the captured state".
    /// </summary>
    public bool? NewInitiallyDisabled { get; init; }

    /// <summary>
    ///     Optional planner-selected XESP enable parent for an emitted NEW ref. This is
    ///     intentionally separate from the global source-to-emitted map: compatibility
    ///     aliases for one placed-reference network must not redirect or suppress the
    ///     source parent records themselves. Null keeps the captured parent.
    /// </summary>
    public uint? NewEnableParentFormId { get; init; }

    /// <summary>
    ///     True when this Override ref must mark its master FormID "covered" so the carry-forward
    ///     pass doesn't re-emit the master copy (the <c>actor.temp-override-suppressed-esm</c> path).
    /// </summary>
    public bool MarksMasterCovered { get; init; }

    /// <summary>
    ///     When set on a reparented Override emit, the record-header Initially-Disabled bit
    ///     (0x800) is forced to this value instead of inheriting master's. A reparented actor
    ///     applies the proto's authored enable-state — e.g. a cut NPC whose only retail
    ///     placement is disabled (EthelPhebus) but whom the proto shipped live must emit
    ///     enabled, or the move just places a disabled actor. Null = keep master's bit.
    /// </summary>
    public bool? OverrideInitiallyDisabled { get; init; }
}
