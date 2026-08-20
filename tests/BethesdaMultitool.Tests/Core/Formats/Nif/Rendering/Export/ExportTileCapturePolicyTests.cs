using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

public sealed class ExportTileCapturePolicyTests
{
    [Theory]
    [InlineData((int)ExportTileCapturePolicy.FullySettledOnly, false, false, false)]
    [InlineData((int)ExportTileCapturePolicy.FullySettledOnly, true, false, false)]
    [InlineData((int)ExportTileCapturePolicy.FullySettledOnly, false, true, true)]
    [InlineData((int)ExportTileCapturePolicy.CompleteOrFullySettled, false, false, false)]
    [InlineData((int)ExportTileCapturePolicy.CompleteOrFullySettled, true, false, true)]
    [InlineData((int)ExportTileCapturePolicy.CompleteOrFullySettled, false, true, true)]
    [InlineData((int)ExportTileCapturePolicy.Always, false, false, true)]
    [InlineData((int)ExportTileCapturePolicy.Always, true, false, true)]
    public void ShouldCapture_ImplementsCurrentFramePolicy(
        int policyValue,
        bool isComplete,
        bool isFullySettled,
        bool expected)
    {
        var policy = (ExportTileCapturePolicy)policyValue;
        Assert.Equal(expected, ExportTileCaptureDecision.ShouldCapture(policy, isComplete, isFullySettled));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ShouldCapture_RejectsUnknownPolicyRegardlessOfStatus(bool isComplete, bool isFullySettled)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ExportTileCaptureDecision.ShouldCapture(
                (ExportTileCapturePolicy)999, isComplete, isFullySettled));
    }
}