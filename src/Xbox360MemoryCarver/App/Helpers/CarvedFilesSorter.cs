namespace Xbox360MemoryCarver;

/// <summary>
///     Handles sorting logic for the carved files list view.
/// </summary>
internal sealed class CarvedFilesSorter
{
    public enum SortColumn
    {
        None,
        Offset,
        Length,
        Type,
        Filename
    }

    public SortColumn CurrentColumn { get; private set; } = SortColumn.None;

    public bool IsAscending { get; private set; } = true;

    public void Reset()
    {
        CurrentColumn = SortColumn.None;
        IsAscending = true;
    }

    /// <summary>
    ///     Cycle sort state: ascending -> descending -> none
    /// </summary>
    public void CycleSortState(SortColumn column)
    {
        if (CurrentColumn == column)
        {
            if (IsAscending)
            {
                IsAscending = false;
            }
            else
            {
                CurrentColumn = SortColumn.None;
                IsAscending = true;
            }
        }
        else
        {
            CurrentColumn = column;
            IsAscending = true;
        }
    }

    public IEnumerable<CarvedFileEntry> Sort(IList<CarvedFileEntry> files)
    {
        return CurrentColumn switch
        {
            SortColumn.Offset => SortByOffset(files),
            SortColumn.Length => SortByLength(files),
            SortColumn.Type => SortByType(files),
            SortColumn.Filename => SortByFilename(files),
            _ => files.OrderBy(f => f.Offset)
        };
    }

    private IEnumerable<CarvedFileEntry> SortByOffset(IList<CarvedFileEntry> files)
    {
        return IsAscending ? files.OrderBy(f => f.Offset) : files.OrderByDescending(f => f.Offset);
    }

    private IEnumerable<CarvedFileEntry> SortByLength(IList<CarvedFileEntry> files)
    {
        return IsAscending ? files.OrderBy(f => f.Length) : files.OrderByDescending(f => f.Length);
    }

    private IEnumerable<CarvedFileEntry> SortByType(IList<CarvedFileEntry> files)
    {
        return IsAscending
            ? files.OrderBy(f => f.FileType, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Offset)
            : files.OrderByDescending(f => f.FileType, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Offset);
    }

    private IEnumerable<CarvedFileEntry> SortByFilename(IList<CarvedFileEntry> files)
    {
        return IsAscending
            ? files.OrderBy(f => string.IsNullOrEmpty(f.FileName) ? 1 : 0)
                .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Offset)
            : files.OrderBy(f => string.IsNullOrEmpty(f.FileName) ? 1 : 0)
                .ThenByDescending(f => f.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Offset);
    }
}
