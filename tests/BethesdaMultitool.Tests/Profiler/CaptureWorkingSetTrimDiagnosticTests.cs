using BethesdaMultitool.Tests.Helpers;
using BethesdaRendererProfiler;
using Xunit;

namespace BethesdaMultitool.Tests.Profiler;

public sealed class CaptureWorkingSetTrimDiagnosticTests
{
    [Fact]
    public void PageFaultObservation_ReportsRawDeltaAndRateWithoutPolicy()
    {
        var observation = CaptureWorkingSetTrimDiagnostic.CalculatePageFaultObservation(
            1_000,
            1_500,
            TimeSpan.FromSeconds(2));

        Assert.Equal(500, observation.Delta);
        Assert.Equal(250d, observation.PerSecond);
    }

    [Fact]
    public void PageFaultObservation_PreservesOneUnsignedCounterWrap()
    {
        var observation = CaptureWorkingSetTrimDiagnostic.CalculatePageFaultObservation(
            uint.MaxValue - 4,
            5,
            TimeSpan.FromSeconds(1));

        Assert.Equal(10, observation.Delta);
        Assert.Equal(10d, observation.PerSecond);
    }

    [Fact]
    public void PageFaultObservation_LeavesUnavailableCountersUnclassified()
    {
        var observation = CaptureWorkingSetTrimDiagnostic.CalculatePageFaultObservation(
            null,
            5,
            TimeSpan.FromSeconds(1));

        Assert.Null(observation.Delta);
        Assert.Null(observation.PerSecond);
    }

    [Fact]
    public void PerspectiveCapture_TrimRunsAfterFramingAndBeforePhaseTwoSettle()
    {
        var source = SourceContract.ReadSource("src", "BethesdaRendererProfiler", "MainWindow.cs");
        var method = SourceContract.Extract(
            source,
            "private async Task RunSceneCaptureAsync()",
            "private bool ValidateCaptureSelection");

        SourceContract.AssertOrder(
            method,
            "var framedPose = ApplyRequestedFraming",
            "CaptureWorkingSetTrimDiagnostic.RunBeforeSettle()",
            "Renderer3DScenario.Start(_worldView, DispatcherQueue, _options)",
            "while (quiesceTimer.Elapsed < settleTimeout)",
            "CaptureWorkingSetTrimDiagnostic.ObserveAfterSettle");
    }

    [Fact]
    public void TrimHelper_PinsNonCompactingGcNativeTrimAndObservationOnlyTelemetry()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaRendererProfiler", "CaptureWorkingSetTrimDiagnostic.cs");

        Assert.Contains("GCCollectionMode.Forced", source, StringComparison.Ordinal);
        Assert.Contains("blocking: true", source, StringComparison.Ordinal);
        Assert.Contains("compacting: false", source, StringComparison.Ordinal);
        Assert.Contains("K32EmptyWorkingSet(process.Handle)", source, StringComparison.Ordinal);
        Assert.Contains("AddSnapshotFields(fields, \"before\", before)", source, StringComparison.Ordinal);
        Assert.Contains("AddSnapshotFields(fields, \"after\", after)", source, StringComparison.Ordinal);
        Assert.Contains("{prefix}ManagedHeapBytes", source, StringComparison.Ordinal);
        Assert.Contains("{prefix}GcCommittedBytes", source, StringComparison.Ordinal);
        Assert.Contains("{prefix}GcFragmentedBytes", source, StringComparison.Ordinal);
        Assert.Contains("{prefix}PrivateBytes", source, StringComparison.Ordinal);
        Assert.Contains("{prefix}WorkingSetBytes", source, StringComparison.Ordinal);
        Assert.Contains("{prefix}SystemAvailableBytes", source, StringComparison.Ordinal);
        Assert.Contains("{prefix}PageFaultCount", source, StringComparison.Ordinal);
        Assert.Contains("capture-working-set-trim-settle-observation", source, StringComparison.Ordinal);
        Assert.Contains("[\"acceptanceThresholdApplied\"] = false", source, StringComparison.Ordinal);
    }
}
