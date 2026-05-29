namespace FalloutXbox360Utils.Core.Formats.Esm.Planner;

/// <summary>
///     What <c>ReferenceResolver</c> decided about one outgoing FormID reference.
/// </summary>
public enum ResolvedRefAction
{
    /// <summary>
    ///     The reference target is present in <see cref="EmitPlan.EmittedFormIds" />.
    ///     <see cref="ResolvedRef.FinalFormId" /> carries the value the writer should emit
    ///     (post-remap if the source FormID was a proto alias).
    /// </summary>
    Resolved,

    /// <summary>
    ///     Target not present; per-field policy specifies the subrecord should remain but
    ///     with a null FormID (<c>0x00000000</c>). The engine treats null refs as no-ops.
    /// </summary>
    NullRef,

    /// <summary>
    ///     Target not present; per-field policy specifies the entire subrecord (or in
    ///     repeated cases, this specific entry) should be omitted from the output.
    /// </summary>
    DropSubrecord,

    /// <summary>
    ///     Target not present; per-field policy specifies the containing subrecord should
    ///     be re-shaped to a safer variant. Used by PACK <c>PLDT</c> / <c>PTDT</c> which
    ///     can downgrade from <c>Type 0 (NearReference)</c> to <c>Type 2 (NearCurrentLocation)</c>.
    ///     <see cref="ResolvedRef.Downgrade" /> describes the shape change.
    /// </summary>
    DowngradeContainer,

    /// <summary>
    ///     The reference was not in scope for validation (e.g. inside an unrecognized
    ///     subrecord) and is passed through to the writer unchanged. Sentinel for
    ///     unmigrated codepaths; should be empty post-Tier-5.
    /// </summary>
    KeepAsIs,
}

/// <summary>
///     One resolved outgoing FormID reference. Embedded in <see cref="RecordPlan.References" />
///     and looked up by encoders via <see cref="FieldPath" />.
/// </summary>
public sealed record ResolvedRef
{
    /// <summary>
    ///     Canonical path identifying this reference within the record, e.g. <c>"SCRO[3]"</c>,
    ///     <c>"PLDT.Union"</c>, <c>"XLKR"</c>. Constructed via the <c>FieldPath</c> static
    ///     helpers — encoders never build paths ad hoc.
    /// </summary>
    public required string FieldPath { get; init; }

    /// <summary>
    ///     The FormID the source record carried for this reference, before any planner
    ///     resolution. Null if the source had no value (e.g. an optional subrecord absent
    ///     from the DMP capture).
    /// </summary>
    public uint? OriginalFormId { get; init; }

    /// <summary>What the resolver decided.</summary>
    public required ResolvedRefAction Action { get; init; }

    /// <summary>
    ///     The FormID the writer should emit for this reference, when <see cref="Action" />
    ///     is <see cref="ResolvedRefAction.Resolved" />. For other actions this is null —
    ///     the writer consults <see cref="Action" /> first.
    /// </summary>
    public uint? FinalFormId { get; init; }

    /// <summary>
    ///     Container reshape descriptor when <see cref="Action" /> is
    ///     <see cref="ResolvedRefAction.DowngradeContainer" />. Null otherwise.
    /// </summary>
    public ContainerDowngrade? Downgrade { get; init; }

    /// <summary>
    ///     Human-readable explanation of any non-Resolved action. Required by
    ///     <c>PlanValidator</c>; null for <see cref="ResolvedRefAction.Resolved" />.
    /// </summary>
    public string? Reason { get; init; }
}
