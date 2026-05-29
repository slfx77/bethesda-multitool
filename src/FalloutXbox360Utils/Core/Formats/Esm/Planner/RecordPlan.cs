using System.Collections.Immutable;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner;

/// <summary>
///     Per-record disposition deciding what (if anything) the writer emits for one FormID.
/// </summary>
public enum RecordDisposition
{
    /// <summary>
    ///     The DMP did not override this master record (or the override was rejected). The
    ///     plugin emits NOTHING for this FormID — the engine loads it from the master ESM at
    ///     runtime. KeepMaster entries are tracked in the plan so reference resolution knows
    ///     these FormIDs are live, but the writer skips them.
    /// </summary>
    KeepMaster,

    /// <summary>
    ///     A master record exists AND a DMP capture differs from it. The writer calls the
    ///     planned encoder + replays <see cref="RecordPlan.OverrideSubrecords" /> against
    ///     the master subrecord stream.
    /// </summary>
    Override,

    /// <summary>
    ///     No master record exists; this is a fresh plugin-range FormID. The writer calls
    ///     the planned encoder and emits the result as a new top-level record.
    /// </summary>
    New,

    /// <summary>
    ///     The planner decided this record will not appear in the output. Reasons include
    ///     runtime-state FormIDs, encoder-not-registered, drop-cascades from dangling refs,
    ///     and explicit per-options skips. <see cref="EmitPlan.Diagnostics" /> carries the
    ///     reason; the writer treats <c>Skip</c> entries as no-ops and they do not appear in
    ///     <see cref="EmitPlan.Records" />.
    /// </summary>
    Skip,
}

/// <summary>
///     One planned record. Carries everything the writer needs to emit bytes for one FormID
///     without making any decision of its own.
/// </summary>
public sealed record RecordPlan
{
    /// <summary>Record signature (4-char), e.g. "REFR", "SCPT", "WEAP".</summary>
    public required string Type { get; init; }

    /// <summary>What the writer should do with this record.</summary>
    public required RecordDisposition Disposition { get; init; }

    /// <summary>
    ///     The FormID under which the writer will emit (or retain) this record. For
    ///     <c>KeepMaster</c> / <c>Override</c> this is the master FormID; for <c>New</c>
    ///     this is the plugin-range FormID assigned by <c>FormIdPlanner</c>.
    /// </summary>
    public required uint FormId { get; init; }

    /// <summary>
    ///     If this record came from a DMP capture, the FormID that capture had. May differ
    ///     from <see cref="FormId" /> when the planner aliased a proto FormID into a master
    ///     FormID, or allocated a plugin-range FormID for a proto-only DMP record.
    /// </summary>
    public uint? SourceFormId { get; init; }

    /// <summary>
    ///     The typed DMP-derived record model (e.g. <c>WeaponRecord</c>, <c>NpcRecord</c>).
    ///     Null for <c>KeepMaster</c> dispositions where no DMP capture exists.
    /// </summary>
    public object? Model { get; init; }

    /// <summary>
    ///     The parsed master record, when one exists. The writer uses this for verbatim copy
    ///     on <c>KeepMaster</c>, and as the merge target on <c>Override</c>.
    /// </summary>
    public ParsedMainRecord? Master { get; init; }

    /// <summary>
    ///     Every outgoing FormID reference on this record, already resolved by
    ///     <c>ReferenceResolver</c>. Encoders look references up by their canonical
    ///     <see cref="ResolvedRef.FieldPath" /> rather than re-computing.
    /// </summary>
    public required ImmutableArray<ResolvedRef> References { get; init; }

    /// <summary>
    ///     For <c>Override</c> dispositions, the per-signature merge decisions computed
    ///     up front. <c>SubrecordReplay</c> walks these in order; it never consults any
    ///     <c>SubrecordMergePolicy</c> at write time. Null for <c>New</c> / <c>KeepMaster</c>.
    /// </summary>
    public ImmutableArray<SubrecordDecision>? OverrideSubrecords { get; init; }

    /// <summary>
    ///     Containment edges this record participates in (e.g. INFO → its parent DIAL,
    ///     REFR → its parent CELL). Phase E uses these to topologically sort the plan.
    /// </summary>
    public required ImmutableArray<RecordContainmentEdge> ContainedBy { get; init; }

    /// <summary>Why the planner chose this disposition. Captured for diagnostics.</summary>
    public required PlanProvenance Provenance { get; init; }
}
