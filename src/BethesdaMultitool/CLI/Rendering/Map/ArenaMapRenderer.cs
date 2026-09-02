using BethesdaMultitool.Core.Formats.Arena;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;

namespace BethesdaMultitool.CLI.Rendering.Map;

/// <summary>
///     Renders Arena voxel maps (<c>.MIF</c> levels and <c>.RMD</c> wilderness chunks) to PNGs —
///     one image per layer, one pixel per voxel.
///     <para>
///         The colours are diagnostic, not the game's: a .MIF stores texture-table indices, and
///         resolving those to real textures needs the level's <c>.INF</c> plus (for cities) tables
///         still locked inside the packed executable. So each distinct voxel id gets a stable,
///         well-separated hue and id 0 — empty space — stays black. That makes layout, rooms and
///         corridors legible, which is what a map view is for at this stage.
///     </para>
/// </summary>
internal static class ArenaMapRenderer
{
    /// <summary>One rendered layer image.</summary>
    internal sealed record RenderedLayer(string Path, string Layer, int Width, int Height, int DistinctVoxels);

    /// <summary>Renders every layer of every level in a .MIF.</summary>
    public static IReadOnlyList<RenderedLayer> RenderMif(ArenaMifFile map, string outputDir, int scale)
    {
        ArgumentNullException.ThrowIfNull(map);

        Directory.CreateDirectory(outputDir);
        var baseName = Path.GetFileNameWithoutExtension(map.Name);
        var written = new List<RenderedLayer>();

        for (var levelIndex = 0; levelIndex < map.Levels.Count; levelIndex++)
        {
            var level = map.Levels[levelIndex];
            foreach (var (layerName, voxels) in EnumerateLayers(level))
            {
                if (voxels.Length == 0)
                {
                    continue;
                }

                var fileName = map.Levels.Count == 1
                    ? $"{baseName}_{layerName}.png"
                    : $"{baseName}_L{levelIndex:D2}_{layerName}.png";
                written.Add(WriteLayer(
                    Path.Combine(outputDir, fileName), layerName, voxels, map.Width, map.Depth, scale));
            }
        }

        return written;
    }

    /// <summary>Renders the three layers of a .RMD wilderness chunk.</summary>
    public static IReadOnlyList<RenderedLayer> RenderRmd(ArenaRmdFile chunk, string name, string outputDir, int scale)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        Directory.CreateDirectory(outputDir);
        var baseName = Path.GetFileNameWithoutExtension(name);
        var layers = new (string Name, ushort[] Voxels)[]
        {
            ("FLOR", chunk.Floor),
            ("MAP1", chunk.Map1),
            ("MAP2", chunk.Map2)
        };

        return
        [
            .. layers.Select(l => WriteLayer(
                Path.Combine(outputDir, $"{baseName}_{l.Name}.png"),
                l.Name,
                l.Voxels,
                ArenaRmdFile.Width,
                ArenaRmdFile.Depth,
                scale))
        ];
    }

    private static IEnumerable<(string Name, ushort[] Voxels)> EnumerateLayers(ArenaMifLevel level)
    {
        yield return ("FLOR", level.Floor);
        yield return ("MAP1", level.Map1);
        yield return ("MAP2", level.Map2);
    }

    private static RenderedLayer WriteLayer(
        string path,
        string layerName,
        ushort[] voxels,
        int width,
        int depth,
        int scale)
    {
        scale = Math.Max(1, scale);
        var outWidth = width * scale;
        var outHeight = depth * scale;
        var pixels = new byte[outWidth * outHeight * 4];
        var distinct = new HashSet<ushort>();

        for (var z = 0; z < depth; z++)
        {
            for (var x = 0; x < width; x++)
            {
                var voxel = ArenaMifLevel.VoxelAt(voxels, width, x, z);
                distinct.Add(voxel);
                var (r, g, b) = ColorFor(voxel);

                for (var dy = 0; dy < scale; dy++)
                {
                    var row = ((z * scale) + dy) * outWidth;
                    for (var dx = 0; dx < scale; dx++)
                    {
                        var offset = (row + (x * scale) + dx) * 4;
                        pixels[offset + 0] = r;
                        pixels[offset + 1] = g;
                        pixels[offset + 2] = b;
                        pixels[offset + 3] = 255;
                    }
                }
            }
        }

        PngWriter.SaveRgba(pixels, outWidth, outHeight, path);
        return new RenderedLayer(path, layerName, outWidth, outHeight, distinct.Count);
    }

    /// <summary>
    ///     A stable diagnostic colour per voxel id. Id 0 is empty space and stays black; every
    ///     other id is spread around the hue circle by the golden-ratio conjugate, which keeps
    ///     numerically adjacent ids visually far apart.
    /// </summary>
    internal static (byte R, byte G, byte B) ColorFor(ushort voxel)
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
