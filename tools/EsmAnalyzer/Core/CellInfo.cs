namespace EsmAnalyzer.Core;

/// <summary>
///     Represents information about a cell in a worldspace.
/// </summary>
public sealed class CellInfo
{
    /// <summary>
    ///     FormID of the cell record.
    /// </summary>
    public uint FormId { get; init; }

    /// <summary>
    ///     Grid X coordinate of the cell.
    /// </summary>
    public int GridX { get; init; }

    /// <summary>
    ///     Grid Y coordinate of the cell.
    /// </summary>
    public int GridY { get; init; }

    /// <summary>
    ///     Editor ID of the cell (if any).
    /// </summary>
    public string? EditorId { get; init; }

    /// <summary>
    ///     File offset of the CELL record.
    /// </summary>
    public uint Offset { get; init; }

    /// <summary>
    ///     Returns a display string for the cell location.
    /// </summary>
    public string LocationDisplay => string.IsNullOrEmpty(EditorId)
        ? $"({GridX}, {GridY})"
        : $"{EditorId} ({GridX}, {GridY})";
}