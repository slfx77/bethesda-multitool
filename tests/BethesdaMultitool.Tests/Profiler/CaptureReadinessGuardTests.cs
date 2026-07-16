using BethesdaRendererProfiler;
using Xunit;

namespace BethesdaMultitool.Tests.Profiler;

public sealed class CaptureReadinessGuardTests
{
    [Fact]
    public void TryValidateStreamingQuiesced_AcceptsReadyScene()
    {
        Assert.True(CaptureReadinessGuard.TryValidateStreamingQuiesced(
            true,
            TimeSpan.FromSeconds(60),
            out var error));
        Assert.Null(error);
    }

    [Fact]
    public void TryValidateStreamingQuiesced_RejectsTimedOutSceneAsAcceptanceEvidence()
    {
        Assert.False(CaptureReadinessGuard.TryValidateStreamingQuiesced(
            false,
            TimeSpan.FromSeconds(90),
            out var error));
        Assert.NotNull(error);
        Assert.Contains("90 seconds", error, StringComparison.Ordinal);
        Assert.Contains("no acceptance image was saved", error, StringComparison.Ordinal);
    }
}
