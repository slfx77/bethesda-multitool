using FalloutXbox360Utils.Core.Formats.Esm.Planner.Disposition;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Per-cell disposition policy. Decides what the planner does with one cell catalog
///     entry. Mirrors the top-level <c>IDispositionPolicy</c> shape but specialized for
///     <see cref="CellCatalogEntry" />.
/// </summary>
public interface ICellDispositionPolicy
{
    /// <summary>
    ///     Decide what to do with this cell. Returns null to defer to the next policy in
    ///     the chain. The default policy never returns null so the chain always terminates.
    /// </summary>
    DispositionDecision? Decide(CellCatalogEntry entry);
}
