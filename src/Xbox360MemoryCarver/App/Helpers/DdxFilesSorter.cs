namespace Xbox360MemoryCarver;

/// <summary>
///     Handles sorting logic for the DDX files list view.
/// </summary>
internal sealed class DdxFilesSorter
{
    public enum SortColumn
    {
        None,
        FilePath,
        Size,
        Format,
        Status
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

    public IEnumerable<DdxFileEntry> Sort(IList<DdxFileEntry> files)
    {
        return CurrentColumn switch
        {
            SortColumn.FilePath => IsAscending
                ? files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                : files.OrderByDescending(f => f.RelativePath, StringComparer.OrdinalIgnoreCase),
            SortColumn.Size => IsAscending
                ? files.OrderBy(f => f.FileSize)
                : files.OrderByDescending(f => f.FileSize),
            SortColumn.Format => IsAscending
                ? files.OrderBy(f => f.FormatDescription, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                : files.OrderByDescending(f => f.FormatDescription, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase),
            SortColumn.Status => IsAscending
                ? files.OrderBy(f => f.Status, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                : files.OrderByDescending(f => f.Status, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase),
            _ => files.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
        };
    }
}
