namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     Converts the NIF viewer's delimited texture-source text to and from the
///     case-insensitively unique paths consumed by <see cref="NifBrowserService" />.
/// </summary>
internal static class NifTextureSourcePathText
{
    internal static string[]? ParseOverride(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var paths = text.Split(
                [';', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return paths.Length == 0 ? null : paths;
    }

    internal static string Format(string[] paths)
    {
        return string.Join("; ", paths);
    }
}
