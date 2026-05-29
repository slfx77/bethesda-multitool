namespace FalloutXbox360Utils.Core.Formats.Esm.Planner;

/// <summary>
///     Why the planner chose the disposition it did. Captured per-record for diagnostics
///     and audit, not consumed by the writer.
/// </summary>
public sealed record PlanProvenance
{
    /// <summary>
    ///     Short stable identifier of the policy that made the call, e.g.
    ///     <c>"DefaultDispositionPolicy.OverrideFromMaster"</c> or
    ///     <c>"ScriptDispositionPolicy.ProvenScriptOverride"</c>.
    /// </summary>
    public required string PolicyId { get; init; }

    /// <summary>Human-readable rationale. May be surfaced verbatim to the diagnostic sink.</summary>
    public required string Reason { get; init; }
}
