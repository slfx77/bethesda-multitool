namespace FalloutXbox360Utils.Core.Formats.Esm;

internal sealed record RuntimeLayoutProbeCandidateResult<TLayout>(
    RuntimeLayoutProbeCandidate<TLayout> Candidate,
    int TotalScore);