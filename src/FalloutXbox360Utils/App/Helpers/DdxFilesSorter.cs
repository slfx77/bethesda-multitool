namespace FalloutXbox360Utils;

/// <summary>
///     Handles sorting logic for the DDX files list view.
/// </summary>
internal sealed class DdxFilesSorter : FileSorterBase<DdxFileEntry, DdxFilesSorter.SortColumn>
{
    protected override SortColumn NoneColumn => SortColumn.None;

    public override IEnumerable<DdxFileEntry> Sort(IList<DdxFileEntry> files)
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

    public enum SortColumn
    {
        None,
        FilePath,
        Size,
        Format,
        Status
    }
}
