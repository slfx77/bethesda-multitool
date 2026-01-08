namespace Xbox360MemoryCarver;

/// <summary>
///     Handles sorting logic for the NIF files list view.
/// </summary>
internal sealed class NifFilesSorter
{
    private SortColumn _currentSortColumn = SortColumn.None;
    private bool _sortAscending = true;

    public SortColumn CurrentColumn => _currentSortColumn;
    public bool IsAscending => _sortAscending;

    public void Reset()
    {
        _currentSortColumn = SortColumn.None;
        _sortAscending = true;
    }

    /// <summary>
    ///     Cycle sort state: ascending -> descending -> none
    /// </summary>
    public void CycleSortState(SortColumn column)
    {
        if (_currentSortColumn == column)
        {
            if (_sortAscending)
            {
                _sortAscending = false;
            }
            else
            {
                _currentSortColumn = SortColumn.None;
                _sortAscending = true;
            }
        }
        else
        {
            _currentSortColumn = column;
            _sortAscending = true;
        }
    }

    public IEnumerable<NifFileEntry> Sort(IList<NifFileEntry> files)
    {
        return _currentSortColumn switch
        {
            SortColumn.FilePath => _sortAscending
                ? files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                : files.OrderByDescending(f => f.RelativePath, StringComparer.OrdinalIgnoreCase),
            SortColumn.Size => _sortAscending
                ? files.OrderBy(f => f.FileSize)
                : files.OrderByDescending(f => f.FileSize),
            SortColumn.Format => _sortAscending
                ? files.OrderBy(f => f.FormatDescription, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                : files.OrderByDescending(f => f.FormatDescription, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase),
            SortColumn.Status => _sortAscending
                ? files.OrderBy(f => f.Status, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                : files.OrderByDescending(f => f.Status, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase),
            _ => files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
        };
    }

    public enum SortColumn
    {
        None,
        FilePath,
        Size,
        Format,
        Status
    }
}
