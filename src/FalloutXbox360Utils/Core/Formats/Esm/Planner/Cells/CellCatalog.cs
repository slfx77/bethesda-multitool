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
///     Runs <see cref="CellCaptureUnioner" /> on the DMP input to merge repeated captures
///     of the same logical cell (same FormID across multiple memory regions, or different
///     FormID but matching interior EditorID / exterior grid). Pre-Tier-7b the union step
///     lived inside legacy <c>BuildCellOverrideBundles</c>; with planner-on by default that
///     path is bypassed, so the planner runs union itself. Idempotent on already-unioned
///     input (test fixtures).
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

        // Merge repeated captures of the same logical cell so a single FormID resolves to
        // a single entry — required for the cells.Add(formId, ...) call in
        // CellSectionPlanner.BuildCellPlans to not throw on duplicate keys.
        dmpCells = CellCaptureUnioner.Union(dmpCells).Cells;

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
