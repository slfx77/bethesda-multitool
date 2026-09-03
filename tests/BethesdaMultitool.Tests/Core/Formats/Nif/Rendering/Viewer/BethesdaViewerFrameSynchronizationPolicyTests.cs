using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Viewer;

public sealed class BethesdaViewerFrameSynchronizationPolicyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void RequiresIdleOnlyForReadyUnsettledTextureStreaming(
        bool sessionReady,
        bool texturesSettled,
        bool expected)
    {
        var actual = BethesdaViewerFrameSynchronizationPolicy.RequiresGpuIdleBeforeFrame(
            sessionReady,
            texturesSettled);

        Assert.Equal(expected, actual);
    }
}
