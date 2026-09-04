using BethesdaMultitool.Core.Formats.Daggerfall;

namespace BethesdaMultitool.CLI.Rendering.Map;

/// <summary>
///     Diagnostic images of Daggerfall blocks. Colours are stable hues per value (a golden-ratio
///     walk), NOT the game's textures: the automap shows the authored building-type codes, the
///     ground image the tile texture records (darker when flipped, lighter when rotated, a centre
///     dot where scenery stands), and the dungeon plan plots each object by kind over the cell
///     grid.
/// </summary>
internal static class DaggerfallBlockRenderer
{
    private const int BytesPerPixel = 4;

    /// <summary>The 64x64 automap scaled up, RGBA row-major.</summary>
    public static (byte[] Pixels, int Width, int Height) RenderAutoMap(DaggerfallRmbBlock block, int scale)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 1);

        var size = DaggerfallRmbBlock.AutoMapSize;
        var pixels = new byte[size * scale * size * scale * BytesPerPixel];
        var map = block.AutoMap.Span;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var value = map[y * size + x];
                var (r, g, b) = value == 0 ? ((byte)16, (byte)16, (byte)16) : DiagnosticColor(value);
                FillCell(pixels, size * scale, x, y, scale, r, g, b);
            }
        }

        return (pixels, size * scale, size * scale);
    }

    /// <summary>The 16x16 ground grid scaled up, RGBA row-major.</summary>
    public static (byte[] Pixels, int Width, int Height) RenderGround(DaggerfallRmbBlock block, int scale)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentOutOfRangeException.ThrowIfLessThan(scale, 1);

        var size = DaggerfallRmbBlock.TilesPerSide;
        var width = size * scale;
        var pixels = new byte[width * width * BytesPerPixel];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var tile = block.GroundTileAt(x, y);
                var (r, g, b) = DiagnosticColor(tile.TextureRecord + 1);
                if (tile.IsFlipped)
                {
                    (r, g, b) = ((byte)(r / 2), (byte)(g / 2), (byte)(b / 2));
                }

                if (tile.IsRotated)
                {
                    (r, g, b) = ((byte)Math.Min(255, r + 64), (byte)Math.Min(255, g + 64), (byte)Math.Min(255, b + 64));
                }

                FillCell(pixels, width, x, y, scale, r, g, b);

                if (block.GroundScenery[y * size + x].HasScenery && scale >= 3)
                {
                    var dot = Math.Max(1, scale / 3);
                    var origin = (x * scale + (scale - dot) / 2, y * scale + (scale - dot) / 2);
                    FillRect(pixels, width, origin.Item1, origin.Item2, dot, dot, 255, 255, 255);
                }
            }
        }

        return (pixels, width, width);
    }

    /// <summary>Block units of margin drawn around the 2,048-unit dungeon block in a plan.</summary>
    public const int DungeonPlanMargin = 128;

    /// <summary>
    ///     A top-down plan of a dungeon block: the whole 2,048-unit block (plus a margin, since a
    ///     few retail objects sit just outside it) on a square canvas, a helper grid every 512
    ///     units, and one dot per object. The block's width x height is its number of object lists,
    ///     not a spatial grid, so the grid here is only a ruler.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height) RenderDungeonPlan(DaggerfallRdbBlock block, int imageSize)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentOutOfRangeException.ThrowIfLessThan(imageSize, 16);

        var pixels = new byte[imageSize * imageSize * BytesPerPixel];
        FillRect(pixels, imageSize, 0, 0, imageSize, imageSize, 24, 24, 32);

        var unitsAcross = DaggerfallRdbBlock.UnitsPerBlock + 2 * DungeonPlanMargin;
        var pixelsPerUnit = (float)imageSize / unitsAcross;
        for (var line = 0; line <= DaggerfallRdbBlock.UnitsPerBlock; line += 512)
        {
            var at = (int)((line + DungeonPlanMargin) * pixelsPerUnit);
            FillRect(pixels, imageSize, Math.Min(at, imageSize - 1), 0, 1, imageSize, 64, 64, 80);
            FillRect(pixels, imageSize, 0, Math.Min(at, imageSize - 1), imageSize, 1, 64, 64, 80);
        }

        var dot = Math.Max(1, imageSize / 128);
        foreach (var rdbObject in block.AllObjects)
        {
            var px = (int)((rdbObject.XPos + DungeonPlanMargin) * pixelsPerUnit);
            var py = (int)((rdbObject.ZPos + DungeonPlanMargin) * pixelsPerUnit);
            if (px < 0 || py < 0 || px >= imageSize || py >= imageSize)
            {
                continue;
            }

            var (r, g, b) = rdbObject.Type switch
            {
                DaggerfallRdbResourceType.Model => ((byte)230, (byte)230, (byte)230),
                DaggerfallRdbResourceType.Flat => ((byte)80, (byte)220, (byte)80),
                _ => ((byte)250, (byte)210, (byte)60)
            };
            FillRect(pixels, imageSize, px, py, dot, dot, r, g, b);
        }

        return (pixels, imageSize, imageSize);
    }

    /// <summary>A stable, well-separated colour for a small integer id.</summary>
    internal static (byte R, byte G, byte B) DiagnosticColor(int id)
    {
        var hue = (id * 0.618033988749895) % 1.0;
        var saturation = 0.65;
        var value = 0.95;
        var sector = (int)(hue * 6) % 6;
        var f = hue * 6 - Math.Floor(hue * 6);
        var p = value * (1 - saturation);
        var q = value * (1 - f * saturation);
        var t = value * (1 - (1 - f) * saturation);
        var (r, g, b) = sector switch
        {
            0 => (value, t, p),
            1 => (q, value, p),
            2 => (p, value, t),
            3 => (p, q, value),
            4 => (t, p, value),
            _ => (value, p, q)
        };
        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private static void FillCell(byte[] pixels, int imageWidth, int cellX, int cellY, int scale, byte r, byte g, byte b)
    {
        FillRect(pixels, imageWidth, cellX * scale, cellY * scale, scale, scale, r, g, b);
    }

    private static void FillRect(byte[] pixels, int imageWidth, int x, int y, int width, int height, byte r, byte g, byte b)
    {
        var imageHeight = pixels.Length / (imageWidth * BytesPerPixel);
        for (var py = y; py < y + height && py < imageHeight; py++)
        {
            for (var px = x; px < x + width && px < imageWidth; px++)
            {
                var offset = (py * imageWidth + px) * BytesPerPixel;
                pixels[offset] = r;
                pixels[offset + 1] = g;
                pixels[offset + 2] = b;
                pixels[offset + 3] = 255;
            }
        }
    }
}
