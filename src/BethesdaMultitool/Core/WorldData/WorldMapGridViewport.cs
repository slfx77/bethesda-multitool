using System.Numerics;

namespace BethesdaMultitool;

/// <summary>
///     A normalized world-space viewport for pure exterior-grid visibility tests. It lives in the
///     shared non-GUI compile set so tile culling can be tested on the cross-platform test TFM.
///     World north-Y maps to canvas -Y, matching the 2D terrain and water tile rectangles.
/// </summary>
internal readonly record struct WorldMapGridViewport(float MinX, float MinY, float MaxX, float MaxY)
{
    /// <summary>
    ///     Normalizes arbitrary viewport corners and applies a non-negative world-space margin once,
    ///     before a renderer scans its cached tile entries.
    /// </summary>
    internal static WorldMapGridViewport FromCorners(
        Vector2 cornerA, Vector2 cornerB, float margin = 0f)
    {
        margin = float.IsFinite(margin) ? Math.Max(0f, margin) : 0f;
        return new WorldMapGridViewport(
            Math.Min(cornerA.X, cornerB.X) - margin,
            Math.Min(cornerA.Y, cornerB.Y) - margin,
            Math.Max(cornerA.X, cornerB.X) + margin,
            Math.Max(cornerA.Y, cornerB.Y) + margin);
    }

    /// <summary>
    ///     Returns whether the exterior cell at <paramref name="gridX" />, <paramref name="gridY" />
    ///     intersects this viewport for the supplied game-specific cell size.
    /// </summary>
    internal bool IntersectsCell(int gridX, int gridY, float cellWorldSize)
    {
        if (cellWorldSize <= 0 || !float.IsFinite(cellWorldSize))
        {
            return false;
        }

        var cellMinX = gridX * cellWorldSize;
        var cellMaxX = cellMinX + cellWorldSize;
        // Calculate from gridY directly instead of (gridY + 1), which would overflow for a corrupt
        // int.MaxValue cache key before its conversion to float.
        var cellMaxY = -gridY * cellWorldSize;
        var cellMinY = cellMaxY - cellWorldSize;

        // Inclusive edges intentionally retain a cell whose destination touches the viewport. That
        // avoids a one-frame edge flicker while panning by sub-pixels.
        return cellMaxX >= MinX && cellMinX <= MaxX &&
               cellMaxY >= MinY && cellMinY <= MaxY;
    }
}
