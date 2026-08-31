using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    private void EnableGpuTimestamps(string reason)
    {
        if (_gpuTimestampProfiler12 is not null || _gpu12 is null)
        {
            return;
        }

        try
        {
            _gpuTimestampProfiler12 = new GpuTimestampProfiler12(_gpu12);
            Log.Info("WorldView3DControl: D3D12 GPU timestamp profiling enabled ({0}).", reason);
            RendererProfilerTrace.Event("gpu-timestamps-enabled", new Dictionary<string, object?>
            {
                ["frame"] = _profileFrameIndex,
                ["reason"] = reason
            });
        }
        catch (Exception ex)
        {
            Log.Warn("WorldView3DControl: failed to enable GPU timestamp profiling: {0}", ex.Message);
        }
    }

    private void EnableReferencePipelineStatistics()
    {
        if (_gpuReferencePipelineStatistics12 is not null || _gpu12 is null)
        {
            return;
        }

        try
        {
            _gpuReferencePipelineStatistics12 = new GpuReferencePipelineStatistics12(_gpu12);
            Log.Info("WorldView3DControl: primary-reference D3D12 pipeline statistics enabled.");
            RendererProfilerTrace.Event(
                "gpu-reference-pipeline-statistics-status",
                new Dictionary<string, object?>
                {
                    ["frame"] = _profileFrameIndex,
                    ["requested"] = true,
                    ["enabled"] = true,
                    ["error"] = null
                });
        }
        catch (Exception ex)
        {
            _gpuReferencePipelineStatistics12?.Dispose();
            _gpuReferencePipelineStatistics12 = null;
            Log.Warn(
                "WorldView3DControl: failed to enable primary-reference D3D12 pipeline statistics: {0}",
                ex.Message);
            RendererProfilerTrace.Event(
                "gpu-reference-pipeline-statistics-status",
                new Dictionary<string, object?>
                {
                    ["frame"] = _profileFrameIndex,
                    ["requested"] = true,
                    ["enabled"] = false,
                    ["error"] = ex.Message
                });
        }
    }

    /// <summary>
    ///     Initializes the D3D12 backend stack: device + direct queue + per-frame command
    ///     recorder + upload-heap ring + shader-visible descriptor heaps + shared root
    ///     signature. Called from <see cref="OnLoaded" />.
    /// </summary>
    private bool TryInitD3D12Backend()
    {
        try
        {
            // FALLOUT_VIEWER_D3D12_DEBUG=1 enables the D3D12 debug + validation layer. Catches
            // resource-state mismatches, descriptor mismatches, lifetime-after-free, etc. that
            // the runtime would otherwise silently corrupt or hard-crash on. ~30% CPU overhead
            // per draw on validation alone — leave off in normal use.
            var debugLayer = EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.D3D12Debug);
            // PreferHardwareThenWarp: a machine with no 12_0-capable GPU gets the WARP software
            // rasterizer (slow, visibly reported via IsSoftwareAdapter) instead of a blank panel.
            _gpu12 = BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDevice12.Create(
                debugLayer,
                BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuAdapterPolicy.PreferHardwareThenWarp);
            if (_gpu12 is null) return false;

            _commandRecorder12 =
                new BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuCommandRecorder12(_gpu12);
            // Force the fixed-footprint trackers' static init HERE, on the UI thread, before any
            // renderer worker exists. CLR type-init lock contention on an STA thread goes through
            // Thread::DoAppropriateWait → CoWaitForMultipleHandles and therefore PUMPS: if a worker
            // ever won the race to first-touch these while the UI thread sat inside a XAML callback,
            // that would be another instance of the 0xc000027b reentrancy fail-fast. Today every
            // touch happens to be on this thread; this makes the ordering deterministic rather than
            // incidental.
            _ = BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuFixedFootprintTracker12.LocalInstance;
            _ = BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuFixedFootprintTracker12.NonLocalInstance;
            // Ring size per frame slot (env-overridable; default SCALES WITH SYSTEM RAM — upload
            // heaps are system-memory-backed). Shared by every renderer's per-draw CBs and the
            // reference instance uploads. A whole-map perspective view (max render distance, most
            // of the worldspace visible) genuinely fills 64 MB — user-reported frame-slot
            // exhaustion — and every consumer soft-fails (TryAllocate: stops adding draws,
            // presents what fit) rather than abandoning the frame, so big-RAM machines buy
            // headroom instead of dropped draws.
            var ringMegabytes = EnvironmentVariables.GetClampedInt(
                EnvironmentVariables.Viewer.RingBufferMegabytes,
                defaultValue: BethesdaMultitool.Core.Resources.AdaptiveMemoryDefaults.RingBufferMegabytes(
                    BethesdaMultitool.Core.Resources.AdaptiveMemoryDefaults.SystemMemoryMb),
                min: 16, max: 512);
            _ringBuffer12 = new BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuRingBuffer12(
                _gpu12,
                BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuCommandRecorder12.FramesInFlight,
                bytesPerFrame: (uint)ringMegabytes * 1024 * 1024);
            // CBV/SRV/UAV heap layout (4a — bindless): the persistent region holds every texture's
            // bindless SRV (stable index over a worldspace session); the remainder rings across N
            // frames for per-frame transient SRVs (the reference renderer's per-frame instance
            // structured-buffer SRV, water's per-frame instance-SRV copy, etc.).
            //
            // The persistent/per-frame split is pure bookkeeping over one heap that is committed at
            // `capacity` regardless, so widening the persistent share costs NOTHING. It was 16384,
            // which was sized for FNV's ~5K unique textures and is a hard failure for dense
            // worldspaces — AllocatePersistent THROWS on exhaustion rather than degrading, and
            // persistent slots are consumed by both texture caches, the shadow cascades, the ~80
            // water noise tiles and the reflection/depth SRVs. 49152 matches the resident-mesh LRU
            // ceiling (ReferenceMeshCapacityPlanner.CeilingCapacity) so slot supply tracks the mesh
            // population that consumes it, and still leaves 40960 per-frame slots — ample, since
            // bindless lookups made the legacy per-batch SRV pattern the exception.
            _cbvSrvUavHeap12 = new BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDescriptorHeapAllocator12(
                    _gpu12,
                    Vortice.Direct3D12.DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                    capacity: 131072,
                    framesInFlight: BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuCommandRecorder12
                        .FramesInFlight,
                    persistentCapacity: 49152)
                .RegisterWith(BethesdaMultitool.Core.Diagnostics.ResourceRegistry.Instance, "viewer");
            _rootSignature12 = BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuRootSignature12.Create(_gpu12);
            _deletionQueue12 = new BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDeletionQueue12(
                    framesToHold: BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuCommandRecorder12
                        .FramesInFlight)
                .RegisterWith(BethesdaMultitool.Core.Diagnostics.ResourceRegistry.Instance, "viewer");
            if (_forceGpuTimestamps)
            {
                EnableGpuTimestamps("forced");
            }

            if (_referencePipelineStatisticsRequested)
            {
                EnableReferencePipelineStatistics();
            }

            // The cell-grid overlay is the simplest D3D12 renderer: PSO creation +
            // ring-buffer-backed CB/VB + root signature + command list draws, exercising the
            // whole lightweight backend path the heavier renderers (terrain/reference/water) share.
            _cellGrid = new BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.CellGridDebugRenderer12(
                _gpu12, _commandRecorder12, _ringBuffer12, _rootSignature12)
            {
                DetailedProfilingEnabled = _profileLogging
            };

            // Selection outline overlay. Same lightweight backend handles as the cell grid;
            // created once and reused across worldspace/cell switches (selection state lives on the
            // control and is cleared on those switches).
            _selectionHighlight = new BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.SelectionHighlightRenderer12(
                _gpu12, _commandRecorder12, _ringBuffer12, _rootSignature12);

            // WaterRenderer12 doesn't depend on per-ESM data (water height is
            // discovered at LoadData), so we can instantiate up front alongside CellGrid.
            _water = new BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.WaterRenderer12(
                _gpu12, _commandRecorder12, _ringBuffer12, _rootSignature12, _cbvSrvUavHeap12, _deletionQueue12)
            {
                DetailedProfilingEnabled = _profileLogging,
                ModernPipelineEnabled = EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.ModernWater)
            };

            // Real sky-dome NIF renderer — the exterior sky drawn from the climate's own Sky\*.nif
            // (atmosphere gradient + stars + clouds layers) on their authored UVs. Per-layer geometry +
            // textures are loaded from data in EnsureSkyTexturesResolved; reads the shared b3 sky colors.
            _skyGeometry = new BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.SkyGeometryRenderer12(
                _gpu12, _commandRecorder12, _ringBuffer12, _rootSignature12, _cbvSrvUavHeap12);

            // Textured sky billboards — sun (disc + glare) + moon, drawn after the gradient. Uses the
            // shared bindless table (textures resolved per-climate from the terrain texture cache).
            _skyBillboards = new BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.SkyBillboardRenderer12(
                _gpu12, _commandRecorder12, _ringBuffer12, _rootSignature12, _cbvSrvUavHeap12);

            // Navmesh overlay — like water/cellgrid, no per-ESM dependency at construction;
            // the navmesh-by-cell data + spatial index arrive in LoadData via TryBuildCellGrid.
            _navMesh = new BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.NavMeshRenderer12(
                _gpu12, _commandRecorder12, _ringBuffer12, _rootSignature12, _deletionQueue12)
            {
                DetailedProfilingEnabled = _profileLogging
            };

            // Collision-cage debug overlay (off by default). Reads collision meshes live from the
            // reference cache (created later in the reference pipeline) via ResolveCollisionMesh; the
            // spatial index arrives in LoadData via TryBuildCellGrid like the other overlays.
            _collisionDebug = new BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.CollisionDebugRenderer12(
                _gpu12, _commandRecorder12, _ringBuffer12, _rootSignature12, _deletionQueue12,
                _referenceEnabledOverrides)
            {
                CollisionResolver = ResolveCollisionMesh,
                CollisionWarmup = ResolveOrWarmCollisionMesh,
                ShowDisabled = _showDisabled,
                DayNightStates = _dayNightStates
            };

            // Export framing preview: reuses the collision line shaders (no spatial index needed — it
            // draws the export's world-AABB + a view gizmo from a handful of edges).
            _exportFraming = new BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.ExportFramingOverlay(
                _gpu12, _commandRecorder12, _ringBuffer12, _rootSignature12);

            // TerrainRenderer12 is instantiated lazily in LoadData (needs the per-ESM
            // LTEX/TXST dictionaries + BSA paths) rather than here.

            return true;
        }
        catch (Exception ex)
        {
            // ERROR, and the WHOLE exception rather than just .Message: a shader-compile failure
            // arrives here as an InvalidOperationException whose FXC diagnostics (the X#### code and
            // the offending line) live in the message body and inner exception. Logging .Message
            // alone discarded exactly the text needed to diagnose it. The caller
            // (Lifecycle.cs OnLoaded) surfaces "3D view unavailable" to the user.
            Log.Error("WorldView3DControl: D3D12 backend init failed: {0}", ex);
            // Record how far init got BEFORE disposal nulls _gpu12 — the caller uses this to
            // tell "no device could be created" apart from "device fine, later stage failed".
            _backendFailedAfterDeviceCreation = _gpu12 is not null;
            DisposeD3D12Backend();
            return false;
        }
    }

    /// <summary>
    ///     Tears down the D3D12 backend in reverse construction order, draining the
    ///     GPU first so resources aren't referenced by in-flight command lists.
    /// </summary>
    private void DisposeD3D12Backend()
    {
        _commandRecorder12?.WaitForGpuIdle();
        // The ordinary frame-start drain is two submissions behind the CPU. A timed profiler exit
        // can therefore arrive immediately after an aggregate flush while its last one or two GPU
        // rows are still pending. The idle wait above makes every pending readback safe; publish the
        // producing-frame rows now, while the profiler host still has its trace open, before the
        // query heaps and readback buffers disappear. This closes the tail for both query streams.
        EmitCompletedGpuFrames();
        EmitCompletedReferencePipelineStatistics();
        _gpuReferencePipelineStatistics12?.Dispose();
        _gpuReferencePipelineStatistics12 = null;
        _gpuTimestampProfiler12?.Dispose();
        _gpuTimestampProfiler12 = null;
        // Drain pending deletions now that the GPU is idle — anything queued is safe to release.
        _deletionQueue12?.Dispose();
        _deletionQueue12 = null;
        _rootSignature12?.Dispose();
        _rootSignature12 = null;
        _shadowMap?.Dispose();
        _shadowMap = null; // its SRV slot lives in the heap disposed next
        _cbvSrvUavHeap12?.Dispose();
        _cbvSrvUavHeap12 = null;
        _depthSrv = null; // its slot lived in the heap just disposed; reallocate on the next surface
        _captureDepthSrv = null; // same heap — the capture-depth slot dies with it too
        _waterOpaqueSnapshotSrv = null;
        _waterOpaqueSnapshotSrvResource = null;
        _captureWaterOpaqueSnapshotSrv = null;
        _opaqueDepthSnapshotSrv = null; // same heap — the depth-snapshot slot dies with it too
        _opaqueDepthSnapshotSrvResource = null;

        _ringBuffer12?.Dispose();
        _ringBuffer12 = null;
        _surface12?.Dispose();
        _surface12 = null;
        _commandRecorder12?.Dispose();
        _commandRecorder12 = null;
        _gpu12?.Dispose();
        _gpu12 = null;
    }
}
