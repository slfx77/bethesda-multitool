using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The control instance lives for the whole app session; a SubTabView tab switch unloads +
        // reloads its content. First load creates the device-level backend; on return the device +
        // scene pipelines persist (OnUnloaded only released the swapchain surface), so we just
        // re-hook input + recreate the surface. This is what keeps 3D + the 2D top-down overlay
        // working after switching to another tab and back, without re-streaming the scene.
        var firstInit = _gpu12 is null;
        if (firstInit)
        {
            if (!TryInitD3D12Backend())
            {
                ShowStatus("3D view unavailable: D3D12 init failed (see logs).");
                return;
            }
            Log.Info("WorldView3DControl: D3D12 backend active.");
        }

        // (Re)subscribe input handlers — OnUnloaded unconditionally unsubscribes them, so this never
        // double-subscribes. The XAML markup compiler types RenderPanel as SwapChainPanel.
        RenderPanel.SizeChanged += OnRenderPanelSizeChanged;
        RenderPanel.CompositionScaleChanged += OnRenderPanelCompositionScaleChanged;
        RenderPanel.KeyDown += OnRenderPanelKeyDown;
        RenderPanel.KeyUp += OnRenderPanelKeyUp;
        RenderPanel.LostFocus += OnRenderPanelLostFocus;
        RenderPanel.PointerPressed += OnRenderPanelPointerPressed;
        RenderPanel.PointerMoved += OnRenderPanelPointerMoved;
        RenderPanel.PointerReleased += OnRenderPanelPointerReleased;
        RenderPanel.PointerWheelChanged += OnRenderPanelPointerWheelChanged;

        RenderPanel.Focus(FocusState.Programmatic);

        // Walk-mode ground sampling: read the camera's current cell heightmap and bilinearly
        // interpolate, then take the highest ground under the player's capsule footprint (center + a
        // ring) so the camera rides over thin seams instead of dropping through a single point.
        // _cellGridLookup is set in TryBuildCellGrid. On return the grid persists.
        _controller.GroundHeightSampler = SampleGroundHeightCapsule;
        // Walk-mode ceiling sampling for jumps: up-ray against the same per-ref collision meshes so the
        // camera bonks low roofs instead of clipping through them.
        _controller.CeilingHeightSampler = SampleCeilingHeight;
        // Continuous XY capsule sweep against those same Havok-first candidates. This is separate from
        // vertical sampling so wall sliding cannot disturb stair easing, ledge falls, or jump/ceiling Z.
        _controller.HorizontalMovementResolver = ResolveWalkHorizontalMovement;

        if (firstInit)
        {
            TryBuildCellGrid();
        }
        UpdateHudLayoutBounds();
        // _surface12 was released on unload, so on return this recreates the swapchain on the
        // re-attached panel + EnsureDepthSrv + AttachRenderLoop (its create branch).
        TryEnsureSurface();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        RenderPanel.SizeChanged -= OnRenderPanelSizeChanged;
        RenderPanel.CompositionScaleChanged -= OnRenderPanelCompositionScaleChanged;
        RenderPanel.KeyDown -= OnRenderPanelKeyDown;
        RenderPanel.KeyUp -= OnRenderPanelKeyUp;
        RenderPanel.LostFocus -= OnRenderPanelLostFocus;
        RenderPanel.PointerPressed -= OnRenderPanelPointerPressed;
        RenderPanel.PointerMoved -= OnRenderPanelPointerMoved;
        RenderPanel.PointerReleased -= OnRenderPanelPointerReleased;
        RenderPanel.PointerWheelChanged -= OnRenderPanelPointerWheelChanged;

        // Light teardown: a tab switch unloads this content but the control instance persists.
        // Keep the device + scene alive (see DetachForUnload / OnLoaded); full teardown is Dispose().
        DetachForUnload();
        _ = _pointerStartPosition;
    }

    /// <summary>
    ///     Light teardown for a tab-switch unload (the SubTabView swaps WorldMapTab content out of
    ///     the visual tree). Detaches the render loop and releases ONLY the swapchain surface — its
    ///     back buffers are bound to the SwapChainPanel being unloaded. The D3D12 device, descriptor
    ///     heaps, root signature, and the per-data scene pipelines (_terrain/_references/_water/
    ///     _navMesh/_cellGrid) + caches stay resident so 3D rendering and the 2D top-down overlay
    ///     resume instantly on return without re-streaming or resetting the camera. The persistent
    ///     <see cref="_depthSrv" /> slot is intentionally NOT nulled (its heap persists; EnsureDepthSrv
    ///     re-points it at the new depth resource on return — nulling would leak a persistent slot per
    ///     switch). Full teardown (device + all pipelines) lives in <see cref="DisposeRenderResources" />.
    /// </summary>
    private void DetachForUnload()
    {
        // Drain any in-flight top-down readback first — its Task.Run body maps the readback buffer,
        // which must not race the surface release below. Non-pumping: Task.Wait() on the STA UI
        // thread pumps messages and can re-enter XAML → CheckReentrancy fail-fast (see NonPumpingWait).
        var readback = _topDownReadbackTask;
        if (readback is { IsCompleted: false })
        {
            Core.Orchestration.NonPumpingWait.Wait(readback);
            _ = readback.Exception; // observe so it never resurfaces as an UnobservedTaskException
        }

        DetachRenderLoop();
        // The surface's back buffers must not be referenced by in-flight command lists when disposed.
        _commandRecorder12?.WaitForGpuIdle();
        _surface12?.Dispose();
        _surface12 = null;
    }

    private void OnRenderPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateHudLayoutBounds();
        TryEnsureSurface();
    }

    private void OnRenderPanelCompositionScaleChanged(SwapChainPanel sender, object args)
    {
        UpdateHudLayoutBounds();
        TryEnsureSurface();
    }

    private void UpdateHudLayoutBounds()
    {
        var width = RenderPanel.ActualWidth;
        if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width)) return;

        var maxPanelWidth = Math.Max(0d, width - 16d);
        if (ShouldUpdateLayoutValue(HudPanel.MaxWidth, maxPanelWidth))
        {
            HudPanel.MaxWidth = maxPanelWidth;
        }

        var maxTextWidth = Math.Max(0d, maxPanelWidth - 16d);
        if (ShouldUpdateLayoutValue(HudText.MaxWidth, maxTextWidth))
        {
            HudText.MaxWidth = maxTextWidth;
        }
    }

    private void TryEnsureSurface()
    {
        if (_gpu12 is null) return;

        var widthLayout = RenderPanel.ActualWidth;
        var heightLayout = RenderPanel.ActualHeight;
        if (widthLayout <= 0 || heightLayout <= 0) return;
        UpdateHudLayoutBounds();

        var width = (uint)Math.Max(1, Math.Round(widthLayout * RenderPanel.CompositionScaleX));
        var height = (uint)Math.Max(1, Math.Round(heightLayout * RenderPanel.CompositionScaleY));

        if (_surface12 is null)
        {
            _surface12 = BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuSwapChainSurface12.Create(
                _gpu12!, RenderPanel, width, height);
            if (_surface12 is null)
            {
                ShowStatus("3D view: D3D12 swapchain bind failed (see logs).");
                return;
            }
            HideStatus();
            EnsureDepthSrv();
            _lastFrameTime = DateTime.UtcNow;
            AttachRenderLoop();
        }
        else
        {
            // Resize requires GPU idle so the back buffers aren't referenced by in-flight
            // command lists. The recorder's WaitForGpuIdle drains the queue.
            _commandRecorder12!.WaitForGpuIdle();
            _surface12.Resize(width, height);
            EnsureDepthSrv(); // depth resource was recreated → repoint the SRV at the new one
            HideStatus();
        }
    }

    /// <summary>
    ///     Allocates (once) and (re)creates an R32_FLOAT Texture2D or Texture2DMS SRV over the
    ///     swap-chain's R32_TYPELESS depth resource in the shared bindless heap. The persistent slot is allocated once and
    ///     the SRV rewritten in place after each surface resize (the depth resource changes
    ///     identity). Leaves <see cref="_depthSrv" /> null (→ proxy fade) if anything is missing.
    /// </summary>
    private void EnsureDepthSrv()
    {
        var depth = _surface12?.DepthResource;
        if (_gpu12 is null || _cbvSrvUavHeap12 is null || depth is null)
        {
            return;
        }

        _depthSrv ??= _cbvSrvUavHeap12.AllocatePersistent();
        // Reserve the CAPTURE depth slot alongside the live one, at surface-init time, from the
        // early bump region — NOT lazily at first capture from the streaming-churned free-list.
        // Empirically (RTX 4080, water MSAA scene-depth reads), descriptor rewrites into
        // early-allocated low slots are reliably visible to subsequent capture submissions, while
        // rewrites into late/recycled slots intermittently read the slot's stale prior content on
        // the GPU — the live-window-sized depth "rectangle" / opaque-water capture corruption.
        _captureDepthSrv ??= _cbvSrvUavHeap12.AllocatePersistent();
        var srvDesc = new Vortice.Direct3D12.ShaderResourceViewDescription
        {
            Format = Vortice.DXGI.Format.R32_Float,
            ViewDimension = _surface12!.IsMsaa
                ? Vortice.Direct3D12.ShaderResourceViewDimension.Texture2DMultisampled
                : Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = Vortice.Direct3D12.ShaderComponentMapping.Default,
        };
        if (!_surface12.IsMsaa)
        {
            srvDesc.Texture2D = new Vortice.Direct3D12.Texture2DShaderResourceView
            {
                MipLevels = 1,
                MostDetailedMip = 0,
            };
        }
        _gpu12.Device.CreateShaderResourceView(depth, srvDesc, _depthSrv.Value.Cpu);
    }

    /// <summary>
    ///     Allocates once and rewrites the Texture2D SRV over the live surface's one-sample HDR
    ///     opaque snapshot. The underlying resource is the MSAA resolve target or the dedicated 1x
    ///     copy and therefore changes identity whenever the surface is recreated or resized.
    /// </summary>
    private bool TryEnsureWaterOpaqueSnapshotSrv()
    {
        if (_gpu12 is null || _cbvSrvUavHeap12 is null || _surface12 is null ||
            !_surface12.TryEnsureWaterOpaqueSnapshotResource() ||
            _surface12.WaterOpaqueSnapshotResource is not { } snapshot)
        {
            return false;
        }

        if (_waterOpaqueSnapshotSrv is not null &&
            ReferenceEquals(_waterOpaqueSnapshotSrvResource, snapshot))
        {
            return true;
        }

        var allocatedDescriptorThisAttempt = false;
        try
        {
            if (_waterOpaqueSnapshotSrv is null)
            {
                _waterOpaqueSnapshotSrv = _cbvSrvUavHeap12.AllocatePersistent();
                allocatedDescriptorThisAttempt = true;
            }

            var srvDesc = new Vortice.Direct3D12.ShaderResourceViewDescription
            {
                Format = Core.Formats.Nif.Rendering.Gpu.D3D12.GpuSwapChainSurface12.SceneColorFormat,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = Vortice.Direct3D12.ShaderComponentMapping.Default,
                Texture2D = new Vortice.Direct3D12.Texture2DShaderResourceView
                {
                    MipLevels = 1,
                    MostDetailedMip = 0,
                },
            };
            _gpu12.Device.CreateShaderResourceView(snapshot, srvDesc, _waterOpaqueSnapshotSrv.Value.Cpu);
            _waterOpaqueSnapshotSrvResource = snapshot;
            return true;
        }
        catch (Exception ex)
        {
            if (allocatedDescriptorThisAttempt && _waterOpaqueSnapshotSrv is { } allocation)
            {
                // No command list has observed this slot yet, so immediate reuse is safe.
                _cbvSrvUavHeap12.FreePersistent(allocation.BindlessIndex);
                _waterOpaqueSnapshotSrv = null;
            }

            _waterOpaqueSnapshotSrvResource = null;
            _surface12.ReleaseDedicatedWaterOpaqueSnapshotResource();
            Log.Warn("WorldView3DControl: WATER001 live snapshot SRV creation failed; using WATER003: {0}",
                ex.Message);
            return false;
        }
    }

    /// <summary>
    ///     Allocates once and rewrites the R32_FLOAT Texture2D SRV over the surface's 1-sample
    ///     post-opaque depth snapshot (the unified transparency stream's depth source). Same
    ///     allocate-once / rewrite-on-identity-change contract as
    ///     <see cref="TryEnsureWaterOpaqueSnapshotSrv" />.
    /// </summary>
    private bool TryEnsureOpaqueDepthSnapshotSrv()
    {
        if (_gpu12 is null || _cbvSrvUavHeap12 is null || _surface12 is null ||
            _surface12.OpaqueDepthSnapshotResource is not { } snapshot)
        {
            return false;
        }

        if (_opaqueDepthSnapshotSrv is not null &&
            ReferenceEquals(_opaqueDepthSnapshotSrvResource, snapshot))
        {
            return true;
        }

        var allocatedDescriptorThisAttempt = false;
        try
        {
            if (_opaqueDepthSnapshotSrv is null)
            {
                _opaqueDepthSnapshotSrv = _cbvSrvUavHeap12.AllocatePersistent();
                allocatedDescriptorThisAttempt = true;
            }

            var srvDesc = new Vortice.Direct3D12.ShaderResourceViewDescription
            {
                Format = Vortice.DXGI.Format.R32_Float,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = Vortice.Direct3D12.ShaderComponentMapping.Default,
                Texture2D = new Vortice.Direct3D12.Texture2DShaderResourceView
                {
                    MipLevels = 1,
                    MostDetailedMip = 0,
                },
            };
            _gpu12.Device.CreateShaderResourceView(snapshot, srvDesc, _opaqueDepthSnapshotSrv.Value.Cpu);
            _opaqueDepthSnapshotSrvResource = snapshot;
            return true;
        }
        catch (Exception ex)
        {
            if (allocatedDescriptorThisAttempt && _opaqueDepthSnapshotSrv is { } allocation)
            {
                // No command list has observed this slot yet, so immediate reuse is safe.
                _cbvSrvUavHeap12.FreePersistent(allocation.BindlessIndex);
                _opaqueDepthSnapshotSrv = null;
            }

            _opaqueDepthSnapshotSrvResource = null;
            _surface12.ReleaseOpaqueDepthSnapshotResource();
            Log.Warn("WorldView3DControl: opaque depth snapshot SRV creation failed; " +
                     "the transparency stream keeps the read-only-DSV depth path: {0}", ex.Message);
            return false;
        }
    }

    /// <summary>
    ///     Ensures the planar sky-reflection target exists at the scene's ASPECT and that one
    ///     persistent bindless SRV points at its resolved single-sample colour. Same allocate-once /
    ///     rewrite-on-identity-change contract as <see cref="TryEnsureWaterOpaqueSnapshotSrv" />.
    ///     <para>
    ///         Only the aspect must track the viewport: the water samples this by normalized screen
    ///         UV, so the target is deliberately kept small.
    ///     </para>
    /// </summary>
    private bool TryEnsureWaterReflectionTarget(int sceneWidth, int sceneHeight)
    {
        if (_gpu12 is null || _cbvSrvUavHeap12 is null || sceneWidth <= 0 || sceneHeight <= 0)
        {
            return false;
        }

        const int reflectionWidth = 512;
        var reflectionHeight = Math.Clamp(
            (int)MathF.Round(reflectionWidth * (float)sceneHeight / sceneWidth), 64, 512);

        if (_waterReflectionTarget is null ||
            _waterReflectionTargetW != reflectionWidth ||
            _waterReflectionTargetH != reflectionHeight)
        {
            // A resize means new resources; the old ones may still be referenced by in-flight
            // frames, so route them through the deletion queue rather than disposing inline.
            if (_waterReflectionTarget is { } previous)
            {
                _deletionQueue12?.EnqueueDispose(previous);
                _waterReflectionTarget = null;
                _waterReflectionSrvResource = null;
            }

            try
            {
                _waterReflectionTarget =
                    new Core.Formats.Nif.Rendering.Gpu.D3D12.GpuOffscreenSceneTarget12(
                        _gpu12, reflectionWidth, reflectionHeight);
                _waterReflectionTargetW = reflectionWidth;
                _waterReflectionTargetH = reflectionHeight;
            }
            catch (Exception ex)
            {
                _waterReflectionTarget = null;
                Log.Warn("WorldView3DControl: water reflection target creation failed; " +
                         "using the sky-gradient stand-in: {0}", ex.Message);
                return false;
            }
        }

        if (!_waterReflectionTarget.TryEnsureWaterOpaqueSnapshotResource() ||
            _waterReflectionTarget.WaterOpaqueSnapshotResource is not { } resolved)
        {
            return false;
        }

        if (_waterReflectionSrv is not null && ReferenceEquals(_waterReflectionSrvResource, resolved))
        {
            return true;
        }

        var allocatedDescriptorThisAttempt = false;
        try
        {
            if (_waterReflectionSrv is null)
            {
                _waterReflectionSrv = _cbvSrvUavHeap12.AllocatePersistent();
                allocatedDescriptorThisAttempt = true;
            }

            var srvDesc = new Vortice.Direct3D12.ShaderResourceViewDescription
            {
                Format = Core.Formats.Nif.Rendering.Gpu.D3D12.GpuSceneFormats.SceneColor,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = Vortice.Direct3D12.ShaderComponentMapping.Default,
                Texture2D = new Vortice.Direct3D12.Texture2DShaderResourceView
                {
                    MipLevels = 1,
                    MostDetailedMip = 0,
                },
            };
            _gpu12.Device.CreateShaderResourceView(resolved, srvDesc, _waterReflectionSrv.Value.Cpu);
            _waterReflectionSrvResource = resolved;
            return true;
        }
        catch (Exception ex)
        {
            if (allocatedDescriptorThisAttempt && _waterReflectionSrv is { } allocation)
            {
                // No command list has observed this slot yet, so immediate reuse is safe.
                _cbvSrvUavHeap12.FreePersistent(allocation.BindlessIndex);
                _waterReflectionSrv = null;
            }

            _waterReflectionSrvResource = null;
            Log.Warn("WorldView3DControl: water reflection SRV creation failed; " +
                     "using the sky-gradient stand-in: {0}", ex.Message);
            return false;
        }
    }

    private void DisposeRenderResources()
    {
        // Drain any in-flight top-down readback first — its Task.Run body waits on the frame
        // fence and maps a readback buffer, which must not race the device teardown below.
        // Non-pumping: Task.Wait() on the STA UI thread pumps messages and can re-enter XAML
        // mid-teardown → CheckReentrancy fail-fast (see NonPumpingWait docs).
        var readback = _topDownReadbackTask;
        if (readback is { IsCompleted: false })
        {
            Core.Orchestration.NonPumpingWait.Wait(readback);
            // The render path already logged the failure; observe it so it never resurfaces as
            // an UnobservedTaskException, then proceed with teardown regardless.
            _ = readback.Exception;
        }

        _commandRecorder12?.WaitForGpuIdle();

        // Reused top-down overlay target — safe to release now (the readback above is drained and
        // the GPU is idle).
        _topDownTarget?.Dispose();
        _topDownTarget = null;
        _topDownTargetW = _topDownTargetH = 0;

        // Planar sky-reflection target + its persistent bindless slot. Without this a device
        // reload leaked the 512-wide colour/depth/resolve set AND one persistent descriptor
        // every time (the persistent region never shrinks, so the leak is permanent).
        _waterReflectionTarget?.Dispose();
        _waterReflectionTarget = null;
        _waterReflectionTargetW = _waterReflectionTargetH = 0;
        if (_waterReflectionSrv is { } reflectionSrv)
        {
            _cbvSrvUavHeap12?.FreePersistent(reflectionSrv.BindlessIndex);
            _waterReflectionSrv = null;
        }
        _waterReflectionSrvResource = null;

        // Reused 3D-export offscreen target (synchronous per tile, never in flight at teardown).
        DisposeExport3DTarget();

        // Reference pipeline disposes first — it borrows the texture caches / mesh archives
        // but owns its own copy, so this is independent of the terrain stack.
        DisposeReferencePipeline();
        _navMesh?.Dispose();
        _navMesh = null;
        _collisionDebug?.Dispose();
        _collisionDebug = null;
        _exportFraming?.Dispose();
        _exportFraming = null;
        _water?.Dispose();
        _water = null;
        _skyGeometry?.Dispose();
        _skyGeometry = null;
        _skyBillboards?.Dispose();
        _skyBillboards = null;
        _terrain?.Dispose();
        _terrain = null;
        _textureResolver12?.Dispose();
        _textureResolver12 = null;
        _cellGrid?.Dispose();
        _cellGrid = null;
        _selectionHighlight?.Dispose();
        _selectionHighlight = null;
        DisposeD3D12Backend();
    }
}
