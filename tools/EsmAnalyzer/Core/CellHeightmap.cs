namespace EsmAnalyzer.Core;

/// <summary>
///     Represents heightmap data for a single cell.
/// </summary>
public sealed class CellHeightmap
{
    /// <summary>
    ///     Grid X coordinate of the cell.
    /// </summary>
    public int CellX { get; init; }

    /// <summary>
    ///     Grid Y coordinate of the cell.
    /// </summary>
    public int CellY { get; init; }

    /// <summary>
    ///     Editor ID of the cell (if any).
    /// </summary>
    public string? EditorId { get; init; }

    /// <summary>
    ///     Height values (33×33 grid).
    /// </summary>
    public required float[,] Heights { get; init; }

    /// <summary>
    ///     Base height from VHGT subrecord.
    /// </summary>
    public float BaseHeight { get; init; }
}