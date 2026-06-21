namespace EgtAnalyzer.Verification;

internal sealed record RuntimeFaceGenProbeComparisonResult
{
    public required string CaptureDirectory { get; init; }
    public required string CaptureDirectoryName { get; init; }
    public required uint FormId { get; init; }
    public required uint RaceFormId { get; init; }
    public required bool IsFemale { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public RuntimeFaceGenProbeArrayComparison? Npc { get; init; }
    public RuntimeFaceGenProbeArrayComparison? Race { get; init; }
    public RuntimeFaceGenProbeArrayComparison? Merged { get; init; }
    public IReadOnlyList<RuntimeFaceGenProbeRaceMatch>? RaceMatches { get; init; }
    public string? FailureReason { get; init; }

    public bool Compared => string.IsNullOrWhiteSpace(FailureReason) && Merged != null;
}
