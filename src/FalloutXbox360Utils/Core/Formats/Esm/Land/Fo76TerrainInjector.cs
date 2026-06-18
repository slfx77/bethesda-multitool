// Copyright (c) 2026 FalloutXbox360Utils Contributors
// Licensed under the MIT License.

using FalloutXbox360Utils.Core.Formats.Esm.Land.Btd;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Land;

/// <summary>
///     Attaches Fallout 76 terrain heights to parsed exterior cells. FO76 worldspaces have CELL
///     records but NO in-record VHGT — the heightmap lives in an external Bethesda Terrain Data
///     (<c>.btd</c>) file under <c>Data/Terrain/&lt;worldspaceEditorId&gt;.btd</c> (e.g.
///     <c>Appalachia.btd</c>). This injector decodes the BTD and fills each exterior cell's
///     <see cref="LandHeightmap.ExactHeights" /> (the same float-grid path the runtime-mesh and
///     Morrowind importers use), so both the 2D world map and the 3D terrain renderer light up with
///     zero changes to either renderer — they read heights through the shared
///     <c>DecodedTerrainCell.Decode</c> abstraction.
/// </summary>
public static class Fo76TerrainInjector
{
    // BTD LOD to decode for each cell: LOD2 = 32x32 samples/cell — fast (skips the huge LOD0/LOD1
    // block layers) and already at the pipeline's 33x33 target resolution. Full 128x128 fidelity
    // would need a variable-resolution terrain mesh (a separate follow-up shared with Morrowind).
    private const int SourceLod = 2;

    // LandHeightmap requires HeightDeltas, but ExactHeights takes precedence in CalculateHeights(),
    // so a single shared all-zero delta grid is never read.
    private static readonly sbyte[] UnusedDeltas = new sbyte[33 * 33];

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
                btd.SetTileCacheSize(8);
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
        var count = 0;
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

            cell.Heightmap = new LandHeightmap
            {
                HeightDeltas = UnusedDeltas,
                ExactHeights = BuildExactHeights(btd, gx, gy)
            };
            count++;
        }

        return count;
    }

    /// <summary>
    ///     Decodes one BTD cell and resamples it to the pipeline's 33×33 grid of world heights
    ///     (game units). Row 0 is the south edge, matching the VHGT / ExactHeights convention.
    /// </summary>
    private static float[,] BuildExactHeights(BtdFile btd, int cellX, int cellY)
    {
        var srcN = 128 >> SourceLod; // 32 at LOD2
        var src = btd.GetCellHeightGrid(cellX, cellY, SourceLod); // float[srcN*srcN], south-to-north
        var exact = new float[33, 33];
        for (var y = 0; y < 33; y++)
        {
            var sy = (int)Math.Round(y * (double)(srcN - 1) / 32.0);
            for (var x = 0; x < 33; x++)
            {
                var sx = (int)Math.Round(x * (double)(srcN - 1) / 32.0);
                exact[y, x] = src[(sy * srcN) + sx];
            }
        }

        return exact;
    }

    private static bool IsFallout76(string esmPath)
    {
        try
        {
            Span<byte> header = stackalloc byte[64];
            using var fs = new FileStream(esmPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var read = fs.Read(header);
            return read >= 6 && PluginFormat.Detect(header[..read]).Game == BethesdaGame.Fallout76;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
