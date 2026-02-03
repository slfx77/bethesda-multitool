namespace FalloutXbox360Utils;

/// <summary>
///     Handles sorting logic for the carved files list view.
/// </summary>
internal sealed class CarvedFilesSorter : FileSorterBase<CarvedFileEntry, CarvedFilesSorter.SortColumn>
{
    protected override SortColumn NoneColumn => SortColumn.None;

    public override IEnumerable<CarvedFileEntry> Sort(IList<CarvedFileEntry> files)
    {
        return CurrentColumn switch
        {
            SortColumn.Offset => IsAscending
                ? files.OrderBy(f => f.Offset)
                : files.OrderByDescending(f => f.Offset),
            SortColumn.Length => IsAscending
                ? files.OrderBy(f => f.Length)
                : files.OrderByDescending(f => f.Length),
            SortColumn.Type => IsAscending
                ? files.OrderBy(f => f.DisplayType, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Offset)
                : files.OrderByDescending(f => f.DisplayType, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Offset),
            SortColumn.Filename => IsAscending
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
