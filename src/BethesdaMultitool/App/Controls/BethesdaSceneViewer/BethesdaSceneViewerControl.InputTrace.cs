using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace BethesdaMultitool;

public sealed partial class BethesdaSceneViewerControl
{
    // Fixed at control creation. The ordinary path allocates no trace and performs no trace I/O.
    private readonly NativeViewerInputTrace? _inputTrace =
        Environment.GetEnvironmentVariable("FALLOUT_VIEWER_NATIVE_INPUT_TRACE") == "1"
            ? new NativeViewerInputTrace()
            : null;

    private Vector2 _traceAcceptedDelta;
    private long _traceCaptureSerial;
    private long _traceFrameSerial;
    private long _traceGestureSerial;
    private long _traceSceneEpoch;

    private object? TraceCameraState() => _inputTrace is null
        ? null
        : new
        {
            azimuthDegrees = _camera.AzimuthDegrees,
            elevationDegrees = _camera.ElevationDegrees,
            target = TraceVector(_camera.Target),
            distance = _camera.Distance,
            fieldOfViewRadians = _camera.FieldOfViewRadians
        };

    private object? TraceSceneState() => _inputTrace is null
        ? null
        : new
        {
            sceneEpoch = _traceSceneEpoch,
            sourceLabel = _scene?.SourceLabel,
            purpose = _scene?.Purpose.ToString(),
            game = _scene?.Game.ToString()
        };

    private static float[] TraceVector(Vector3 value) => [value.X, value.Y, value.Z];

    private static float[] TraceMatrix(Matrix4x4 value) =>
    [
        value.M11, value.M12, value.M13, value.M14,
        value.M21, value.M22, value.M23, value.M24,
        value.M31, value.M32, value.M33, value.M34,
        value.M41, value.M42, value.M43, value.M44
    ];

    private void TracePointer(
        string phase,
        PointerRoutedEventArgs e,
        PointerPoint point,
        string reason,
        bool? captureAdmitted = null,
        Vector2? acceptedDelta = null,
        object? cameraBefore = null)
    {
        if (_inputTrace is null) return;
        var properties = point.Properties;
        _inputTrace.Write(phase, new
        {
            scene = TraceSceneState(),
            gestureSerial = _traceGestureSerial,
            pointerId = e.Pointer.PointerId,
            capturedPointerId = _capturedPointerId,
            gesture = _pointerGesture.ToString(),
            reason,
            captureAdmitted,
            left = properties.IsLeftButtonPressed,
            middle = properties.IsMiddleButtonPressed,
            right = properties.IsRightButtonPressed,
            originalSourceType = e.OriginalSource?.GetType().FullName,
            originalSourceName = (e.OriginalSource as FrameworkElement)?.Name,
            logicalPosition = new[] { point.Position.X, point.Position.Y },
            acceptedLogicalDelta = acceptedDelta is { } delta ? new[] { delta.X, delta.Y } : null,
            accumulatedAcceptedLogicalDelta = new[] { _traceAcceptedDelta.X, _traceAcceptedDelta.Y },
            cameraBefore,
            cameraAfter = TraceCameraState()
        });
    }

    private void TraceRejectedMove(PointerRoutedEventArgs e, string reason)
    {
        if (_inputTrace is null) return;
        var point = e.GetCurrentPoint(RenderPanel);
        var properties = point.Properties;
        if (_capturedPointerId is null &&
            _pointerGesture == BethesdaSceneViewerPointerGesture.None &&
            !properties.IsLeftButtonPressed &&
            !properties.IsMiddleButtonPressed &&
            !properties.IsRightButtonPressed)
        {
            return; // Passive hover is deliberately absent, not a dropped input event.
        }

        TracePointer("pointer-move-rejected", e, point, reason);
    }

    private void TraceCaptureRequest(BethesdaSceneViewerCaptureRequest12 request)
    {
        if (_inputTrace is null) return;
        request.TraceCaptureId = ++_traceCaptureSerial;
        _inputTrace.Write("capture-request-accepted", new
        {
            captureId = request.TraceCaptureId,
            scene = TraceSceneState(),
            camera = TraceCameraState(),
            lastRenderAttempt = _traceFrameSerial
        });
    }

    private void TraceCaptureFrame(
        BethesdaSceneViewerCaptureRequest12? request,
        in BethesdaSceneViewerCameraFrame camera,
        BethesdaViewerScene renderedScene,
        int recorderFrameIndex,
        float deltaSeconds,
        object? cameraBeforeFit)
    {
        if (_inputTrace is null || request is null) return;
        request.TraceFrameSerial = _traceFrameSerial;
        request.TraceRenderSceneEpoch = _traceSceneEpoch;
        _inputTrace.Write("capture-render-begin", new
        {
            captureId = request.TraceCaptureId,
            renderAttempt = request.TraceFrameSerial,
            recorderFrameIndex,
            scene = TraceSceneState(),
            renderedScene = new
            {
                renderedScene.SourceLabel,
                purpose = renderedScene.Purpose.ToString(),
                game = renderedScene.Game.ToString(),
                matchesCurrentSceneReference = ReferenceEquals(renderedScene, _scene)
            },
            deltaSeconds,
            cameraBeforeFit,
            cameraAfterFit = TraceCameraState(),
            immutableCamera = new
            {
                view = TraceMatrix(camera.View),
                projection = TraceMatrix(camera.Projection),
                viewProjection = TraceMatrix(camera.ViewProjection),
                position = TraceVector(camera.Position),
                target = TraceVector(camera.Target),
                forward = TraceVector(camera.Forward),
                right = TraceVector(camera.Right),
                up = TraceVector(camera.Up),
                nearPlane = camera.NearPlane,
                farPlane = camera.FarPlane
            },
            matrixLayout = "row-major-M11-through-M44"
        });
    }

    // Safe on the readback worker: use frozen request IDs only, never live WinUI/camera/scene fields.
    private void TraceCapturePhase(
        BethesdaSceneViewerCaptureRequest12 request,
        string phase,
        ulong? fenceValue = null,
        string? failure = null)
    {
        if (_inputTrace is null) return;
        _inputTrace.Write(phase, new
        {
            captureId = request.TraceCaptureId,
            renderAttempt = request.TraceFrameSerial,
            renderSceneEpoch = request.TraceRenderSceneEpoch,
            fenceValue,
            failure
        });
    }

    private sealed class NativeViewerInputTrace
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        private readonly BethesdaViewerInputTraceBudget _budget = new();
        private readonly string _controlId = Guid.NewGuid().ToString("N");
        private readonly Lock _gate = new();
        private bool _failed;

        internal void Write(string eventName, object data)
        {
            lock (_gate)
            {
                if (_failed || !_budget.TryTake(out var sequence, out var truncation)) return;
                try
                {
                    Log.Info("BethesdaSceneViewer.InputTrace: {0}", JsonSerializer.Serialize(new
                    {
                        schemaVersion = 1,
                        controlId = _controlId,
                        sequence,
                        utc = DateTimeOffset.UtcNow,
                        stopwatchTicks = Stopwatch.GetTimestamp(),
                        stopwatchFrequency = Stopwatch.Frequency,
                        eventName = truncation ? "trace-truncated" : eventName,
                        data = truncation
                            ? new { recordLimit = BethesdaViewerInputTraceBudget.DefaultRecordLimit }
                            : data
                    }, JsonOptions));
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or
                                               ObjectDisposedException)
                {
                    // A failed diagnostic sink must not fault the renderer. Missing/truncated
                    // completion records invalidate negative claims about delivery or resets.
                    _failed = true;
                    Debug.WriteLine($"Native viewer input trace stopped: {ex}");
                }
            }
        }
    }
}
