using System.Collections.Immutable;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner;

/// <summary>
///     The immutable output of <see cref="EsmPlanner" />. Every record disposition, FormID
///     allocation, and reference resolution for the ESP that will be emitted is settled here.
///     The <c>PlannedWriter</c> walks this and produces bytes — it never decides, allocates,
///     or validates.
/// </summary>
/// <remarks>
///     Invariant: <c>formId ∈ EmittedFormIds</c> ⇔ bytes for that FormID will be written.
///     If this invariant ever breaks, the entire two-pass architecture's value collapses.
///     Enforced by <c>PlanValidator</c> in phase E.
/// </remarks>
public sealed record EmitPlan
{
    /// <summary>
    ///     Every record that will appear in the output ESP, in emission order. Already
    ///     topologically sorted: DIAL before INFOs, WRLD before child CELLs, NAVM before
    ///     NAVI references. Within a GRUP, sorted by <see cref="RecordPlan.FormId" />.
    /// </summary>
    public required ImmutableArray<RecordPlan> Records { get; init; }

    /// <summary>
    ///     Maps a DMP / proto source FormID to the plugin-range FormID it was allocated.
    ///     Used by <c>PlannedWriter</c> when it needs to translate an incoming reference to
    ///     its final emitted form. Identity entries (master → master) are NOT stored here —
    ///     query <see cref="EmittedFormIds" /> for "will this resolve?" instead.
    /// </summary>
    public required ImmutableDictionary<uint, uint> SourceToEmittedFormId { get; init; }

    /// <summary>
    ///     Every FormID that will appear in the output ESP — both retained master FormIDs
    ///     (<c>KeepMaster</c> / <c>Override</c> dispositions) and freshly-allocated plugin
    ///     FormIDs (<c>New</c> disposition). The single source of truth for
    ///     "is this reference live?" — <c>PlannedWriter</c> encoders never consult any other
    ///     validity set.
    /// </summary>
    public required ImmutableHashSet<uint> EmittedFormIds { get; init; }

    /// <summary>
    ///     <c>FormId → index into <see cref="Records" /></c>. Lets the writer answer
    ///     "where does this FormID live" in O(1). Populated alongside <see cref="Records" />
    ///     by phase E.
    /// </summary>
    public required ImmutableDictionary<uint, int> RecordIndexByEmittedFormId { get; init; }

    /// <summary>
    ///     Every planner decision that produced anything noteworthy: skipped records,
    ///     degraded references, drop-cascades, ordering choices. Not bytes — diagnostics
    ///     only. The legacy <c>IDiagnosticSink</c> reads these post-hoc.
    /// </summary>
    public required ImmutableArray<PlanDiagnostic> Diagnostics { get; init; }

    /// <summary>Top-level plan metadata: master path, NextObjectId, encoder coverage, etc.</summary>
    public required PlanMetadata Meta { get; init; }
}
