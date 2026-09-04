using BethesdaMultitool.Core.Formats.Arena;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Rendering.Level2D;

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
        var (pixels, outWidth, outHeight, distinct) = VoxelLayerRasterizer.Rasterize(
            width, depth, scale, (x, z) => ArenaMifLevel.VoxelAt(voxels, width, x, z));

        PngWriter.SaveRgba(pixels, outWidth, outHeight, path);
        return new RenderedLayer(path, layerName, outWidth, outHeight, distinct);
    }

}
