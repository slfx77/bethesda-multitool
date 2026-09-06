using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Redguard;
using BethesdaMultitool.Core.Rendering.Level2D;

namespace BethesdaMultitool.CLI.Rendering.Map;

/// <summary>
///     Renders a Redguard <c>.WLD</c>'s eight terrain layers to PNG, one greyscale image each with
///     the layer byte taken straight as luminance. The layers' meanings are not established (see
///     <see cref="RedguardWldFile" />), so nothing here pretends otherwise: each file is named by
///     its layer number, and the summary marks which layers vary smoothly enough to be heights.
/// </summary>
internal static class RedguardMapRenderer
{
    /// <summary>Writes every layer as <c>&lt;stem&gt;_layerN.png</c>.</summary>
    public static IReadOnlyList<ArenaMapRenderer.RenderedLayer> RenderLayers(RedguardWldFile wld, string outputDir, int scale)
    {
        ArgumentNullException.ThrowIfNull(wld);

        Directory.CreateDirectory(outputDir);
        var stem = Path.GetFileNameWithoutExtension(wld.Name).ToUpperInvariant();
        var results = new List<ArenaMapRenderer.RenderedLayer>(wld.Layers.Count);
        for (var i = 0; i < wld.Layers.Count; i++)
        {
            var layer = wld.Layers[i];
            var distinct = new HashSet<byte>();
            var (pixels, width, height) = VoxelLayerRasterizer.RasterizeColored(
                layer.Width, layer.Height, scale,
                (x, y) =>
                {
                    var v = layer.Indices[y * layer.Width + x];
                    distinct.Add(v);
                    return (v, v, v);
                });

            var path = Path.Combine(outputDir, $"{stem}_layer{i}.png");
            PngWriter.SaveRgba(pixels, width, height, path);
            var kind = RedguardWldFile.MeanStep(layer) < RedguardWldFile.SmoothStepThreshold ? "smooth" : "categorical";
            results.Add(new ArenaMapRenderer.RenderedLayer(path, $"LAYER{i} ({kind})", width, height, distinct.Count));
        }

        return results;
    }
}
