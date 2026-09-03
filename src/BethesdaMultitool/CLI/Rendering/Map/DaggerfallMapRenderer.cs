using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;

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
        scale = Math.Max(1, scale);
        var width = DaggerfallWoodsFile.Width * scale;
        var height = DaggerfallWoodsFile.Height * scale;
        var pixels = new byte[width * height * 4];
        var heights = woods.HeightMap.Span;
        var distinct = new HashSet<byte>();

        for (var y = 0; y < DaggerfallWoodsFile.Height; y++)
        {
            for (var x = 0; x < DaggerfallWoodsFile.Width; x++)
            {
                var h = heights[(y * DaggerfallWoodsFile.Width) + x];
                distinct.Add(h);
                Fill(pixels, width, x, y, scale, h, h, h);
            }
        }

        var path = Path.Combine(outputDir, "WOODS_heightmap.png");
        PngWriter.SaveRgba(pixels, width, height, path);
        return new ArenaMapRenderer.RenderedLayer(path, "HEIGHT", width, height, distinct.Count);
    }

    /// <summary>Writes a PAK overlay with one diagnostic hue per value.</summary>
    public static ArenaMapRenderer.RenderedLayer RenderOverlay(DaggerfallPakFile pak, string outputDir, int scale)
    {
        ArgumentNullException.ThrowIfNull(pak);

        Directory.CreateDirectory(outputDir);
        scale = Math.Max(1, scale);
        var width = DaggerfallPakFile.Width * scale;
        var height = DaggerfallPakFile.Height * scale;
        var pixels = new byte[width * height * 4];
        var distinct = new HashSet<byte>();

        for (var y = 0; y < DaggerfallPakFile.Height; y++)
        {
            for (var x = 0; x < DaggerfallPakFile.Width; x++)
            {
                var value = pak[x, y];
                distinct.Add(value);
                var (r, g, b) = ArenaMapRenderer.ColorFor(value);
                Fill(pixels, width, x, y, scale, r, g, b);
            }
        }

        var path = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(pak.Name) + "_overlay.png");
        PngWriter.SaveRgba(pixels, width, height, path);
        return new ArenaMapRenderer.RenderedLayer(path, "OVERLAY", width, height, distinct.Count);
    }

    private static void Fill(byte[] pixels, int rowWidth, int x, int y, int scale, byte r, byte g, byte b)
    {
        for (var dy = 0; dy < scale; dy++)
        {
            var row = ((y * scale) + dy) * rowWidth;
            for (var dx = 0; dx < scale; dx++)
            {
                var offset = (row + (x * scale) + dx) * 4;
                pixels[offset] = r;
                pixels[offset + 1] = g;
                pixels[offset + 2] = b;
                pixels[offset + 3] = 255;
            }
        }
    }
}
