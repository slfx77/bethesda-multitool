using FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Disposition;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Cells.Policies;

/// <summary>
///     Fallback per-cell policy. Master-only → KeepMaster (writer emits nothing; engine
///     loads from master ESM). DMP override → Override. DMP new → New (planner allocates).
/// </summary>
public sealed class DefaultCellDispositionPolicy : ICellDispositionPolicy
{
    public DispositionDecision? Decide(CellCatalogEntry entry)
    {
        return entry.Source switch
        {
            SourceKind.MasterOnly => new DispositionDecision
            {
                Disposition = RecordDisposition.KeepMaster,
                Provenance = new PlanProvenance
                {
                    PolicyId = "DefaultCellDispositionPolicy.MasterOnly",
                    Reason = "Master CELL had no DMP override; copy verbatim from master ESM.",
                },
            },
            SourceKind.DmpOverride => new DispositionDecision
            {
                Disposition = RecordDisposition.Override,
                Provenance = new PlanProvenance
                {
                    PolicyId = "DefaultCellDispositionPolicy.DmpOverride",
                    Reason = "DMP captured a CELL sharing FormID with master; emit override.",
                },
            },
            SourceKind.DmpNew => new DispositionDecision
            {
                Disposition = RecordDisposition.New,
                Provenance = new PlanProvenance
                {
                    PolicyId = "DefaultCellDispositionPolicy.DmpNew",
                    Reason = "DMP captured a CELL without a master counterpart; allocate plugin FormID.",
                },
            },
            SourceKind.Fo3Source => new DispositionDecision
            {
                Disposition = RecordDisposition.Skip,
                Provenance = new PlanProvenance
                {
                    PolicyId = "DefaultCellDispositionPolicy.Fo3NotSupported",
                    Reason = "FO3 source CELLs are not yet wired into the planner; deferred.",
                },
            },
            _ => throw new InvalidOperationException($"Unknown SourceKind: {entry.Source}"),
        };
    }
}
