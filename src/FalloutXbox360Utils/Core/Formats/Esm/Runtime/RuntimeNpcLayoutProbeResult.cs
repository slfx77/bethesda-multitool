namespace FalloutXbox360Utils.Core.Formats.Esm;

internal sealed record RuntimeNpcLayoutProbeResult(
    RuntimeNpcLayout Layout,
    bool IsHighConfidence,
    int WinnerScore,
    int RunnerUpScore,
    int SampleCount)
{
    public int Margin => WinnerScore - RunnerUpScore;
}
