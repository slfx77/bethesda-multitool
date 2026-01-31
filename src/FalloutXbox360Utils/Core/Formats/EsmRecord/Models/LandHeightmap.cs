namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     VHGT heightmap data from a LAND record.
///     Contains 33×33 = 1089 height values.
/// </summary>
public record LandHeightmap
{
    /// <summary>Base height offset for the cell.</summary>
    public float HeightOffset { get; init; }

    /// <summary>33×33 grid of height deltas (sbyte values).</summary>
    public required sbyte[] HeightDeltas { get; init; }

    /// <summary>Offset in the dump where VHGT was found.</summary>
    public long Offset { get; init; }

    /// <summary>
    ///     Calculate actual heights for visualization.
    ///     Heights are cumulative: each row starts from the previous row's end value.
    /// </summary>
    public float[,] CalculateHeights()
    {
        var heights = new float[33, 33];
        var rowStart = HeightOffset;

        for (var y = 0; y < 33; y++)
        {
            var height = rowStart;
            for (var x = 0; x < 33; x++)
            {
                height += HeightDeltas[y * 33 + x] * 8; // Height scale factor
                heights[y, x] = height;
            }

            // Next row starts from the first column of current row
            rowStart = heights[y, 0];
        }

        return heights;
    }
}
