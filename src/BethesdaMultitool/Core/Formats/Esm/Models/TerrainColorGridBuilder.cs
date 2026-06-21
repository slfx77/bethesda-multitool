namespace BethesdaMultitool.Core.Formats.Esm.Models;

/// <summary>
///     Stateless reconstruction of canonical 33x33 vertex-color bytes (VCLR) from sparse runtime color
///     samples, including neighbor fill and bilinear interpolation. Split out of
///     <see cref="RuntimeTerrainMesh" /> to keep the record's public surface focused.
/// </summary>
internal static class TerrainColorGridBuilder
{
    private const int GridSize = RuntimeTerrainMesh.GridSize;
    private const int VertexCount = RuntimeTerrainMesh.VertexCount;

    internal static (float R, float G, float B)[,]? BuildSourceColorGrid(
        TerrainColorCell?[,] cells,
        int gridSize)
    {
        var source = new (float R, float G, float B)[gridSize, gridSize];
        var hasValue = new bool[gridSize, gridSize];
        var sumR = 0f;
        var sumG = 0f;
        var sumB = 0f;
        var count = 0;

        for (var y = 0; y < gridSize; y++)
        {
            for (var x = 0; x < gridSize; x++)
            {
                var cell = cells[y, x];
                if (cell == null)
                {
                    continue;
                }

                source[y, x] = (cell.R, cell.G, cell.B);
                hasValue[y, x] = true;
                sumR += cell.R;
                sumG += cell.G;
                sumB += cell.B;
                count++;
            }
        }

        if (count == 0)
        {
            return null;
        }

        var fallback = (R: sumR / count, G: sumG / count, B: sumB / count);
        bool madeProgress;
        do
        {
            madeProgress = false;
            for (var y = 0; y < gridSize; y++)
            {
                for (var x = 0; x < gridSize; x++)
                {
                    if (hasValue[y, x])
                    {
                        continue;
                    }

                    var r = 0f;
                    var g = 0f;
                    var b = 0f;
                    var neighborCount = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0)
                            {
                                continue;
                            }

                            var nx = x + dx;
                            var ny = y + dy;
                            if (nx < 0 || nx >= gridSize || ny < 0 || ny >= gridSize || !hasValue[ny, nx])
                            {
                                continue;
                            }

                            r += source[ny, nx].R;
                            g += source[ny, nx].G;
                            b += source[ny, nx].B;
                            neighborCount++;
                        }
                    }

                    if (neighborCount >= 2)
                    {
                        source[y, x] = (r / neighborCount, g / neighborCount, b / neighborCount);
                        hasValue[y, x] = true;
                        madeProgress = true;
                    }
                }
            }
        } while (madeProgress);

        for (var y = 0; y < gridSize; y++)
        {
            for (var x = 0; x < gridSize; x++)
            {
                if (!hasValue[y, x])
                {
                    source[y, x] = fallback;
                }
            }
        }

        return source;
    }

    internal static byte[] InterpolateColorsToVclr((float R, float G, float B)[,] source, int sourceGridSize)
    {
        var bytes = new byte[VertexCount * 3];
        for (var y = 0; y < GridSize; y++)
        {
            for (var x = 0; x < GridSize; x++)
            {
                var srcX = x * (sourceGridSize - 1) / (float)(GridSize - 1);
                var srcY = y * (sourceGridSize - 1) / (float)(GridSize - 1);

                var x0 = (int)MathF.Floor(srcX);
                var y0 = (int)MathF.Floor(srcY);
                var x1 = Math.Min(x0 + 1, sourceGridSize - 1);
                var y1 = Math.Min(y0 + 1, sourceGridSize - 1);
                var fx = srcX - x0;
                var fy = srcY - y0;

                var c00 = source[y0, x0];
                var c10 = source[y0, x1];
                var c01 = source[y1, x0];
                var c11 = source[y1, x1];

                var r = Bilinear(c00.R, c10.R, c01.R, c11.R, fx, fy);
                var g = Bilinear(c00.G, c10.G, c01.G, c11.G, fx, fy);
                var b = Bilinear(c00.B, c10.B, c01.B, c11.B, fx, fy);

                var index = (y * GridSize + x) * 3;
                bytes[index] = ToColorByte(r);
                bytes[index + 1] = ToColorByte(g);
                bytes[index + 2] = ToColorByte(b);
            }
        }

        return bytes;
    }

    private static float Bilinear(float c00, float c10, float c01, float c11, float fx, float fy)
    {
        return c00 * (1 - fx) * (1 - fy)
               + c10 * fx * (1 - fy)
               + c01 * (1 - fx) * fy
               + c11 * fx * fy;
    }

    private static byte ToColorByte(float value)
    {
        var scaled = value > 1.5f ? value : value * 255f;
        return (byte)Math.Clamp((int)MathF.Round(scaled), 0, 255);
    }
}
