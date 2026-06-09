using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

public class RuntimeCellLayoutReadPolicyTests
{
    [Fact]
    public void ShouldAllowStructuralReads_ProtoOffsets_AreAllowed()
    {
        var probe = MakeProbe(false, 0, 0);

        Assert.True(RuntimeCellLayoutReadPolicy.ShouldAllowStructuralReads(true, probe));
    }

    [Fact]
    public void ShouldAllowStructuralReads_HighConfidenceProbe_IsAllowed()
    {
        var probe = MakeProbe(true, 10, 0);

        Assert.True(RuntimeCellLayoutReadPolicy.ShouldAllowStructuralReads(false, probe));
    }

    [Fact]
    public void ShouldAllowStructuralReads_LowConfidenceHighAbsoluteScore_IsAllowed()
    {
        var probe = MakeProbe(
            false,
            RuntimeCellLayoutReadPolicy.HighAbsoluteScoreThreshold,
            RuntimeCellLayoutReadPolicy.HighAbsoluteScoreThreshold - 1);

        Assert.True(RuntimeCellLayoutReadPolicy.ShouldAllowStructuralReads(false, probe));
    }

    [Fact]
    public void ShouldAllowStructuralReads_LowConfidenceLowScore_IsBlocked()
    {
        var probe = MakeProbe(false, 4, 4);

        Assert.False(RuntimeCellLayoutReadPolicy.ShouldAllowStructuralReads(false, probe));
    }

    [Fact]
    public void ShouldAllowStructuralReads_NoProbe_IsAllowed()
    {
        Assert.True(RuntimeCellLayoutReadPolicy.ShouldAllowStructuralReads(false, null));
    }

    private static RuntimeWorldCellLayoutProbeResult MakeProbe(
        bool highConfidence,
        int winnerScore,
        int runnerUpScore)
    {
        return new RuntimeWorldCellLayoutProbeResult(
            new RuntimeWorldCellLayout(0, 0),
            highConfidence,
            winnerScore,
            runnerUpScore,
            1);
    }
}