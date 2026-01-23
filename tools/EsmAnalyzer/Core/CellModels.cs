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
