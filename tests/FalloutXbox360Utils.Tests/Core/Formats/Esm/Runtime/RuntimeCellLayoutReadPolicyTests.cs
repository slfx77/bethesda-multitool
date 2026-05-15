using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

public class RuntimeCellLayoutReadPolicyTests
{
    [Fact]
    public void ShouldAllowStructuralReads_ProtoOffsets_AreAllowed()
    {
        var probe = MakeProbe(highConfidence: false, winnerScore: 0, runnerUpScore: 0);

        Assert.True(RuntimeCellLayoutReadPolicy.ShouldAllowStructuralReads(useProtoOffsets: true, probe));
    }

    [Fact]
    public void ShouldAllowStructuralReads_HighConfidenceProbe_IsAllowed()
    {
        var probe = MakeProbe(highConfidence: true, winnerScore: 10, runnerUpScore: 0);

        Assert.True(RuntimeCellLayoutReadPolicy.ShouldAllowStructuralReads(useProtoOffsets: false, probe));
    }

    [Fact]
    public void ShouldAllowStructuralReads_LowConfidenceHighAbsoluteScore_IsAllowed()
    {
        var probe = MakeProbe(
            highConfidence: false,
            winnerScore: RuntimeCellLayoutReadPolicy.HighAbsoluteScoreThreshold,
            runnerUpScore: RuntimeCellLayoutReadPolicy.HighAbsoluteScoreThreshold - 1);

        Assert.True(RuntimeCellLayoutReadPolicy.ShouldAllowStructuralReads(useProtoOffsets: false, probe));
    }

    [Fact]
    public void ShouldAllowStructuralReads_LowConfidenceLowScore_IsBlocked()
    {
        var probe = MakeProbe(highConfidence: false, winnerScore: 4, runnerUpScore: 4);

        Assert.False(RuntimeCellLayoutReadPolicy.ShouldAllowStructuralReads(useProtoOffsets: false, probe));
    }

    [Fact]
    public void ShouldAllowStructuralReads_NoProbe_IsAllowed()
    {
        Assert.True(RuntimeCellLayoutReadPolicy.ShouldAllowStructuralReads(useProtoOffsets: false, probe: null));
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
            SampleCount: 1);
    }
}
