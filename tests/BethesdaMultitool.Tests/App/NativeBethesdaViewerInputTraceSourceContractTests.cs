using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

/// <summary>WinUI/D3D call-site pins; the record budget itself has executable Core tests.</summary>
public sealed class NativeBethesdaViewerInputTraceSourceContractTests
{
    [Fact]
    public void PointerAdmissionLogsTheSingleActualCaptureResultBeforeFocus()
    {
        var camera = SourceContract.ReadAppSource("BethesdaSceneViewerControl.Camera.cs");
        var pressed = SourceContract.Extract(camera,
            "private void OnRenderPanelPointerPressed(", "private void OnRenderPanelPointerMoved(");
        Assert.Equal(1, SourceContract.CountOccurrences(camera, "RenderPanel.CapturePointer(e.Pointer)"));
        SourceContract.AssertOrder(pressed,
            "if (gesture == BethesdaSceneViewerPointerGesture.None)",
            "\"no-button-gesture\"", "return;",
            "var captureAdmitted = RenderPanel.CapturePointer(e.Pointer);",
            "if (!captureAdmitted)", "\"capture-pointer-failed\"", "return;",
            "_capturedPointerId = e.Pointer.PointerId;", "captureAdmitted: true",
            "RenderPanel.Focus(FocusState.Pointer);", "e.Handled = true;");
        Assert.Contains("\"pointer-press-enter\"", pressed, StringComparison.Ordinal);
    }

    [Fact]
    public void MoveTraceSurroundsAcceptedCameraMathAndSuppressesPassiveHover()
    {
        var camera = SourceContract.ReadAppSource("BethesdaSceneViewerControl.Camera.cs");
        var moved = SourceContract.Extract(camera,
            "private void OnRenderPanelPointerMoved(", "private void OnRenderPanelPointerReleased(");
        SourceContract.AssertOrder(moved,
            "_capturedPointerId != e.Pointer.PointerId", "TraceRejectedMove(", "return;",
            "var delta = current - _previousPointerPosition;", "if (delta == Vector2.Zero)",
            "\"zero-logical-delta\"", "return;", "var cameraBefore = TraceCameraState();",
            "_camera.Orbit(delta);", "_camera.Pan(delta, (float)RenderPanel.ActualHeight);",
            "_traceAcceptedDelta += delta;", "TracePointer(\"pointer-move-accepted\"",
            "InvalidateViewport();");
        var trace = SourceContract.ReadAppSource("BethesdaSceneViewerControl.InputTrace.cs");
        var rejected = SourceContract.Extract(trace,
            "private void TraceRejectedMove(", "private void TraceCaptureRequest(");
        SourceContract.AssertOrder(rejected,
            "if (_inputTrace is null) return;", "if (_capturedPointerId is null &&",
            "_pointerGesture == BethesdaSceneViewerPointerGesture.None &&",
            "!properties.IsLeftButtonPressed &&", "!properties.IsMiddleButtonPressed &&",
            "!properties.IsRightButtonPressed)", "return;", "TracePointer(\"pointer-move-rejected\"");
    }

    [Fact]
    public void SceneResetRecordsKeepTheExistingCallsAndDistinctNpcReasons()
    {
        var control = SourceContract.ReadAppSource("BethesdaSceneViewerControl.xaml.cs");
        var setScene = SourceContract.Extract(control, "internal void SetScene(", "internal void ClearScene()");
        SourceContract.AssertOrder(setScene,
            "if (ReferenceEquals(_scene, scene))", "\"set-scene-same-instance\"", "return;",
            "var traceCameraBefore = TraceCameraState();", "_scene = scene;", "_traceSceneEpoch++;",
            "_camera.Frame(", "\"set-scene-frame-reset\"");
        Assert.Equal(1, SourceContract.CountOccurrences(setScene, "_camera.Frame("));
        var frameScene = SourceContract.Extract(control,
            "internal void FrameScene(", "internal new void InvalidateViewport()");
        SourceContract.AssertOrder(frameScene,
            "CallerMemberName", "var traceCameraBefore = TraceCameraState();", "_camera.Frame(",
            "\"frame-scene-reset\"", "cameraAfter = TraceCameraState()", "InvalidateViewport();");
        var npc = SourceContract.ReadAppSource("SingleFileTab.NpcBrowser.cs");
        Assert.Contains("NpcSceneViewer.FrameScene(\"npc-scene-publication\");", npc, StringComparison.Ordinal);
        Assert.Contains("NpcSceneViewer.FrameScene(\"npc-native-ready-promotion\");", npc, StringComparison.Ordinal);
        var camera = SourceContract.ReadAppSource("BethesdaSceneViewerControl.Camera.cs");
        Assert.Contains("FrameScene(\"double-tap\");", camera, StringComparison.Ordinal);
        Assert.Contains("FrameScene(e.Key == VirtualKey.R ? \"key-R\" : \"key-Home\");", camera,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureTraceUsesTheExistingImmutableCameraAndActualRenderSequence()
    {
        var lifecycle = SourceContract.ReadAppSource("BethesdaSceneViewerControl.Lifecycle.cs");
        var render = SourceContract.Extract(lifecycle,
            "private void RenderNativeFrame(", "private static byte[] CreateNeutralAtmosphereConstants()");
        Assert.Equal(1, SourceContract.CountOccurrences(lifecycle, "_camera.GetFrame("));
        SourceContract.AssertOrder(render,
            "var traceCameraBeforeFit = capture is not null ? TraceCameraState() : null;",
            "var camera = _camera.GetFrame(", "var frame = new BethesdaSceneViewerFrame12(",
            "TraceCaptureFrame(capture, camera, scene,", "session.Render(frame);", "\"capture-render-return\"",
            "surface.ResolveTo(graphics.Recorder, backBuffer);", "capture.RecordCopy(commandList, backBuffer);",
            "\"capture-copy-recorded\"", "recording.Submit(capture);",
            "submittedFenceValue = submission.FenceValue;", "\"capture-queue-submitted\"",
            "surface.Present();", "\"capture-present-return\"");
        var trace = SourceContract.ReadAppSource("BethesdaSceneViewerControl.InputTrace.cs");
        Assert.DoesNotContain(".GetFrame(", trace, StringComparison.Ordinal);
        Assert.DoesNotContain(".Frame(", trace, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveBasis(", trace, StringComparison.Ordinal);
        Assert.Contains("if (_inputTrace is null || request is null) return;", trace, StringComparison.Ordinal);
        Assert.Contains("view = TraceMatrix(camera.View)", trace, StringComparison.Ordinal);
        Assert.Contains("projection = TraceMatrix(camera.Projection)", trace, StringComparison.Ordinal);
        Assert.Contains("viewProjection = TraceMatrix(camera.ViewProjection)", trace, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(renderedScene, _scene)", trace, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadbackTraceKeepsFrozenRequestIdentityWithoutReadingUiState()
    {
        var capture = SourceContract.ReadAppSource("BethesdaSceneViewerControl.Capture.cs");
        var api = SourceContract.Extract(capture,
            "internal Task<BethesdaSceneViewerFrameCapture> CaptureFrameAsync(",
            "internal async Task<byte[]> CapturePngAsync(");
        SourceContract.AssertOrder(api,
            "_captureRequest = request;", "TraceCaptureRequest(request);",
            "request.RegisterCancellation(", "InvalidateViewport();");
        var worker = SourceContract.Extract(capture,
            "private void SubmitCaptureReadback(", "private void OnCaptureCancellationRequested(");
        SourceContract.AssertOrder(worker,
            "request.MarkSubmitted();", "\"capture-readback-start\"", "_ = Task.Run(() =>",
            "request.WaitForFence(fenceValue);", "request.ReadbackToBytes()",
            "\"capture-readback-bytes-produced\"", "request.Dispose();");
        Assert.DoesNotContain("TraceCameraState(", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("TraceSceneState(", worker, StringComparison.Ordinal);
        var trace = SourceContract.ReadAppSource("BethesdaSceneViewerControl.InputTrace.cs");
        var phase = SourceContract.Extract(trace,
            "private void TraceCapturePhase(", "private sealed class NativeViewerInputTrace");
        Assert.Contains("captureId = request.TraceCaptureId", phase, StringComparison.Ordinal);
        Assert.Contains("renderAttempt = request.TraceFrameSerial", phase, StringComparison.Ordinal);
        Assert.Contains("renderSceneEpoch = request.TraceRenderSceneEpoch", phase, StringComparison.Ordinal);
        Assert.DoesNotContain("_camera", phase, StringComparison.Ordinal);
        Assert.DoesNotContain("_scene", phase, StringComparison.Ordinal);
    }

    [Fact]
    public void TraceIsExplicitlyOptInAndBudgetAdmissionPrecedesSerialization()
    {
        var trace = SourceContract.ReadAppSource("BethesdaSceneViewerControl.InputTrace.cs");
        Assert.Contains("Environment.GetEnvironmentVariable(\"FALLOUT_VIEWER_NATIVE_INPUT_TRACE\") == \"1\"",
            trace, StringComparison.Ordinal);
        SourceContract.AssertOrder(trace,
            "lock (_gate)", "_budget.TryTake(out var sequence, out var truncation)",
            "JsonSerializer.Serialize(", "sequence,", "Stopwatch.GetTimestamp()",
            "eventName = truncation ? \"trace-truncated\" : eventName");
        Assert.DoesNotContain("Task.Delay(", trace, StringComparison.Ordinal);
        Assert.DoesNotContain("Thread.Sleep(", trace, StringComparison.Ordinal);
        Assert.DoesNotContain(".Focus(", trace, StringComparison.Ordinal);
        Assert.DoesNotContain("SetLogFile(", trace, StringComparison.Ordinal);
    }
}
