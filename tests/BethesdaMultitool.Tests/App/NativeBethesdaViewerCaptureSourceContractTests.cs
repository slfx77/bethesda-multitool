using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class NativeBethesdaViewerCaptureSourceContractTests
{
    private static string CaptureSource()
    {
        return SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "BethesdaSceneViewer",
            "BethesdaSceneViewerControl.Capture.cs");
    }

    private static string LifecycleSource()
    {
        return SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "BethesdaSceneViewer",
            "BethesdaSceneViewerControl.Lifecycle.cs");
    }

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
            "surface.ResolveTo(graphics.Recorder, backBuffer);",
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
            "private void CancelPendingCapture(string reason)");
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
            "_native.EndFrame(retainIfUnfenced);",
            "result.Succeeded, result.CommandListMayHaveReachedQueue, result.FenceValue, result.SubmissionFailure");
        var nativeRecorder = SourceContract.ReadSource(
            "shared", "Multitool.Shared", "src", "Slfx77.Multitool.WinUI.Direct3D12.Shaders",
            "NativeFrameRecorder.cs");
        var nativeEndFrame = SourceContract.Extract(
            nativeRecorder,
            "public NativeFrameSubmissionResult EndFrame(",
            "public NativeFrameAbortResult AbortFrame()");
        SourceContract.AssertOrder(
            nativeEndFrame,
            "conditional = new NativeFrameConditionalLifetime(retainIfUnfenced);",
            "_submissions.Retain(conditional,",
            "_submissions.MarkSubmissionPossible();",
            "_queue.ExecuteCommandList(_commands);",
            "_queue.Signal(_fence, signal).CheckError();",
            "FinishFailedSubmission(failure, true, conditional)");
        var finalizeFailure = SourceContract.Extract(
            nativeRecorder,
            "private NativeFrameSubmissionResult FinishFailedSubmission(",
            "private Exception? NotifyOutcome(");
        SourceContract.AssertOrder(
            finalizeFailure,
            "possible ? SubmissionOutcome.SubmissionUncertain : SubmissionOutcome.DefinitelyAbandoned",
            "if (possible) conditional?.TakeOwnership();",
            "NotifyOutcome(outcome);",
            "_submissionPoisoned = true;",
            "AdvanceFrame();");
        Assert.Contains("VerifyRecordingAdmission();", nativeRecorder, StringComparison.Ordinal);
        Assert.Contains("_gpu.TryForceDeviceRemoval(\"command-recorder-teardown\")", recorder,
            StringComparison.Ordinal);

        var context = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "BethesdaSceneViewer",
            "BethesdaSceneViewerGraphicsContext12.cs");
        var terminalize = SourceContract.Extract(
            context,
            "private void TerminalizeDeviceAfterUnfencedSubmissionCore(",
            "private static BethesdaSceneViewerGraphicsContext12 Create()");
        SourceContract.AssertOrder(
            terminalize,
            "Gpu.TryForceDeviceRemoval(context)",
            "throw new InvalidOperationException(",
            "_deviceTerminal = true;");
        Assert.DoesNotContain("Gpu.Dispose()", terminalize, StringComparison.Ordinal);

        var gpu = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuDevice12.cs");
        var removeDevice = SourceContract.Extract(
            gpu,
            "internal bool TryForceDeviceRemoval(string context)",
            "public void PumpDebugMessages()");
        SourceContract.AssertOrder(
            removeDevice,
            "Device.QueryInterfaceOrNull<ID3D12Device5>()",
            "if (device5 is null)",
            "return false;",
            "device5.RemoveDevice();",
            "return true;");
    }

    [Fact]
    public void SharedContextRequiresProofAndKeepsDependencyOrderedRetirement()
    {
        var context = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "BethesdaSceneViewer",
            "BethesdaSceneViewerGraphicsContext12.cs");
        var dispose =
            SourceContract.Extract(context, "public void Dispose()", "private static BethesdaSceneViewerGraphicsContext12 Create()");
        // Source-level ownership wiring only; shared executable tests verify retry behavior.
        SourceContract.AssertOrder(dispose, "Recorder.WaitForGpuIdle();", "new RetiredResourceDisposal(");
        Assert.Contains("Recorder.DisposeAfterGpuIdleAttempt()", dispose, StringComparison.Ordinal);
        Assert.Contains("_retiredResources.Dispose();", dispose, StringComparison.Ordinal);
        Assert.DoesNotContain("DisposeOwnedNoThrow", dispose, StringComparison.Ordinal);
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
