namespace BethesdaMultitool;

/// <summary>
///     View model for a gap entry in the coverage tab.
/// </summary>
internal sealed class CoverageGapEntry
{
    public int Index { get; init; }
    public string FileOffset { get; init; } = "";
    public string Size { get; init; } = "";
    public string Classification { get; init; } = "";
    public string Recoverable { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string Context { get; init; } = "";
    public long RawFileOffset { get; init; }
    public long RawSize { get; init; }
}
