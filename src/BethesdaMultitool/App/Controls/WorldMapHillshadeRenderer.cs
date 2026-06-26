using System.Numerics;

namespace BethesdaMultitool;

/// <summary>
///     Lambertian hillshade computation for the world-map "Slope" layer, extracted from
///     <see cref="WorldMapLayerRenderer" />. Converts a height field into a grayscale RGBA buffer
///     by shading each pixel against a fixed north-west sun direction.
/// </summary>
internal static class WorldMapHillshadeRenderer
{
    /// <summary>Default sun: north-west, slightly elevated. The image-space light vector convention is
    /// X = east (+dx), Y = south (image rows grow southward), Z = elevation up. Used when no time-of-day
    /// light direction is supplied (lighting off / no lighting control).</summary>
    internal static readonly Vector3 DefaultLightDir = Vector3.Normalize(new Vector3(-1f, 1f, 1.5f));

    /// <summary>Slope-emphasis scale tuned for the 4096-unit / 33-vert-per-cell Fallout-family grid.
    /// Larger cells (Morrowind's 8192) sample heights over a wider horizontal step, so the same height
    /// delta reads as a steeper slope — <see cref="ZScaleForCellSize" /> scales this down for them.</summary>
    internal const float DefaultZScale = 0.02f;

    /// <summary>Per-game zScale: inversely proportional to cell world size so the hillshade contrast is
    /// consistent across games (Fallout 4096 → 0.02; Morrowind 8192 → 0.01, which tames the harsh relief).</summary>
    internal static float ZScaleForCellSize(float cellSize) =>
        DefaultZScale * (4096f / MathF.Max(cellSize, 1f));

    internal static byte[] ComputeHillshade(
        float[] heightField, bool[] hasHeight, int width, int height,
        Vector3? lightDir = null, float zScale = DefaultZScale)
    {
        var rgba = new byte[width * height * 4];
        // Lambertian shade w/ ambient floor against the (configurable) sun direction.
        var light = Vector3.Normalize(lightDir ?? DefaultLightDir);
        const float ambient = 0.15f;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var idx = y * width + x;
                if (!hasHeight[idx])
                {
                    rgba[idx * 4 + 3] = 255;
                    continue;
                }

                var x0 = Math.Max(0, x - 1);
                var x1 = Math.Min(width - 1, x + 1);
                var y0 = Math.Max(0, y - 1);
                var y1 = Math.Min(height - 1, y + 1);

                var hCenter = heightField[idx];
                var hRight = hasHeight[y * width + x1] ? heightField[y * width + x1] : hCenter;
                var hLeft = hasHeight[y * width + x0] ? heightField[y * width + x0] : hCenter;
                var hUp = hasHeight[y0 * width + x] ? heightField[y0 * width + x] : hCenter;
                var hDown = hasHeight[y1 * width + x] ? heightField[y1 * width + x] : hCenter;

                var dx = hRight - hLeft;
                var dy = hDown - hUp;

                var normal = Vector3.Normalize(new Vector3(-dx * zScale, -dy * zScale, 1f));
                var shade = Math.Max(ambient, Vector3.Dot(normal, light));
                var gray = (byte)Math.Clamp(shade * 255f, 0f, 255f);

                rgba[idx * 4] = gray;
                rgba[idx * 4 + 1] = gray;
                rgba[idx * 4 + 2] = gray;
                rgba[idx * 4 + 3] = 255;
            }
        }

        return rgba;
    }
}
