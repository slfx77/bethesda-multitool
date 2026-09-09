using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace BethesdaMultitool;

public sealed partial class BethesdaSceneViewerControl
{
    private void OnRenderPanelPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(RenderPanel);
        var properties = point.Properties;
        var gesture = properties switch
        {
            { IsLeftButtonPressed: true } => BethesdaSceneViewerPointerGesture.Orbit,
            { IsMiddleButtonPressed: true } or { IsRightButtonPressed: true } =>
                BethesdaSceneViewerPointerGesture.Pan,
            _ => BethesdaSceneViewerPointerGesture.None
        };
        if (_inputTrace is not null)
        {
            _traceGestureSerial++;
            _traceAcceptedDelta = Vector2.Zero;
        }

        var cameraBefore = TraceCameraState();
        if (_inputTrace is not null)
        {
            TracePointer("pointer-press-enter", e, point, $"requested-gesture-{gesture}",
                cameraBefore: cameraBefore);
        }

        if (gesture == BethesdaSceneViewerPointerGesture.None)
        {
            TracePointer("pointer-press", e, point, "no-button-gesture", cameraBefore: cameraBefore);
            return;
        }

        var captureAdmitted = RenderPanel.CapturePointer(e.Pointer);
        if (!captureAdmitted)
        {
            TracePointer("pointer-press", e, point, "capture-pointer-failed",
                captureAdmitted: false, cameraBefore: cameraBefore);
            return;
        }

        _capturedPointerId = e.Pointer.PointerId;
        _pointerGesture = gesture;
        _previousPointerPosition = new Vector2(
            (float)point.Position.X,
            (float)point.Position.Y);
        TracePointer("pointer-press", e, point, "accepted",
            captureAdmitted: true, cameraBefore: cameraBefore);
        RenderPanel.Focus(FocusState.Pointer);
        e.Handled = true;
    }

    private void OnRenderPanelPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_capturedPointerId != e.Pointer.PointerId ||
            _pointerGesture == BethesdaSceneViewerPointerGesture.None)
        {
            TraceRejectedMove(e, _pointerGesture == BethesdaSceneViewerPointerGesture.None
                ? "no-active-gesture"
                : "pointer-id-mismatch");
            return;
        }

        var point = e.GetCurrentPoint(RenderPanel);
        var current = new Vector2((float)point.Position.X, (float)point.Position.Y);
        var delta = current - _previousPointerPosition;
        _previousPointerPosition = current;
        if (delta == Vector2.Zero)
        {
            TracePointer("pointer-move-rejected", e, point, "zero-logical-delta");
            return;
        }

        var cameraBefore = TraceCameraState();

        if (_pointerGesture == BethesdaSceneViewerPointerGesture.Orbit)
        {
            _camera.Orbit(delta);
        }
        else
        {
            _camera.Pan(delta, (float)RenderPanel.ActualHeight);
        }

        if (_inputTrace is not null) _traceAcceptedDelta += delta;
        TracePointer("pointer-move-accepted", e, point, "accepted",
            acceptedDelta: delta, cameraBefore: cameraBefore);
        InvalidateViewport();
        e.Handled = true;
    }

    private void OnRenderPanelPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_inputTrace is not null)
        {
            TracePointer("pointer-release", e, e.GetCurrentPoint(RenderPanel),
                _capturedPointerId == e.Pointer.PointerId ? "matching-owner" : "pointer-id-mismatch");
        }

        if (_capturedPointerId != e.Pointer.PointerId) return;

        RenderPanel.ReleasePointerCapture(e.Pointer);
        ResetPointerGesture();
        e.Handled = true;
    }

    private void OnRenderPanelPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_inputTrace is not null)
        {
            TracePointer("pointer-capture-lost", e, e.GetCurrentPoint(RenderPanel),
                _capturedPointerId == e.Pointer.PointerId ? "matching-owner" : "pointer-id-mismatch");
        }

        if (_capturedPointerId == e.Pointer.PointerId)
        {
            ResetPointerGesture();
        }
    }

    private void OnRenderPanelPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var cameraBefore = TraceCameraState();
        _camera.Zoom(e.GetCurrentPoint(RenderPanel).Properties.MouseWheelDelta);
        _inputTrace?.Write("camera-wheel", new
        {
            scene = TraceSceneState(),
            wheelDelta = e.GetCurrentPoint(RenderPanel).Properties.MouseWheelDelta,
            cameraBefore,
            cameraAfter = TraceCameraState()
        });
        InvalidateViewport();
        e.Handled = true;
    }

    private void OnRenderPanelKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.R or VirtualKey.Home)) return;

        FrameScene(e.Key == VirtualKey.R ? "key-R" : "key-Home");
        e.Handled = true;
    }

    private void OnRenderPanelDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        FrameScene("double-tap");
        e.Handled = true;
    }

    private void ResetPointerGesture(
        [System.Runtime.CompilerServices.CallerMemberName]
        string traceReason = "")
    {
        _inputTrace?.Write("pointer-gesture-reset", new
        {
            reason = traceReason,
            scene = TraceSceneState(),
            capturedPointerId = _capturedPointerId,
            gesture = _pointerGesture.ToString(),
            gestureSerial = _traceGestureSerial,
            accumulatedAcceptedLogicalDelta = new[] { _traceAcceptedDelta.X, _traceAcceptedDelta.Y },
            camera = TraceCameraState()
        });
        _capturedPointerId = null;
        _pointerGesture = BethesdaSceneViewerPointerGesture.None;
        _previousPointerPosition = Vector2.Zero;
    }
}
