namespace Xbox360MemoryCarver.App;

/// <summary>
///     Handles sorting logic for the carved files list view.
/// </summary>
internal sealed class CarvedFilesSorter
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

    public IEnumerable<CarvedFileEntry> Sort(IList<CarvedFileEntry> files)
    {
        return _currentSortColumn switch
        {
            SortColumn.Offset => _sortAscending
                ? files.OrderBy(f => f.Offset)
                : files.OrderByDescending(f => f.Offset),
            SortColumn.Length => _sortAscending
                ? files.OrderBy(f => f.Length)
                : files.OrderByDescending(f => f.Length),
            SortColumn.Type => _sortAscending
                ? files.OrderBy(f => f.FileType, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Offset)
                : files.OrderByDescending(f => f.FileType, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Offset),
            SortColumn.Filename => _sortAscending
                ? files.OrderBy(f => string.IsNullOrEmpty(f.FileName) ? 1 : 0)
                    .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Offset)
                : files.OrderBy(f => string.IsNullOrEmpty(f.FileName) ? 1 : 0)
                    .ThenByDescending(f => f.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Offset),
            _ => files.OrderBy(f => f.Offset)
        };
    }

    public enum SortColumn
    {
        None,
        Offset,
        Length,
        Type,
        Filename
    }
}
