using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Profiler;

public sealed class LiveProfileRunnerViewportSourceContractTests
{
    [Fact]
    public void Runner_preserves_historical_dimensions_but_accepts_explicit_window_and_viewport_pairs()
    {
        var harness = SourceContract.ReadSource("scratchpad", "live_profiles", "run_live.ps1");

        Assert.Contains("[int]$WindowWidth = 1450", harness, StringComparison.Ordinal);
        Assert.Contains("[int]$WindowHeight = 900", harness, StringComparison.Ordinal);
        Assert.Contains("[int]$ExpectedViewportWidth = 1434", harness, StringComparison.Ordinal);
        Assert.Contains("[int]$ExpectedViewportHeight = 793", harness, StringComparison.Ordinal);
        Assert.Contains("'--width', $WindowWidth.ToString", harness, StringComparison.Ordinal);
        Assert.Contains("'--height', $WindowHeight.ToString", harness, StringComparison.Ordinal);
        Assert.Contains("windowWidth = $WindowWidth", harness, StringComparison.Ordinal);
        Assert.Contains("windowHeight = $WindowHeight", harness, StringComparison.Ordinal);
        Assert.Contains("expectedViewportWidth = $ExpectedViewportWidth", harness,
            StringComparison.Ordinal);
        Assert.Contains("expectedViewportHeight = $ExpectedViewportHeight", harness,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Runner_rejects_invalid_or_aspect_changing_pairs_and_asserts_the_actual_client_viewport()
    {
        var harness = SourceContract.ReadSource("scratchpad", "live_profiles", "run_live.ps1");

        Assert.Contains("$WindowWidth -lt 640 -or $WindowWidth -gt 16384", harness,
            StringComparison.Ordinal);
        Assert.Contains("$WindowHeight -lt 480 -or $WindowHeight -gt 16384", harness,
            StringComparison.Ordinal);
        Assert.Contains("$ExpectedViewportWidth -gt $WindowWidth", harness,
            StringComparison.Ordinal);
        Assert.Contains("$historicalViewportAspect = 1434.0 / 793.0", harness,
            StringComparison.Ordinal);
        Assert.Contains("$viewportAspectTolerance = 0.005", harness, StringComparison.Ordinal);
        Assert.Contains("$viewportAspectRelativeError -gt $viewportAspectTolerance", harness,
            StringComparison.Ordinal);
        Assert.Contains("[int]$aggregate.viewportWidth -ne $ExpectedViewportWidth", harness,
            StringComparison.Ordinal);
        Assert.Contains("[int]$aggregate.viewportHeight -ne $ExpectedViewportHeight", harness,
            StringComparison.Ordinal);
    }
}
