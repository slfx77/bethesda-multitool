using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Phase A.5 (coord-based cell pairing). Catches the case where the proto allocated
///     a different FormID for a cell that ALSO exists in master at the same
///     (worldspace, gridX, gridY). <see cref="CellCatalog" /> pairs by FormID only, so
///     proto's <c>0x01001670</c> at (7,7) WastelandNV and master's <c>0x000DDF1C</c> at
///     the same coord BOTH survive — engine logs "Cell will be destroyed" 500+ times and
///     master content disappears. This pass reconciles them as <see cref="SourceKind.DmpOverride" />
///     keyed on master's FormID, so the DMP placement data flows through the override
///     path and master's worldspace/coord context is honored.
/// </summary>
internal static class CoordBasedCellPairingPass
{
    /// <summary>
    ///     Reconcile a catalog produced by <see cref="CellCatalog.Build" />. For each
    ///     <see cref="SourceKind.DmpNew" /> exterior cell whose
    ///     (WorldspaceFormId, GridX, GridY) matches a master cell that's currently a
    ///     <see cref="SourceKind.MasterOnly" /> entry, fold them into a single
    ///     <see cref="SourceKind.DmpOverride" /> entry keyed on master's FormID. Other
    ///     entries pass through unchanged.
    /// </summary>
    public static IReadOnlyList<CellCatalogEntry> Reconcile(
        IReadOnlyList<CellCatalogEntry> catalog,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId)
    {
        return Reconcile(catalog, masterRecordsByFormId, out _);
    }

    /// <summary>
    ///     Variant that also reports the proto→master cell FormID aliases produced by the
    ///     folds. NAVMs (and any other child keyed by the proto cell FormID) must re-key
    ///     through this map or they orphan — the folded plan entry is keyed on master's
    ///     FormID, so children looked up under the proto FormID never attach.
    /// </summary>
    public static IReadOnlyList<CellCatalogEntry> Reconcile(
        IReadOnlyList<CellCatalogEntry> catalog,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        out Dictionary<uint, uint> protoToMasterCellAlias)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(masterRecordsByFormId);
        protoToMasterCellAlias = [];

        // Build a (worldspace, gridX, gridY) → master CELL FormID index from the catalog's
        // MasterOnly entries. Skip cells without a worldspace context (interiors / unresolved).
        var masterByCoord = new Dictionary<CellCoordinateKey, int>(catalog.Count);
        for (var i = 0; i < catalog.Count; i++)
        {
            var entry = catalog[i];
            if (entry.Source != SourceKind.MasterOnly)
            {
                continue;
            }

            if (!TryGetMasterCellCoord(entry, masterRecordsByFormId, out var key))
            {
                continue;
            }

            // First master at each coord wins. Subsequent duplicates (shouldn't happen in
            // master ESMs but defensive) are left alone — pairing the DMP cell to any one
            // master at that coord is enough to fix the collision symptom.
            masterByCoord.TryAdd(key, i);
        }

        if (masterByCoord.Count == 0)
        {
            return catalog;
        }

        // Walk DmpNew entries; pair any whose model has a matching coord into the master
        // catalog slot, marking that catalog index for "remove because it's been folded
        // into a MasterOnly entry's index". A list copy is unavoidable because we mutate
        // entries in place.
        var result = new List<CellCatalogEntry>(catalog);
        var foldedDmpIndices = new HashSet<int>();
        for (var i = 0; i < result.Count; i++)
        {
            var entry = result[i];
            if (entry.Source != SourceKind.DmpNew || entry.DmpModel is null)
            {
                continue;
            }

            if (!VirtualCellCanonicalizer.TryGetCellCoordinateKey(entry.DmpModel, out var key))
            {
                continue;
            }

            if (!masterByCoord.TryGetValue(key, out var masterIdx))
            {
                continue;
            }

            var masterEntry = result[masterIdx];

            // Convert master's slot to a DmpOverride that carries the proto's placement
            // payload. The cell's FormID, MasterContext, and MasterRecord all stay master's;
            // only the Source flips and DmpModel gets attached.
            result[masterIdx] = masterEntry with
            {
                Source = SourceKind.DmpOverride,
                DmpModel = entry.DmpModel
            };
            foldedDmpIndices.Add(i);

            if (entry.DmpModel.FormId != 0 && entry.DmpModel.FormId != masterEntry.CellFormId)
            {
                protoToMasterCellAlias[entry.DmpModel.FormId] = masterEntry.CellFormId;
            }
        }

        if (foldedDmpIndices.Count == 0)
        {
            return result;
        }

        // Remove the folded DmpNew entries. Preserve order otherwise so downstream
        // disposition iteration is deterministic.
        var trimmed = new List<CellCatalogEntry>(result.Count - foldedDmpIndices.Count);
        for (var i = 0; i < result.Count; i++)
        {
            if (!foldedDmpIndices.Contains(i))
            {
                trimmed.Add(result[i]);
            }
        }

        return trimmed;
    }

    /// <summary>
    ///     Resolve a master cell's (worldspace, gridX, gridY) from its
    ///     <see cref="ParsedMainRecord" />. Worldspace comes from
    ///     <c>PcEsmCellContext.WorldspaceFormId</c> (GRUP nesting context); coords
    ///     come from the XCLC subrecord (parsed little-endian on the PC ESM).
    /// </summary>
    private static bool TryGetMasterCellCoord(
        CellCatalogEntry entry,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        out CellCoordinateKey key)
    {
        key = default;

        if (entry.MasterContext?.WorldspaceFormId is not { } wsFid)
        {
            return false;
        }

        if (!masterRecordsByFormId.TryGetValue(entry.CellFormId, out var masterRecord))
        {
            return false;
        }

        // Find XCLC in the parsed subrecord stream. Master ESM is PC little-endian.
        foreach (var sub in masterRecord.Subrecords)
        {
            if (sub.Signature != "XCLC" || sub.Data.Length < 8)
            {
                continue;
            }

            var gridX = BinaryPrimitives.ReadInt32LittleEndian(sub.Data.AsSpan(0, 4));
            var gridY = BinaryPrimitives.ReadInt32LittleEndian(sub.Data.AsSpan(4, 4));
            key = new CellCoordinateKey(wsFid, gridX, gridY);
            return true;
        }

        return false;
    }
}
