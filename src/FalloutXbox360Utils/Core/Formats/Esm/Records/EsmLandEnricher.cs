using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Terrain;

namespace FalloutXbox360Utils.Core.Formats.Esm.Records;

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
}
