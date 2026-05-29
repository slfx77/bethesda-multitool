namespace FalloutXbox360Utils.Core.Formats.Esm.Planner;

/// <summary>
///     Where one subrecord's bytes will come from during an <see cref="RecordDisposition.Override" />
///     emit. Computed by the planner from per-type policy + per-record data, looked up by
///     <c>SubrecordReplay</c> at write time.
/// </summary>
public enum SubrecordSource
{
    /// <summary>Emit the bytes the planned encoder produced from the DMP model.</summary>
    FromDmp,

    /// <summary>Copy the master record's byte slice for this subrecord verbatim.</summary>
    FromMaster,

    /// <summary>
    ///     Neither source had this subrecord; the planner synthesized it (e.g. for a missing
    ///     GREETING attribution). Bytes live in the encoder output under this signature.
    /// </summary>
    Synthesized,

    /// <summary>
    ///     Both sources had this subrecord but the planner decided to drop it entirely
    ///     (e.g. a runtime-only orphan COED on a CONT inventory entry).
    /// </summary>
    Dropped,
}

/// <summary>
///     One per-signature merge decision for an <see cref="RecordDisposition.Override" /> record.
///     <c>SubrecordReplay</c> walks an <see cref="System.Collections.Immutable.ImmutableArray{T}" />
///     of these in order; the resulting bytes form the override record's payload.
/// </summary>
public sealed record SubrecordDecision
{
    /// <summary>Subrecord signature (4-char), e.g. <c>"FULL"</c>, <c>"DATA"</c>, <c>"CNTO"</c>.</summary>
    public required string Signature { get; init; }

    /// <summary>Where the bytes will come from.</summary>
    public required SubrecordSource Source { get; init; }

    /// <summary>
    ///     For <see cref="SubrecordSource.FromMaster" /> / <see cref="SubrecordSource.Dropped" />,
    ///     the positional index of this subrecord in the master record's <c>Subrecords</c>
    ///     list. Lets the replay engine slice the master's bytes without searching.
    /// </summary>
    public int? MasterIndex { get; init; }

    /// <summary>
    ///     For <see cref="SubrecordSource.FromDmp" /> / <see cref="SubrecordSource.Synthesized" />,
    ///     the positional index in the encoder's output <c>Subrecords</c> list. Same
    ///     purpose as <see cref="MasterIndex" /> on the DMP side.
    /// </summary>
    public int? DmpIndex { get; init; }

    /// <summary>
    ///     Optional explanation of why this decision was made. Required for
    ///     <see cref="SubrecordSource.Dropped" /> / <see cref="SubrecordSource.Synthesized" />,
    ///     optional for the others. Surfaces in diagnostics.
    /// </summary>
    public string? Reason { get; init; }
}
