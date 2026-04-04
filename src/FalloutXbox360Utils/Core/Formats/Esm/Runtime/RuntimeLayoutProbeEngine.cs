namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime;

/// <summary>
///     Generic scoring engine for runtime layout probes.
///     Callers provide candidate layouts, sampled inputs, and a scorer that returns
///     a score plus optional diagnostic details for each sample/candidate pair.
/// </summary>
internal static class RuntimeLayoutProbeEngine
{
    public static RuntimeLayoutProbeResult<TLayout> Probe<TSample, TLayout>(
        IReadOnlyList<TSample> samples,
        IReadOnlyList<RuntimeLayoutProbeCandidate<TLayout>> candidates,
        Func<TSample, RuntimeLayoutProbeCandidate<TLayout>, RuntimeLayoutProbeScore> scorer,
        string probeName,
        Action<string>? log = null,
        Func<TSample, string>? sampleLabel = null,
        bool logPerSampleDetails = false)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(scorer);
        ArgumentException.ThrowIfNullOrWhiteSpace(probeName);

        if (candidates.Count == 0)
        {
            throw new ArgumentException("Probe requires at least one candidate.", nameof(candidates));
        }

        var totals = new int[candidates.Count];

        foreach (var sample in samples)
        {
            if (log != null && logPerSampleDetails)
            {
                var label = sampleLabel?.Invoke(sample) ?? sample?.ToString() ?? "(sample)";
                log($"  [{probeName}] Sample: {label}");
            }

            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                var score = scorer(sample, candidate);
                totals[i] += score.Points;

                if (log == null || !logPerSampleDetails)
                {
                    continue;
                }

                log($"    {candidate.Label}: {FormatScore(score)}");
            }
        }

        var rankings = candidates
            .Select((candidate, index) => new
            {
                Candidate = new RuntimeLayoutProbeCandidateResult<TLayout>(candidate, totals[index]),
                Index = index
            })
            .OrderByDescending(result => result.Candidate.TotalScore)
            .ThenBy(result => result.Index)
            .Select(result => result.Candidate)
            .ToList();

        var winner = rankings[0];
        var runnerUpScore = rankings.Count > 1 ? rankings[1].TotalScore : 0;

        if (log != null)
        {
            log($"  [{probeName}] Candidate totals:");
            foreach (var ranking in rankings)
            {
                log($"    {ranking.Candidate.Label}: {ranking.TotalScore}");
            }

            log(
                $"  [{probeName}] Best: {winner.Candidate.Label} (score {winner.TotalScore}, margin {winner.TotalScore - runnerUpScore})");
        }

        return new RuntimeLayoutProbeResult<TLayout>(
            winner.Candidate,
            winner.TotalScore,
            runnerUpScore,
            samples.Count,
            rankings);
    }

    private static string FormatScore(RuntimeLayoutProbeScore score)
    {
        var baseScore = score.MaxPoints > 0
            ? $"Score {score.Points}/{score.MaxPoints}"
            : $"Score {score.Points}";

        return string.IsNullOrWhiteSpace(score.Detail)
            ? baseScore
            : $"{score.Detail} | {baseScore}";
    }
}
