using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

/// <summary>
///     Source contracts for the GUI crash-capture hardening: the always-on per-process log that
///     opens BEFORE <c>Application.Start</c>, the COM/WinRT detail the crash writers emit, the
///     device-removal guard on the confirmed surface-resize crash path, and the Win2D bitmap
///     appliers routed through <c>WorldMapControl.LogUiThreadFault</c>. All of this lives in the
///     Windows-only GUI TFM (not built by the test target), so these pins read the source text.
///     Background: a real 0xC000027B (stowed E_UNEXPECTED) crash produced a WER dump that could
///     not be matched to the shared, timestamp-less GUI log — the NEXT crash must self-diagnose.
/// </summary>
public sealed class CrashCaptureSourceContractTests
{
    private static string ReadGuiEntryPoint()
    {
        return SourceContract.ReadAppSource("GuiEntryPoint.cs");
    }

    private static string ReadFalloutApp()
    {
        return SourceContract.ReadSource("src", "BethesdaMultitool", "App.xaml.cs");
    }

    private static string ConfigureDiagnosticsRegion()
    {
        return SourceContract.Extract(
            ReadGuiEntryPoint(),
            "private static void ConfigureDiagnostics()",
            "private static void OnUnhandledException");
    }

    // ---- 1. Always-on crash capture ------------------------------------------------------------

    [Fact]
    public void LogOpensBeforeApplicationStart()
    {
        // The whole point of moving the open out of MainWindow: a crash during app construction
        // (XAML metadata, App ctor, first window) must already be captured. Handlers install after
        // the log is open so their writes land in the file.
        SourceContract.AssertOrder(ReadGuiEntryPoint(),
            "ConfigureDiagnostics();",
            "AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;",
            "TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;",
            "Application.Start(");
    }

    [Fact]
    public void ConfigureDiagnosticsNeverEarlyOutsBeforeOpeningTheLog()
    {
        // The old body returned immediately when FALLOUT_GUI_LOG was unset — 122 sessions then
        // shared one MainWindow-opened file. The method must now reach SetLogFile on every path.
        var region = ConfigureDiagnosticsRegion();
        Assert.DoesNotContain("return;", region, StringComparison.Ordinal);
        Assert.Contains("logger.SetLogFile(fullPath);", region, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultLogIsPerProcessWithForcedTimestamps()
    {
        var region = ConfigureDiagnosticsRegion();

        // Per-process file name: concurrent GUI sessions stop interleaving, and a WER record's
        // pid picks the right log.
        Assert.Contains("BethesdaMultitool-gui-{Environment.ProcessId}.log",
            region, StringComparison.Ordinal);

        // Timestamps forced ON unconditionally (not gated on the env var): without them a log
        // cannot be matched to a WER fault time.
        SourceContract.AssertOrder(region,
            "logger.IncludeTimestamp = true;",
            "logger.SetLogFile(fullPath);");
    }

    [Fact]
    public void MainWindowOnlyOpensAFallbackLogWhenNoSinkExists()
    {
        // MainWindow's old unconditional SetLogFile would dispose the entry path's writer and
        // reopen a SHARED file, undoing both guarantees. It must now be guarded and use the same
        // per-process, timestamped shape.
        var source = SourceContract.ReadAppSource("MainWindow.xaml.cs");
        SourceContract.AssertOrder(source,
            "if (!BethesdaMultitool.Core.Diagnostics.Logger.Instance.HasLogFile)",
            "BethesdaMultitool-gui-{Environment.ProcessId}.log",
            "IncludeTimestamp = true;",
            ".SetLogFile(logPath);");
        Assert.DoesNotContain("\"BethesdaMultitool-gui.log\"", source, StringComparison.Ordinal);
    }

    // ---- Crash writers: COM HResult + WinRT restricted-error info ------------------------------

    [Fact]
    public void CrashLogEmitsWinRtDetail()
    {
        // GuiEntryPoint.CrashLog serves the AppDomain.UnhandledException and
        // TaskScheduler.UnobservedTaskException handlers; it must surface the COMException
        // HResult / restricted-error description to console AND file.
        var source = ReadGuiEntryPoint();
        var region = SourceContract.Extract(source,
            "private static void CrashLog(",
            "// --- Native (SEH) fault logging");
        Assert.Contains("WinRtErrorInfo.Describe(ex)", region, StringComparison.Ordinal);
        SourceContract.AssertOrder(region,
            "Console.WriteLine($\"[FATAL] {winRtDetail}\");",
            "log.Error(\"[FATAL] {0}\", winRtDetail);");
    }

    [Fact]
    public void InnerExceptionWalkersEmitHexHResultAndRestrictedError()
    {
        // FalloutApp's walkers serve Application.UnhandledException. Each layer already prints a
        // hex HRESULT; the restricted-error description (often the ONLY real diagnostic on a
        // stowed WinRT failure) must ride along in both the file and console variants.
        var source = ReadFalloutApp();

        var fileWalker = SourceContract.Extract(source,
            "private static void LogInnerExceptions(",
            "protected override void OnLaunched");
        SourceContract.AssertOrder(fileWalker,
            "HRESULT=0x{1:X8}",
            "WinRtErrorInfo.RestrictedDescription(ex)",
            "RestrictedError: {1}");

        var consoleWalker = SourceContract.Extract(source,
            "internal static void PrintInnerExceptions(",
            "\n}");
        SourceContract.AssertOrder(consoleWalker,
            "HRESULT=0x{ex.HResult:X8}",
            "WinRtErrorInfo.RestrictedDescription(ex)",
            "RestrictedError: {restricted}");
    }

    [Fact]
    public void WinRtErrorInfoReadsRestrictedDataKeysAndNeverThrows()
    {
        var source = SourceContract.ReadAppSource("WinRtErrorInfo.cs");
        // The projection stashes IRestrictedErrorInfo strings on Exception.Data under these keys.
        Assert.Contains("\"RestrictedDescription\"", source, StringComparison.Ordinal);
        Assert.Contains("\"RestrictedErrorReference\"", source, StringComparison.Ordinal);
        // COMException HResult rendered in hex, and both public entry points are catch-all safe
        // (a crash writer must never throw).
        Assert.Contains("COMException HResult=0x{com.HResult:X8}", source, StringComparison.Ordinal);
        Assert.True(SourceContract.CountOccurrences(source, "catch") >= 2,
            "Both WinRtErrorInfo entry points must be wrapped so crash writers can never throw.");
    }

    // ---- 2. Confirmed crash path: device removal during surface resize -------------------------

    [Fact]
    public void TryEnsureSurfaceCatchesDeviceRemovalOnTheResizePath()
    {
        var region = SourceContract.Extract(
            SourceContract.ReadAppSource("WorldView3DControl.Lifecycle.cs"),
            "private void TryEnsureSurface()",
            "private void EnsureDepthSrv()");

        // The resize branch (WaitForGpuIdle + Resize, reached from XAML SizeChanged /
        // CompositionScaleChanged / Loaded) must catch SharpGenException, attribute the removal,
        // null the surface so the next call recreates it, and surface a status.
        SourceContract.AssertOrder(region,
            "_commandRecorder12!.WaitForGpuIdle();",
            "_surface12.Resize(width, height);",
            "catch (SharpGen.Runtime.SharpGenException",
            "LogDeviceRemovedDiagnostics(\"surface-resize\")",
            "_surface12 = null;",
            "ShowStatus(");
    }

    [Fact]
    public void SwapChainResizeMirrorsCreatesSharpGenCatch()
    {
        var source = SourceContract.ReadSource("src", "BethesdaMultitool", "Core", "Formats",
            "Nif", "Rendering", "Gpu", "D3D12", "GpuSwapChainSurface12.cs");
        var region = SourceContract.Extract(source,
            "public void Resize(uint width, uint height)",
            "public bool TryPrepareWaterOpaqueSnapshot");

        // Same cleanup Create has (log + release partial state), then rethrow so the owner
        // observes the dead surface and recreates it.
        SourceContract.AssertOrder(region,
            "_swapChain.ResizeBuffers(",
            "catch (SharpGenException ex)",
            "Log.Warn(\"GpuSwapChainSurface12.Resize failed",
            "throw;");

        // Dispose must tolerate the cleared back-buffer slots the failed resize leaves behind.
        Assert.Contains("foreach (var b in _backBuffers) b?.Dispose();",
            source, StringComparison.Ordinal);
    }

    // ---- 3. Win2D bitmap appliers routed through the UI fault helper ---------------------------

    [Fact]
    public void WorldMapBitmapAppliersRouteThroughLogUiThreadFault()
    {
        // Every DispatcherQueue.TryEnqueue callback that runs CanvasBitmap.CreateFromBytes must
        // swallow-and-log via LogUiThreadFault — Win2D throws on device-lost, and an unhandled
        // UI-thread exception terminates the process.
        var bitmaps = SourceContract.ReadAppSource("WorldMapControl.Bitmaps.cs");
        Assert.Contains("LogUiThreadFault(\"ApplyWorldWaterResult\"", bitmaps, StringComparison.Ordinal);
        Assert.Contains("LogUiThreadFault(\"ApplyWorldBitmapBuildResult\"", bitmaps, StringComparison.Ordinal);

        var streaming = SourceContract.ReadAppSource("WorldMapControl.TerrainStreaming.cs");
        Assert.Contains("LogUiThreadFault(\"ApplyTerrainAggregateResult\"", streaming, StringComparison.Ordinal);

        // The top-down overlay apply resumes on the UI thread inside its own catch-all; it must
        // also report through the persistent fault logger (the profiler trace gate is often off).
        var topDown = SourceContract.ReadAppSource("WorldMapControl.TopDown.cs");
        Assert.Contains("LogUiThreadFault(\"TopDownOverlayApply\"", topDown, StringComparison.Ordinal);
    }

    [Fact]
    public void WiredAppliersWrapTheEnqueuedBodyNotTheEnqueueCall()
    {
        // The try/catch must live INSIDE the TryEnqueue lambda — wrapping the TryEnqueue call
        // itself would not catch anything (the callback runs later, on the UI thread).
        var bitmaps = SourceContract.ReadAppSource("WorldMapControl.Bitmaps.cs");
        foreach (var applier in new[] { "ApplyWorldWaterResult(bmp", "ApplyWorldBitmapBuildResult(result" })
        {
            var callOffset = bitmaps.IndexOf(applier, StringComparison.Ordinal);
            Assert.True(callOffset > 0, $"Missing applier call `{applier}`.");
            var window = bitmaps[Math.Max(0, callOffset - 200)..callOffset];
            SourceContract.AssertOrder(window, "TryEnqueue(()", "try");
        }

        var streaming = SourceContract.ReadAppSource("WorldMapControl.TerrainStreaming.cs");
        var aggregateOffset = streaming.IndexOf(
            "ApplyTerrainAggregateResult(result, worldspaceFormId);", StringComparison.Ordinal);
        Assert.True(aggregateOffset > 0, "Missing aggregate applier call.");
        SourceContract.AssertOrder(
            streaming[Math.Max(0, aggregateOffset - 200)..aggregateOffset],
            "TryEnqueue(()", "try");
    }
}