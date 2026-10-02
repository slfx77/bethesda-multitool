using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using Slfx77.Multitool.WinUI.Rendering;

namespace BethesdaMultitool;

public sealed partial class BethesdaSceneViewerControl
{
    private BethesdaNativeViewportRenderer? _viewportAdapter;
    private bool _inputEventsAttached;
    private long _nativeFrameTimestamp;

    // Borrowed aliases preserve all existing camera/input/capture code. The shared host exclusively
    // owns the panel, its graphics lease and surface; these properties never independently dispose them.
    private SwapChainPanel RenderPanel => Viewport.Panel;
    private bool _isLoaded => Viewport.IsMounted;
    private bool _isPresentationActive => Viewport.IsPresentationActive;
    private bool _hasPresentedFrame => Viewport.HasPresentedFrame;
    private bool _renderingFrame => Viewport.Session?.IsOperating == true;
    private BethesdaSceneViewerGraphicsContext12.BethesdaSceneViewerGraphicsLease12? _graphicsLease =>
        (Viewport.Session?.Graphics as BethesdaNativeViewportGraphicsLease)?.Lease;
    private Core.Formats.Nif.Rendering.Gpu.D3D12.GpuSwapChainSurface12? _surface =>
        (Viewport.Session?.Surface as BethesdaNativeViewportSurface)?.Surface;

    /// <summary>Publishes current presentation readiness after the shared host changes state.</summary>
    /// <param name="sender">Shared viewport owning this callback.</param>
    /// <param name="args">Current viewport notification.</param>
    private void OnViewportStateChanged(object? sender, EventArgs args) => NotifyObservableRenderStateChanged();

    /// <summary>Updates Bethesda animation controls after successful native presentation.</summary>
    /// <param name="sender">Shared viewport owning this callback.</param>
    /// <param name="args">Current viewport notification.</param>
    private void OnViewportFrameCompleted(object? sender, EventArgs args)
    {
        if (_disposed) { return; }
        SynchronizeRenderState();
        if (_renderSession is not null)
        {
            UpdateAnimationTimeUiThrottled(_renderSession,
                _nativeFrameTimestamp == 0 ? Stopwatch.GetTimestamp() : _nativeFrameTimestamp);
        }
    }

    /// <summary>Cancels pointer and capture intent when presentation becomes inactive.</summary>
    /// <param name="sender">Shared viewport owning this callback.</param>
    /// <param name="args">Current viewport notification.</param>
    private void OnViewportPresentationSuspended(object? sender, EventArgs args)
    {
        ResetPointerGesture();
        CancelPendingCapture("The native Bethesda viewer became hidden before capture could run.");
    }

    /// <summary>Invalidates capture and presented-frame proof before shared surface retirement.</summary>
    /// <param name="sender">Shared viewport owning this callback.</param>
    /// <param name="args">Current viewport notification.</param>
    private void OnViewportSurfaceRetiring(object? sender, EventArgs args)
    {
        ResetPresentedFrameGate();
        CancelPendingCapture("The native Bethesda presentation surface was released before capture could run.");
    }

    /// <summary>Reports a shared native failure through the existing Bethesda fault state.</summary>
    /// <param name="sender">Shared viewport owning this callback.</param>
    /// <param name="args">Current viewport notification.</param>
    private void OnViewportFailed(object? sender, NativeViewportFailureEventArgs args) =>
        SetFaulted("The native Bethesda viewport failed to " + args.Operation + ".", args.Exception);

}
