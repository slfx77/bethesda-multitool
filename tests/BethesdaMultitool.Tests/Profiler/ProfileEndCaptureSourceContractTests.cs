using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Profiler;

/// <summary>
///     WinUI lifecycle seams for the deterministic live-profile epilogue. The platform-neutral test
///     target cannot construct MainWindow, so these contracts pin ordering around the unit-tested
///     option and artifact-writing components.
/// </summary>
public sealed class ProfileEndCaptureSourceContractTests
{
    [Fact]
    public void BenchmarkConfigurationIsRetainedBeforeMotionAndTimedScoringStart()
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
            "Profiler_BeginProfileWindow()",
            "_profileEndCapturePose = _worldView.Profiler_CameraPose",
            "_profileEndCaptureFovDegrees = _worldView.Profiler_CameraFovDegrees",
            "_profileEndCaptureViewport = _worldView.Profiler_ViewportPixelSize",
            "Renderer3DScenario.Start",
            "StartTimedExitIfRequested()");

        var profiling = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "WorldView3D",
            "WorldView3DControl.Profiling.cs");
        var viewport = SourceContract.Extract(
            profiling,
            "Profiler_ViewportPixelSize",
            "Profiler_CellWorldSize");
        Assert.Contains("_surface12", viewport, StringComparison.Ordinal);
        Assert.DoesNotContain("ActualWidth", viewport, StringComparison.Ordinal);
    }

    [Fact]
    public void TimedExitAwaitsCaptureAndAlwaysUsesHistoricalShutdownReason()
    {
        var source = SourceContract.ReadSource("src", "BethesdaRendererProfiler", "MainWindow.cs");
        var handler = SourceContract.Extract(
            source,
            "private async void OnTimedExitTimerTick",
            "private async Task<int> CaptureProfileEndFrameAsync");
        SourceContract.AssertOrder(
            handler,
            "sender.Stop()",
            "await CaptureProfileEndFrameAsync()",
            "finally",
            "ExitProfilerAfterTimedBoundary(reason, exitCode)");
        Assert.Contains(
            "BethesdaRendererProfiler: duration elapsed ({0}s); exiting.",
            handler,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            source.Split(
                "BethesdaRendererProfiler: duration elapsed ({0}s); exiting.",
                StringSplitOptions.None).Length - 1);
        Assert.Contains("catch (Exception ex)", handler, StringComparison.Ordinal);

        var capture = SourceContract.Extract(
            source,
            "private async Task<int> CaptureProfileEndFrameAsync",
            "private void ExitProfilerAfterTimedBoundary");
        SourceContract.AssertOrder(
            capture,
            "scoringEndFrame = _worldView.Profiler_FrameIndex",
            "var currentPose = _worldView.Profiler_CameraPose",
            "var currentFovDegrees = _worldView.Profiler_CameraFovDegrees",
            "var currentViewport = _worldView.Profiler_ViewportPixelSize",
            "var currentCensus = _worldView.Profiler_CaptureSceneCensus",
            "ProfileEndCaptureStateGuard.TryValidate(",
            "_scenario?.Dispose()",
            "Profiler_SetCameraPose(benchmarkPose)",
            "Profiler_SetCameraFov(_profileEndCaptureFovDegrees)",
            "Visibility = Visibility.Collapsed",
            "await _worldView.Profiler_CaptureSceneAsync(",
            "ProfileEndCaptureArtifactWriter.Save(",
            "fields[\"pixelSha256\"]",
            "fields[\"pngSha256\"]",
            "RendererProfilerTrace.Event(\"profile-end-capture\", fields)");

        var shutdown = SourceContract.Extract(
            source,
            "private void ExitProfilerAfterTimedBoundary",
            "private static void TryWriteTimedExitFailure");
        SourceContract.AssertOrder(
            shutdown,
            "Attempt(\"timed-exit profile-log failure\"",
            "Attempt(\"timed-exit trace-close failure\"",
            "Attempt(\"timed-exit log-close failure\"",
            "Environment.ExitCode = finalExitCode",
            "Application.Current.Exit()");
        Assert.Contains("finalExitCode = 1", shutdown, StringComparison.Ordinal);
        Assert.Contains("Environment.Exit(1)", shutdown, StringComparison.Ordinal);

        var traceClose = SourceContract.Extract(
            source,
            "private void CloseProfilerTrace",
            "private void SetStatus");
        SourceContract.AssertOrder(
            traceClose,
            "try",
            "RendererProfilerTrace.Event(\"shutdown\"",
            "finally",
            "RendererProfilerTrace.Close()");
    }
}
