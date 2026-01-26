namespace EsmAnalyzer.Core;

/// <summary>
///     Represents a height difference between two cells.
/// </summary>
public sealed class CellHeightDifference
{
    public int CellX { get; init; }
    public int CellY { get; init; }
    public string? EditorId { get; init; }
    public float MaxDifference { get; init; }
    public float AvgDifference { get; init; }
    public int DiffPointCount { get; init; }
    public float AvgHeight1 { get; init; }
    public float AvgHeight2 { get; init; }
    public int MaxDiffLocalX { get; init; }
    public int MaxDiffLocalY { get; init; }

    /// <summary>
    ///     Returns a display string for the cell location.
    /// </summary>
    public string LocationDisplay => string.IsNullOrEmpty(EditorId)
        ? $"({CellX}, {CellY})"
        : $"{EditorId} ({CellX}, {CellY})";
}