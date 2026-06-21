namespace BethesdaMultitool.Core.Formats.Esm.Models;

/// <summary>
///     Stateless reconstruction of a canonical 33x33 vertex-normal grid (VNML bytes) from sparse runtime
///     normal samples. Split out of <see cref="RuntimeTerrainMesh" /> to keep the record's public surface
///     focused while the normal interpolation/encoding lives in a cohesive helper.
/// </summary>
internal static class TerrainNormalGridBuilder
{
    private const int GridSize = RuntimeTerrainMesh.GridSize;
    private const int VertexCount = RuntimeTerrainMesh.VertexCount;

    internal static (float Nx, float Ny, float Nz)[,]? BuildSourceNormalGrid(
        TerrainNormalCell?[,] cells,
        int gridSize)
    {
        var source = new (float Nx, float Ny, float Nz)[gridSize, gridSize];
        var hasValue = new bool[gridSize, gridSize];
        var sumX = 0f;
        var sumY = 0f;
        var sumZ = 0f;
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

                source[y, x] = (cell.Nx, cell.Ny, cell.Nz);
                hasValue[y, x] = true;
                sumX += cell.Nx;
                sumY += cell.Ny;
                sumZ += cell.Nz;
                count++;
            }
        }

        if (count == 0)
        {
            return null;
        }

        // Fallback is the renormalized average normal across known cells — preserves "up-ish"
        // orientation when most of the cell is unsampled.
        var fallback = NormalizeOrUp(sumX / count, sumY / count, sumZ / count);
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

                    var nx = 0f;
                    var ny = 0f;
                    var nz = 0f;
                    var neighborCount = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0)
                            {
                                continue;
                            }

                            var nXi = x + dx;
                            var nYi = y + dy;
                            if (nXi < 0 || nXi >= gridSize || nYi < 0 || nYi >= gridSize || !hasValue[nYi, nXi])
                            {
                                continue;
                            }

                            nx += source[nYi, nXi].Nx;
                            ny += source[nYi, nXi].Ny;
                            nz += source[nYi, nXi].Nz;
                            neighborCount++;
                        }
                    }

                    if (neighborCount >= 2)
                    {
                        source[y, x] = NormalizeOrUp(nx / neighborCount, ny / neighborCount, nz / neighborCount);
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

    internal static byte[] ProjectSourceNormalsToVnml((float Nx, float Ny, float Nz)[,] source)
    {
        var bytes = new byte[VertexCount * 3];
        for (var y = 0; y < GridSize; y++)
        {
            for (var x = 0; x < GridSize; x++)
            {
                var (nx, ny, nz) = source[y, x];
                var idx = (y * GridSize + x) * 3;
                bytes[idx + 0] = NormalComponentToByte(nx);
                bytes[idx + 1] = NormalComponentToByte(ny);
                bytes[idx + 2] = NormalComponentToByte(nz);
            }
        }

        return bytes;
    }

    private static (float Nx, float Ny, float Nz) NormalizeOrUp(float nx, float ny, float nz)
    {
        var length = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        if (length <= 0.0001f)
        {
            return (0f, 0f, 1f);
        }

        return (nx / length, ny / length, nz / length);
    }

    private static byte NormalComponentToByte(float value)
    {
        var scaled = Math.Clamp((int)MathF.Round(value * 127f), sbyte.MinValue, sbyte.MaxValue);
        return unchecked((byte)(sbyte)scaled);
    }
}
