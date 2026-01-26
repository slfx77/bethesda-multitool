namespace EsmAnalyzer.Helpers;

/// <summary>
///     Statistics for differences per type.
/// </summary>
public sealed class TypeDiffStats
{
    public required string Type { get; init; }
    public int Total { get; set; }
    public int Identical { get; set; }
    public int SizeDiff { get; set; }
    public int ContentDiff { get; set; }
}