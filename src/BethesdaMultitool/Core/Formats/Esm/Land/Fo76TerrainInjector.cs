using BethesdaMultitool.Core.Formats.Esm.Land.Btd;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Land;

/// <summary>
///     Attaches Fallout 76 terrain heights to parsed exterior cells. FO76 worldspaces have CELL
///     records but NO in-record VHGT — the heightmap lives in an external Bethesda Terrain Data
///     (<c>.btd</c>) file under <c>Data/Terrain/&lt;worldspaceEditorId&gt;.btd</c> (e.g.
///     <c>Appalachia.btd</c>). This injector decodes the BTD and fills each exterior cell's
///     <see cref="LandHeightmap.ExactHeights" /> (the same float-grid path the runtime-mesh and
///     Morrowind importers use), so both the 2D world map and the 3D terrain renderer light up with
///     zero changes to either renderer — they read heights through the shared
///     <c>DecodedTerrainCell.Decode</c> abstraction (the 2D map downsamples; the 3D
///     <c>TerrainMeshBuilder</c> renders the native grid directly).
///     <para>
///         Cells are decoded at full native fidelity (LOD0 = 128×128 samples) into a 129×129 grid:
///         the extra row/column is the <b>shared edge</b> pulled from the east/north neighbor's
///         sample 0. Fallout 76 packs 128 <i>disjoint</i> samples per cell (a BTD tile is exactly
///         8×128 = 1024 wide, with no shared border), so without that +1 the cell mesh's east/north
///         edge would take its own sample 127 instead of the neighbor's sample 0 at the same world
///         position — a height mismatch that renders as a crack between cells.
///     </para>
/// </summary>
public static class Fo76TerrainInjector
{
    // Native BTD resolution: LOD0 = 128 samples per cell edge. Full fidelity (the 3D viewer renders
    // the native grid via the variable-resolution TerrainMeshBuilder; the 2D map downsamples 129->33
    // by an exact step of 4). 40k-cell worldspaces (APPALACHIA) cost ~2.7 GB resident at this size.
    private const int SourceLod = 0;
    private const int CellSamples = 128 >> SourceLod; // 128
    private const int GridSize = CellSamples + 1;      // 129: own 128 samples + the neighbor's shared edge

    // LandHeightmap requires HeightDeltas, but ExactHeights takes precedence in CalculateHeights(),
    // so the deltas are never read — share one empty array (matching the Morrowind importer).
    private static readonly sbyte[] UnusedDeltas = [];

    /// <summary>
    ///     Populates exterior-cell heightmaps for every Fallout 76 worldspace that has a matching
    ///     <c>.btd</c> next to <paramref name="esmPath" />. No-op for non-FO76 plugins. Returns the
    ///     number of cells populated.
    /// </summary>
    public static int Inject(RecordCollection records, string esmPath)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (string.IsNullOrEmpty(esmPath) || !IsFallout76(esmPath))
        {
            return 0;
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(esmPath));
        if (dir is null)
        {
            return 0;
        }

        var terrainDir = Path.Combine(dir, "Terrain");
        if (!Directory.Exists(terrainDir))
        {
            return 0;
        }

        var populated = 0;
        foreach (var worldspace in records.Worldspaces)
        {
            if (string.IsNullOrEmpty(worldspace.EditorId))
            {
                continue;
            }

            var btdPath = Path.Combine(terrainDir, worldspace.EditorId + ".btd");
            if (!File.Exists(btdPath))
            {
                continue;
            }

            try
            {
                using var btd = new BtdFile(btdPath);
                // Hold several 8×8-cell tiles resident: a cell's own tile plus the east/north/NE
                // neighbor tiles its shared edge reads from, so a tile-ordered sweep decompresses
                // each tile's LOD pyramid once instead of thrashing.
                btd.SetTileCacheSize(16);
                populated += InjectWorldspace(worldspace, btd);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
            {
                // Corrupt / unsupported BTD (e.g. LZ4-frame) — leave the worldspace's cells flat.
            }
        }

        return populated;
    }

    private static int InjectWorldspace(WorldspaceRecord worldspace, BtdFile btd)
    {
        var targets = new List<CellRecord>();
        foreach (var cell in worldspace.Cells)
        {
            if (cell.IsInterior || cell.Heightmap is not null)
            {
                continue;
            }

            if (cell.GridX is not { } gx || cell.GridY is not { } gy)
            {
                continue;
            }

            if (gx < btd.CellMinX || gx > btd.CellMaxX || gy < btd.CellMinY || gy > btd.CellMaxY)
            {
                continue;
            }

            targets.Add(cell);
        }

        // Decode in BTD tile order (8×8-cell tiles). Arbitrary cell order would reload — and
        // re-decompress — the same tile repeatedly as the sweep and its neighbor-edge reads jump
        // around the grid; tile-grouping keeps each tile's decompressed LOD pyramid hot in the cache.
        targets.Sort(CompareByTile);

        foreach (var cell in targets)
        {
            cell.Heightmap = new LandHeightmap
            {
                HeightDeltas = UnusedDeltas,
                ExactHeights = BuildExactHeights(btd, cell.GridX!.Value, cell.GridY!.Value)
            };
        }

        return targets.Count;
    }

    /// <summary>Orders cells by their 8×8-cell BTD tile, then by cell within the tile.</summary>
    private static int CompareByTile(CellRecord a, CellRecord b)
    {
        int ay = a.GridY!.Value >> 3, by = b.GridY!.Value >> 3;
        if (ay != by) return ay.CompareTo(by);
        int ax = a.GridX!.Value >> 3, bx = b.GridX!.Value >> 3;
        if (ax != bx) return ax.CompareTo(bx);
        if (a.GridY!.Value != b.GridY!.Value) return a.GridY!.Value.CompareTo(b.GridY!.Value);
        return a.GridX!.Value.CompareTo(b.GridX!.Value);
    }

    /// <summary>
    ///     Decodes one BTD cell at full native resolution into a 129×129 grid of world heights (game
    ///     units). Row/column 0 is the south/west edge (VHGT / ExactHeights convention); the last
    ///     row/column (index 128) is the shared edge taken from the north/east neighbor's sample 0,
    ///     clamped to this cell's own outermost sample at the worldspace boundary.
    /// </summary>
    private static float[,] BuildExactHeights(BtdFile btd, int cellX, int cellY)
    {
        var self = btd.GetCellHeightGrid(cellX, cellY, SourceLod); // float[CellSamples²], south-to-north
        var exact = new float[GridSize, GridSize];

        // Interior: this cell's own samples.
        for (var j = 0; j < CellSamples; j++)
        {
            for (var i = 0; i < CellSamples; i++)
            {
                exact[j, i] = self[(j * CellSamples) + i];
            }
        }

        FillSharedEdges(btd, exact, self, cellX, cellY);
        return exact;
    }

    /// <summary>
    ///     Fills the 129×129 grid's east column and north row (and the NE corner) from the adjacent
    ///     cells' sample 0, so neighboring cell meshes meet without a crack. At the worldspace edge
    ///     (no neighbor) the cell's own outermost sample is repeated.
    /// </summary>
    private static void FillSharedEdges(BtdFile btd, float[,] exact, float[] self, int cellX, int cellY)
    {
        var hasEast = cellX < btd.CellMaxX;
        var hasNorth = cellY < btd.CellMaxY;
        const int last = CellSamples - 1;

        for (var j = 0; j < CellSamples; j++)
        {
            exact[j, CellSamples] = hasEast
                ? btd.GetCellHeightSample(cellX + 1, cellY, 0, j, SourceLod)
                : self[(j * CellSamples) + last];
        }

        for (var i = 0; i < CellSamples; i++)
        {
            exact[CellSamples, i] = hasNorth
                ? btd.GetCellHeightSample(cellX, cellY + 1, i, 0, SourceLod)
                : self[(last * CellSamples) + i];
        }

        // North-east corner: the NE neighbor's (0,0), else extend whichever edge exists inward.
        if (hasEast && hasNorth)
        {
            exact[CellSamples, CellSamples] = btd.GetCellHeightSample(cellX + 1, cellY + 1, 0, 0, SourceLod);
        }
        else if (hasNorth)
        {
            exact[CellSamples, CellSamples] = exact[CellSamples, last];
        }
        else if (hasEast)
        {
            exact[CellSamples, CellSamples] = exact[last, CellSamples];
        }
        else
        {
            exact[CellSamples, CellSamples] = self[(last * CellSamples) + last];
        }
    }

    private static bool IsFallout76(string esmPath) =>
        GameDetector.DetectFromFile(esmPath).Game == BethesdaGame.Fallout76;
}
