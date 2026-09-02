using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class NativeBethesdaViewerCaptureSourceContractTests
{
    private static string CaptureSource() => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "App", "Controls", "BethesdaSceneViewer",
        "BethesdaSceneViewerControl.Capture.cs");

    private static string LifecycleSource() => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "App", "Controls", "BethesdaSceneViewer",
        "BethesdaSceneViewerControl.Lifecycle.cs");

    [Fact]
    public void CaptureCopiesTheTonemappedLiveBackBufferInItsPresentedFrame()
    {
        var lifecycle = SourceContract.Extract(
            LifecycleSource(),
            "private void RenderNativeFrame(",
            "private static byte[] CreateNeutralAtmosphereConstants()");
        SourceContract.AssertOrder(
            lifecycle,
            "session.Render(frame);",
            "surface.ResolveTo(commandList, backBuffer);",
            "capture.RecordCopy(commandList, backBuffer);",
            "recording.Submit(capture);",
            "submittedFenceValue = submission.FenceValue;",
            "surface.Present();");

        var copy = SourceContract.Extract(
            CaptureSource(),
            "internal void RecordCopy(",
            "internal void MarkSubmitted()");
        SourceContract.AssertOrder(
            copy,
            "ResourceStates.RenderTarget,",
            "ResourceStates.CopySource);",
            "commandList.CopyTextureRegion(",
            "new TextureCopyLocation(backBuffer)",
            "ResourceStates.CopySource,",
            "ResourceStates.Present);");
        Assert.Contains("GpuSwapChainSurface12.BackBufferFormat", CaptureSource(), StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureUsesOneSharedFrameAndAnAsynchronousFenceReadback()
    {
        var source = CaptureSource();
        var api = SourceContract.Extract(
            source,
            "internal Task<BethesdaSceneViewerFrameCapture> CaptureFrameAsync(",
            "internal async Task<byte[]> CapturePngAsync(");
        SourceContract.AssertOrder(
            api,
            "if (_captureRequest is not null)",
            "Only one native Bethesda viewer capture may be pending",
            "_captureRequest = request;",
            "InvalidateViewport();");

        var worker = SourceContract.Extract(
            source,
            "private void SubmitCaptureReadback(",
            "private void OnCaptureCancellationRequested(");
        SourceContract.AssertOrder(
            worker,
            "_ = Task.Run(() =>",
            "request.WaitForFence(fenceValue);",
            "request.ReadbackToBytes()",
            "request.Dispose();");

        Assert.DoesNotContain("WaitForGpuIdle", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildGlb", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ExportViewerSceneToGlb", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GpuOffscreenSceneTarget12", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Write", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PixelAndPngApisKeepExactPackingAndOpaquePanelSemantics()
    {
        var source = CaptureSource();
        var readback = SourceContract.Extract(
            source,
            "internal byte[] ReadbackToBytes()",
            "internal void TrySetResult(");
        SourceContract.AssertOrder(
            readback,
            "var rowBytes = checked(PixelWidth * 4);",
            "var pixels = new byte[checked(rowBytes * PixelHeight)];",
            "Marshal.Copy(",
            "return pixels;");

        var png = SourceContract.Extract(
            source,
            "internal byte[] EncodeOpaquePng()",
            "return PngWriter.EncodeRgba(rgba, PixelWidth, PixelHeight);");
        Assert.Contains("rgba[i] = BgraPixels[i + 2];", png, StringComparison.Ordinal);
        Assert.Contains("rgba[i + 2] = BgraPixels[i];", png, StringComparison.Ordinal);
        Assert.Contains("rgba[i + 3] = 255;", png, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellationAndTeardownDoNotReleaseSubmittedGpuWorkEarly()
    {
        var source = CaptureSource();
        Assert.Contains("TaskCreationOptions.RunContinuationsAsynchronously", source, StringComparison.Ordinal);
        Assert.Contains("ownedFence = frameFence.QueryInterface<ID3D12Fence>();", source,
            StringComparison.Ordinal);
        Assert.Contains("if (_captureRequest is { HasGpuWork: false } request)", source,
            StringComparison.Ordinal);
        Assert.Contains("Submitted copies retain", source, StringComparison.Ordinal);
        Assert.Contains("CancelCaptureForControlDisposal();", LifecycleSource(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteWithoutFenceTransfersCaptureLifetimeBeforeSurfacingTheFailure()
    {
        var lifecycle = SourceContract.Extract(
            LifecycleSource(),
            "private void RenderNativeFrame(",
            "private static byte[] CreateNeutralAtmosphereConstants()");
        SourceContract.AssertOrder(
            lifecycle,
            "recording.Submit(capture);",
            "submission.CommandListMayHaveReachedQueue",
            "graphics.TerminalizeDeviceAfterUnfencedSubmission();",
            "submission.ThrowIfFailed();",
            "FailUnfencedCaptureRequest(");

        var failCapture = SourceContract.Extract(
            CaptureSource(),
            "private void FailUnfencedCaptureRequest(",
            "/// <summary>Fails a request that has not entered a submitted GPU frame.");
        Assert.Contains("request.TrySetException(exception);", failCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("request.Dispose();", failCapture, StringComparison.Ordinal);

        var recorder = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuCommandRecorder12.cs");
        var endFrame = SourceContract.Extract(
            recorder,
            "internal GpuCommandSubmissionOutcome12 EndFrameWithOutcome(",
            "public void WaitForGpuIdle()");
        SourceContract.AssertOrder(
            endFrame,
            "_gpu.DirectQueue.ExecuteCommandList(CommandList);",
            "_gpu.DirectQueue.Signal(_gpu.FrameFence, signalValue).CheckError();",
            "FinalizeFailedSubmission(ex, commandListMayHaveReachedQueue: true, retainIfUnfenced)");
        Assert.Contains("_unfencedSubmissionRetirements.Add(retainIfUnfenced);", recorder,
            StringComparison.Ordinal);
        Assert.Contains("ThrowIfSubmissionPoisoned();", recorder, StringComparison.Ordinal);
        Assert.Contains("_gpu.TryForceDeviceRemoval(\"command-recorder-teardown\")", recorder,
            StringComparison.Ordinal);

        var context = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "BethesdaSceneViewer",
            "BethesdaSceneViewerGraphicsContext12.cs");
        var terminalize = SourceContract.Extract(
            context,
            "private void TerminalizeDeviceAfterUnfencedSubmissionCore(",
            "private static void DisposeOwnedNoThrow(");
        SourceContract.AssertOrder(
            terminalize,
            "Gpu.TryForceDeviceRemoval(context)",
            "Gpu.Dispose();",
            "_deviceTerminal = true;",
            "Recorder.DisposeAfterGpuIdleAttempt();");

        var gpu = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuDevice12.cs");
        SourceContract.AssertOrder(
            gpu,
            "Device.QueryInterfaceOrNull<ID3D12Device5>()",
            "device5.RemoveDevice();",
            "public void Dispose()");
    }

    [Fact]
    public void SharedContextWaitsOnceAndStillReleasesEveryOwnerAfterDeviceLoss()
    {
        var context = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "BethesdaSceneViewer",
            "BethesdaSceneViewerGraphicsContext12.cs");
        var dispose = SourceContract.Extract(context, "public void Dispose()", "private static void DisposeOwnedNoThrow(");
        SourceContract.AssertOrder(
            dispose,
            "Recorder.WaitForGpuIdle();",
            "catch (Exception ex)",
            "DisposeOwnedNoThrow(DeletionQueue",
            "Recorder.DisposeAfterGpuIdleAttempt();",
            "DisposeOwnedNoThrow(Gpu");
        Assert.DoesNotContain("Recorder.Dispose();", dispose, StringComparison.Ordinal);

        var idle = SourceContract.Extract(
            context,
            "internal void WaitForGpuIdle()",
            "internal void TerminalizeDeviceAfterUnfencedSubmission()");
        SourceContract.AssertOrder(
            idle,
            "Recorder.WaitForGpuIdle();",
            "catch",
            "TerminalizeDeviceAfterUnfencedSubmissionCore(\"idle-wait-failure\");",
            "throw;");
    }
}
