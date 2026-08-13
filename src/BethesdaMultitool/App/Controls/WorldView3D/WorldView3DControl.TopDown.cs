using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Rasterization;
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

    // Interior top-down overlay state: the interior currently loaded for the overlay + its converged
    // ceiling-clip world Z (the height below which the floor plan shows; null until the scene's mesh
    // bounds have streamed in enough to compute it). Reset when the interior changes / on exterior.
    private uint? _topDownInteriorFormId;
    private float? _topDownInteriorCeilingZ;

    /// <inheritdoc />
    async Task<TopDownRender?> ITopDownSceneRenderer.RenderTopDownAsync(
        float worldMinX, float worldMaxX, float worldMinY, float worldMaxY,
        int pixelWidth, int pixelHeight, bool showDisabled, bool showWater, uint? worldspaceFormId,
        IReadOnlyCollection<PlacedObjectCategory> hiddenCategories,
        bool enableLighting, float gameHour, uint? interiorCellFormId, CancellationToken ct)
    {
        if (!CanRenderTopDownCore) return null;
        if (worldMaxX <= worldMinX || worldMaxY <= worldMinY) return null;
        if (pixelWidth <= 0 || pixelHeight <= 0) return null;
        ct.ThrowIfCancellationRequested();

        // Sync the 3D control's active view to what the 2D map is showing (the 3D view picks its own
        // initial view independently). Switching reloads the renderers' cells, so the overlay matches
        // the map instead of a stale view. No-op when already current. Interior cells take the
        // single-cell path + a ceiling clip (the roof is removed so the floor plan shows); exteriors
        // render the whole worldspace as before.
        float? ceilingClipZ = null;
        if (interiorCellFormId is uint interiorId)
        {
            if (!EnsureActiveInteriorCell(interiorId)) return null;
            if (_topDownInteriorFormId != interiorId)
            {
                _topDownInteriorFormId = interiorId;
                _topDownInteriorCeilingZ = null; // converge afresh for the new interior
            }

            // First render has no resident mesh bounds yet → no clip (roof shows briefly); the
            // post-render pass below computes the real ceiling and re-requests until it converges.
            ceilingClipZ = _topDownInteriorCeilingZ;
        }
        else
        {
            if (!EnsureActiveExteriorWorldspace(worldspaceFormId)) return null;
            _topDownInteriorFormId = null;
            _topDownInteriorCeilingZ = null;
        }

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
        var viewProj = TopDownViewProjBuilder.BuildViewProj(
            worldMinX, worldMaxX, worldMinY, worldMaxY, ceilingClipZ);
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

        // === Terrain depth pre-pass scope gate ===
        // The pre-pass exists so placed references depth-test against the ground (partially buried
        // meshes get clipped); it writes NO colour — the 2D map draws its own terrain layer beneath.
        // Its cell set is the CULL CYLINDER, which at whole-worldspace zoom is the entire worldspace:
        // measured on WastelandNV zoom-to-fit, terrainVisible=16,395 cells every pass, admitted for
        // full-detail build (33x33 grid + 4-layer blend weights + textures, ~150 KB of GPU geometry
        // each) at a hard 16 cells/pass — 583 passes reached only 9,312 cells, i.e. ~6.3 minutes and
        // ~2.4 GB of geometry to finish. Worse, `NewUploads` stays non-zero for that whole time, so
        // StreamingQuiescence never reports quiesced and the overlay re-renders the FULL scene
        // (1.29 M-candidate cull + ~41 k submesh draws + readback, ~371 ms) on a loop indefinitely.
        //
        // At that zoom one CELL covers ~16 screen px, so the ground silhouette a 33x33 mesh resolves
        // is far below what the readback can show, and every reference on it is sub-pixel. Gate the
        // pre-pass on the SAME projected-cell-size threshold the 2D map already uses to swap its own
        // terrain layer to the aggregate LOD bitmap (TerrainAggregateScreenPxThreshold = 24 px/cell),
        // so the two agree about when the view is an overview rather than a per-cell view.
        // TRADE-OFF (visible, deliberate): below the threshold references are no longer occluded by
        // terrain, so geometry authored beneath the surface (vault/cave shells, foundation blocks)
        // can show as specks. Interiors always keep the pre-pass — their ceiling clip is what makes
        // the floor plan readable, and a single interior cell is never an overview.
        var worldUnitsPerPixel = (worldMaxX - worldMinX) / MathF.Max(ssWidth, 1);
        var pixelsPerCell = _cellSize / MathF.Max(worldUnitsPerPixel, 1e-6f);
        var terrainDepthEnabled = interiorCellFormId is not null
                                  || pixelsPerCell >= TopDownTerrainDepthMinPixelsPerCell;

        ulong fenceValue;
        bool isComplete;
        bool isFullySettled;
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
                // (reference.frag). Fog stays OFF (the 2D map has no fog control), but lighting follows
                // the 2D map's lighting toggle + its time-of-day (gameHourOverride) so the overlay's
                // directional sun/ambient match the map's hillshade. Lighting off ⇒ flat legacy shade.
                // enableShadows off: the top-down overlay records no shadow pass (and drives its own
                // hour), so the live view's cached map would be stale/mismatched here.
                BindAtmosphereConstants(cmd, recorder.FrameIndex, enableFog: false,
                    enableLighting: enableLighting, gameHourOverride: gameHour, enableShadows: false,
                    shadingCameraPosOverride: cylinder.Position,
                    lightVisibility: cylinder);

                target.Bind(cmd);
                if (terrainDepthEnabled)
                {
                    _terrain!.RenderDepthOnly(viewProj, cylinder); // depth pre-pass: ground occludes refs
                }
                // SpeedTree leaf cards re-face the billboard basis; the live frame sets it from the
                // perspective camera, but this ortho capture looks straight down (east→+X right,
                // north→+Y up per TopDownViewProjBuilder). Lay the leaf cards flat in the ground plane
                // so foliage is visible from above instead of edge-on (otherwise SPT canopies vanish in
                // the overlay). Pin wind strength/time for a clean deterministic pose. In FNV,
                // weather strength zero intentionally retains GRASS2000's five-unit minimum bend;
                // only the Animations switch requests the undeformed rest pose.
                _references.SetLeafBillboardBasis(System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY);
                // MUST be the CAPTURE seam, never SetWind: the leaf basis above is re-supplied by the
                // live frame every tick, but the wind rig INTEGRATES history — SetWind(dir, 0, 0) writes
                // the live rig's lastTime = 0 and foldStrength = 0, so the next live frame's
                // `_matrixTimes *= foldStrength / S` zeroes all four oscillator clocks. Every canopy
                // group then restarts at phase 0 (yaw 0, pitch 0.61·S — the cos extreme) in unison, and
                // this overlay re-renders on a loop while streaming converges, so the reset repeats: the
                // 2026-08-11 "SPT trees sway too strongly again" report. The capture rig is per-render
                // state the live frame's SetWind clears, so a one-shot render cannot perturb live sway.
                _references.SetWindForCapture(WindDirection, 0f, 0f);
                _references.Render(
                    viewProj,
                    cylinder,
                    deferBlended: false,
                    cameraPosition: cylinder.Position,
                    cameraForward: -System.Numerics.Vector3.UnitZ); // textured objects, depth-tested
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
                    // Clear the LIVE window's planar sky-reflection binding, for the same reason
                    // SceneCapture does (see WorldView3DControl.SceneCapture.cs): the reflection is a
                    // SCREEN-SPACE lookup (water_common.hlsli's SampleSkyReflection divides
                    // SV_Position by the scene dimensions the live frame uploaded), and both the
                    // bindless index and those dimensions PERSIST across frames on the water renderer.
                    // Left bound, this overlay would sample the live perspective window's mirrored sky
                    // at the live window's scale — stretched affinely across whatever world rectangle
                    // the map is showing, so the reflection's world-space feature size tracked 1/zoom.
                    // Unlike the capture path we do NOT render our own: this view is ORTHOGRAPHIC and
                    // top-down, where a planar mirror reflects the zenith for every pixel, so a
                    // mirrored-camera target carries no information a parallel projection can use.
                    // Unbinding falls back to the direction-based sky gradient (lerp(skyHorizon,
                    // skyTop, saturate(R.z)) with R = reflect(-V, N)), which depends only on the
                    // world-space normal and view vector and is therefore zoom- and
                    // resolution-invariant. No restore is needed: the live frame re-supplies the
                    // binding unconditionally every frame.
                    _water.SetWaterReflection(null, 0, 0);
                    // Pin the water clock: the 2D map is a STATIC view of the world (user ruling
                    // 2026-08-10), and the overlay re-renders while streaming converges — a live
                    // clock re-phases the noise scroll, shader time, and the legacy water00..31
                    // frame on every pass. t=0 matches the SetWindForCapture(dir, 0, 0) pose pin above and
                    // makes settled overlay renders pixel-stable. No restore needed: the live frame
                    // renders through the null-time overloads, which fall back to the live clock.
                    _water.RenderAtTime(viewProj, cylinder, default, 0f, isPerspectiveProjection: false);
                }
                target.RecordReadback(cmd);
            }
            finally
            {
                recorder.EndFrame();
            }

            fenceValue = recorder.LastSubmittedFenceValue;

            // Interior ceiling clip: now that this render populated the survivor set + their mesh
            // bounds, compute the scene's real geometric ceiling and clip just below it (the roof slab
            // is thin relative to room height, so a small margin off the top removes it while keeping
            // walls/furniture). If the clip changed materially from what THIS render used, request one
            // more pass so the displayed overlay is the clipped one. Converges in 1–2 extra renders as
            // meshes stream in; exteriors never set a clip.
            var ceilingNeedsAnotherPass = false;
            if (interiorCellFormId is not null &&
                _references!.GetRenderedSceneWorldZExtent() is { } zext)
            {
                var span = MathF.Max(zext.Max - zext.Min, 0f);
                var margin = Math.Clamp(span * 0.05f, 64f, 512f);
                var clip = zext.Max - margin;
                if (_topDownInteriorCeilingZ is not float prev || MathF.Abs(prev - clip) > 16f)
                {
                    _topDownInteriorCeilingZ = clip;
                    ceilingNeedsAnotherPass = true;
                }
            }

            // Terrain only participates in convergence when this pass actually rendered it. When the
            // scope gate skipped the pre-pass, _terrain.LastStats is whatever the last call left
            // behind (RenderInternal resets it on entry, so nothing clears it while skipped) — reading
            // it would pin the overlay incomplete on a stale non-zero NewUploads forever.
            var terrainStatsForQuiescence = terrainDepthEnabled ? _terrain.LastStats : null;
            isComplete = StreamingQuiescence.IsQuiesced(
                    _references!.LastStats, terrainStatsForQuiescence, strict: false)
                && !ceilingNeedsAnotherPass;
            // Strict variant for one-shot consumers (2D map EXPORT tiles): also waits out the
            // texture-withheld window. The live overlay keeps keying on the loose IsComplete —
            // a pinned ReferenceTexturePending would make it re-render forever.
            isFullySettled = StreamingQuiescence.IsQuiesced(
                    _references.LastStats, terrainStatsForQuiescence, strict: true)
                && !ceilingNeedsAnotherPass;
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
            // Deliberately NOT restoring the 3D view's category filter here. The two sets genuinely
            // differ (this control hides Sky, the 2D map legend hides nothing), so restoring per pass
            // was two REAL filter changes per overlay render, and each one invalidates the cull cache
            // outright — measured cullVeto=Invalidated on 205/205 WastelandNV zoom-to-fit passes,
            // costing a full 1.29 M-candidate re-cull (85-190 ms) on every one. The live frame path
            // re-asserts its own set before it renders (WorldView3DControl.Frame.cs), which is the
            // only moment the live value has to be correct, so nothing observable changes.
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
        // Snapshot diagnostics before the async readback. LastStats is reset by the next render,
        // while these values belong to the exact pixels returned below (and let the 2D verification
        // harness prove that SpeedTree geometry participated in the top-down pass).
        var referenceStats = _references!.LastStats;
        var referenceInstances = referenceStats.ReferenceInstances;
        var referenceDrawn = referenceStats.ReferenceDrawn;
        var speedTreeBranchInstances = referenceStats.ReferenceSpeedTreeBranchInstances;
        var speedTreeLeafInstances = referenceStats.ReferenceSpeedTreeLeafInstances;
        var speedTreeBillboardInstances = referenceStats.ReferenceSpeedTreeBillboardInstances;

        // Per-pass cost attribution for the 2D map's convergence benchmark. Without this the map's
        // own trace only shows total request duration, which cannot distinguish "the pass is
        // expensive" (cull/resolve/draw over a whole worldspace) from "there are many passes"
        // (streaming backlog) — the two need opposite fixes.
        if (Map2DProfilerTrace.IsEnabled)
        {
            var terrainStats = _terrain.LastStats;
            Map2DProfilerTrace.Event("topdown-render-stats", string.Concat(
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"cells={referenceStats.ReferenceCellsVisited} candidates={referenceStats.ReferenceCandidates} "),
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"culled={referenceStats.ReferenceCulled} drawn={referenceStats.ReferenceDrawn} "),
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"cullMs={referenceStats.ReferenceCullMilliseconds:F1} meshUpMs={referenceStats.ReferenceMeshUploadMilliseconds:F1} "),
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"cbMs={referenceStats.ReferenceCbUpdateMilliseconds:F1} srvMs={referenceStats.ReferenceSrvBindMilliseconds:F1} "),
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"drawMs={referenceStats.ReferenceDrawCallMilliseconds:F1} refCpuMs={referenceStats.CpuFrameMilliseconds:F1} "),
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"submeshDraws={referenceStats.ReferenceSubmeshDraws} srvBinds={referenceStats.ReferenceSrvBinds} "),
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"meshMissing={referenceStats.ReferenceMeshMissing} texPending={referenceStats.ReferenceTexturePending} "),
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"decodeReq={referenceStats.ReferenceDecodeRequests} decodeQueued={referenceStats.ReferenceQueuedDecodes} "),
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"decodeStarts={referenceStats.ReferenceDecodeStarts} decodeActive={referenceStats.ReferenceActiveDecodes} "),
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"meshUploads={referenceStats.ReferenceGpuUploads} cacheMiss={referenceStats.ReferenceMeshCacheMisses} "),
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"texResolveQueued={referenceStats.ReferenceTextureQueuedResolves} texResolveActive={referenceStats.ReferenceTextureActiveResolves} "),
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"texPendResolve={referenceStats.ReferenceTexturePendingResolves} texPendUpload={referenceStats.ReferenceTexturePendingUploads} "),
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"cullCacheHit={referenceStats.ReferenceCullCacheHit} cullVeto={referenceStats.ReferenceCullCacheVeto} batchReuse={referenceStats.ReferenceBatchesReused} "),
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"terrainCpuMs={terrainStats.CpuFrameMilliseconds:F1} terrainNewUploads={terrainStats.NewUploads} "),
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"terrainDraws={terrainStats.TerrainDraws} terrainTruncated={terrainStats.TerrainDrawsTruncated} "),
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"terrainVisible={terrainStats.VisibleCandidates} terrainTexMiss={terrainStats.TextureCacheMisses} "),
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"terrainTexPendResolve={terrainStats.TexturePendingResolves} terrainTexPendUpload={terrainStats.TexturePendingUploads} "),
                string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"terrainDepth={terrainDepthEnabled} pxPerCell={pixelsPerCell:F1} ssPixels={ssWidth}x{ssHeight}")));
        }

        var readbackTask = Task.Run(() =>
        {
            WaitForFrameFence(frameFence, fenceValue);
            ct.ThrowIfCancellationRequested();
            var ssBytes = target.ReadbackToBytes();
            var pixels = supersample > 1
                ? NifSpriteRenderer.Downsample(ssBytes, ssWidth, ssHeight, supersample)
                : ssBytes;
            return new TopDownRender(pixels, finalW, finalH,
                worldMinX, worldMaxX, worldMinY, worldMaxY, isComplete, isFullySettled,
                referenceInstances, referenceDrawn,
                speedTreeBranchInstances, speedTreeLeafInstances, speedTreeBillboardInstances);
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

