using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;
internal static class HeightmapExportPixelRenderer
{
    internal static byte[] VclrToImagePixels(byte[] vclr)
    {
        var pixels = new byte[33 * 33 * 3];
        for (var py = 0; py < 33; py++)
        {
            var sourceY = 32 - py;
            for (var px = 0; px < 33; px++)
            {
                var srcIdx = (sourceY * 33 + px) * 3;
                var dstIdx = (py * 33 + px) * 3;
                pixels[dstIdx] = vclr[srcIdx];
                pixels[dstIdx + 1] = vclr[srcIdx + 1];
                pixels[dstIdx + 2] = vclr[srcIdx + 2];
            }
        }

        return pixels;
    }

    internal static byte[] BuildLayerMaskPixels(LandTextureLayer layer)
    {
        var mask = new byte[33 * 33];
        foreach (var entry in layer.BlendEntries)
        {
            var localX = entry.Position % 17;
            var localY = entry.Position / 17;
            var (baseX, baseY) = QuadrantBase(layer.Quadrant);
            var x = baseX + localX;
            var y = baseY + localY;
            if (x is < 0 or >= 33 || y is < 0 or >= 33)
            {
                continue;
            }

            var imageY = 32 - y;
            var opacity = (byte)Math.Clamp((int)MathF.Round(entry.Opacity * 255f), 0, 255);
            var index = imageY * 33 + x;
            if (opacity > mask[index])
            {
                mask[index] = opacity;
            }
        }

        return mask;
    }

    internal static byte[]? BuildTextureIdPreviewPixels(LandVisualData visualData)
    {
        if (visualData.TextureLayers.Count == 0)
        {
            return null;
        }

        var terrain = new (float R, float G, float B)[33, 33];
        var hasAny = false;

        foreach (var layer in visualData.TextureLayers.Where(l => l.Kind == LandTextureLayerKind.Base))
        {
            var color = TextureIdColor(layer.TextureFormId);
            var (baseX, baseY) = QuadrantBase(layer.Quadrant);
            for (var y = baseY; y < Math.Min(baseY + 17, 33); y++)
            {
                for (var x = baseX; x < Math.Min(baseX + 17, 33); x++)
                {
                    terrain[y, x] = color;
                    hasAny = true;
                }
            }
        }

        foreach (var layer in visualData.TextureLayers.Where(l => l.Kind == LandTextureLayerKind.Alpha))
        {
            var color = TextureIdColor(layer.TextureFormId);
            var (baseX, baseY) = QuadrantBase(layer.Quadrant);
            foreach (var entry in layer.BlendEntries)
            {
                var localX = entry.Position % 17;
                var localY = entry.Position / 17;
                var x = baseX + localX;
                var y = baseY + localY;
                if (x is < 0 or >= 33 || y is < 0 or >= 33)
                {
                    continue;
                }

                var opacity = Math.Clamp(entry.Opacity, 0f, 1f);
                var existing = terrain[y, x];
                terrain[y, x] = (
                    existing.R * (1f - opacity) + color.R * opacity,
                    existing.G * (1f - opacity) + color.G * opacity,
                    existing.B * (1f - opacity) + color.B * opacity);
                hasAny = true;
            }
        }

        if (!hasAny)
        {
            return null;
        }

        var pixels = new byte[33 * 33 * 3];
        for (var py = 0; py < 33; py++)
        {
            var sourceY = 32 - py;
            for (var px = 0; px < 33; px++)
            {
                var color = terrain[sourceY, px];
                var idx = (py * 33 + px) * 3;
                pixels[idx] = (byte)Math.Clamp((int)MathF.Round(color.R), 0, 255);
                pixels[idx + 1] = (byte)Math.Clamp((int)MathF.Round(color.G), 0, 255);
                pixels[idx + 2] = (byte)Math.Clamp((int)MathF.Round(color.B), 0, 255);
            }
        }

        return pixels;
    }

    internal static (int X, int Y) QuadrantBase(byte quadrant)
    {
        return quadrant switch
        {
            0 => (0, 0),
            1 => (16, 0),
            2 => (0, 16),
            3 => (16, 16),
            _ => (0, 0)
        };
    }

    internal static (float R, float G, float B) TextureIdColor(uint formId)
    {
        var hash = formId * 2654435761u;
        var r = 64 + ((hash >> 16) & 0x7F);
        var g = 64 + ((hash >> 8) & 0x7F);
        var b = 64 + (hash & 0x7F);
        return (r, g, b);
    }

    internal static void DrawGridOverlay(byte[] pixels, int width, int height, bool rgb, int cellStride = 33)
    {
        for (var x = 0; x < width; x += cellStride)
        {
            DrawVerticalLine(pixels, width, height, x, rgb);
        }

        DrawVerticalLine(pixels, width, height, width - 1, rgb);

        for (var y = 0; y < height; y += cellStride)
        {
            DrawHorizontalLine(pixels, width, height, y, rgb);
        }

        DrawHorizontalLine(pixels, width, height, height - 1, rgb);
    }

    internal static void DrawVerticalLine(byte[] pixels, int width, int height, int x, bool rgb)
    {
        if (x < 0 || x >= width)
        {
            return;
        }

        for (var y = 0; y < height; y++)
        {
            SetGridPixel(pixels, width, x, y, rgb);
        }
    }

    internal static void DrawHorizontalLine(byte[] pixels, int width, int height, int y, bool rgb)
    {
        if (y < 0 || y >= height)
        {
            return;
        }

        for (var x = 0; x < width; x++)
        {
            SetGridPixel(pixels, width, x, y, rgb);
        }
    }

    internal static void SetGridPixel(byte[] pixels, int width, int x, int y, bool rgb)
    {
        if (rgb)
        {
            var idx = (y * width + x) * 3;
            pixels[idx] = 255;
            pixels[idx + 1] = 255;
            pixels[idx + 2] = 255;
        }
        else
        {
            pixels[y * width + x] = 255;
        }
    }
}
