using Xbox360MemoryCarver.Core.Models;

namespace Xbox360MemoryCarver.App;

/// <summary>
///     Maps display names to file signature keys for extraction filtering.
///     Uses FileTypeMetadata from Core as single source of truth.
/// </summary>
public static class FileTypeMapping
{
    /// <summary>
    ///     Display names for UI checkboxes (from Core metadata).
    /// </summary>
    public static readonly string[] DisplayNames = FileTypeMetadata.DisplayNames;

    /// <summary>
    ///     Get signature keys for the given display names.
    /// </summary>
    public static IEnumerable<string> GetSignatureKeys(IEnumerable<string> displayNames)
    {
        return FileTypeMetadata.GetSignatureKeys(displayNames);
    }
}
