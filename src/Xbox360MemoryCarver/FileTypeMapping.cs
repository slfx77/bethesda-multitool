using Xbox360MemoryCarver.Core.FileTypes;

namespace Xbox360MemoryCarver.App;

/// <summary>
///     Maps display names to file signature IDs for extraction filtering.
///     Uses FileTypeRegistry as single source of truth.
/// </summary>
public static class FileTypeMapping
{
    /// <summary>
    ///     Display names for UI checkboxes.
    /// </summary>
    public static IReadOnlyList<string> DisplayNames => FileTypeRegistry.DisplayNames;

    /// <summary>
    ///     Get signature IDs for the given display names.
    /// </summary>
    public static IEnumerable<string> GetSignatureIds(IEnumerable<string> displayNames)
    {
        return FileTypeRegistry.GetSignatureIdsForDisplayNames(displayNames);
    }
}
