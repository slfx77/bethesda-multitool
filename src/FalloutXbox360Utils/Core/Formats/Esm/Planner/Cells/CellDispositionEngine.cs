using FalloutXbox360Utils.Core.Formats.Esm.Planner.Cells.Policies;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Disposition;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Phase B for cells. Walks the cell catalog and produces a disposition + provenance
///     per entry. Type-agnostic by design; the chain falls through to
///     <see cref="DefaultCellDispositionPolicy" /> which always returns a decision.
/// </summary>
public sealed class CellDispositionEngine
{
    private readonly IReadOnlyList<ICellDispositionPolicy> _chain;

    public CellDispositionEngine(IEnumerable<ICellDispositionPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        _chain = policies.ToList();

        if (!_chain.OfType<DefaultCellDispositionPolicy>().Any())
        {
            throw new InvalidOperationException(
                "CellDispositionEngine requires a DefaultCellDispositionPolicy in the policy chain.");
        }
    }

    /// <summary>Decide every catalog entry. Output indices match input indices.</summary>
    public IReadOnlyList<(CellCatalogEntry Entry, DispositionDecision Decision)> Decide(
        IReadOnlyList<CellCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var result = new List<(CellCatalogEntry, DispositionDecision)>(entries.Count);
        foreach (var entry in entries)
        {
            DispositionDecision? decision = null;
            foreach (var policy in _chain)
            {
                decision = policy.Decide(entry);
                if (decision is not null)
                {
                    break;
                }
            }

            if (decision is null)
            {
                throw new InvalidOperationException(
                    $"No policy returned a decision for CELL 0x{entry.CellFormId:X8}.");
            }

            result.Add((entry, decision));
        }

        return result;
    }
}
