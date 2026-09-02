using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Subrecords;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Terrain;

namespace BethesdaMultitool.Core.Formats.Esm.Records;

/// <summary>
///     Enriches LAND records with runtime data from memory dumps.
///     Handles matching runtime terrain data to ESM LAND records, synthesizing
///     heightmaps from runtime terrain meshes, and sanitizing vertex data.
/// </summary>
internal static class EsmLandEnricher
{
    /// <summary>
    ///     Enrich LAND records with runtime data from TESForm pointers.
    /// </summary>
    internal static void EnrichLandRecordsWithRuntimeData(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        EsmRecordScanResult scanResult,
        Dictionary<uint, string>? editorIdMap)
    {
        // This method can be extended to read additional runtime data
        // from TESForm pointers associated with LAND records
        _ = accessor;
        _ = fileSize;
        _ = scanResult;
        _ = editorIdMap;
    }

    /// <summary>
    ///     Enrich LAND records with runtime data loaded from memory.
    /// </summary>
    internal static void EnrichLandRecordsWithRuntimeData(
        EsmRecordScanResult scanResult,
        Dictionary<uint, RuntimeLoadedLandData> runtimeLandData)
    {
        // Match runtime land data with detected LAND records by FormId
        // ExtractedLandRecord is immutable, so we replace entries with updated versions
        var existingFormIds = new HashSet<uint>(scanResult.LandRecords.Select(l => l.Header.FormId));

        for (var i = 0; i < scanResult.LandRecords.Count; i++)
        {
            var landRecord = scanResult.LandRecords[i];
            if (runtimeLandData.TryGetValue(landRecord.Header.FormId, out var runtimeData))
            {
                // Create new record with runtime cell coordinates and terrain mesh
                scanResult.LandRecords[i] = landRecord with
                {
                    ParentCellFormId = runtimeData.ParentCellFormId ?? landRecord.ParentCellFormId,
                    RuntimeCellX = runtimeData.CellX,
                    RuntimeCellY = runtimeData.CellY,
                    RuntimeBaseHeight = runtimeData.BaseHeight,
                    RuntimeLandOffset = runtimeData.LandOffset,
                    RuntimeLoadedDataOffset = runtimeData.LoadedDataOffset,
                    RuntimeTerrainMesh = runtimeData.TerrainMesh,
                    VisualData = LandVisualData.MergeCategories(landRecord.VisualData, runtimeData.VisualData),
                    RuntimeLandTextures = runtimeData.RuntimeLandTextures,
                    RuntimeTextureSets = runtimeData.RuntimeTextureSets,
                    RuntimeLandDiagnostics = runtimeData.Diagnostics
                };
            }
        }

        // Add synthetic LAND records for runtime entries that don't match any ESM record
        // (common: ESM scan finds few LAND records, but runtime has many loaded terrain cells)
        foreach (var (formId, data) in runtimeLandData)
        {
            if (!existingFormIds.Contains(formId))
            {
                scanResult.LandRecords.Add(new ExtractedLandRecord
                {
                    Header = new DetectedMainRecord("LAND", 0, 0, formId, data.LandOffset, true),
                    ParentCellFormId = data.ParentCellFormId,
                    RuntimeCellX = data.CellX,
                    RuntimeCellY = data.CellY,
                    RuntimeBaseHeight = data.BaseHeight,
                    RuntimeLandOffset = data.LandOffset,
                    RuntimeLoadedDataOffset = data.LoadedDataOffset,
                    RuntimeTerrainMesh = data.TerrainMesh,
                    VisualData = data.VisualData,
                    RuntimeLandTextures = data.RuntimeLandTextures,
                    RuntimeTextureSets = data.RuntimeTextureSets,
                    RuntimeLandDiagnostics = data.Diagnostics
                });
            }
        }

        // Diagnose quality BEFORE sanitization to capture true data state.
        // After sanitization, garbage vertices are interpolated and corruption is hidden.
        for (var i = 0; i < scanResult.LandRecords.Count; i++)
        {
            var record = scanResult.LandRecords[i];
            if (record.RuntimeTerrainMesh != null)
            {
                var diag = RuntimeTerrainDiagnosticService.DiagnoseQuality(
                    record.RuntimeTerrainMesh,
                    record.BestCellX ?? 0,
                    record.BestCellY ?? 0,
                    record.Header.FormId,
                    record.RuntimeBaseHeight ?? 0f);
                scanResult.LandRecords[i] = record with { PreSanitizationDiagnostic = diag };
            }
        }

        // Sanitize runtime terrain mesh vertex data (replace garbage Z values with
        // neighbor interpolation or zeros) before any downstream processing.
        for (var i = 0; i < scanResult.LandRecords.Count; i++)
        {
            var record = scanResult.LandRecords[i];
            if (record.RuntimeTerrainMesh != null)
            {
                var sanitized = RuntimeTerrainSanitizationService.Sanitize(record.RuntimeTerrainMesh);
                if (sanitized != record.RuntimeTerrainMesh)
                {
                    scanResult.LandRecords[i] = record with { RuntimeTerrainMesh = sanitized };
                }
            }
        }

        // Synthesize or upgrade heightmaps from runtime terrain meshes.
        // When a RuntimeTerrainMesh exists, always use it — ToLandHeightmap() sets ExactHeights
        // which bypasses lossy VHGT delta decoding in CalculateHeights().
        for (var i = 0; i < scanResult.LandRecords.Count; i++)
        {
            var record = scanResult.LandRecords[i];
            if (record.RuntimeTerrainMesh != null)
            {
                try
                {
                    scanResult.LandRecords[i] = record with
                    {
                        Heightmap = RuntimeTerrainHeightmapEncoder.Encode(
                            record.RuntimeTerrainMesh,
                            record.RuntimeBaseHeight ?? 0f)
                    };
                }
                catch (InvalidOperationException)
                {
                    // Keep any ESM VHGT that was already present; diagnostics record the rejected runtime mesh.
                }
            }
        }
    }

    /// <summary>
    ///     Backfills LAND worldspace metadata from the semantic cell list after DMP cell/worldspace inference.
    /// </summary>
    internal static void EnrichLandRecordsWithCellWorldspaces(
        EsmRecordScanResult scanResult,
        IEnumerable<CellRecord> cells)
    {
        if (scanResult.LandRecords.Count == 0)
        {
            return;
        }

        var cellsByFormId = cells
            .GroupBy(c => c.FormId)
            .ToDictionary(g => g.Key, SelectBestCellForLandEnrichment);
        var cellsByGrid = cells
            .Where(c => !c.IsInterior &&
                        !c.IsVirtual &&
                        c.GridX.HasValue &&
                        c.GridY.HasValue &&
                        c.WorldspaceFormId is > 0)
            .GroupBy(c => (c.GridX!.Value, c.GridY!.Value))
            .ToDictionary(
                g => g.Key,
                g => g.ToList());

        for (var i = 0; i < scanResult.LandRecords.Count; i++)
        {
            var land = scanResult.LandRecords[i];
            var updated = land;

            if (land.ParentCellFormId is uint parentCellFormId &&
                cellsByFormId.TryGetValue(parentCellFormId, out var parentCell))
            {
                var parentWorldspace = parentCell.WorldspaceFormId;
                var useAuthorityWorldspace =
                    parentWorldspace is > 0 &&
                    string.Equals(parentCell.WorldspaceAssignmentSource, "Authority", StringComparison.Ordinal);

                updated = updated with
                {
                    CellX = parentCell.GridX ?? land.CellX,
                    CellY = parentCell.GridY ?? land.CellY,
                    WorldspaceFormId = useAuthorityWorldspace
                        ? parentWorldspace
                        : land.WorldspaceFormId ?? parentWorldspace
                };
            }

            if (updated.WorldspaceFormId is null &&
                scanResult.LandToWorldspaceMap.TryGetValue(land.Header.FormId, out var landWorldspace))
            {
                updated = updated with { WorldspaceFormId = landWorldspace };
            }

            if (updated.WorldspaceFormId is null &&
                updated.ParentCellFormId is uint cellFormId &&
                scanResult.CellToWorldspaceMap.TryGetValue(cellFormId, out var cellWorldspace))
            {
                updated = updated with { WorldspaceFormId = cellWorldspace };
            }

            if (updated.WorldspaceFormId is null &&
                updated.BestCellX.HasValue &&
                updated.BestCellY.HasValue &&
                cellsByGrid.TryGetValue((updated.BestCellX.Value, updated.BestCellY.Value), out var candidates))
            {
                var worldspaces = candidates
                    .Select(c => c.WorldspaceFormId!.Value)
                    .Distinct()
                    .ToList();
                if (worldspaces.Count == 1)
                {
                    updated = updated with { WorldspaceFormId = worldspaces[0] };
                    if (candidates.Count == 1)
                    {
                        updated = updated with { ParentCellFormId = candidates[0].FormId };
                    }
                }
            }

            scanResult.LandRecords[i] = updated;
        }
    }

    private static CellRecord SelectBestCellForLandEnrichment(IEnumerable<CellRecord> cells)
    {
        return cells
            .OrderByDescending(c => !c.IsVirtual && !c.IsUnresolvedBucket)
            .ThenByDescending(c => c.WorldspaceFormId is > 0)
            .ThenByDescending(c => c.GridX.HasValue && c.GridY.HasValue)
            .ThenByDescending(c => !c.IsInterior)
            .ThenByDescending(c => c.PlacedObjects.Count)
            .First();
    }

    /// <summary>
    ///     For each exterior <see cref="CellRecord" /> whose
    ///     <see cref="CellRecord.LandVisualData" /> has any missing category (VCLR / VNML /
    ///     TextureLayers / TextureIndices), merge the matching master-ESM LAND visual data
    ///     into the cell record. Mirrors the per-field provenance semantics of
    ///     <see cref="LandVisualData.MergeForEmission" />: existing per-field values win, master
    ///     fallback only fills in nulls.
    ///     <para>
    ///         ⚠ This converter-shaped overload has zero production callers: its inputs
    ///         (<c>MasterRecordIndex</c>-derived lookups + raw <see cref="ParsedMainRecord" />s)
    ///         only exist in the conversion pipeline, which needs none of this (its planner passes
    ///         master data through <c>CellLandPlanner</c>/<c>LandOverrideBuilder</c> itself). The
    ///         user-ruled consumer — the viewer's DMP-only "Master ESM terrain" toggle — is served
    ///         by the <c>(cells, masterCells)</c> overload below, which works from the Load Order's
    ///         already-parsed <see cref="CellRecord" />s and additionally fills the heightmap hole
    ///         this variant cannot.
    ///     </para>
    /// </summary>
    /// <param name="cells">
    ///     Input cell list. The returned list has the same length and order; cells whose
    ///     <c>LandVisualData</c> grew new categories from master are replaced via record
    ///     <c>with</c>, all others come back identical-by-reference.
    /// </param>
    /// <param name="masterExteriorCellByGrid">
    ///     <c>(worldspaceFormId, gridX, gridY) → master-CELL FormId</c> index. Comes from
    ///     <c>PluginConversionPipeline</c>'s <c>_masterExteriorCellByGrid</c> in the converter
    ///     pipeline; can be built directly in the GUI when master ESM is loaded.
    /// </param>
    /// <param name="landsByCell">
    ///     <c>master-CELL FormId → list of master-LAND FormIds</c> mapping.
    /// </param>
    /// <param name="pcRecordsByFormId">
    ///     Master-ESM <see cref="ParsedMainRecord" /> store keyed by FormId. Used to pull
    ///     the raw LAND record bytes via <see cref="CellGrupBuilder.ReconstructRecordBytes(ParsedMainRecord)" />.
    /// </param>
    internal static IReadOnlyList<CellRecord> EnrichCellsWithMasterEsmLandFallback(
        IReadOnlyList<CellRecord> cells,
        IReadOnlyDictionary<(uint Worldspace, int GridX, int GridY), uint> masterExteriorCellByGrid,
        IReadOnlyDictionary<uint, List<uint>> landsByCell,
        IReadOnlyDictionary<uint, ParsedMainRecord> pcRecordsByFormId)
    {
        if (cells.Count == 0 || masterExteriorCellByGrid.Count == 0)
        {
            return cells;
        }

        // Cache parsed master visuals by LAND FormId so worldspaces with thousands of
        // exterior cells don't re-parse the same master LAND record for every visit.
        var masterVisualCache = new Dictionary<uint, LandVisualData?>();
        var result = new List<CellRecord>(cells.Count);

        foreach (var cell in cells)
        {
            var enriched = TryApplyMasterFallback(
                cell, masterExteriorCellByGrid, landsByCell, pcRecordsByFormId, masterVisualCache);
            result.Add(enriched ?? cell);
        }

        return result;
    }

    private static CellRecord? TryApplyMasterFallback(
        CellRecord cell,
        IReadOnlyDictionary<(uint Worldspace, int GridX, int GridY), uint> masterExteriorCellByGrid,
        IReadOnlyDictionary<uint, List<uint>> landsByCell,
        IReadOnlyDictionary<uint, ParsedMainRecord> pcRecordsByFormId,
        Dictionary<uint, LandVisualData?> masterVisualCache)
    {
        if (cell.IsInterior
            || cell.WorldspaceFormId is not { } wsId
            || cell.GridX is not { } gx
            || cell.GridY is not { } gy)
        {
            return null;
        }

        if (!masterExteriorCellByGrid.TryGetValue((wsId, gx, gy), out var masterCellFormId)
            || !landsByCell.TryGetValue(masterCellFormId, out var masterLandFormIds)
            || masterLandFormIds.Count == 0)
        {
            return null;
        }

        var masterLandFormId = masterLandFormIds[0];
        if (!masterVisualCache.TryGetValue(masterLandFormId, out var masterVisualData))
        {
            masterVisualData = TryExtractMasterLandVisualData(masterLandFormId, pcRecordsByFormId);
            masterVisualCache[masterLandFormId] = masterVisualData;
        }

        if (masterVisualData is null)
        {
            return null;
        }

        var merged = LandVisualData.MergeForEmission(
            cell.LandVisualData,
            null,
            masterVisualData);

        if (merged is null || ReferenceEquals(merged, cell.LandVisualData))
        {
            return null;
        }

        return cell with { LandVisualData = merged };
    }

    private static LandVisualData? TryExtractMasterLandVisualData(
        uint masterLandFormId,
        IReadOnlyDictionary<uint, ParsedMainRecord> pcRecordsByFormId)
    {
        if (!pcRecordsByFormId.TryGetValue(masterLandFormId, out var masterLandRecord)
            || masterLandRecord.Header.Signature != "LAND")
        {
            return null;
        }

        var recordBytes = CellGrupBuilder.ReconstructRecordBytes(masterLandRecord);
        if (recordBytes.Length <= 24)
        {
            return null;
        }

        var dataSize = recordBytes.Length - 24;
        var data = new byte[dataSize];
        Buffer.BlockCopy(recordBytes, 24, data, 0, dataSize);
        return LandSubrecordParser.ParseVisualOnly(data, dataSize, false);
    }

    /// <summary>
    ///     Viewer-shaped master-terrain fallback: master data comes from already-parsed Load-Order
    ///     <see cref="CellRecord" />s instead of the converter pipeline's raw record index.
    ///     Grid-keyed (<c>(worldspace, gridX, gridY)</c>), so it also reaches dump cells whose
    ///     FormIDs diverged from the master (the FormID-keyed load-order cell merge cannot), and
    ///     per-category via <see cref="LandVisualData.MergeForEmission" /> (a dump cell that kept
    ///     its texture layers but lost VCLR still gains the master's colors — the whole-object
    ///     <c>override ?? base</c> merge cannot). Unlike the converter-shaped overload it also
    ///     fills the terrain GEOMETRY hole: a cell with neither <see cref="CellRecord.Heightmap" />
    ///     nor <see cref="CellRecord.RuntimeTerrainMesh" /> inherits the master's heightmap,
    ///     because the 3D terrain decode reads exactly those two fields — visual categories alone
    ///     leave recovered placements floating over nothing.
    ///     <para>
    ///         Wired behind the viewer's DMP-only "Master ESM terrain" preview toggle
    ///         (<c>SingleFileTab.PopulateWorldMapAsync</c>). The returned list is a viewer-local
    ///         copy: enriched cells are new instances whose merged <see cref="LandVisualData" />
    ///         drops <c>SourceParentCellFormId</c> (the provenance field <c>CellLandPlanner</c>
    ///         gates on), so they must never flow into conversion — the tab enriches only the
    ///         merged world-view collection, never <c>_session.SemanticResult</c>.
    ///     </para>
    /// </summary>
    internal static IReadOnlyList<CellRecord> EnrichCellsWithMasterEsmLandFallback(
        IReadOnlyList<CellRecord> cells,
        IReadOnlyList<CellRecord> masterCells)
    {
        if (cells.Count == 0 || masterCells.Count == 0)
        {
            return cells;
        }

        // Grid index over master exteriors, each terrain half resolved independently with
        // last-wins precedence: loadOrderRecords.Cells lists base plugins first and appends later
        // overlays, so the later plugin's data should win (matching every other merge here), and a
        // same-grid cell that only carries visuals must not starve the heightmap out of the cell
        // that carries the geometry.
        var masterByGrid = new Dictionary<(uint Worldspace, int GridX, int GridY),
            (LandVisualData? Visual, LandHeightmap? Heightmap)>();
        foreach (var master in masterCells)
        {
            if (master.IsInterior
                || master.WorldspaceFormId is not { } wsId
                || master.GridX is not { } gx
                || master.GridY is not { } gy
                || (master.LandVisualData is null && master.Heightmap is null))
            {
                continue;
            }

            var key = (wsId, gx, gy);
            masterByGrid.TryGetValue(key, out var slot);
            masterByGrid[key] = (master.LandVisualData ?? slot.Visual, master.Heightmap ?? slot.Heightmap);
        }

        if (masterByGrid.Count == 0)
        {
            return cells;
        }

        var result = new List<CellRecord>(cells.Count);
        foreach (var cell in cells)
        {
            result.Add(TryApplyMasterCellFallback(cell, masterByGrid) ?? cell);
        }

        return result;
    }

    private static CellRecord? TryApplyMasterCellFallback(
        CellRecord cell,
        Dictionary<(uint Worldspace, int GridX, int GridY),
            (LandVisualData? Visual, LandHeightmap? Heightmap)> masterByGrid)
    {
        if (cell.IsInterior
            || cell.WorldspaceFormId is not { } wsId
            || cell.GridX is not { } gx
            || cell.GridY is not { } gy
            || !masterByGrid.TryGetValue((wsId, gx, gy), out var master))
        {
            return null;
        }

        // Fast path: a cell with geometry plus every visual category from an AUTHORED source
        // gains nothing from the master — skip the merge so complete cells keep reference
        // identity and no lazy (BTD-backed) layer list gets force-materialized for nothing.
        // Runtime-stamped layers must NOT take this exit (the master's authored set outranks
        // them, per the 2026-08-31 ruling). VtexTextureFormIds is ignored here: it is
        // Morrowind/BTD-only and no dump-capable game authors it.
        var visual = cell.LandVisualData;
        if (cell.Heightmap is not null
            && visual is
            {
                HasVertexColors: true, HasVertexNormals: true, HasTextureIndices: true, HasTextureLayers: true
            }
            && (visual.TextureLayersSource != VisualDataSource.None
                ? visual.TextureLayersSource
                : visual.Source) is VisualDataSource.Dmp or VisualDataSource.MasterEsm)
        {
            return null;
        }

        // Per-category visual fill: the dump wins every field it has (MergeForEmission), and
        // master layers outrank Runtime-captured ones per the 2026-08-31 authored-source ruling.
        var mergedVisual = master.Visual is null
            ? cell.LandVisualData
            : LandVisualData.MergeForEmission(cell.LandVisualData, null, master.Visual)
              ?? cell.LandVisualData;

        // Geometry fill only when the cell has no height source at all — the 3D decode reads
        // Heightmap ?? RuntimeTerrainMesh, so a cell with its own shape keeps it.
        var heightmap = cell.Heightmap;
        if (heightmap is null && cell.RuntimeTerrainMesh is null)
        {
            heightmap = master.Heightmap;
        }

        if (ReferenceEquals(mergedVisual, cell.LandVisualData) && ReferenceEquals(heightmap, cell.Heightmap))
        {
            return null;
        }

        return cell with { LandVisualData = mergedVisual, Heightmap = heightmap };
    }
}
