using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Planner.Catalog;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Cell;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Phase A for cells. Combines master CELL records (from <c>PcEsmCellContext</c> index)
///     with DMP-captured CELLs (from <c>RecordCollection.Cells</c>) into a uniform
///     <see cref="CellCatalogEntry" /> list. Same shape as top-level <c>RecordCatalog</c>.
/// </summary>
/// <remarks>
///     The DMP side is assumed to be already-unioned (via legacy <c>CellCaptureUnioner</c>)
///     when this runs in production. Synthetic test fixtures pass pre-unioned cells.
/// </remarks>
public sealed class CellCatalog
{
    /// <summary>
    ///     Build the catalog. The output preserves master discovery order, then appends
    ///     any DMP-new cells in <see cref="RecordCollection.Cells" /> order.
    /// </summary>
    public static IReadOnlyList<CellCatalogEntry> Build(
        IReadOnlyDictionary<uint, PcEsmCellContext> masterContexts,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        IReadOnlyList<CellRecord> dmpCells)
    {
        ArgumentNullException.ThrowIfNull(masterContexts);
        ArgumentNullException.ThrowIfNull(masterRecordsByFormId);
        ArgumentNullException.ThrowIfNull(dmpCells);

        var entries = new List<CellCatalogEntry>(masterContexts.Count + dmpCells.Count);
        var dmpByFormId = new Dictionary<uint, CellRecord>(dmpCells.Count);
        foreach (var dmp in dmpCells)
        {
            dmpByFormId[dmp.FormId] = dmp;
        }

        foreach (var (cellFormId, context) in masterContexts)
        {
            masterRecordsByFormId.TryGetValue(cellFormId, out var masterRecord);
            dmpByFormId.TryGetValue(cellFormId, out var dmpMatch);

            entries.Add(new CellCatalogEntry
            {
                CellFormId = cellFormId,
                Source = dmpMatch is null ? SourceKind.MasterOnly : SourceKind.DmpOverride,
                MasterContext = context,
                MasterRecord = masterRecord,
                DmpModel = dmpMatch,
            });

            if (dmpMatch is not null)
            {
                dmpByFormId.Remove(cellFormId);
            }
        }

        foreach (var dmp in dmpCells)
        {
            if (!dmpByFormId.ContainsKey(dmp.FormId))
            {
                continue; // Already paired with a master entry above.
            }

            entries.Add(new CellCatalogEntry
            {
                CellFormId = dmp.FormId,
                Source = SourceKind.DmpNew,
                MasterContext = null,
                MasterRecord = null,
                DmpModel = dmp,
            });
        }

        return entries;
    }
}
