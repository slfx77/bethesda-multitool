namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime;

internal sealed record RuntimeWorldCellLayoutProbeResult(
    RuntimeWorldCellLayout Layout,
    bool IsHighConfidence,
    int WinnerScore,
    int RunnerUpScore,
    int SampleCount)
{
    public int Margin => WinnerScore - RunnerUpScore;
}
