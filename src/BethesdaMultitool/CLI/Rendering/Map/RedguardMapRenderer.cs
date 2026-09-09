using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Redguard;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Imaging;
using BethesdaMultitool.Core.Rendering.Level2D;

namespace BethesdaMultitool.CLI.Rendering.Map;

/// <summary>
///     Renders a Redguard <c>.WLD</c> terrain to PNG by what its layers MEAN (see
///     <see cref="RedguardWldFile" />): a height map from layer 0 through the game's height
///     table, the surface composited from layer 2's texture records and rotations when the
///     install's <c>3dart\TEXTURE.302</c> and world palette can be found beside the file, and a
///     scatter map from layer 1 only when it holds anything (retail: never).
///     <para>
///         Without the install the surface falls back to a DIAGNOSTIC hue per texture index —
///         stable, legible, and not the game's art. The textured composite applies the rotation
///         the surface sampler applies to its texel lookups; whether the polygon renderer turns
///         the texture the same way is a stated assumption, not a measured one.
///     </para>
/// </summary>
internal static class RedguardMapRenderer
{
    /// <summary>
    ///     Finds the texture set and palette for a terrain by walking up from the file to its
    ///     Redguard install: <c>3dart\TEXTURE.&lt;set&gt;</c> from the tile headers, and the palette
    ///     WORLD.INI assigns to the first world that names this terrain (ISLAND.COL when none does —
    ///     EXTPALAC.WLD is registered nowhere). Null when the file is not inside an install.
    /// </summary>
    public static TerrainArt? TryLocateArt(RedguardWldFile wld, string? sourcePath)
    {
        ArgumentNullException.ThrowIfNull(wld);
        if (sourcePath is null || ClassicGameLocator.DetectRootForFile(sourcePath) is not { } detected
                               || detected.Profile.Game != BethesdaGame.Redguard)
        {
            return null;
        }

        var root = detected.Root;
        var texturePath = Path.Combine(root, "3dart", $"TEXTURE.{wld.TextureSet:000}");
        if (!File.Exists(texturePath))
        {
            return null;
        }

        string? palettePath = null;
        var iniPath = Path.Combine(root, RedguardWorldIni.FileName);
        if (File.Exists(iniPath))
        {
            var registry = RedguardWorldIni.Load(root);
            var owner = registry.Worlds.FirstOrDefault(w =>
                w.TerrainPath is not null
                && string.Equals(Path.GetFileName(w.TerrainPath), Path.GetFileName(wld.Name),
                    StringComparison.OrdinalIgnoreCase));
            if (owner?.PalettePath is { } relative)
            {
                palettePath = Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar));
            }
        }

        palettePath ??= Path.Combine(root, "3dart", "ISLAND.COL");
        if (!File.Exists(palettePath))
        {
            return null;
        }

        var textures = DaggerfallTextureFile.Parse(File.ReadAllBytes(texturePath), Path.GetFileName(texturePath));
        var palette = Palette.LoadDaggerfallCol(File.ReadAllBytes(palettePath));
        return new TerrainArt(textures, palette, texturePath, palettePath);
    }

    /// <summary>
    ///     Writes <c>&lt;stem&gt;_height.png</c>, <c>&lt;stem&gt;_surface.png</c> (textured when
    ///     <paramref name="art" /> is given, else <c>&lt;stem&gt;_surface_index.png</c> in diagnostic
    ///     hues) and, only when layer 1 carries anything, <c>&lt;stem&gt;_scatter.png</c>.
    /// </summary>
    public static IReadOnlyList<ArenaMapRenderer.RenderedLayer> RenderTerrain(RedguardWldFile wld, string outputDir,
        int scale, TerrainArt? art)
    {
        ArgumentNullException.ThrowIfNull(wld);
        ArgumentNullException.ThrowIfNull(outputDir);

        Directory.CreateDirectory(outputDir);
        scale = Math.Max(1, scale);
        var stem = Path.GetFileNameWithoutExtension(wld.Name).ToUpperInvariant();
        var results = new List<ArenaMapRenderer.RenderedLayer>(3);

        results.Add(RenderHeight(wld, Path.Combine(outputDir, $"{stem}_height.png"), scale));
        results.Add(art is null
            ? RenderSurfaceIndex(wld, Path.Combine(outputDir, $"{stem}_surface_index.png"), scale)
            : RenderSurfaceTextured(wld, art, Path.Combine(outputDir, $"{stem}_surface.png"), scale));

        if (wld.ScatterLayer.Indices.Any(b => b != 0))
        {
            results.Add(RenderScatter(wld, Path.Combine(outputDir, $"{stem}_scatter.png"), scale));
        }

        return results;
    }

    /// <summary>Height index doubled to luminance: 128 distinct levels, monotonic in world height.</summary>
    private static ArenaMapRenderer.RenderedLayer RenderHeight(RedguardWldFile wld, string path, int scale)
    {
        var distinct = new HashSet<int>();
        var (pixels, width, height) = VoxelLayerRasterizer.RasterizeColored(
            RedguardWldFile.MapSize, RedguardWldFile.MapSize, scale,
            (x, z) =>
            {
                var index = wld.HeightIndexAt(x, z);
                distinct.Add(index);
                var v = (byte)(index * 2);
                return (v, v, v);
            });
        PngWriter.SaveRgba(pixels, width, height, path);
        return new ArenaMapRenderer.RenderedLayer(path, "height (index x2; world Y = -table[index])", width, height,
            distinct.Count);
    }

    private static ArenaMapRenderer.RenderedLayer RenderSurfaceIndex(RedguardWldFile wld, string path, int scale)
    {
        var distinct = new HashSet<int>();
        var (pixels, width, height) = VoxelLayerRasterizer.RasterizeColored(
            RedguardWldFile.MapSize, RedguardWldFile.MapSize, scale,
            (x, z) =>
            {
                var index = wld.SurfaceTextureAt(x, z);
                distinct.Add(index);
                return VoxelLayerRasterizer.ColorFor((ushort)index);
            });
        PngWriter.SaveRgba(pixels, width, height, path);
        return new ArenaMapRenderer.RenderedLayer(path, "surface texture index (DIAGNOSTIC hues, no install found)",
            width, height, distinct.Count);
    }

    private static ArenaMapRenderer.RenderedLayer RenderSurfaceTextured(RedguardWldFile wld, TerrainArt art,
        string path, int scale)
    {
        var size = RedguardWldFile.MapSize * scale;
        var pixels = new byte[size * size * 4];
        var rgba = art.Palette.Rgba.ToArray();
        var distinct = new HashSet<int>();
        var records = art.Textures.Records;

        for (var cz = 0; cz < RedguardWldFile.MapSize; cz++)
        {
            for (var cx = 0; cx < RedguardWldFile.MapSize; cx++)
            {
                var index = wld.SurfaceTextureAt(cx, cz);
                var rotation = wld.SurfaceRotationAt(cx, cz);
                distinct.Add(index);
                var frame = index < records.Count && records[index].Frames.Count > 0 ? records[index].Frames[0] : null;

                for (var py = 0; py < scale; py++)
                {
                    var row = (cz * scale + py) * size;
                    for (var px = 0; px < scale; px++)
                    {
                        var dst = (row + cx * scale + px) * 4;
                        if (frame is null || frame.Width == 0 || frame.Height == 0)
                        {
                            pixels[dst] = 255;
                            pixels[dst + 3] = 255;
                            continue;
                        }

                        var u = px * frame.Width / scale;
                        var v = py * frame.Height / scale;
                        var (ru, rv) = RedguardWldFile.RotateTexel(u, v, rotation, frame.Width);
                        var colour = frame.Indices[rv * frame.Width + ru] * 4;
                        pixels[dst] = rgba[colour];
                        pixels[dst + 1] = rgba[colour + 1];
                        pixels[dst + 2] = rgba[colour + 2];
                        pixels[dst + 3] = 255;
                    }
                }
            }
        }

        PngWriter.SaveRgba(pixels, size, size, path);
        return new ArenaMapRenderer.RenderedLayer(
            path,
            $"surface ({Path.GetFileName(art.TexturePath)} through {Path.GetFileName(art.PalettePath)}, rotation as the surface sampler applies it)",
            size, size, distinct.Count);
    }

    private static ArenaMapRenderer.RenderedLayer RenderScatter(RedguardWldFile wld, string path, int scale)
    {
        var distinct = new HashSet<int>();
        var (pixels, width, height) = VoxelLayerRasterizer.RasterizeColored(
            RedguardWldFile.MapSize, RedguardWldFile.MapSize, scale,
            (x, z) =>
            {
                var kind = wld.ScatterKindAt(x, z);
                distinct.Add(kind);
                return VoxelLayerRasterizer.ColorFor((ushort)kind);
            });
        PngWriter.SaveRgba(pixels, width, height, path);
        return new ArenaMapRenderer.RenderedLayer(path, "scatter flat kind (DIAGNOSTIC hues)", width, height,
            distinct.Count);
    }

    /// <summary>The install-side art a textured surface render needs.</summary>
    internal sealed record TerrainArt(
        DaggerfallTextureFile Textures,
        Palette Palette,
        string TexturePath,
        string PalettePath);
}
