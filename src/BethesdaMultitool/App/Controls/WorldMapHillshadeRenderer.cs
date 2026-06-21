using System.Numerics;

namespace BethesdaMultitool;

/// <summary>
///     Lambertian hillshade computation for the world-map "Slope" layer, extracted from
///     <see cref="WorldMapLayerRenderer" />. Converts a height field into a grayscale RGBA buffer
///     by shading each pixel against a fixed north-west sun direction.
/// </summary>
internal static class WorldMapHillshadeRenderer
{
    internal static byte[] ComputeHillshade(float[] heightField, bool[] hasHeight, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        // NW sun, slightly elevated. Lambertian shade w/ ambient floor.
        var lightDir = Vector3.Normalize(new Vector3(-1f, 1f, 1.5f));
        // Tunes how punchy slope reads in a 33-vert-per-cell world. ~0.02 keeps
        // gentle dunes visible without hard cliffs blowing to pure white.
        const float zScale = 0.02f;
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
                var shade = Math.Max(ambient, Vector3.Dot(normal, lightDir));
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
