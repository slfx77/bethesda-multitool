namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Wireframe overlay rendering for debug visualization.
///     Draws triangle edges over the filled rasterization output.
///     Extracted from <see cref="NifScanlineRasterizer" />.
/// </summary>
internal static class NifWireframeRenderer
{
    private const float WireframeDepthEpsilon = 0.05f;

    internal static void DrawTriangleWireframeOverlay(
        byte[] pixels,
        float[] depthBuffer,
        int width,
        int height,
        TriangleData tri,
        float ppu,
        float offsetX,
        float offsetY)
    {
        var sx0 = tri.X0 * ppu + offsetX;
        var sy0 = tri.Y0 * ppu + offsetY;
        var sx1 = tri.X1 * ppu + offsetX;
        var sy1 = tri.Y1 * ppu + offsetY;
        var sx2 = tri.X2 * ppu + offsetX;
        var sy2 = tri.Y2 * ppu + offsetY;

        var color = ResolveWireframeColor(tri.RenderOrder);

        DrawWireframeEdge(
            pixels, depthBuffer, width, height,
            sx0, sy0, tri.Z0, sx1, sy1, tri.Z1,
            color.R, color.G, color.B);
        DrawWireframeEdge(
            pixels, depthBuffer, width, height,
            sx1, sy1, tri.Z1, sx2, sy2, tri.Z2,
            color.R, color.G, color.B);
        DrawWireframeEdge(
            pixels, depthBuffer, width, height,
            sx2, sy2, tri.Z2, sx0, sy0, tri.Z0,
            color.R, color.G, color.B);
    }

    private static void DrawWireframeEdge(
        byte[] pixels,
        float[] depthBuffer,
        int width,
        int height,
        float x0,
        float y0,
        float z0,
        float x1,
        float y1,
        float z1,
        byte r,
        byte g,
        byte b)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var steps = Math.Max(1, (int)MathF.Ceiling(MathF.Max(MathF.Abs(dx), MathF.Abs(dy))));

        for (var i = 0; i <= steps; i++)
        {
            var t = i / (float)steps;
            var px = (int)MathF.Round(x0 + dx * t);
            var py = (int)MathF.Round(y0 + dy * t);
            var z = z0 + (z1 - z0) * t;

            PlotWireframePixel(pixels, depthBuffer, width, height, px, py, z, r, g, b);
        }
    }

    private static void PlotWireframePixel(
        byte[] pixels,
        float[] depthBuffer,
        int width,
        int height,
        int px,
        int py,
        float z,
        byte r,
        byte g,
        byte b)
    {
        for (var oy = -1; oy <= 1; oy++)
        {
            for (var ox = -1; ox <= 1; ox++)
            {
                var tx = px + ox;
                var ty = py + oy;
                if ((uint)tx >= (uint)width || (uint)ty >= (uint)height)
                {
                    continue;
                }

                if (!IsNearFrontmostDepth(depthBuffer, width, height, tx, ty, z))
                {
                    continue;
                }

                BlendWireframePixel(pixels, width, tx, ty, r, g, b);
            }
        }
    }

    private static bool IsNearFrontmostDepth(
        float[] depthBuffer,
        int width,
        int height,
        int px,
        int py,
        float z)
    {
        var maxDepth = float.MinValue;

        for (var oy = -1; oy <= 1; oy++)
        {
            var ty = py + oy;
            if ((uint)ty >= (uint)height)
            {
                continue;
            }

            for (var ox = -1; ox <= 1; ox++)
            {
                var tx = px + ox;
                if ((uint)tx >= (uint)width)
                {
                    continue;
                }

                var depth = depthBuffer[ty * width + tx];
                if (depth > maxDepth)
                {
                    maxDepth = depth;
                }
            }
        }

        return maxDepth is float.MinValue || z >= maxDepth - WireframeDepthEpsilon;
    }

    private static void BlendWireframePixel(
        byte[] pixels,
        int width,
        int px,
        int py,
        byte r,
        byte g,
        byte b)
    {
        var idx = (py * width + px) * 4;
        const float overlay = 0.85f;
        const float baseWeight = 1f - overlay;

        pixels[idx + 0] = (byte)Math.Clamp(pixels[idx + 0] * baseWeight + r * overlay, 0f, 255f);
        pixels[idx + 1] = (byte)Math.Clamp(pixels[idx + 1] * baseWeight + g * overlay, 0f, 255f);
        pixels[idx + 2] = (byte)Math.Clamp(pixels[idx + 2] * baseWeight + b * overlay, 0f, 255f);
        pixels[idx + 3] = 255;
    }

    private static (byte R, byte G, byte B) ResolveWireframeColor(int renderOrder)
    {
        return renderOrder switch
        {
            2 => (0, 255, 255),
            1 => (255, 220, 0),
            _ => (0, 255, 0)
        };
    }
}
