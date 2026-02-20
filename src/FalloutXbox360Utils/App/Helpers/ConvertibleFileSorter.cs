namespace FalloutXbox360Utils;

/// <summary>
///     Shared sorting logic for convertible file list views (DDX, NIF).
/// </summary>
internal sealed class ConvertibleFileSorter<TEntry>
    : FileSorterBase<TEntry, ConvertibleSortColumn>
    where TEntry : IConvertibleFileEntry
{
    protected override ConvertibleSortColumn NoneColumn => ConvertibleSortColumn.None;

    public override IEnumerable<TEntry> Sort(IList<TEntry> items)
    {
        return CurrentColumn switch
        {
            ConvertibleSortColumn.FilePath => IsAscending
                ? items.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                : items.OrderByDescending(f => f.RelativePath, StringComparer.OrdinalIgnoreCase),
            ConvertibleSortColumn.Size => IsAscending
                ? items.OrderBy(f => f.FileSize)
                : items.OrderByDescending(f => f.FileSize),
            ConvertibleSortColumn.Format => IsAscending
                ? items.OrderBy(f => f.FormatDescription, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                : items.OrderByDescending(f => f.FormatDescription, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase),
            ConvertibleSortColumn.Status => IsAscending
                ? items.OrderBy(f => f.Status, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                : items.OrderByDescending(f => f.Status, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase),
            _ => items.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
        };
    }
}

internal enum ConvertibleSortColumn
{
    None,
    FilePath,
    Size,
    Format,
    Status
}

/// <summary>
///     Shared interface for file entries in convertible file lists.
/// </summary>
internal interface IConvertibleFileEntry
{
    string RelativePath { get; }
    long FileSize { get; }
    string FormatDescription { get; set; }
    string Status { get; set; }
}
