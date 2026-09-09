using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.VanBuren;
using BethesdaMultitool.Core.Rendering.Level2D;

namespace BethesdaMultitool.CLI.Rendering.Map;

/// <summary>
///     Renders a Van Buren level to PNG through <see cref="VanBurenMapLevel2DSource" /> — the walk
///     grid as <c>&lt;stem&gt;_walk.png</c> and the placement plan as <c>&lt;stem&gt;_plan.png</c>.
///     <para>
///         ⚠ The walk-grid colours are DIAGNOSTIC, a stable hue per cell value with the border
///         value 47 dark; the values' meaning is not established and no real art is drawn. The
///         plan's orientation (+Z down) against the game's camera is likewise not established.
///     </para>
/// </summary>
internal static class VanBurenMapRenderer
{
    /// <summary>Writes the level's layers and returns what was written.</summary>
    public static IReadOnlyList<ArenaMapRenderer.RenderedLayer> RenderLevel(VanBurenMapLevel level, string outputDir,
        int scale)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(outputDir);

        Directory.CreateDirectory(outputDir);
        var source = new VanBurenMapLevel2DSource(level, scale);
        var results = new List<ArenaMapRenderer.RenderedLayer>(2);
        foreach (var layer in source.Layers)
        {
            if (source.Render(layer) is not { } render)
            {
                continue;
            }

            var suffix = layer == Level2DLayer.Floor ? "_walk" : "_plan";
            var path = Path.Combine(outputDir, level.Stem + suffix + ".png");
            PngWriter.SaveRgba(render.Rgba, render.Width, render.Height, path);
            var distinct = layer == Level2DLayer.Floor && level.WalkGrid is { } grid
                ? grid.Cells.Distinct().Count()
                : 0;
            results.Add(new ArenaMapRenderer.RenderedLayer(
                path, layer == Level2DLayer.Floor ? "WALK GRID" : "PLAN", render.Width, render.Height, distinct));
        }

        return results;
    }
}
