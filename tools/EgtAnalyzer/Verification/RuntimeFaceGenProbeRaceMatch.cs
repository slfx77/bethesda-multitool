namespace EgtAnalyzer.Verification;

internal sealed record RuntimeFaceGenProbeRaceMatch(
    uint RaceFormId,
    string? EditorId,
    string RuntimePage,
    string CandidateSex,
    string RelationToRuntimeRace,
    RuntimeFaceGenProbeArrayComparison Comparison,
    RuntimeFaceGenProbeScaledFit Fit);
