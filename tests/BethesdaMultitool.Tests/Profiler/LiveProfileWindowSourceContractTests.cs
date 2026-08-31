using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Profiler;

/// <summary>
///     Integration seams for the settled live-profile lifecycle. These WinUI sources are not linked
///     into the cross-platform test target, so the contract pins the ordering around the separately
///     unit-tested settlement policy.
/// </summary>
public sealed class LiveProfileWindowSourceContractTests
{
    [Fact]
    public void LiveProfile_SettlesAtRequestedDistanceBeforeMotionAndDurationStart()
    {
        var source = SourceContract.ReadSource("src", "BethesdaRendererProfiler", "MainWindow.cs");
        var loaded = SourceContract.Extract(
            source,
            "private async void OnWorldViewLoaded",
            "private RendererProfilerCameraPose ApplyRequestedFraming");

        SourceContract.AssertOrder(
            loaded,
            "ApplyRequestedFraming(\"Profiler\")",
            "RenderDistance = renderDistanceCells * _worldView.Profiler_CellWorldSize",
            "WaitForProfileSceneSettledAsync",
            "Profiler_BeginProfileWindow()",
            "Renderer3DScenario.Start",
            "StartTimedExitIfRequested()");
    }

    [Fact]
    public void PausedWindow_DiscardsWarmupGpuRowsAndRejectsAggregates()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "WorldView3D",
            "WorldView3DControl.Profiling.cs");

        var gpu = SourceContract.Extract(source, "private void EmitCompletedGpuFrames()", "private void EmitFrameStall");
        var aggregate = SourceContract.Extract(source, "private void MaybeLogProfile", "private int BeginSceneSelection");
        var frame = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "WorldView3D",
            "WorldView3DControl.Frame.cs");

        Assert.Contains("while (_gpuTimestampProfiler12.TryCollectCompleted", gpu, StringComparison.Ordinal);
        Assert.Contains("if (!IsProfileWindowFrame(timings.FrameNumber))", gpu, StringComparison.Ordinal);
        Assert.Contains("!IsProfileWindowFrame(sample.FrameNumber)", aggregate, StringComparison.Ordinal);
        Assert.Contains(
            "if (IsProfileWindowFrame(sample.FrameNumber) &&",
            frame,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SettlementPolling_ObservesAtMostOncePerRenderedFrameAndKeepsProgressLogging()
    {
        var source = SourceContract.ReadSource("src", "BethesdaRendererProfiler", "MainWindow.cs");
        var settlement = SourceContract.Extract(
            source,
            "private async Task<bool> WaitForProfileSceneSettledAsync",
            "private async Task RunTopDownBatchCaptureAsync");

        Assert.Matches(
            @"if \(frame > lastObservedFrame\)\s*\{\s*" +
            @"lastObservedFrame = frame;\s*" +
            @"var census = _worldView\.Profiler_CaptureSceneCensus;\s*" +
            @"if \(tracker\.Observe\(in census\)\)",
            settlement);
        Assert.Equal(1, SourceContract.CountOccurrences(settlement, "tracker.Observe(in census)"));
        SourceContract.AssertOrder(
            settlement,
            "var lastObservedFrame = _worldView.Profiler_FrameIndex",
            "var frame = _worldView.Profiler_FrameIndex",
            "if (frame > lastObservedFrame)",
            "tracker.Observe(in census)",
            "if (timer.Elapsed >= nextProgressLog)");
    }
}
