namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Directly detected VHGT heightmap subrecord from memory dump.
///     Unlike ExtractedLandRecord, this doesn't require a valid LAND main record header.
/// </summary>
public record DetectedVhgtHeightmap
{
    /// <summary>Offset in the dump where VHGT signature was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether detected as big-endian (Xbox 360) or little-endian (PC).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Base height offset for the cell.</summary>
    public float HeightOffset { get; init; }

    /// <summary>33×33 grid of height deltas (sbyte values).</summary>
    public required sbyte[] HeightDeltas { get; init; }

    /// <summary>
    ///     Calculate actual heights for visualization.
    /// </summary>
    public float[,] CalculateHeights()
    {
        var heights = new float[33, 33];
        var rowStart = HeightOffset * 8;

        for (var y = 0; y < 33; y++)
        {
            var height = rowStart;
            for (var x = 0; x < 33; x++)
            {
                height += HeightDeltas[y * 33 + x] * 8; // Height scale factor
                heights[y, x] = height;
            }

            rowStart = heights[y, 0];
        }

        return heights;
    }
}
