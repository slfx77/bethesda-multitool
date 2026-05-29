using FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Disposition.Policies;

/// <summary>
///     Fallback policy applied to any catalog entry no type-specific policy claimed.
///     Implements the baseline rules: master-only → keep verbatim, DMP override → override,
///     DMP new → allocate, FO3 → skip (not yet wired). Lives at the end of the
///     <c>DispositionEngine</c> chain.
/// </summary>
public sealed class DefaultDispositionPolicy : IDispositionPolicy
{
    public IReadOnlySet<string> RecordTypes { get; } = new HashSet<string>(StringComparer.Ordinal);

    public DispositionDecision? Decide(CatalogEntry entry)
    {
        return entry.Source switch
        {
            SourceKind.MasterOnly => new DispositionDecision
            {
                Disposition = RecordDisposition.KeepMaster,
                Provenance = new PlanProvenance
                {
                    PolicyId = "DefaultDispositionPolicy.MasterOnly",
                    Reason = "Master record had no DMP override; copy verbatim.",
                },
            },
            SourceKind.DmpOverride => new DispositionDecision
            {
                Disposition = RecordDisposition.Override,
                Provenance = new PlanProvenance
                {
                    PolicyId = "DefaultDispositionPolicy.DmpOverride",
                    Reason = "DMP captured a record sharing FormID with master; emit override.",
                },
            },
            SourceKind.DmpNew => new DispositionDecision
            {
                Disposition = RecordDisposition.New,
                Provenance = new PlanProvenance
                {
                    PolicyId = "DefaultDispositionPolicy.DmpNew",
                    Reason = "DMP captured a record without a master counterpart; allocate plugin FormID.",
                },
            },
            SourceKind.Fo3Source => new DispositionDecision
            {
                Disposition = RecordDisposition.Skip,
                Provenance = new PlanProvenance
                {
                    PolicyId = "DefaultDispositionPolicy.Fo3NotSupported",
                    Reason = "FO3 source records are not yet wired into the planner; deferred to Plan C.",
                },
            },
            _ => throw new InvalidOperationException($"Unknown SourceKind: {entry.Source}"),
        };
    }
}
