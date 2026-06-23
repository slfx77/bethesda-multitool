using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    // Explicit interface implementation: the interface + TopDownRender are internal, so these can't
    // be public members on this public control (CS0050). The 2D map only ever calls them via the
    // ITopDownSceneRenderer reference it's handed, so explicit implementation is exactly right.

    private bool CanRenderTopDownCore =>
        _gpu12 is not null && _commandRecorder12 is not null &&
        _ringBuffer12 is not null && _cbvSrvUavHeap12 is not null &&
        _rootSignature12 is not null && _deletionQueue12 is not null &&
        _terrain is not null && _references is not null;

    /// <inheritdoc />
    bool ITopDownSceneRenderer.CanRenderTopDown => CanRenderTopDownCore;

    /// <inheritdoc />
    async Task<TopDownRender?> ITopDownSceneRenderer.RenderTopDownAsync(
        float worldMinX, float worldMaxX, float worldMinY, float worldMaxY,
        int pixelWidth, int pixelHeight, bool showDisabled, bool showWater, uint? worldspaceFormId,
        IReadOnlyCollection<PlacedObjectCategory> hiddenCategories, CancellationToken ct)
    {
        if (!CanRenderTopDownCore) return null;
        if (worldMaxX <= worldMinX || worldMaxY <= worldMinY) return null;
        if (pixelWidth <= 0 || pixelHeight <= 0) return null;
        ct.ThrowIfCancellationRequested();

        // Sync the 3D control's active worldspace to the one the 2D map is showing (the 3D view picks
        // its own initial worldspace independently). Switching reloads the renderers' cells, so the
        // overlay matches the map instead of showing a stale worldspace. No-op when already current.
        if (!EnsureActiveExteriorWorldspace(worldspaceFormId)) return null;

        var finalW = Math.Clamp(pixelWidth, 1, MaxTopDownFinalDimension);
        var finalH = Math.Clamp(pixelHeight, 1, MaxTopDownFinalDimension);
        // When scene MSAA is active the offscreen target is multisampled (GpuOffscreenSceneTarget12
        // reads GpuDevice12.SceneSampleCount), so MSAA provides the edge AA and we skip the
        // supersample multiply — keeping the target memory-neutral (4x MSAA at NxN == 1 sample at
        // 2Nx2N) and the terrain/reference PSOs (SampleDesc = SceneSampleCount) matching the target.
        var supersample = _gpu12!.SceneSampleCount > 1 ? 1 : TopDownSupersample;
        var ssWidth = finalW * supersample;
        var ssHeight = finalH * supersample;

        // Orthographic top-down viewProj + covering cylinder (see TopDownViewProjBuilder for the
        // orientation contract — east→right, north→top, no readback flip). The renderers treat
        // viewProj as an opaque matrix, so this bypasses the perspective CameraState.
        var viewProj = TopDownViewProjBuilder.BuildViewProj(worldMinX, worldMaxX, worldMinY, worldMaxY);
        var cylinder = TopDownViewProjBuilder.BuildCoverCylinder(
            worldMinX, worldMaxX, worldMinY, worldMaxY, _cellSize);

        // Reuse the offscreen target across requests; recreate only when the supersampled size
        // changes. Avoids per-request allocation of two committed textures + RTV/DSV heaps + a
        // readback buffer (steady allocator/GPU churn that compounded the overlay-on slowdown).
        GpuOffscreenSceneTarget12 target;
        try
        {
            if (_topDownTarget is null || _topDownTargetW != ssWidth || _topDownTargetH != ssHeight)
            {
                _topDownTarget?.Dispose();
                _topDownTarget = new GpuOffscreenSceneTarget12(_gpu12!, ssWidth, ssHeight);
                _topDownTargetW = ssWidth;
                _topDownTargetH = ssHeight;
            }
            target = _topDownTarget;
        }
        catch (Exception ex)
        {
            Log.Warn("WorldView3DControl: top-down target alloc failed: {0}", ex.Message);
            _topDownTarget = null;
            _topDownTargetW = _topDownTargetH = 0;
            return null;
        }

        ulong fenceValue;
        bool isComplete;
        var prevShowDisabled = _references!.ShowInitiallyDisabled;
        var prevRefThrottled = _references.StreamingThrottled;
        var prevTerrainThrottled = _terrain!.StreamingThrottled;
        try
        {
            _references.ShowInitiallyDisabled = showDisabled;
            // Apply the 2D map's category filter for this overlay render so the rendered meshes match
            // the map's legend toggles (restored to the 3D view's own set in the finally block).
            _references.SetHiddenCategories(hiddenCategories);
            // The overlay is on-demand, not a 60fps loop, so lift the live renderers' per-frame
            // streaming budget: a single render starts every visible decode (CPU-saturating
            // concurrency) and uploads everything that has decoded. Combined with the caller's
            // re-render-while-incomplete loop, the overlay loads meshes as fast as the hardware
            // allows instead of trickling 8/frame. Restored below so the live 3D loop stays smooth.
            _references.StreamingThrottled = false;
            _terrain.StreamingThrottled = false;
            var recorder = _commandRecorder12!;
            recorder.BeginFrame();
            try
            {
                var cmd = recorder.CommandList;
                _deletionQueue12!.Tick();
                _ringBuffer12!.ResetFrame();
                _cbvSrvUavHeap12!.BeginFrame(recorder.FrameIndex);
                _gpu12!.PumpDebugMessages();

                // SetDescriptorHeaps before SetGraphicsRootSignature (bindless ordering), mirroring
                // RenderFrameD3D12.
                cmd.SetDescriptorHeaps(1, new[] { _cbvSrvUavHeap12.Heap });
                cmd.SetGraphicsRootSignature(_rootSignature12!.RootSignature);

                // Bind the atmosphere CB here too so the references the overlay draws read b3
                // (reference.frag). Fog AND lighting OFF: the overlay is an orthographic top-down
                // capture with no fog/lighting controls on the 2D map, so directional shading and
                // distance fog would bake an uncontrollable look into the composite. Flat shade only.
                BindAtmosphereConstants(cmd, recorder.FrameIndex, enableFog: false, enableLighting: false);

                target.Bind(cmd);
                _terrain!.RenderDepthOnly(viewProj, cylinder); // depth pre-pass: ground occludes refs
                // SpeedTree leaf cards re-face the billboard basis; the live frame sets it from the
                // perspective camera, but this ortho capture looks straight down (east→+X right,
                // north→+Y up per TopDownViewProjBuilder). Lay the leaf cards flat in the ground plane
                // so foliage is visible from above instead of edge-on (otherwise SPT canopies vanish in
                // the overlay). Wind off for a clean static capture.
                _references.SetLeafBillboardBasis(System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY);
                _references.SetWind(WindDirection, 0f, 0f);
                _references.Render(viewProj, cylinder);        // textured objects, depth-tested
                if (showWater && _water is not null)
                {
                    // Height-correct water for the overlay: the offscreen DSV already holds the terrain +
                    // reference depth, so a depth-tested water pass lets the ortho-topmost surface win —
                    // submerged geometry is covered by water, geometry ABOVE the water plane (docks,
                    // bridges) shows through. A flat 2D water layer can't do this; that's why the map
                    // suppresses its own water wherever this overlay draws. No depth SRV → the hardware
                    // depth-test water PSO (same path the live frame uses when no SRV is bound); near/far
                    // are unused without the SRV soft-fade.
                    _water.SetNifWaterPlanes(_references.NifWaterPlanes);
                    _water.SetSceneDepth(NoDepthSrv, _camera.NearPlane, _camera.FarPlane);
                    _water.Render(viewProj, cylinder);
                }
                target.RecordReadback(cmd);
            }
            finally
            {
                recorder.EndFrame();
            }

            fenceValue = recorder.LastSubmittedFenceValue;
            isComplete = TopDownStreamingComplete();
            _gpu12.PumpDebugMessages();
        }
        catch (Exception ex)
        {
            // The render failed mid-record, so the shared target's resource state may be
            // inconsistent. EndFrame already submitted whatever was recorded; drain the GPU before
            // dropping the shared target so the next request rebuilds it cleanly (and we never
            // release a resource the GPU still references).
            try { _commandRecorder12?.WaitForGpuIdle(); }
            catch (Exception waitEx) { Log.Warn("WaitForGpuIdle after top-down failure threw: {0}", waitEx.Message); }
            _topDownTarget?.Dispose();
            _topDownTarget = null;
            _topDownTargetW = _topDownTargetH = 0;
            Log.Warn("WorldView3DControl: top-down render failed: {0}", ex.Message);
            try { _gpu12?.PumpDebugMessages(); }
            catch (Exception pumpEx) { Log.Warn("PumpDebugMessages threw: {0}", pumpEx.Message); }
            return null;
        }
        finally
        {
            _references.ShowInitiallyDisabled = prevShowDisabled;
            // Restore the 3D view's own category filter (the authoritative live-view set).
            _references.SetHiddenCategories(_hiddenCategories);
            _references.StreamingThrottled = prevRefThrottled;
            _terrain.StreamingThrottled = prevTerrainThrottled;
        }

        // Off the UI thread: wait for the submission fence, then map + downsample. The target is
        // SHARED/reused across requests, so the body must NOT dispose it (RecordReadback left the
        // color target back in RenderTarget state for the next Bind). Teardown disposes it in
        // DisposeRenderResources, which first drains _topDownReadbackTask so this map completes
        // before the resource is released. The fence is captured HERE: the body must not dereference
        // _gpu12, which the UI thread nulls during teardown while this task is still in flight.
        var frameFence = _gpu12!.FrameFence;
        var readbackTask = Task.Run(() =>
        {
            WaitForFrameFence(frameFence, fenceValue);
            ct.ThrowIfCancellationRequested();
            var ssBytes = target.ReadbackToBytes();
            var pixels = supersample > 1
                ? NifSpriteRenderer.Downsample(ssBytes, ssWidth, ssHeight, supersample)
                : ssBytes;
            return new TopDownRender(pixels, finalW, finalH,
                worldMinX, worldMaxX, worldMinY, worldMaxY, isComplete);
        });
        _topDownReadbackTask = readbackTask;
        try
        {
            return await readbackTask;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            if (ReferenceEquals(_topDownReadbackTask, readbackTask))
            {
                _topDownReadbackTask = null;
            }
        }
    }

    /// <summary>
    ///     Heuristic completeness: is any terrain/mesh/texture streaming work still ACTIVE this frame?
    ///     While false the 2D map keeps re-requesting so the overlay fills in (the 3D control's live
    ///     loop is idle when the map is showing, so nothing else drives the caches forward).
    ///     <para>
    ///         Keyed on in-flight work (uploads this frame + queued/active decodes + texture queue
    ///         depth) — deliberately NOT on <c>ReferenceMeshMissing</c>/<c>ReferenceTexturePending</c>,
    ///         which count assets that returned null/pending THIS frame and never reach zero when a
    ///         region has permanently-missing/cut meshes or textures (those are negative-cached, not
    ///         re-queued). Settling on "nothing actively loading" lets the overlay converge instead of
    ///         re-requesting forever.
    ///     </para>
    /// </summary>
    private bool TopDownStreamingComplete()
    {
        var t = _terrain!.LastStats;
        var r = _references!.LastStats;
        return t.NewUploads == 0
            && r.ReferenceGpuUploads == 0
            && r.ReferenceQueuedDecodes == 0
            && r.ReferenceActiveDecodes == 0
            && r.ReferenceTexturePendingResolves == 0
            && r.ReferenceTexturePendingUploads == 0;
    }

    /// <summary>Blocks until <paramref name="fence" /> reaches <paramref name="value" />. Safe to
    /// call off the UI thread (fence read + event are thread-safe). The fence is a parameter, not
    /// read from <c>_gpu12</c>, because callers run on worker threads after teardown may have
    /// nulled the field. Always completes (the offscreen frame is a single submission), so callers
    /// can dispose GPU resources afterwards safely.</summary>
    private static void WaitForFrameFence(Vortice.Direct3D12.ID3D12Fence fence, ulong value)
    {
        if (fence.CompletedValue >= value) return;
        using var ev = new AutoResetEvent(false);
        BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.D3D12FenceWaiter.WaitForFence(
            fence, value, ev);
    }
}
