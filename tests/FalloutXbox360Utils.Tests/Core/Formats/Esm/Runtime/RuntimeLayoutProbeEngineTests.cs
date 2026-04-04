using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

public sealed class RuntimeLayoutProbeEngineTests
{
    [Fact]
    public void Probe_UsesHighestAggregateScore()
    {
        var samples = new[] { 1, 2, 3 };
        var candidates = new[]
        {
            new RuntimeLayoutProbeCandidate<int>("A", 1),
            new RuntimeLayoutProbeCandidate<int>("B", 2)
        };

        var result = RuntimeLayoutProbeEngine.Probe(
            samples,
            candidates,
            (sample, candidate) => new RuntimeLayoutProbeScore(candidate.Layout == 1 ? 1 : sample),
            "Unit Probe");

        Assert.Equal(2, result.Winner.Layout);
        Assert.Equal(6, result.WinnerScore);
        Assert.Equal(3, result.RunnerUpScore);
        Assert.Equal(3, result.Margin);
    }

    [Fact]
    public void Probe_PreservesCandidateOrderOnTie()
    {
        var candidates = new[]
        {
            new RuntimeLayoutProbeCandidate<int>("First", 10),
            new RuntimeLayoutProbeCandidate<int>("Second", 20)
        };

        var result = RuntimeLayoutProbeEngine.Probe(
            [0],
            candidates,
            (_, _) => new RuntimeLayoutProbeScore(4),
            "Tie Probe");

        Assert.Equal(10, result.Winner.Layout);
        Assert.Equal(4, result.WinnerScore);
        Assert.Equal(4, result.RunnerUpScore);
        Assert.Equal(0, result.Margin);
    }

    [Fact]
    public void Probe_EmitsDiagnosticsWithoutChangingSelection()
    {
        var logs = new List<string>();
        var candidates = new[]
        {
            new RuntimeLayoutProbeCandidate<string>("Alpha", "a"),
            new RuntimeLayoutProbeCandidate<string>("Beta", "b")
        };

        var result = RuntimeLayoutProbeEngine.Probe(
            ["sample-1"],
            candidates,
            (sample, candidate) => new RuntimeLayoutProbeScore(
                candidate.Layout == "b" ? 5 : 1,
                5,
                $"{sample}:{candidate.Label}"),
            "Diag Probe",
            logs.Add,
            sample => sample,
            true);

        Assert.Equal("b", result.Winner.Layout);
        Assert.Contains(logs, line => line.Contains("[Diag Probe] Sample: sample-1", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("sample-1:Beta", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("Candidate totals", StringComparison.Ordinal));
    }
}