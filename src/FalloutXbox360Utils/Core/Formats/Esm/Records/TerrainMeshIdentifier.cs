using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Identifies terrain meshes among geometry-scanned results and converts them
///     to LAND records with derived cell coordinates for heightmap rendering.
///     Terrain meshes are 33×33 vertex grids (1089 vertices) spanning ~4096 world units
///     per cell. Cell coordinates are derived from vertex XY positions.
/// </summary>
internal static class TerrainMeshIdentifier
{
    private const int TerrainVertexCount = RuntimeTerrainMesh.VertexCount; // 1089
    private const float CellWorldSize = 4096f;
    private const float MinCellRange = 3500f;
    private const float MaxCellRange = 5000f;
    private const float MaxCoordinate = 200_000f;

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

            // Compute XY range and min values from valid vertices
            var (minX, maxX, minY, maxY, validCount) = ComputeXYBounds(mesh.Vertices);

            // Require 90% valid XY coordinates
            if (validCount < TerrainVertexCount * 0.9)
            {
                continue;
            }

            var rangeX = maxX - minX;
            var rangeY = maxY - minY;

            // Terrain cells span ~4096 units in each dimension
            if (rangeX < MinCellRange || rangeX > MaxCellRange ||
                rangeY < MinCellRange || rangeY > MaxCellRange)
            {
                continue;
            }

            // Derive cell coordinates from minimum vertex positions
            var cellX = (int)Math.Floor(minX / CellWorldSize);
            var cellY = (int)Math.Floor(minY / CellWorldSize);

            // Skip if this cell already exists
            if (!existingCells.Add((cellX, cellY)))
            {
                duplicateCount++;
                continue;
            }

            // Convert ExtractedMesh → RuntimeTerrainMesh
            var terrainMesh = new RuntimeTerrainMesh
            {
                Vertices = mesh.Vertices,
                Normals = mesh.Normals,
                Colors = mesh.VertexColors,
                VertexDataOffset = mesh.VertexDataFileOffset > 0 ? mesh.VertexDataFileOffset : mesh.SourceOffset
            };

            // Diagnose quality before sanitization
            var preDiag = terrainMesh.DiagnoseQuality(cellX, cellY);

            // Sanitize vertices
            var sanitized = terrainMesh.SanitizeVertices();

            // Synthesize heightmap from sanitized mesh
            var heightmap = sanitized.ToLandHeightmap();

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

    private static (float MinX, float MaxX, float MinY, float MaxY, int ValidCount) ComputeXYBounds(float[] vertices)
    {
        var minX = float.MaxValue;
        var maxX = float.MinValue;
        var minY = float.MaxValue;
        var maxY = float.MinValue;
        var validCount = 0;

        for (var i = 0; i < TerrainVertexCount; i++)
        {
            var x = vertices[i * 3];
            var y = vertices[i * 3 + 1];

            if (!float.IsNaN(x) && !float.IsInfinity(x) && Math.Abs(x) <= MaxCoordinate &&
                !float.IsNaN(y) && !float.IsInfinity(y) && Math.Abs(y) <= MaxCoordinate)
            {
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
                validCount++;
            }
        }

        return (minX, maxX, minY, maxY, validCount);
    }
}
