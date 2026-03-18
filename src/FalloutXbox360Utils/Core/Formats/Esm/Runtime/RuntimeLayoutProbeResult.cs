namespace FalloutXbox360Utils.Core.Formats.Esm;

internal sealed record RuntimeLayoutProbeResult<TLayout>(
    RuntimeLayoutProbeCandidate<TLayout> Winner,
    int WinnerScore,
    int RunnerUpScore,
    int SampleCount,
    IReadOnlyList<RuntimeLayoutProbeCandidateResult<TLayout>> Rankings)
{
    public int Margin => WinnerScore - RunnerUpScore;
}
