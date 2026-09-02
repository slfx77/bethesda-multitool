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
        var gesture = properties.IsLeftButtonPressed
            ? BethesdaSceneViewerPointerGesture.Orbit
            : properties.IsMiddleButtonPressed || properties.IsRightButtonPressed
                ? BethesdaSceneViewerPointerGesture.Pan
                : BethesdaSceneViewerPointerGesture.None;
        if (gesture == BethesdaSceneViewerPointerGesture.None ||
            !RenderPanel.CapturePointer(e.Pointer))
        {
            return;
        }

        _capturedPointerId = e.Pointer.PointerId;
        _pointerGesture = gesture;
        _previousPointerPosition = new Vector2(
            (float)point.Position.X,
            (float)point.Position.Y);
        RenderPanel.Focus(FocusState.Pointer);
        e.Handled = true;
    }

    private void OnRenderPanelPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_capturedPointerId != e.Pointer.PointerId ||
            _pointerGesture == BethesdaSceneViewerPointerGesture.None)
        {
            return;
        }

        var point = e.GetCurrentPoint(RenderPanel);
        var current = new Vector2((float)point.Position.X, (float)point.Position.Y);
        var delta = current - _previousPointerPosition;
        _previousPointerPosition = current;
        if (delta == Vector2.Zero) return;

        if (_pointerGesture == BethesdaSceneViewerPointerGesture.Orbit)
        {
            _camera.Orbit(delta);
        }
        else
        {
            _camera.Pan(delta, (float)RenderPanel.ActualHeight);
        }

        InvalidateViewport();
        e.Handled = true;
    }

    private void OnRenderPanelPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_capturedPointerId != e.Pointer.PointerId) return;

        RenderPanel.ReleasePointerCapture(e.Pointer);
        ResetPointerGesture();
        e.Handled = true;
    }

    private void OnRenderPanelPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_capturedPointerId == e.Pointer.PointerId)
        {
            ResetPointerGesture();
        }
    }

    private void OnRenderPanelPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        _camera.Zoom(e.GetCurrentPoint(RenderPanel).Properties.MouseWheelDelta);
        InvalidateViewport();
        e.Handled = true;
    }

    private void OnRenderPanelKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.R or VirtualKey.Home)) return;

        FrameScene();
        e.Handled = true;
    }

    private void OnRenderPanelDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        FrameScene();
        e.Handled = true;
    }

    private void ResetPointerGesture()
    {
        _capturedPointerId = null;
        _pointerGesture = BethesdaSceneViewerPointerGesture.None;
        _previousPointerPosition = Vector2.Zero;
    }
}
