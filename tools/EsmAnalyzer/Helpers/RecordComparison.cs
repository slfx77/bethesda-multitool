namespace EsmAnalyzer.Helpers;

/// <summary>
///     Result of comparing two records.
/// </summary>
public sealed record RecordComparison
{
    public bool IsIdentical { get; set; }
    public bool OnlySizeDiffers { get; set; }
    public List<SubrecordDiff> SubrecordDiffs { get; } = [];
}