namespace EgtAnalyzer.Verification;

internal sealed record RuntimeFaceGenProbeArrayComparison(
    string Label,
    int RuntimeCount,
    int CurrentCount,
    int ComparedCount,
    double MeanAbsoluteDelta,
    double RootMeanSquareDelta,
    double MaxAbsoluteDelta,
    double MeanSignedDelta,
    int ExactMatchCount,
    IReadOnlyList<RuntimeFaceGenProbeArrayComparisonRow> Rows);
