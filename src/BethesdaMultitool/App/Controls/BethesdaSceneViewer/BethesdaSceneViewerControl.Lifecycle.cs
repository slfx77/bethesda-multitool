using System.Diagnostics;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Microsoft.UI.Xaml;
using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace BethesdaMultitool;

public sealed partial class BethesdaSceneViewerControl
{
    // Keep the mandatory b3 upload aligned with NifHeadlessRenderer.BindFlatAtmosphere: ten base
    // vectors, four matrices, four shadow vectors, six directional-ambient vectors,
    // GrassSunColorScale, ClipPlane, then Skyrim's three matrix rows + mode. Zero lighting/fog fields select reference.frag's stable
    // 0.4 + 0.6*Lambert presentation light; the three non-zero W lanes retain HDR/emissive behavior
    // and disable clipping. A real contextual scene session may overwrite b3 after this baseline.
    private const int NeutralAtmosphereClipPlaneFloat4Slot = AtmosphereConstantBufferLayout.ClipPlaneFloat4Slot;
    private const int NeutralAtmosphereBytes = (int)AtmosphereConstantBufferLayout.ByteSize;
    private const int EmptyPointLightBytes = 4 * 16;
    private static readonly byte[] NeutralAtmosphereConstants = CreateNeutralAtmosphereConstants();
    private static readonly byte[] EmptyPointLightConstants = CreateEmptyPointLightConstants();

    /// <summary>Reattaches existing input handlers while the shared host mounts its surface.</summary>
    /// <param name="sender">Current control or exact subscribed backend.</param>
    /// <param name="e">Native event notification.</param>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_disposed || _inputEventsAttached) { return; }
        _inputEventsAttached = true;
        SubscribePanelEvents();
        Viewport.Refresh();
        // Remount must reread current backend readiness after any old surface-generation callback
        // was retired during unload. This preserves the donor's existing remount synchronization.
        SynchronizeRenderState();
    }

    /// <summary>Detaches input handlers while the shared host releases only the surface.</summary>
    /// <param name="sender">Current control or exact subscribed backend.</param>
    /// <param name="e">Native event notification.</param>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_inputEventsAttached) { return; }
        _inputEventsAttached = false;
        UnsubscribePanelEvents();
        ResetPointerGesture();
    }

    /// <summary>Attaches camera gestures to the shared panel and keyboard gestures to its focusable viewport.</summary>
    private void SubscribePanelEvents()
    {
        RenderPanel.PointerPressed += OnRenderPanelPointerPressed;
        RenderPanel.PointerMoved += OnRenderPanelPointerMoved;
        RenderPanel.PointerReleased += OnRenderPanelPointerReleased;
        RenderPanel.PointerCaptureLost += OnRenderPanelPointerCaptureLost;
        RenderPanel.PointerWheelChanged += OnRenderPanelPointerWheelChanged;
        Viewport.KeyDown += OnRenderPanelKeyDown;
        RenderPanel.DoubleTapped += OnRenderPanelDoubleTapped;
    }

    /// <summary>Detaches the same gesture handlers before unmount or final disposal.</summary>
    private void UnsubscribePanelEvents()
    {
        RenderPanel.PointerPressed -= OnRenderPanelPointerPressed;
        RenderPanel.PointerMoved -= OnRenderPanelPointerMoved;
        RenderPanel.PointerReleased -= OnRenderPanelPointerReleased;
        RenderPanel.PointerCaptureLost -= OnRenderPanelPointerCaptureLost;
        RenderPanel.PointerWheelChanged -= OnRenderPanelPointerWheelChanged;
        Viewport.KeyDown -= OnRenderPanelKeyDown;
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

    /// <summary>Rejects stale backend notifications before dispatching current native readiness.</summary>
    /// <param name="sender">Current control or exact subscribed backend.</param>
    /// <param name="e">Native event notification.</param>
    private void OnRenderSessionStateChanged(object? sender, EventArgs e)
    {
        if (_disposed || !ReferenceEquals(sender, _renderSession)) { return; }
        if (!DispatcherQueue.HasThreadAccess)
        {
            var generation = Viewport.Session?.Generation;
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (!_disposed && ReferenceEquals(sender, _renderSession) && generation == Viewport.Session?.Generation)
                {
                    OnRenderSessionStateChanged(sender, e);
                }
            });
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
        var changed = _renderState != state || !string.Equals(_renderStatusMessage, message, StringComparison.Ordinal);
        _renderState = state;
        _renderStatusMessage = message;
        ApplyRenderStateVisuals();
        SynchronizeAnimationControls();

        if (state != BethesdaSceneViewerRenderState.Ready || _scene is null)
        {
            CancelPendingCapture(
                "The native Bethesda scene stopped being ready before capture could run.");
        }

        // A non-null scene commonly moves Ready -> Initializing -> Ready while the session replaces
        // its GPU graph. Keep the already-bound SwapChainPanel surface across that transient state:
        // no frames are recorded while the loop is detached, and destroying/recreating a composition
        // swap chain for every mesh/NPC selection is both unnecessary and unsafe on the live 4x-MSAA
        // path. No-scene, terminal fault, unload, and control disposal remain real ownership
        // boundaries and still release the surface.
        if (_scene is null || state == BethesdaSceneViewerRenderState.Faulted)
        {
            ReleasePanelSurface();
        }

        NotifyObservableRenderStateChanged();
        if (changed) { _viewportAdapter?.NotifyStateChanged(); }
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

        StatusText.Text = _scene is null && _renderState != BethesdaSceneViewerRenderState.Faulted
            ? _emptySceneMessage
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

    private void TryEnsureSurface()
    {
        try { Viewport.EnsureSurface(); }
        catch (Exception exception) { SetFaulted("The native Bethesda renderer could not create or resize its surface.", exception); }
    }

    private bool IsEffectivelyVisible() => Viewport.IsEffectivelyVisible();

    private void RenderNativeFrame(
        BethesdaSceneViewerGraphicsContext12 graphics,
        GpuSwapChainSurface12 surface,
        IBethesdaSceneViewerRenderSession12 session,
        Core.Formats.Nif.Rendering.Viewer.BethesdaViewerScene scene,
        float deltaSeconds)
    {
        if (session.RequiresGpuIdleBeforeFrame)
        {
            // Async texture promotion rewrites a stable slot in the shader-visible bindless heap.
            // BeginFrame waits only this ring slot; the other frame can still sample the placeholder
            // descriptor. Drain every earlier direct submission before allowing that rewrite.
            var drainStarted = Stopwatch.GetTimestamp();
            graphics.WaitForGpuIdle();
            var drainMilliseconds = Stopwatch.GetElapsedTime(drainStarted).TotalMilliseconds;
            _streamingGpuIdleDrainCount++;
            _streamingGpuIdleDrainMilliseconds += drainMilliseconds;
            Log.Debug(
                "BethesdaSceneViewer: texture-streaming descriptor drain #{0} completed in {1:F2} ms (cumulative {2:F2} ms).",
                _streamingGpuIdleDrainCount,
                drainMilliseconds,
                _streamingGpuIdleDrainMilliseconds);
        }

        var capture = TryPrepareCapture(graphics, surface);
        if (_inputTrace is not null) _traceFrameSerial++;
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

            var sceneClear = BethesdaViewerPresentationPolicy.ResolveSceneClearColor(scene.Purpose);
            commandList.ClearRenderTargetView(
                sceneRtv,
                new Color4(sceneClear.X, sceneClear.Y, sceneClear.Z, sceneClear.W));
            commandList.ClearDepthStencilView(sceneDsv, ClearFlags.Depth, 0f, 0);
            commandList.OMSetRenderTargets(sceneRtv, sceneDsv);
            commandList.RSSetViewport(new Viewport(0f, 0f, surface.Width, surface.Height, 0f, 1f));
            commandList.RSSetScissorRect((int)surface.Width, (int)surface.Height);
            BindNeutralFrameConstants(
                commandList,
                graphics.Recorder.FrameIndex,
                graphics.RingBuffer);

            var traceCameraBeforeFit = capture is not null ? TraceCameraState() : null;
            var camera = _camera.GetFrame(surface.Width / (float)surface.Height);
            var frame = new BethesdaSceneViewerFrame12(
                scene,
                graphics,
                surface,
                commandList,
                camera,
                graphics.Recorder.FrameIndex,
                deltaSeconds);
            TraceCaptureFrame(capture, camera, scene, graphics.Recorder.FrameIndex, deltaSeconds, traceCameraBeforeFit);
            session.Render(frame);
            if (capture is not null) TraceCapturePhase(capture, "capture-render-return");

            surface.ResolveTo(graphics.Recorder, backBuffer);
            if (capture is not null)
            {
                capture.RecordCopy(commandList, backBuffer);
                TraceCapturePhase(capture, "capture-copy-recorded");
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
            if (capture is not null) TraceCapturePhase(capture, "capture-queue-submitted", submittedFenceValue);
            surface.Present();
            if (capture is not null) TraceCapturePhase(capture, "capture-present-return", submittedFenceValue);
            if (!_streamingGpuIdleDrainSummaryLogged &&
                _streamingGpuIdleDrainCount > 0 &&
                !session.RequiresGpuIdleBeforeFrame)
            {
                _streamingGpuIdleDrainSummaryLogged = true;
                Log.Info(
                    "BethesdaSceneViewer: texture streaming settled after {0} descriptor-safety drain(s), {1:F2} ms total GPU-idle wait.",
                    _streamingGpuIdleDrainCount,
                    _streamingGpuIdleDrainMilliseconds);
            }
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
        try { Viewport.ReleaseSurface(); }
        catch (Exception exception)
        {
            Log.Warn("BethesdaSceneViewer: presentation retirement is incomplete: {0}", exception.Message);
        }
    }

    private void ResetPresentedFrameGate()
    {
        Viewport.ResetPresentation();
        // A later successful first Present must be observable even when the state/message tuple is
        // textually identical to the prior surface or prior scene.
        if (_lastNotifiedRenderState == BethesdaSceneViewerRenderState.Ready)
        {
            _lastNotifiedRenderState = null;
            _lastNotifiedRenderStatusMessage = null;
        }
    }

    /// <summary>Retires capture and input before retryable disposal of the shared native owner.</summary>
    private void DisposeControlResources()
    {
        try { CancelCaptureForControlDisposal(); }
        finally
        {
            if (_inputEventsAttached)
            {
                _inputEventsAttached = false;
                UnsubscribePanelEvents();
            }
            ResetPointerGesture();
            Viewport.StateChanged -= OnViewportStateChanged;
            Viewport.FrameCompleted -= OnViewportFrameCompleted;
            Viewport.PresentationSuspended -= OnViewportPresentationSuspended;
            Viewport.SurfaceRetiring -= OnViewportSurfaceRetiring;
            Viewport.Failed -= OnViewportFailed;
            try { Viewport.Dispose(); }
            finally { _scene = null; }
        }
    }
}
