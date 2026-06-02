using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Bilinear-interpolated terrain height lookup for the v3 walk-mode camera. Given a world
///     XY position and the per-cell record dictionary, returns the ground Z by:
///     1. Locating the containing exterior cell.
///     2. Mapping the world position to a fractional grid coordinate in [0..32] × [0..32].
///     3. Bilinearly interpolating the four surrounding vertex heights from the LAND heightmap.
///     <para>
///         Returns <c>null</c> when the camera is over a cell without a usable heightmap (no
///         LAND record, or worldspace edge). The caller should leave the camera Z untouched in
///         that case rather than snapping to an unknown ground.
///     </para>
/// </summary>
internal static class TerrainHeightSampler
{
    private const int Grid = 33;
    private const int LastIndex = Grid - 1;

    public static float? Sample(
        IReadOnlyDictionary<(int gx, int gy), CellRecord> cells,
        float worldX,
        float worldY,
        global::FalloutXbox360Utils.WorldRenderCache? cache = null)
    {
        var gx = (int)MathF.Floor(worldX / global::FalloutXbox360Utils.WorldGridConstants.CellSize);
        var gy = (int)MathF.Floor(worldY / global::FalloutXbox360Utils.WorldGridConstants.CellSize);

        if (!cells.TryGetValue((gx, gy), out var cell)) return null;
        var terrain = cache?.GetTerrain(cell);
        float[,]? heights = null;
        if (terrain is null)
        {
            var heightmap = cell.Heightmap ?? cell.RuntimeTerrainMesh?.ToLandHeightmap();
            if (heightmap is null) return null;
            heights = heightmap.CalculateHeights();
        }
        else if (!terrain.HasTerrain)
        {
            return null;
        }

        // Local position within the cell, in vertex units [0..32].
        var localXVerts = (worldX - gx * global::FalloutXbox360Utils.WorldGridConstants.CellSize) / global::FalloutXbox360Utils.WorldGridConstants.CellSize * LastIndex;
        var localYVerts = (worldY - gy * global::FalloutXbox360Utils.WorldGridConstants.CellSize) / global::FalloutXbox360Utils.WorldGridConstants.CellSize * LastIndex;

        var i0 = Math.Clamp((int)MathF.Floor(localXVerts), 0, LastIndex);
        var j0 = Math.Clamp((int)MathF.Floor(localYVerts), 0, LastIndex);
        var i1 = Math.Min(i0 + 1, LastIndex);
        var j1 = Math.Min(j0 + 1, LastIndex);

        var fx = Math.Clamp(localXVerts - i0, 0f, 1f);
        var fy = Math.Clamp(localYVerts - j0, 0f, 1f);

        var h00 = terrain?.HeightAt(i0, j0) ?? heights![j0, i0];
        var h10 = terrain?.HeightAt(i1, j0) ?? heights![j0, i1];
        var h01 = terrain?.HeightAt(i0, j1) ?? heights![j1, i0];
        var h11 = terrain?.HeightAt(i1, j1) ?? heights![j1, i1];

        var h0 = h00 * (1 - fx) + h10 * fx;
        var h1 = h01 * (1 - fx) + h11 * fx;
        return h0 * (1 - fy) + h1 * fy;
    }
}
