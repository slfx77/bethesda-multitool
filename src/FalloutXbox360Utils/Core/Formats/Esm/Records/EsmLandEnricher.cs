using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm;

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
                    RuntimeCellX = runtimeData.CellX,
                    RuntimeCellY = runtimeData.CellY,
                    RuntimeTerrainMesh = runtimeData.TerrainMesh
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
                    RuntimeCellX = data.CellX,
                    RuntimeCellY = data.CellY,
                    RuntimeBaseHeight = data.BaseHeight,
                    RuntimeTerrainMesh = data.TerrainMesh
                });
            }
        }

        // Sanitize runtime terrain mesh vertex data (replace garbage Z values with
        // neighbor interpolation or zeros) before any downstream processing.
        for (var i = 0; i < scanResult.LandRecords.Count; i++)
        {
            var record = scanResult.LandRecords[i];
            if (record.RuntimeTerrainMesh != null)
            {
                var sanitized = record.RuntimeTerrainMesh.SanitizeVertices();
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
                scanResult.LandRecords[i] = record with
                {
                    Heightmap = record.RuntimeTerrainMesh.ToLandHeightmap()
                };
            }
        }
    }
}
