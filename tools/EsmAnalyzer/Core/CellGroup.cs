namespace EsmAnalyzer.Core;

/// <summary>
///     Represents a group of adjacent cells with terrain differences.
/// </summary>
public sealed class CellGroup
{
    /// <summary>
    ///     Cells in this group.
    /// </summary>
    public List<CellHeightDifference> Cells { get; } = [];

    // Aggregated statistics
    public float MaxDifference => Cells.Count > 0 ? Cells.Max(c => c.MaxDifference) : 0;
    public float AvgDifference => Cells.Count > 0 ? Cells.Average(c => c.AvgDifference) : 0;
    public int TotalDiffPointCount => Cells.Sum(c => c.DiffPointCount);
    public int TotalPoints => Cells.Count * EsmConstants.LandGridArea;

    // Bounding box
    public int MinX => Cells.Count > 0 ? Cells.Min(c => c.CellX) : 0;
    public int MaxX => Cells.Count > 0 ? Cells.Max(c => c.CellX) : 0;
    public int MinY => Cells.Count > 0 ? Cells.Min(c => c.CellY) : 0;
    public int MaxY => Cells.Count > 0 ? Cells.Max(c => c.CellY) : 0;

    /// <summary>
    ///     Size description (e.g., "1 cell" or "3×2 (5 cells)").
    /// </summary>
    public string SizeDescription => Cells.Count == 1
        ? "1 cell"
        : $"{MaxX - MinX + 1}×{MaxY - MinY + 1} ({Cells.Count} cells)";

    /// <summary>
    ///     Impact score for sorting (combines magnitude and coverage).
    /// </summary>
    public long ImpactScore => (long)MaxDifference * TotalDiffPointCount;

    /// <summary>
    ///     Cell with the maximum difference (for teleportation target).
    /// </summary>
    public CellHeightDifference? MaxDiffCell => Cells.OrderByDescending(c => c.MaxDifference).FirstOrDefault();

    /// <summary>
    ///     Center cell for teleportation (same as MaxDiffCell for now).
    /// </summary>
    public CellHeightDifference? CenterCell => MaxDiffCell;

    /// <summary>
    ///     Combined editor IDs (unique named cells in the group).
    /// </summary>
    public string? CombinedEditorIds
    {
        get
        {
            var namedCells = Cells.Where(c => !string.IsNullOrEmpty(c.EditorId))
                .Select(c => c.EditorId!)
                .Distinct()
                .ToList();
            return namedCells.Count switch
            {
                0 => null,
                1 => namedCells[0],
                2 => string.Join(", ", namedCells),
                _ => $"{namedCells[0]} +{namedCells.Count - 1} more"
            };
        }
    }
}