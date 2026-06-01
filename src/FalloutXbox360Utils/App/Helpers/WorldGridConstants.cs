using System.Numerics;

namespace FalloutXbox360Utils;

/// <summary>
///     Shared cell-grid math for new v3 code. The 2D viewer (WorldMapControl + helpers)
///     duplicates `CellSize = 4096f` in many private consts; new Phase 1+ code uses this
///     module instead. A mass migration of the 79 existing 2D-renderer sites is out of
///     scope — they continue to use their local consts.
/// </summary>
internal static class WorldGridConstants
{
    /// <summary>Width of one exterior cell in world units (Bethesda standard, all 4 builds — verified by ExteriorCellCrossBuildParityTests).</summary>
    public const float CellSize = 4096f;

    /// <summary>Conservative vertical extent for cell AABB tests — wide enough to contain any cell's terrain + REFR Z range. Tighten when terrain bounds are known.</summary>
    public const float CellMinZ = -32768f;

    /// <summary>Conservative vertical extent for cell AABB tests — wide enough to contain any cell's terrain + REFR Z range.</summary>
    public const float CellMaxZ = 32768f;

    /// <summary>
    ///     Axis-aligned bounding box for an exterior cell at (<paramref name="gridX" />,
    ///     <paramref name="gridY" />) in world coordinates. Cell origin = (gridX × CellSize, gridY × CellSize, 0).
    /// </summary>
    public static (Vector3 Min, Vector3 Max) GetCellWorldBounds(int gridX, int gridY)
    {
        var minX = gridX * CellSize;
        var minY = gridY * CellSize;
        return (new Vector3(minX, minY, CellMinZ),
                new Vector3(minX + CellSize, minY + CellSize, CellMaxZ));
    }

}
