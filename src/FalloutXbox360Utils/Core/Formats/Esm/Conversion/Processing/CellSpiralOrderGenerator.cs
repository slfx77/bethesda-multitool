namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion;

/// <summary>
///     Generates center-spiral ordering for exterior cell block/sub-block iteration.
///     PC ESMs use a specific traversal order that starts at the world origin and
///     spirals outward through block coordinates.
/// </summary>
internal static class CellSpiralOrderGenerator
{
    /// <summary>
    ///     Generates block ordering matching PC's pattern:
    ///     For both X and Y: start at origin, go positive to max, then go from min toward origin.
    ///     Pattern: [origin, origin+1, ..., max, min, min+1, ..., origin-1]
    /// </summary>
    internal static List<(int x, int y)> GenerateCenterSpiralOrder(
        int minX, int maxX, int minY, int maxY,
        int originX, int originY)
    {
        var result = new List<(int x, int y)>();

        // Clamp origin to actual bounds
        var startX = Math.Clamp(originX, minX, maxX);
        var startY = Math.Clamp(originY, minY, maxY);

        // PC pattern for axis: [origin, origin+1, ..., max, min, min+1, ..., origin-1]
        // i.e., from origin going positive first, then from most negative toward origin
        var xOrder = GenerateAxisOrder(minX, maxX, startX);
        var yOrder = GenerateAxisOrder(minY, maxY, startY);

        // Combine: for each X in order, iterate Y in order
        foreach (var x in xOrder)
        {
            foreach (var y in yOrder)
            {
                result.Add((x, y));
            }
        }

        return result;
    }

    /// <summary>
    ///     Generate axis order: [origin, origin+1, ..., max, min, min+1, ..., origin-1]
    /// </summary>
    internal static List<int> GenerateAxisOrder(int min, int max, int origin)
    {
        var result = new List<int>();

        // First: origin and positive direction (origin to max)
        for (var v = origin; v <= max; v++)
        {
            result.Add(v);
        }

        // Second: negative direction from far to near (min to origin-1)
        for (var v = min; v < origin; v++)
        {
            result.Add(v);
        }

        return result;
    }
}
