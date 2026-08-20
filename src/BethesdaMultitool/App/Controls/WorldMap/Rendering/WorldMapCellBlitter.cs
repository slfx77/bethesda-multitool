using BethesdaMultitool.Core.WorldData;

namespace BethesdaMultitool;

/// <summary>
///     Per-cell RGBA block blitters for the world-map overview layers, extracted from
///     <see cref="WorldMapLayerRenderer" />. Each helper writes one cell's pixels (vertex colors,
///     terrain-region colors, white fill) into a larger aggregate buffer, plus the aggregate
///     block copy and box-downsample used by the coarse-tile path.
/// </summary>
internal static class WorldMapCellBlitter
{
    private const int HmGridSize = 33;

    /// <summary>Cells lacking the layer's source data render in this neutral gray.</summary>
    private const byte MissingR = 40, MissingG = 40, MissingB = 45;

    /// <summary>
    ///     Copies a <paramref name="cellSize" />×<paramref name="cellSize" /> RGBA cell block into
    ///     the aggregate buffer at pixel offset (<paramref name="dstX" />,<paramref name="dstY" />).
    /// </summary>
    internal static void BlitCellRgbaBlock(byte[] dst, int dstStride, byte[] cell, int cellSize, int dstX, int dstY)
    {
        var rowBytes = cellSize * 4;
        for (var row = 0; row < cellSize; row++)
        {
            var srcOff = row * rowBytes;
            var dstOff = ((dstY + row) * dstStride + dstX) * 4;
            Array.Copy(cell, srcOff, dst, dstOff, rowBytes);
        }
    }

    /// <summary>
    ///     Box-downsamples a square <paramref name="srcSize" />² RGBA cell tile to
    ///     <paramref name="dstSize" />² (averaging the covered source texels). Used to render coarse-tile
    ///     cells below the native 33 px/cell so a large worldspace's overview stays memory-bounded.
    /// </summary>
    internal static byte[] DownsampleCell(byte[] src, int srcSize, int dstSize)
    {
        if (dstSize >= srcSize) return src;
        var dst = new byte[dstSize * dstSize * 4];
        var scale = (float)srcSize / dstSize;
        for (var dy = 0; dy < dstSize; dy++)
        {
            var sy0 = (int)(dy * scale);
            var sy1 = Math.Min(srcSize, (int)((dy + 1) * scale));
            if (sy1 <= sy0) sy1 = sy0 + 1;
            for (var dx = 0; dx < dstSize; dx++)
            {
                var sx0 = (int)(dx * scale);
                var sx1 = Math.Min(srcSize, (int)((dx + 1) * scale));
                if (sx1 <= sx0) sx1 = sx0 + 1;

                int r = 0, g = 0, b = 0, a = 0, n = 0;
                for (var sy = sy0; sy < sy1; sy++)
                {
                    for (var sx = sx0; sx < sx1; sx++)
                    {
                        var s = (sy * srcSize + sx) * 4;
                        r += src[s];
                        g += src[s + 1];
                        b += src[s + 2];
                        a += src[s + 3];
                        n++;
                    }
                }

                var d = (dy * dstSize + dx) * 4;
                dst[d] = (byte)(r / n);
                dst[d + 1] = (byte)(g / n);
                dst[d + 2] = (byte)(b / n);
                dst[d + 3] = (byte)(a / n);
            }
        }

        return dst;
    }

    /// <summary>
    ///     Fills one cell's px block with opaque white — the engine's default (no-tint) vertex
    ///     color, used for valid terrain cells that carry no VCLR subrecord.
    /// </summary>
    internal static void FillCellWhite(byte[] rgba, int stride, int imgCellX, int imgCellY)
    {
        for (var py = 0; py < HmGridSize; py++)
        {
            for (var px = 0; px < HmGridSize; px++)
            {
                var imgX = imgCellX * HmGridSize + px;
                var imgY = imgCellY * HmGridSize + py;
                var dst = (imgY * stride + imgX) * 4;
                rgba[dst] = 255;
                rgba[dst + 1] = 255;
                rgba[dst + 2] = 255;
                rgba[dst + 3] = 255;
            }
        }
    }

    /// <summary>
    ///     Returns the cell's VCLR as a 33×33×3 grid, downsampling a larger native grid (Morrowind's
    ///     65×65 → 12,675 bytes) by integer step — the same way <c>DecodedTerrainCell</c> downsamples its
    ///     heights — so the 2D map's fixed 33×33 path can consume it. Returns the input unchanged when it's
    ///     already 33×33; null when it isn't a square RGB grid whose edge maps cleanly onto 33 (caller then
    ///     falls back to white, the engine's no-VCLR default).
    /// </summary>
    internal static byte[]? NormalizeVertexColorsTo33(byte[]? vc)
    {
        if (vc is null) return null;
        if (vc.Length == HmGridSize * HmGridSize * 3) return vc;
        if (vc.Length % 3 != 0) return null;

        var verts = vc.Length / 3;
        var edge = (int)Math.Round(Math.Sqrt(verts));
        if (edge * edge != verts || edge <= HmGridSize) return null;
        if ((edge - 1) % (HmGridSize - 1) != 0) return null; // not an integer-step downsample of 33

        var step = (edge - 1) / (HmGridSize - 1);
        var dst = new byte[HmGridSize * HmGridSize * 3];
        for (var y = 0; y < HmGridSize; y++)
        {
            for (var x = 0; x < HmGridSize; x++)
            {
                var src = (y * step * edge + x * step) * 3;
                var d = (y * HmGridSize + x) * 3;
                dst[d] = vc[src];
                dst[d + 1] = vc[src + 1];
                dst[d + 2] = vc[src + 2];
            }
        }

        return dst;
    }

    internal static void BlitVertexColorsToCell(byte[] rgba, int stride, byte[] vc, int imgCellX, int imgCellY)
    {
        for (var py = 0; py < HmGridSize; py++)
        {
            for (var px = 0; px < HmGridSize; px++)
            {
                // VCLR stored in LAND vertex order (north-up). Mirror HeightmapRenderer's
                // flip so vertex-color textures align with the heightmap layer.
                var srcRow = HmGridSize - 1 - py;
                var srcIdx = (srcRow * HmGridSize + px) * 3;

                var imgX = imgCellX * HmGridSize + px;
                var imgY = imgCellY * HmGridSize + py;
                var dst = (imgY * stride + imgX) * 4;

                rgba[dst] = vc[srcIdx];
                rgba[dst + 1] = vc[srcIdx + 1];
                rgba[dst + 2] = vc[srcIdx + 2];
                rgba[dst + 3] = 255;
            }
        }
    }

    internal static void BlitTerrainRegionsToCell(byte[] rgba, int stride, TextureWinnerGrid winners, int imgCellX,
        int imgCellY)
    {
        for (var py = 0; py < HmGridSize; py++)
        {
            for (var px = 0; px < HmGridSize; px++)
            {
                var imgX = imgCellX * HmGridSize + px;
                var imgY = imgCellY * HmGridSize + py;
                var dst = (imgY * stride + imgX) * 4;

                // Clip cells that fall outside the bitmap. The regions buffer is sized to the heightmap
                // (terrain) extent, but this runs for every cell with a texture-winner grid — a cell with
                // VTEX but no heightmap lands outside that extent and would otherwise index out of bounds
                // ("Terrain regions failed: Index was outside the bounds of the array").
                if (imgX < 0 || imgX >= stride || dst < 0 || dst + 3 >= rgba.Length)
                {
                    continue;
                }

                var formId = winners.Lookup(px, py);
                var (r, g, b) = formId.HasValue
                    ? WorldMapLayerColors.FormIdToColor(formId.Value)
                    : (MissingR, MissingG, MissingB);

                rgba[dst] = r;
                rgba[dst + 1] = g;
                rgba[dst + 2] = b;
                rgba[dst + 3] = 255;
            }
        }
    }
}
