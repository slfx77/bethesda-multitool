using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool.Core.Formats.Esm.Models;

/// <summary>
///     Stateless conversion between a 33x33 float height grid and the VHGT-compatible
///     <see cref="LandHeightmap" /> (cumulative sbyte deltas), plus the height-grid utilities used during
///     diagnostics. Split out of <see cref="RuntimeTerrainMesh" /> to keep the record's public surface focused.
/// </summary>
internal static class TerrainHeightmapEncoder
{
    private const int GridSize = RuntimeTerrainMesh.GridSize;
    private const int VertexCount = RuntimeTerrainMesh.VertexCount;

    /// <summary>
    ///     Encode a 33x33 height grid as a VHGT-compatible LandHeightmap with cumulative sbyte deltas.
    /// </summary>
    internal static LandHeightmap EncodeHeightmap(float[,] heights, long vertexDataOffset)
    {
        var heightOffset = heights[0, 0] / 8.0f;
        var deltas = new sbyte[VertexCount];

        var decoderCol0 = heightOffset * 8.0f;
        for (var y = 0; y < GridSize; y++)
        {
            var runningHeight = decoderCol0;

            for (var x = 0; x < GridSize; x++)
            {
                var exactDelta = (heights[y, x] - runningHeight) / 8.0f;
                var clamped = Math.Clamp((int)Math.Round(exactDelta), sbyte.MinValue, sbyte.MaxValue);
                deltas[y * GridSize + x] = (sbyte)clamped;
                runningHeight += clamped * 8.0f;
            }

            decoderCol0 += deltas[y * GridSize] * 8.0f;
        }

        var decodedHeights = DecodeHeightmap(heightOffset, deltas);
        var maxError = 0f;
        for (var y = 0; y < GridSize; y++)
        {
            for (var x = 0; x < GridSize; x++)
            {
                maxError = Math.Max(maxError, MathF.Abs(decodedHeights[y, x] - heights[y, x]));
            }
        }

        return new LandHeightmap
        {
            HeightOffset = heightOffset,
            HeightDeltas = deltas,
            Offset = vertexDataOffset,
            ExactHeights = heights,
            EncodedRoundTripMaxError = maxError
        };
    }

    private static float[,] DecodeHeightmap(float heightOffset, sbyte[] deltas)
    {
        var heights = new float[GridSize, GridSize];
        var rowStart = heightOffset * 8f;

        for (var y = 0; y < GridSize; y++)
        {
            var height = rowStart;
            for (var x = 0; x < GridSize; x++)
            {
                height += deltas[y * GridSize + x] * 8f;
                heights[y, x] = height;
            }

            rowStart = heights[y, 0];
        }

        return heights;
    }

    internal static float[] FlattenHeights(float[,] heights)
    {
        var values = new float[VertexCount];
        var index = 0;
        for (var y = 0; y < GridSize; y++)
        {
            for (var x = 0; x < GridSize; x++)
            {
                values[index++] = heights[y, x];
            }
        }

        return values;
    }

    internal static float[,] ApplyHeightOffset(float[,] heights, float offset)
    {
        if (MathF.Abs(offset) < 0.001f)
        {
            return heights;
        }

        var adjusted = new float[GridSize, GridSize];
        for (var y = 0; y < GridSize; y++)
        {
            for (var x = 0; x < GridSize; x++)
            {
                adjusted[y, x] = heights[y, x] + offset;
            }
        }

        return adjusted;
    }
}
