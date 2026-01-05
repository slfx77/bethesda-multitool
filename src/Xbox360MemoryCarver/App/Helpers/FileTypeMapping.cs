using Xbox360MemoryCarver.Core.Formats;

namespace Xbox360MemoryCarver;

/// <summary>
///     Maps display names to file signature IDs for extraction filtering.
///     Uses FormatRegistry as single source of truth.
/// </summary>
public static class FileTypeMapping
{
    /// <summary>
    ///     Display names for UI checkboxes.
    /// </summary>
    public static IReadOnlyList<string> DisplayNames => FormatRegistry.DisplayNames;

    /// <summary>
    ///     Get signature IDs for the given display names.
    /// </summary>
    public static IEnumerable<string> GetSignatureIds(IEnumerable<string> displayNames)
    {
        return FormatRegistry.GetSignatureIdsForDisplayNames(displayNames);
    }
}
