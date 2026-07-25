using BethesdaRendererProfiler;
using Xunit;

namespace BethesdaMultitool.Tests.Profiler;

public sealed class CaptureSelectionGuardTests
{
    [Fact]
    public void TryValidate_AcceptsCaseInsensitiveRoundTrip()
    {
        Assert.True(CaptureSelectionGuard.TryValidate(
            "weather",
            " NVWastelandClear ",
            "nvwastelandclear",
            out var error));
        Assert.Null(error);
    }

    [Fact]
    public void TryValidate_AcceptsUnspecifiedSelector()
    {
        Assert.True(CaptureSelectionGuard.TryValidate("worldspace", null, "WastelandNV", out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("worldspace", "WastelandNV", "CapitalWasteland", "requested worldspace 'WastelandNV'")]
    [InlineData("weather", "NVWastelandClear", null, "actual weather is '(none)'")]
    public void TryValidate_RejectsRequestedActualMismatch(
        string selectorKind,
        string requested,
        string? actual,
        string expectedError)
    {
        Assert.False(CaptureSelectionGuard.TryValidate(
            selectorKind,
            requested,
            actual,
            out var error));
        Assert.NotNull(error);
        Assert.Contains(expectedError, error, StringComparison.Ordinal);
    }
}