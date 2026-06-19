namespace BethesdaMultitool.Core.Formats.Esm.Runtime;

internal sealed record RuntimeLayoutProbeCandidateResult<TLayout>(
    RuntimeLayoutProbeCandidate<TLayout> Candidate,
    int TotalScore);
