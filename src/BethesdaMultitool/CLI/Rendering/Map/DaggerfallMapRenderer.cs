using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Rendering.Level2D;

namespace BethesdaMultitool.CLI.Rendering.Map;

/// <summary>
///     Renders Daggerfall's world-map layers to PNG: the WOODS.WLD heightmap as greyscale
///     (elevation byte straight to luminance, so sea level is near-black and the mountains are
///     bright) and the CLIMATE/POLITIC overlays with the same stable diagnostic hue per value that
///     the Arena voxel layers use — the values are indices into engine tables, not colours.
/// </summary>
internal static class DaggerfallMapRenderer
{
    /// <summary>Writes the heightmap as a greyscale PNG.</summary>
    public static ArenaMapRenderer.RenderedLayer RenderHeightMap(DaggerfallWoodsFile woods, string outputDir, int scale)
    {
        ArgumentNullException.ThrowIfNull(woods);

        Directory.CreateDirectory(outputDir);
        var distinct = new HashSet<byte>();
        var (pixels, width, height) = VoxelLayerRasterizer.RasterizeColored(
            DaggerfallWoodsFile.Width, DaggerfallWoodsFile.Height, scale,
            (x, y) =>
            {
                var h = woods.GetHeight(x, y);
                distinct.Add(h);
                return (h, h, h);
            });

        var path = Path.Combine(outputDir, "WOODS_heightmap.png");
        PngWriter.SaveRgba(pixels, width, height, path);
        return new ArenaMapRenderer.RenderedLayer(path, "HEIGHT", width, height, distinct.Count);
    }

    /// <summary>Writes a PAK overlay with one diagnostic hue per value.</summary>
    public static ArenaMapRenderer.RenderedLayer RenderOverlay(DaggerfallPakFile pak, string outputDir, int scale)
    {
        ArgumentNullException.ThrowIfNull(pak);

        Directory.CreateDirectory(outputDir);
        var distinct = new HashSet<byte>();
        var (pixels, width, height) = VoxelLayerRasterizer.RasterizeColored(
            DaggerfallPakFile.Width, DaggerfallPakFile.Height, scale,
            (x, y) =>
            {
                var value = pak[x, y];
                distinct.Add(value);
                return VoxelLayerRasterizer.ColorFor(value);
            });

        var path = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(pak.Name) + "_overlay.png");
        PngWriter.SaveRgba(pixels, width, height, path);
        return new ArenaMapRenderer.RenderedLayer(path, "OVERLAY", width, height, distinct.Count);
    }
}
