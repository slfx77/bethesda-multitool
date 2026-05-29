namespace FalloutXbox360Utils.Core.Formats.Esm.Planner;

/// <summary>
///     Severity / category of a planner-emitted diagnostic. Mirrors the legacy
///     <c>IDiagnosticSink</c> Decision / Info / Warning split so existing consumers can
///     route plan output through the same pipeline.
/// </summary>
public enum PlanDiagnosticKind
{
    /// <summary>
    ///     A non-trivial choice the planner made (override vs new, drop a record, downgrade
    ///     a subrecord). Surfaces in the audit log; not a warning.
    /// </summary>
    Decision,

    /// <summary>Informational only — not a choice, not a problem.</summary>
    Info,

    /// <summary>
    ///     A condition that may produce incorrect runtime behavior but that the planner
    ///     proceeded past. E.g. a dangling SCRO[i] that was dropped; the script will still
    ///     load but with one fewer operand.
    /// </summary>
    Warning,
}

/// <summary>
///     One observation emitted by a planner phase. The writer does not read these; the
///     diagnostic sink consumes them post-plan.
/// </summary>
public sealed record PlanDiagnostic
{
    /// <summary>Severity bucket.</summary>
    public required PlanDiagnosticKind Kind { get; init; }

    /// <summary>
    ///     Which planner phase emitted this — e.g. <c>"Catalog"</c>, <c>"Disposition"</c>,
    ///     <c>"Allocation"</c>, <c>"References"</c>, <c>"Validation"</c>. Stable identifier
    ///     suitable for grouping in summaries.
    /// </summary>
    public required string Phase { get; init; }

    /// <summary>
    ///     Stable short-code suitable for aggregation, e.g.
    ///     <c>"disposition.skip.runtime-state"</c>, <c>"references.drop.scro-dangle"</c>.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>The record type the diagnostic is about, when applicable.</summary>
    public string? RecordType { get; init; }

    /// <summary>The FormID the diagnostic is about, when applicable.</summary>
    public uint? FormId { get; init; }

    /// <summary>Human-readable description.</summary>
    public required string Message { get; init; }
}
