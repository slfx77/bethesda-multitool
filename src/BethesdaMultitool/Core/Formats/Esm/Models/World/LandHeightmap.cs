namespace BethesdaMultitool.Core.Formats.Esm.Models.World;

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
    ///     Parent CELL FormID from the recovered LAND hierarchy. Conversion planning uses this
    ///     provenance to avoid copying grid-matched terrain onto a different CELL at the same coordinates.
    /// </summary>
    internal uint? SourceParentCellFormId { get; set; }

    private readonly float[,]? _exactHeights;

    /// <summary>
    ///     Optional exact height grid (from runtime mesh data, or decoded on demand from an external
    ///     BTD via <see cref="ExactHeightsProvider" />). When present, CalculateHeights() returns this
    ///     directly, bypassing lossy delta decoding.
    /// </summary>
    public float[,]? ExactHeights
    {
        get => _exactHeights ?? ExactHeightsProvider?.Invoke();
        init => _exactHeights = value;
    }

    /// <summary>
    ///     Lazy source for <see cref="ExactHeights" /> — FO76/Starfield BTD terrain attaches a
    ///     per-cell decoder here instead of materializing every cell's grid at load (Appalachia's
    ///     ~40k cells ≈ 2.7 GB eager). The provider is expected to cache: repeated gets are cheap,
    ///     and callers may not hold the returned array across frames. Ignored when
    ///     <see cref="ExactHeights" /> was set directly.
    /// </summary>
    internal Func<float[,]>? ExactHeightsProvider { get; init; }

    /// <summary>Maximum absolute height error after encoding this heightmap to VHGT deltas and decoding it back.</summary>
    public float EncodedRoundTripMaxError { get; init; }

    /// <summary>
    ///     Calculate actual heights for visualization.
    ///     Heights are cumulative: each row starts from the previous row's end value.
    /// </summary>
    public float[,] CalculateHeights()
    {
        if (ExactHeights is { } exact)
        {
            return exact;
        }

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

            // Next row starts from the first column of current row
            rowStart = heights[y, 0];
        }

        return heights;
    }
}
