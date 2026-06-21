namespace EgtAnalyzer.Verification;

internal sealed record RuntimeFaceGenProbeArrayComparisonRow(
    int Index,
    float RuntimeValue,
    float CurrentValue,
    double Delta,
    double AbsoluteDelta);
