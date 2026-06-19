namespace BethesdaMultitool;

/// <summary>
///     Result of a search operation.
/// </summary>
internal readonly record struct SearchResult(bool HasResults, long? MatchOffset)
{
    /// <summary>A completed search that found no matches.</summary>
    public static readonly SearchResult NoResults = new(false, null);

    /// <summary>The search text could not be parsed as a hex pattern.</summary>
    public static readonly SearchResult InvalidHex = new(false, null);

    /// <summary>True when this result represents an unparseable hex query rather than a genuine no-match.</summary>
    public bool IsInvalidHex => !HasResults && MatchOffset == null;
}
