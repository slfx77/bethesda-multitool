namespace EsmAnalyzer.Core;

/// <summary>
///     Represents the playable bounds of a worldspace from the MNAM subrecord.
/// </summary>
public sealed class WorldspaceBounds
{
    /// <summary>
    ///     Minimum cell X coordinate (westernmost).
    /// </summary>
    public int MinCellX { get; init; }

    /// <summary>
    ///     Maximum cell X coordinate (easternmost).
    /// </summary>
    public int MaxCellX { get; init; }

    /// <summary>
    ///     Minimum cell Y coordinate (southernmost).
    /// </summary>
    public int MinCellY { get; init; }

    /// <summary>
    ///     Maximum cell Y coordinate (northernmost).
    /// </summary>
    public int MaxCellY { get; init; }

    /// <summary>
    ///     Width of the playable area in cells.
    /// </summary>
    public int Width => MaxCellX - MinCellX + 1;

    /// <summary>
    ///     Height of the playable area in cells.
    /// </summary>
    public int Height => MaxCellY - MinCellY + 1;

    /// <summary>
    ///     Total number of cells in the playable area.
    /// </summary>
    public int TotalCells => Width * Height;

    /// <summary>
    ///     Checks if a cell coordinate is within the playable bounds.
    /// </summary>
    public bool IsInBounds(int cellX, int cellY) =>
        cellX >= MinCellX && cellX <= MaxCellX &&
        cellY >= MinCellY && cellY <= MaxCellY;

    /// <summary>
    ///     Returns a formatted string representation of the bounds.
    /// </summary>
    public override string ToString() =>
        $"X=[{MinCellX} to {MaxCellX}], Y=[{MinCellY} to {MaxCellY}]";
}
