using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Fallout;
using BethesdaMultitool.Core.Rendering.Level2D;

namespace BethesdaMultitool.CLI.Rendering.Map;

/// <summary>
///     Renders a Fallout <c>.MAP</c>'s tile grids to PNG — one image per present elevation, floor
///     tiles and roof tiles side by side.
///     <para>
///         ⚠ The colours are DIAGNOSTIC, a stable hue per tile id, not the game's art: drawing the
///         real terrain means resolving each id through <c>TILES.LST</c> to a prototype, then to its
///         <c>ART\TILES</c> frame, and compositing on the isometric grid. This is the "does the
///         geometry read as a place" view, and it is also how the grid's ORIENTATION should be
///         settled — by eye, against a location whose shape is known.
///     </para>
/// </summary>
internal static class FalloutMapRenderer
{
    /// <summary>Writes each elevation as <c>&lt;stem&gt;_elevN_floor.png</c> and <c>_roof.png</c>.</summary>
    public static IReadOnlyList<ArenaMapRenderer.RenderedLayer> RenderElevations(
        FalloutMapFile map, string outputDir, int scale)
    {
        ArgumentNullException.ThrowIfNull(map);

        Directory.CreateDirectory(outputDir);
        var stem = Path.GetFileNameWithoutExtension(map.Name).ToUpperInvariant();
        var results = new List<ArenaMapRenderer.RenderedLayer>(map.Elevations.Count * 2);

        foreach (var elevation in map.Elevations)
        {
            results.Add(Render(map, elevation, outputDir, stem, scale, false));
            results.Add(Render(map, elevation, outputDir, stem, scale, true));
        }

        return results;
    }

    private static ArenaMapRenderer.RenderedLayer Render(
        FalloutMapFile map, FalloutMapElevation elevation, string outputDir, string stem, int scale, bool roof)
    {
        var distinct = new HashSet<ushort>();
        var (pixels, width, height) = VoxelLayerRasterizer.RasterizeColored(
            FalloutMapFile.GridWidth, FalloutMapFile.GridHeight, scale,
            (x, y) =>
            {
                var tile = elevation.Tiles[y * FalloutMapFile.GridWidth + x];
                var id = roof ? tile.Roof : tile.Floor;
                distinct.Add(id);

                // Tile 1 is the empty tile, and drawing it as a colour would bury the shape of the
                // map in noise — it is the background, so it reads as background.
                return id <= 1 ? ((byte)0, (byte)0, (byte)0) : VoxelLayerRasterizer.ColorFor(id);
            });

        var kind = roof ? "roof" : "floor";
        var path = Path.Combine(outputDir, $"{stem}_elev{elevation.Index}_{kind}.png");
        PngWriter.SaveRgba(pixels, width, height, path);
        return new ArenaMapRenderer.RenderedLayer(
            path, $"ELEV{elevation.Index} {kind.ToUpperInvariant()} (of {map.Elevations.Count})",
            width, height, distinct.Count);
    }
}
