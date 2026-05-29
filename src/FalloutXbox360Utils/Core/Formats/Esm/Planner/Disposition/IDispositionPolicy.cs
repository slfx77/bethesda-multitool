using FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Disposition;

/// <summary>
///     One per-record disposition policy. Returns the disposition + provenance for a single
///     catalog entry. Policies are pure: same input → same output. Per-type policies override
///     the default (e.g. <c>ScriptDispositionPolicy</c> for SCPT, <c>RuntimeStatePolicy</c>
///     for engine-reserved FormIDs).
/// </summary>
public interface IDispositionPolicy
{
    /// <summary>
    ///     Record types this policy can decide on. Empty means "applies to all types"
    ///     (the default policy uses this); the engine consults type-specific policies
    ///     first and falls back to the default.
    /// </summary>
    IReadOnlySet<string> RecordTypes { get; }

    /// <summary>
    ///     Decide what to do with this catalog entry. Returns null to defer to the next
    ///     policy in the chain (lets <c>RuntimeStatePolicy</c> skip first, falling through
    ///     to <c>DefaultDispositionPolicy</c> when not runtime-state).
    /// </summary>
    DispositionDecision? Decide(CatalogEntry entry);
}

/// <summary>
///     The output of a policy: the chosen disposition plus the rationale that gets stored
///     in <see cref="RecordPlan.Provenance" />.
/// </summary>
public sealed record DispositionDecision
{
    public required RecordDisposition Disposition { get; init; }
    public required PlanProvenance Provenance { get; init; }
}
