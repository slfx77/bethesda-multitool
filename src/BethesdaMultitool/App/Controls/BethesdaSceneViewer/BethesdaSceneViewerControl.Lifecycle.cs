using System.Diagnostics;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace BethesdaMultitool;

public sealed partial class BethesdaSceneViewerControl
{
    // Keep the mandatory b3 upload aligned with NifHeadlessRenderer.BindFlatAtmosphere: ten base
    // vectors, four matrices, four shadow vectors, six directional-ambient vectors,
    // GrassSunColorScale, then ClipPlane. Zero lighting/fog fields select reference.frag's stable
    // 0.4 + 0.6*Lambert presentation light; the three non-zero W lanes retain HDR/emissive behavior
    // and disable clipping. A real contextual scene session may overwrite b3 after this baseline.
    private const int NeutralAtmosphereClipPlaneFloat4Slot = 37;
    private const int NeutralAtmosphereBytes = (NeutralAtmosphereClipPlaneFloat4Slot + 1) * 16;
    private const int EmptyPointLightBytes = 4 * 16;
    private static readonly byte[] NeutralAtmosphereConstants = CreateNeutralAtmosphereConstants();
    private static readonly byte[] EmptyPointLightConstants = CreateEmptyPointLightConstants();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_disposed || _isLoaded) return;
        _isLoaded = true;
        SubscribePanelEvents();

        // A direct-render session is mandatory. Do not allocate a device/ring or present a blank
        // native surface before a real Bethesda renderer has been attached.
        if (_renderSession is null)
        {
            SynchronizeRenderState();
            return;
        }

        EnsureGraphicsAndInitializeSession();
    }

    private void EnsureGraphicsAndInitializeSession()
    {
        if (_graphicsLease is null)
        {
            _graphicsLease = BethesdaSceneViewerGraphicsContext12.TryAcquire(out var error);
            if (_graphicsLease is null)
            {
                SetFaulted(
                    "The native Bethesda renderer could not initialize Direct3D 12" +
                    (string.IsNullOrWhiteSpace(error) ? "." : $": {error}"));
                return;
            }
        }

        InitializeRenderSession();
        SynchronizeRenderState();
        if (_renderState == BethesdaSceneViewerRenderState.Ready && _scene is not null)
        {
            InvalidateViewport();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_isLoaded) return;
        _isLoaded = false;
        UnsubscribePanelEvents();
        ResetPointerGesture();
        DetachRenderLoop();
        ReleasePanelSurface();
    }

    private void SubscribePanelEvents()
    {
        RenderPanel.SizeChanged += OnRenderPanelSizeChanged;
        RenderPanel.CompositionScaleChanged += OnRenderPanelCompositionScaleChanged;
        RenderPanel.PointerPressed += OnRenderPanelPointerPressed;
        RenderPanel.PointerMoved += OnRenderPanelPointerMoved;
        RenderPanel.PointerReleased += OnRenderPanelPointerReleased;
        RenderPanel.PointerCaptureLost += OnRenderPanelPointerCaptureLost;
        RenderPanel.PointerWheelChanged += OnRenderPanelPointerWheelChanged;
        RenderPanel.KeyDown += OnRenderPanelKeyDown;
        RenderPanel.DoubleTapped += OnRenderPanelDoubleTapped;
    }

    private void UnsubscribePanelEvents()
    {
        RenderPanel.SizeChanged -= OnRenderPanelSizeChanged;
        RenderPanel.CompositionScaleChanged -= OnRenderPanelCompositionScaleChanged;
        RenderPanel.PointerPressed -= OnRenderPanelPointerPressed;
        RenderPanel.PointerMoved -= OnRenderPanelPointerMoved;
        RenderPanel.PointerReleased -= OnRenderPanelPointerReleased;
        RenderPanel.PointerCaptureLost -= OnRenderPanelPointerCaptureLost;
        RenderPanel.PointerWheelChanged -= OnRenderPanelPointerWheelChanged;
        RenderPanel.KeyDown -= OnRenderPanelKeyDown;
        RenderPanel.DoubleTapped -= OnRenderPanelDoubleTapped;
    }

    private void InitializeRenderSession()
    {
        if (_sessionInitialized || _renderSession is null || _graphicsLease is null)
        {
            return;
        }

        try
        {
            _renderSession.Initialize(_graphicsLease.Context);
            _sessionInitialized = true;
            _renderSession.SetScene(_scene);
        }
        catch (Exception ex)
        {
            SetFaulted("The native Bethesda render session failed to initialize.", ex);
        }
    }

    private void OnRenderSessionStateChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (!DispatcherQueue.HasThreadAccess)
        {
            _ = DispatcherQueue.TryEnqueue(() => OnRenderSessionStateChanged(sender, e));
            return;
        }

        // A session may finish streaming (or fault) synchronously inside Render. Defer teardown or
        // promotion until the open command list has been submitted/aborted; releasing its surface in
        // the middle of recording would invalidate the target still referenced by that list.
        if (_renderingFrame)
        {
            return;
        }

        SynchronizeRenderState();
        if (_renderState == BethesdaSceneViewerRenderState.Ready && _scene is not null)
        {
            InvalidateViewport();
        }
    }

    private void SynchronizeRenderState()
    {
        if (_hostFaultMessage is not null)
        {
            PublishRenderState(BethesdaSceneViewerRenderState.Faulted, _hostFaultMessage);
            return;
        }

        BethesdaSceneViewerRenderState state;
        string? message;
        try
        {
            state = _renderSession is not null && _sessionInitialized
                ? _renderSession.State
                : BethesdaSceneViewerRenderState.Initializing;
            message = _sessionInitialized ? _renderSession?.StatusMessage : null;
        }
        catch (Exception ex)
        {
            SetFaulted("The native Bethesda render session failed while reporting its state.", ex);
            return;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            message = state switch
            {
                BethesdaSceneViewerRenderState.Initializing =>
                    _renderSession is null
                        ? "Waiting for the native Bethesda renderer session."
                        : "Preparing native Bethesda renderer…",
                BethesdaSceneViewerRenderState.Faulted => "The native Bethesda renderer is unavailable.",
                _ => null
            };
        }

        PublishRenderState(state, message);
        SynchronizeAnimationControls();
    }

    private void PublishRenderState(BethesdaSceneViewerRenderState state, string? message)
    {
        _renderState = state;
        _renderStatusMessage = message;
        ApplyRenderStateVisuals();
        SynchronizeAnimationControls();

        if (state != BethesdaSceneViewerRenderState.Ready || _scene is null)
        {
            DetachRenderLoop();
            ReleasePanelSurface();
        }

        NotifyObservableRenderStateChanged();
    }

    private void NotifyObservableRenderStateChanged()
    {
        // Session Ready is intentionally internal before the first CompositionTarget frame so the
        // control can allocate and render without a readiness deadlock. Hosts must not promote the
        // native path until this scene has actually survived its first Present: surface
        // creation alone cannot detect a first-frame shader/state/material failure.
        if (_renderState == BethesdaSceneViewerRenderState.Ready &&
            _scene is not null &&
            (_surface is null || !_hasPresentedFrame))
        {
            return;
        }

        if (_lastNotifiedRenderState == _renderState &&
            string.Equals(
                _lastNotifiedRenderStatusMessage,
                _renderStatusMessage,
                StringComparison.Ordinal))
        {
            return;
        }

        _lastNotifiedRenderState = _renderState;
        _lastNotifiedRenderStatusMessage = _renderStatusMessage;
        RenderStateChanged?.Invoke(
            this,
            new BethesdaSceneViewerRenderStateChangedEventArgs(_renderState, _renderStatusMessage));
    }

    private void ApplyRenderStateVisuals()
    {
        if (StatusPanel is null || StatusText is null) return;

        if (_renderState == BethesdaSceneViewerRenderState.Ready && _scene is not null)
        {
            StatusPanel.Visibility = Visibility.Collapsed;
            return;
        }

        StatusText.Text = _scene is null && _renderState == BethesdaSceneViewerRenderState.Ready
            ? "No Bethesda scene selected."
            : _renderStatusMessage ?? "Preparing native Bethesda renderer…";
        StatusPanel.Visibility = Visibility.Visible;
    }

    private void SetFaulted(string message, Exception? exception = null)
    {
        _hostFaultMessage = exception is null ? message : $"{message} {exception.Message}";
        if (exception is null)
        {
            Log.Warn("BethesdaSceneViewer: {0}", message);
        }
        else
        {
            Log.Error("BethesdaSceneViewer: {0} {1}", message, exception);
        }

        PublishRenderState(BethesdaSceneViewerRenderState.Faulted, _hostFaultMessage);
    }

    private void OnRenderPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_renderState == BethesdaSceneViewerRenderState.Ready && _scene is not null)
        {
            TryEnsureSurface();
            InvalidateViewport();
        }
    }

    private void OnRenderPanelCompositionScaleChanged(SwapChainPanel sender, object args)
    {
        if (_renderState == BethesdaSceneViewerRenderState.Ready && _scene is not null)
        {
            TryEnsureSurface();
            InvalidateViewport();
        }
    }

    private void TryEnsureSurface()
    {
        if (!_isLoaded ||
            _graphicsLease is null ||
            _renderState != BethesdaSceneViewerRenderState.Ready ||
            _scene is null)
        {
            return;
        }

        var layoutWidth = RenderPanel.ActualWidth;
        var layoutHeight = RenderPanel.ActualHeight;
        if (!(layoutWidth > 0d) || !(layoutHeight > 0d)) return;

        var width = (uint)Math.Max(1d, Math.Round(layoutWidth * RenderPanel.CompositionScaleX));
        var height = (uint)Math.Max(1d, Math.Round(layoutHeight * RenderPanel.CompositionScaleY));
        try
        {
            if (_surface is null)
            {
                _surface = GpuSwapChainSurface12.Create(
                    _graphicsLease.Context.Gpu,
                    RenderPanel,
                    width,
                    height);
                if (_surface is null)
                {
                    SetFaulted("The native Bethesda renderer could not bind its WinUI swap chain.");
                    return;
                }
            }
            else if (_surface.Width != width || _surface.Height != height)
            {
                ResetPresentedFrameGate();
                _graphicsLease.Context.WaitForGpuIdle();
                _surface.Resize(width, height);
            }

            _frameInvalidated = true;
            NotifyObservableRenderStateChanged();
        }
        catch (Exception ex)
        {
            try
            {
                _surface?.Dispose();
            }
            catch
            {
                // A removed device may reject cleanup calls; it still owns the dead resources.
            }

            _surface = null;
            SetFaulted("The native Bethesda renderer could not create or resize its surface.", ex);
        }
    }

    private void AttachRenderLoop()
    {
        if (_renderLoopAttached || _surface is null) return;
        _lastFrameTimestamp = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRendering;
        _renderLoopAttached = true;
    }

    private void DetachRenderLoop()
    {
        if (!_renderLoopAttached) return;
        CompositionTarget.Rendering -= OnRendering;
        _renderLoopAttached = false;
    }

    private void OnRendering(object? sender, object e)
    {
        if (!_isPresentationActive || !IsEffectivelyVisible())
        {
            _lastFrameTimestamp = Stopwatch.GetTimestamp();
            DetachRenderLoop();
            CancelPendingCapture(
                "The native Bethesda viewer became hidden before capture could run.");
            return;
        }

        var session = _renderSession;
        var scene = _scene;
        var surface = _surface;
        var graphics = _graphicsLease?.Context;
        if (_renderState != BethesdaSceneViewerRenderState.Ready ||
            session is null ||
            scene is null ||
            surface is null ||
            graphics is null)
        {
            DetachRenderLoop();
            return;
        }

        try
        {
            var continuous = _isAnimationPlaying || session.RequiresContinuousFrames;
            if (!_frameInvalidated && !continuous)
            {
                DetachRenderLoop();
                return;
            }

            _frameInvalidated = false;
            var now = Stopwatch.GetTimestamp();
            var deltaSeconds = (float)Stopwatch.GetElapsedTime(_lastFrameTimestamp, now).TotalSeconds;
            _lastFrameTimestamp = now;
            deltaSeconds = Math.Clamp(deltaSeconds, 0f, 0.1f);

            _renderingFrame = true;
            try
            {
                RenderNativeFrame(graphics, surface, session, scene, deltaSeconds);
            }
            finally
            {
                _renderingFrame = false;
            }

            SynchronizeRenderState();
            UpdateAnimationTimeUiThrottled(session, now);
            if (_renderState == BethesdaSceneViewerRenderState.Ready &&
                !_frameInvalidated &&
                !_isAnimationPlaying &&
                !session.RequiresContinuousFrames)
            {
                DetachRenderLoop();
            }
        }
        catch (Exception ex)
        {
            SetFaulted("The native Bethesda renderer failed while recording or presenting a frame.", ex);
        }
    }

    private bool IsEffectivelyVisible()
    {
        DependencyObject? current = this;
        while (current is not null)
        {
            if (current is UIElement element && element.Visibility != Visibility.Visible)
            {
                return false;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return true;
    }

    private void RenderNativeFrame(
        BethesdaSceneViewerGraphicsContext12 graphics,
        GpuSwapChainSurface12 surface,
        IBethesdaSceneViewerRenderSession12 session,
        Core.Formats.Nif.Rendering.Viewer.BethesdaViewerScene scene,
        float deltaSeconds)
    {
        var capture = TryPrepareCapture(graphics, surface);
        var submitted = false;
        var unfencedCaptureLifetimeTransferred = false;
        ulong submittedFenceValue = 0;
        Exception? frameException = null;
        try
        {
            using var recording = graphics.BeginFrame();
            var commandList = graphics.Recorder.CommandList;
            var (backBuffer, _) = surface.AcquireBackBufferRtv();
            var sceneRtv = surface.MsaaColorRtv;
            var sceneDsv = surface.DepthStencilView;

            commandList.ClearRenderTargetView(sceneRtv, new Color4(0.025f, 0.03f, 0.04f, 1f));
            commandList.ClearDepthStencilView(sceneDsv, ClearFlags.Depth, 0f, 0);
            commandList.OMSetRenderTargets(sceneRtv, sceneDsv);
            commandList.RSSetViewport(new Viewport(0f, 0f, surface.Width, surface.Height, 0f, 1f));
            commandList.RSSetScissorRect((int)surface.Width, (int)surface.Height);
            BindNeutralFrameConstants(
                commandList,
                graphics.Recorder.FrameIndex,
                graphics.RingBuffer);

            var camera = _camera.GetFrame(surface.Width / (float)surface.Height);
            var frame = new BethesdaSceneViewerFrame12(
                scene,
                graphics,
                surface,
                commandList,
                camera,
                graphics.Recorder.FrameIndex,
                deltaSeconds);
            session.Render(frame);

            surface.ResolveTo(commandList, backBuffer);
            if (capture is not null)
            {
                capture.RecordCopy(commandList, backBuffer);
            }
            else
            {
                GpuSwapChainSurface12.FinishBackBuffer(commandList, backBuffer);
            }

            var submission = recording.Submit(capture);
            if (!submission.Succeeded)
            {
                // EndFrame has already transferred a prepared capture to recorder-owned teardown
                // storage when Execute may have reached the queue but Signal produced no waitable
                // fence. Surface the frame failure without releasing that possibly in-flight buffer.
                unfencedCaptureLifetimeTransferred =
                    capture is not null && submission.CommandListMayHaveReachedQueue;
                if (submission.CommandListMayHaveReachedQueue)
                {
                    // The same list references the surface, depth/MSAA targets, descriptor/ring
                    // allocations, textures, and session geometry—not only the optional readback.
                    // Establish a device-terminal boundary before SetFaulted can release that graph.
                    graphics.TerminalizeDeviceAfterUnfencedSubmission();
                }

                submission.ThrowIfFailed();
            }

            submitted = true;
            submittedFenceValue = submission.FenceValue;
            surface.Present();
            _hasPresentedFrame = true;
        }
        catch (Exception ex)
        {
            frameException = ex;
            throw;
        }
        finally
        {
            if (capture is not null)
            {
                if (submitted)
                {
                    SubmitCaptureReadback(capture, submittedFenceValue);
                }
                else if (unfencedCaptureLifetimeTransferred)
                {
                    FailUnfencedCaptureRequest(
                        capture,
                        frameException ?? new InvalidOperationException(
                            "The native Bethesda frame reached the GPU queue without a completion fence."));
                }
                else
                {
                    AbandonCaptureRequest(
                        capture,
                        frameException ?? new InvalidOperationException(
                            "The native Bethesda frame was abandoned before capture submission."));
                }
            }
        }
    }

    private static byte[] CreateNeutralAtmosphereConstants()
    {
        var constants = new float[NeutralAtmosphereBytes / sizeof(float)];
        constants[4 * 4 + 3] = 1f; // SkyHorizon.w: HDR/reference Lighting30 route active.
        constants[9 * 4 + 3] = 1f; // CameraOrigin.w: neutral emissive multiplier.
        constants[NeutralAtmosphereClipPlaneFloat4Slot * 4 + 3] = 1f; // clip(+1): neutral half-space.
        var bytes = new byte[NeutralAtmosphereBytes];
        Buffer.BlockCopy(constants, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte[] CreateEmptyPointLightConstants()
    {
        var constants = new byte[EmptyPointLightBytes];
        BitConverter.TryWriteBytes(constants.AsSpan(0, sizeof(int)), 1);
        BitConverter.TryWriteBytes(constants.AsSpan(sizeof(int), sizeof(int)), 1);
        return constants;
    }

    private static void BindNeutralFrameConstants(
        ID3D12GraphicsCommandList commandList,
        int frameIndex,
        Core.Formats.Nif.Rendering.Gpu.D3D12.GpuRingBuffer12 ringBuffer)
    {
        var allocation = ringBuffer.Allocate(frameIndex, NeutralAtmosphereBytes);
        Marshal.Copy(
            NeutralAtmosphereConstants,
            0,
            allocation.CpuPtr,
            NeutralAtmosphereConstants.Length);
        commandList.SetGraphicsRootConstantBufferView(
            Core.Formats.Nif.Rendering.Gpu.D3D12.GpuRootSignature12.Slots.AtmosphereCbv,
            allocation.GpuAddress);

        // Match NifHeadlessRenderer.BindFlatAtmosphere completely. With a zero atmosphere light
        // count the reference shader never dereferences t9. The same deterministic allocation can
        // carry t10's required 1x1 tile header plus an empty mask.
        var emptyLights = ringBuffer.Allocate(frameIndex, EmptyPointLightBytes, 16);
        Marshal.Copy(
            EmptyPointLightConstants,
            0,
            emptyLights.CpuPtr,
            EmptyPointLightConstants.Length);
        commandList.SetGraphicsRootShaderResourceView(
            Core.Formats.Nif.Rendering.Gpu.D3D12.GpuRootSignature12.Slots.PointLightsSrv,
            emptyLights.GpuAddress);
        commandList.SetGraphicsRootShaderResourceView(
            Core.Formats.Nif.Rendering.Gpu.D3D12.GpuRootSignature12.Slots.PointLightTilesSrv,
            emptyLights.GpuAddress);
    }

    private void ReleasePanelSurface()
    {
        ResetPresentedFrameGate();
        CancelPendingCapture(
            "The native Bethesda presentation surface was released before capture could run.");
        if (_surface is null) return;

        try
        {
            _graphicsLease?.Context.WaitForGpuIdle();
        }
        catch (Exception ex)
        {
            Log.Warn("BethesdaSceneViewer: GPU idle wait before surface release failed: {0}", ex.Message);
        }

        try
        {
            _surface.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("BethesdaSceneViewer: surface release failed: {0}", ex.Message);
        }

        _surface = null;
    }

    private void ResetPresentedFrameGate()
    {
        _hasPresentedFrame = false;
        // A later successful first Present must be observable even when the state/message tuple is
        // textually identical to the prior surface or prior scene.
        if (_lastNotifiedRenderState == BethesdaSceneViewerRenderState.Ready)
        {
            _lastNotifiedRenderState = null;
            _lastNotifiedRenderStatusMessage = null;
        }
    }

    private void DisposeControlResources()
    {
        CancelCaptureForControlDisposal();
        DetachRenderLoop();
        if (_isLoaded)
        {
            _isLoaded = false;
            UnsubscribePanelEvents();
        }

        ResetPointerGesture();
        ReleasePanelSurface();
        if (_renderSession is not null)
        {
            _renderSession.StateChanged -= OnRenderSessionStateChanged;
            try
            {
                _graphicsLease?.Context.WaitForGpuIdle();
            }
            catch (Exception ex)
            {
                Log.Warn("BethesdaSceneViewer: GPU idle wait before session release failed: {0}", ex.Message);
            }

            _renderSession.Dispose();
            _renderSession = null;
        }

        _graphicsLease?.Dispose();
        _graphicsLease = null;
        _scene = null;
    }
}
