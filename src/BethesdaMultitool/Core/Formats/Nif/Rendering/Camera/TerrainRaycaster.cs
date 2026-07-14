using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Terrain;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     CPU raycast against one rendered LAND cell. The triangle split is intentionally identical to
///     <see cref="TerrainMeshBuilder" />: each heightfield quad is <c>(v00,v10,v01)</c> plus
///     <c>(v01,v10,v11)</c>. This lets object picking use the terrain depth as an occluder without a
///     synchronous GPU depth readback.
/// </summary>
internal static class TerrainRaycaster
{
    private const int CachedGridSize = 33;

    /// <summary>
    ///     Returns the nearest forward ray hit in <paramref name="cell" />, no farther than
    ///     <paramref name="maxDistance" />. The direction need not be normalized; the returned
    ///     parameter uses the caller's ray units. A shared render cache avoids re-decoding the common
    ///     33x33 terrain grid; native larger grids (Morrowind's 65x65 LAND) stay at renderer resolution.
    /// </summary>
    public static bool RaycastNearest(
        CellRecord cell,
        Vector3 origin,
        Vector3 direction,
        out float t,
        global::BethesdaMultitool.WorldRenderCache? cache = null,
        float maxDistance = float.PositiveInfinity)
    {
        t = 0f;
        if (cell.GridX is not int gridX || cell.GridY is not int gridY) return false;
        if (!IsFinite(origin) || !IsFinite(direction) || direction.LengthSquared() < 1e-20f) return false;
        if (float.IsNaN(maxDistance) || maxDistance < 0f) return false;

        var cellSize = cell.CellWorldSize > 0f
            ? cell.CellWorldSize
            : TerrainConstants.LandCellWorldSize;
        var originX = gridX * cellSize;
        var originY = gridY * cellSize;

        float[]? cachedHeights = null;
        float[,]? nativeHeights = null;
        int gridSize;
        float spacing;

        if (TerrainMeshBuilder.ResolveGridSize(cell) == CachedGridSize && cache is not null)
        {
            var terrain = cache.GetTerrain(cell);
            if (!terrain.HasTerrain) return false;
            cachedHeights = terrain.Heights;
            gridSize = CachedGridSize;
            // Mirror TerrainMeshBuilder's cached overload exactly. Its decoded 33x33 grid always
            // uses the Fallout-family 128-unit spacing, even if a malformed/mixed-source cell has
            // a different CellWorldSize. Picking must cover the same footprint the GPU received.
            spacing = TerrainConstants.LandVertexSpacing;
        }
        else
        {
            var heightmap = cell.Heightmap ?? cell.RuntimeTerrainMesh?.ToLandHeightmap();
            if (heightmap is null) return false;
            nativeHeights = heightmap.CalculateHeights();
            gridSize = nativeHeights.GetLength(0);
            if (gridSize < 2 || nativeHeights.GetLength(1) != gridSize) return false;
            spacing = cellSize / (gridSize - 1);
        }

        // Reject cells whose rendered XY footprint the ray never crosses. This leaves only the
        // handful of cells along the click ray for the triangle loop, even when the viewer queried a
        // wide radius. Use the generated mesh span, not the nominal cell size (see cached path above).
        var meshSpan = spacing * (gridSize - 1);
        if (!RayCrossesFootprint(
                origin, direction,
                originX, originX + meshSpan,
                originY, originY + meshSpan,
                maxDistance))
        {
            return false;
        }

        var best = maxDistance;
        var hit = false;
        for (var y = 0; y < gridSize - 1; y++)
        {
            var y0 = originY + y * spacing;
            var y1 = y0 + spacing;
            for (var x = 0; x < gridSize - 1; x++)
            {
                var x0 = originX + x * spacing;
                var x1 = x0 + spacing;
                var z00 = HeightAt(x, y);
                var z10 = HeightAt(x + 1, y);
                var z01 = HeightAt(x, y + 1);
                var z11 = HeightAt(x + 1, y + 1);
                if (!float.IsFinite(z00) || !float.IsFinite(z10) ||
                    !float.IsFinite(z01) || !float.IsFinite(z11))
                {
                    continue;
                }

                var v00 = new Vector3(x0, y0, z00);
                var v10 = new Vector3(x1, y0, z10);
                var v01 = new Vector3(x0, y1, z01);
                var v11 = new Vector3(x1, y1, z11);

                TestTriangle(v00, v10, v01);
                TestTriangle(v01, v10, v11);
            }
        }

        if (!hit) return false;
        t = best;
        return true;

        float HeightAt(int x, int y) => cachedHeights is not null
            ? cachedHeights[y * CachedGridSize + x]
            : nativeHeights![y, x];

        void TestTriangle(Vector3 v0, Vector3 v1, Vector3 v2)
        {
            // TerrainRenderer12 rasterizes with CullMode.Back. TerrainMeshBuilder emits +Z-facing
            // triangles, so only a ray approaching that visible top face may occlude a reference;
            // a ray from below must not hit the invisible underside.
            var normal = Vector3.Cross(v1 - v0, v2 - v0);
            if (Vector3.Dot(normal, direction) >= 0f) return;
            if (RayTriangleIntersector.Intersect(origin, direction, v0, v1, v2, out var candidate) &&
                candidate <= best)
            {
                best = candidate;
                hit = true;
            }
        }
    }

    /// <summary>
    ///     Returns whether a reference hit is meaningfully behind the terrain hit. Contact within a
    ///     small ULP-scaled tolerance remains selectable: inverse placement transforms and world-space
    ///     LAND vertices can round the same surface to adjacent floats at large world coordinates.
    /// </summary>
    public static bool IsHitBehindTerrain(float referenceDistance, float terrainDistance)
    {
        if (!float.IsFinite(referenceDistance) || !float.IsFinite(terrainDistance)) return false;

        var scale = MathF.Max(MathF.Abs(referenceDistance), MathF.Abs(terrainDistance));
        var next = MathF.BitIncrement(scale);
        var ulp = float.IsFinite(next) ? next - scale : 0f;
        var contactTolerance = MathF.Max(0.01f, ulp * 8f);
        return referenceDistance > terrainDistance + contactTolerance;
    }

    private static bool RayCrossesFootprint(
        Vector3 origin,
        Vector3 direction,
        float minX,
        float maxX,
        float minY,
        float maxY,
        float maxDistance)
    {
        var tMin = 0f;
        var tMax = maxDistance;
        return Slab(origin.X, direction.X, minX, maxX, ref tMin, ref tMax) &&
               Slab(origin.Y, direction.Y, minY, maxY, ref tMin, ref tMax);

        static bool Slab(float rayOrigin, float rayDirection, float min, float max, ref float enter, ref float exit)
        {
            if (MathF.Abs(rayDirection) < 1e-8f) return rayOrigin >= min && rayOrigin <= max;
            var inverse = 1f / rayDirection;
            var first = (min - rayOrigin) * inverse;
            var second = (max - rayOrigin) * inverse;
            if (first > second) (first, second) = (second, first);
            if (first > enter) enter = first;
            if (second < exit) exit = second;
            return enter <= exit;
        }
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
