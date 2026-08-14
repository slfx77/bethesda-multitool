namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>A physical row/column in an export tile grid.</summary>
internal readonly record struct ExportTileCoordinate(int Row, int Column);

/// <summary>
///     Pure visit-order mapping for an export tile grid. Even rows run left-to-right and odd rows
///     right-to-left, keeping the transition to the next row in the same column. The returned physical
///     coordinate remains the authority for framing, output placement, naming, and manifest identity.
/// </summary>
internal static class ExportTileTraversal
{
    /// <summary>Maps a zero-based serpentine visit ordinal to its physical row and column.</summary>
    public static ExportTileCoordinate GetSerpentineCoordinate(
        long visitOrdinal,
        int columns,
        int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        var tileCount = (long)columns * rows;
        if (visitOrdinal < 0 || visitOrdinal >= tileCount)
        {
            throw new ArgumentOutOfRangeException(nameof(visitOrdinal));
        }

        var row = (int)(visitOrdinal / columns);
        var offsetInRow = (int)(visitOrdinal % columns);
        var column = (row & 1) == 0
            ? offsetInRow
            : columns - 1 - offsetInRow;
        return new ExportTileCoordinate(row, column);
    }
}
