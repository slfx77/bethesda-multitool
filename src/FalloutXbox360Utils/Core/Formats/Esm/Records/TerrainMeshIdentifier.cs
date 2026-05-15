using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Terrain;

namespace FalloutXbox360Utils.Core.Formats.Esm.Records;

/// <summary>
///     Identifies terrain meshes among geometry-scanned results and converts them
///     to LAND records with derived cell coordinates for heightmap rendering.
///     Terrain meshes are 33×33 vertex grids (1089 vertices) spanning ~4096 world units
///     per cell. Cell coordinates are derived from vertex XY positions.
/// </summary>
internal static class TerrainMeshIdentifier
{
    private const int TerrainVertexCount = RuntimeTerrainMesh.VertexCount; // 1089
    private const float CellWorldSize = TerrainConstants.LandCellWorldSize;

    /// <summary>
    ///     Find terrain meshes among geometry-scanned results and create synthetic LAND records.
    ///     Skips cells that already have LAND records in the scan results (deduplication by CellX/CellY).
    /// </summary>
    public static List<ExtractedLandRecord> IdentifyTerrainMeshes(
        List<ExtractedMesh>? meshes,
        EsmRecordScanResult esmRecords)
    {
        if (meshes == null || meshes.Count == 0)
        {
            return [];
        }

        var log = Logger.Instance;

        // Build set of existing cell coordinates to avoid duplicates
        var existingCells = new HashSet<(int, int)>();
        foreach (var land in esmRecords.LandRecords)
        {
            if (land.BestCellX.HasValue && land.BestCellY.HasValue)
            {
                existingCells.Add((land.BestCellX.Value, land.BestCellY.Value));
            }
        }

        var results = new List<ExtractedLandRecord>();
        var candidateCount = 0;
        var duplicateCount = 0;

        foreach (var mesh in meshes)
        {
            if (mesh.VertexCount != TerrainVertexCount)
            {
                continue;
            }

            candidateCount++;

            // Convert ExtractedMesh → RuntimeTerrainMesh
            var terrainMesh = new RuntimeTerrainMesh
            {
                Vertices = mesh.Vertices,
                Normals = mesh.Normals,
                Colors = mesh.VertexColors,
                VertexDataOffset = mesh.VertexDataFileOffset > 0 ? mesh.VertexDataFileOffset : mesh.SourceOffset
            };

            var reconstruction = RuntimeTerrainGridReconstructionService.Reconstruct(terrainMesh);
            if (reconstruction == null)
            {
                continue;
            }

            // Derive cell coordinates from the reconstructed terrain bounds.
            var cellX = (int)Math.Floor(reconstruction.MinX / CellWorldSize);
            var cellY = (int)Math.Floor(reconstruction.MinY / CellWorldSize);

            // Skip if this cell already exists.
            if (!existingCells.Add((cellX, cellY)))
            {
                duplicateCount++;
                continue;
            }

            // Diagnose quality before sanitization
            var preDiag = RuntimeTerrainDiagnosticService.DiagnoseQuality(terrainMesh, cellX, cellY);

            // Sanitize vertices
            var sanitized = RuntimeTerrainSanitizationService.Sanitize(terrainMesh);

            // Synthesize heightmap from sanitized mesh
            var heightmap = RuntimeTerrainHeightmapEncoder.Encode(sanitized);

            results.Add(new ExtractedLandRecord
            {
                Header = new DetectedMainRecord("LAND", 0, 0, 0, mesh.SourceOffset, true),
                RuntimeCellX = cellX,
                RuntimeCellY = cellY,
                RuntimeTerrainMesh = sanitized,
                PreSanitizationDiagnostic = preDiag,
                Heightmap = heightmap
            });
        }

        if (candidateCount > 0)
        {
            log.Info("Terrain mesh identifier: {0} meshes with 1089 vertices → {1} terrain cells " +
                     "({2} duplicates skipped)", candidateCount, results.Count, duplicateCount);
        }

        return results;
    }
}
