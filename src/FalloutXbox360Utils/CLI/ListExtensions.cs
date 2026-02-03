namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Extension methods for List trimming in rendering.
/// </summary>
internal static class ListExtensions
{
    /// <summary>
    ///     Returns the list with trailing empty strings removed.
    /// </summary>
    public static List<string> TrimEnd(this List<string> list)
    {
        var result = new List<string>(list);
        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1]))
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }
}