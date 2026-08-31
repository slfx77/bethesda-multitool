using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Scene;

public sealed class ReferenceBatchBuildPolicyTests
{
    [Theory]
    [InlineData(true, false, false, 0, true)]
    [InlineData(true, true, true, 0, true)]
    [InlineData(false, false, false, 0, false)]
    [InlineData(true, true, false, 0, false)]
    [InlineData(true, false, false, 1, false)]
    public void Periodic_refresh_is_skipped_only_for_terminal_settled_content(
        bool buildQuiesced,
        bool hasMissingMeshes,
        bool missingMeshesStalled,
        int pendingMaterializationRetries,
        bool expected)
    {
        Assert.Equal(
            expected,
            ReferenceBatchBuildPolicy.CanSkipPeriodicRefresh(
                buildQuiesced,
                hasMissingMeshes,
                missingMeshesStalled,
                pendingMaterializationRetries));
    }

    [Fact]
    public void Refresh_only_build_with_every_hard_key_matching_can_be_amortized()
    {
        Assert.True(CanAmortize());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Any_false_input_forces_a_synchronous_build(int falseInput)
    {
        var inputs = Enumerable.Repeat(true, 8).ToArray();
        inputs[falseInput] = false;

        Assert.False(ReferenceBatchBuildPolicy.CanAmortize(
            inputs[0], inputs[1], inputs[2], inputs[3],
            inputs[4], inputs[5], inputs[6], inputs[7]));
    }

    private static bool CanAmortize()
    {
        return ReferenceBatchBuildPolicy.CanAmortize(
            streamingThrottled: true,
            refreshOnlyBlocker: true,
            cullCacheHit: true,
            publishedBuildValid: true,
            cullEpochMatches: true,
            renderOriginMatches: true,
            evictionGenerationMatches: true,
            streamRoutingMatches: true);
    }
}
