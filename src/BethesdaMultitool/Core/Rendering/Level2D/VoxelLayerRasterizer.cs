namespace BethesdaMultitool.Core.Rendering.Level2D;

/// <summary>
///     Turns a grid of voxel ids into an RGBA image, one pixel per voxel before scaling.
///     <para>
///         The colours are DIAGNOSTIC, not the game's: an Arena <c>.MIF</c> stores texture-table
///         indices, and resolving those to real textures needs the level's <c>.INF</c> plus (for
///         cities) tables still locked inside the packed executable. So each distinct id gets a
///         stable, well-separated hue and id 0 — empty space — stays black. That makes layout,
///         rooms and corridors legible, which is what a map view is for at this stage.
///     </para>
///     <para>
///         This lives in <c>Core/</c> so both the PNG-writing CLI renderer and the in-memory
///         <see cref="ILevel2DSource" /> the GUI map pane consumes produce the SAME pixels; when
///         the two drifted apart the viewer and the exported file would disagree about a level.
///     </para>
/// </summary>
internal static class VoxelLayerRasterizer
{
    /// <summary>Bytes per pixel in the buffers this class writes.</summary>
    public const int BytesPerPixel = 4;

    /// <summary>
    ///     Rasterizes a <paramref name="width" /> x <paramref name="depth" /> grid, reading each
    ///     cell through <paramref name="voxelAt" /> and drawing it as a <paramref name="scale" />
    ///     -square block. Also reports how many distinct ids the layer used, which is what tells a
    ///     caller whether a layer is authored or blank.
    /// </summary>
    /// <param name="voxelAt">Reads the id at a grid (x, z) — each format packs its rows its own way.</param>
    public static (byte[] Pixels, int Width, int Height, int DistinctVoxels) Rasterize(
        int width, int depth, int scale, Func<int, int, ushort> voxelAt)
    {
        ArgumentNullException.ThrowIfNull(voxelAt);

        var distinct = new HashSet<ushort>();
        var (pixels, outWidth, outHeight) = RasterizeColored(width, depth, scale, (x, z) =>
        {
            var voxel = voxelAt(x, z);
            distinct.Add(voxel);
            return ColorFor(voxel);
        });

        return (pixels, outWidth, outHeight, distinct.Count);
    }

    /// <summary>
    ///     The general grid rasterizer: reads a colour per cell and expands each into a
    ///     <paramref name="scale" />-square opaque block. Daggerfall's WOODS heightmap reads
    ///     elevation straight to luminance through this, while its PAK overlays and Arena's voxel
    ///     planes read through <see cref="ColorFor" /> — one fill loop for all of them.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height) RasterizeColored(
        int width, int height, int scale, Func<int, int, (byte R, byte G, byte B)> colorAt)
    {
        ArgumentNullException.ThrowIfNull(colorAt);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        scale = Math.Max(1, scale);
        var outWidth = width * scale;
        var outHeight = height * scale;
        var pixels = new byte[outWidth * outHeight * BytesPerPixel];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = colorAt(x, y);

                for (var dy = 0; dy < scale; dy++)
                {
                    var row = ((y * scale) + dy) * outWidth;
                    for (var dx = 0; dx < scale; dx++)
                    {
                        var offset = (row + (x * scale) + dx) * BytesPerPixel;
                        pixels[offset + 0] = r;
                        pixels[offset + 1] = g;
                        pixels[offset + 2] = b;
                        pixels[offset + 3] = 255;
                    }
                }
            }
        }

        return (pixels, outWidth, outHeight);
    }

    /// <summary>
    ///     A stable diagnostic colour per voxel id. Id 0 is empty space and stays black; every
    ///     other id is spread around the hue circle by the golden-ratio conjugate, which keeps
    ///     numerically adjacent ids visually far apart.
    /// </summary>
    public static (byte R, byte G, byte B) ColorFor(ushort voxel)
    {
        if (voxel == 0)
        {
            return (0, 0, 0);
        }

        const double goldenRatioConjugate = 0.618033988749895;
        var hue = (voxel * goldenRatioConjugate) % 1.0;
        return HsvToRgb(hue, 0.65, 0.95);
    }

    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        var sector = (int)(h * 6) % 6;
        var f = (h * 6) - Math.Floor(h * 6);
        var p = v * (1 - s);
        var q = v * (1 - (f * s));
        var t = v * (1 - ((1 - f) * s));

        var (r, g, b) = sector switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q)
        };

        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
}
