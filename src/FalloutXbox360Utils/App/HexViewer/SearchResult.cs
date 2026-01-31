namespace FalloutXbox360Utils;

/// <summary>
///     Result of a search operation.
/// </summary>
internal readonly record struct SearchResult(bool HasResults, long? MatchOffset)
{
    public static readonly SearchResult NoResults = new(false, null);
    public static readonly SearchResult InvalidHex = new(false, null);
    public bool IsInvalidHex => !HasResults && MatchOffset == null;
}
