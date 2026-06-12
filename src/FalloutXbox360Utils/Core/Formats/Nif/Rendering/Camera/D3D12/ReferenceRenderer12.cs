#if WINDOWS_GUI
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D12 = Vortice.Direct3D12;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>
///     D3D12 placed-object renderer. Opaque/cutout references are batched by cached submesh
///     and rendered with instancing; blended/effect submeshes remain sorted back-to-front.
/// </summary>
internal sealed class ReferenceRenderer12 : Abstractions.IReferenceRenderer
{
    private const uint PerFrameByteSize = 64;
    private const uint PerDrawByteSize = 128;
    // 3 float4 (material) + uint4 (tex indices) + uint base + uint3 pad = 80 bytes.
    private const uint InstanceDrawByteSize = 80;
    private const float FrustumCullMargin = 512f;

    // Cold mesh realization is render-thread work. By DEFAULT there is no per-frame COUNT cap: the
    // wall-clock time budget (MaxUploadMillisecondsPerFrame) + the mesh cache's byte budget pace
    // uploads, so the per-frame *cost* is bounded by frame-TIME rather than an arbitrary mesh count.
    // A count cap couples loading rate to frame rate (N meshes × FPS); time-budgeting keeps the
    // per-frame cost constant regardless of FPS, which is the correct "spend a fixed slice of each
    // frame" pacing. The env var still imposes an explicit count for profiling.
    private static readonly int MaxNewUploadsPerFrame = ParsePositiveIntEnvironment(
        EnvironmentVariables.Viewer.ReferenceUploadsPerFrame,
        defaultValue: int.MaxValue,
        min: 1,
        max: int.MaxValue);

    // Wall-clock ceiling on render-thread mesh-upload work per frame: the primary pacer now that the
    // count cap is lifted by default. Caps *how long* uploading may take so a frame entering a dense
    // area can't spike. A single very large mesh can still consume a frame, but this stops clusters
    // of completed decoded meshes from being realized together.
    private static readonly double MaxUploadMillisecondsPerFrame = ParsePositiveDoubleEnvironment(
        EnvironmentVariables.Viewer.ReferenceUploadMillisecondsPerFrame,
        defaultValue: 2.0,
        min: 0.25,
        max: 10.0);

    // A5 — distance LOD for tiny clutter only. References whose bounding radius is below this are
    // culled past SmallPropDistanceFraction of the view distance. Kept deliberately small (96) so
    // it only drops debris/rocks/small props — solar reflectors (~150-200), cars, shacks and
    // buildings stay visible to the far plane (a prior 256 threshold dropped reflector rows).
    private const float SmallPropLodRadius = 96f;
    private const float SmallPropDistanceFraction = 0.6f;
    private const int OpaqueBatchStaleFrameWindow = 120;
    private const int OpaqueBatchPruneIntervalFrames = 30;
    private const ShaderFlags EnableUnboundedDescriptorTables = (ShaderFlags)0x00100000;

    private readonly GpuDevice12 _gpu;
    private readonly GpuCommandRecorder12 _recorder;
    private readonly GpuRingBuffer12 _ringBuffer;
    private readonly GpuRootSignature12 _rootSignature;
    private readonly GpuDescriptorHeapAllocator12 _cbvSrvUavHeap;
    private readonly ReferenceMeshCache12 _meshCache;
    private readonly ID3D12PipelineState _opaqueBackPso;
    private readonly ID3D12PipelineState _opaqueDoublePso;
    private readonly byte[] _blendedVsBytecode;
    private readonly byte[] _psBytecode;
    private readonly Dictionary<BlendPipelineKey, ID3D12PipelineState> _blendPsos = new();
    private readonly Dictionary<CachedSubmesh12, OpaqueBatchState> _opaqueBatches = new();
    private readonly List<OpaqueBatchState> _activeOpaqueBatches = new(256);
    private readonly List<CachedSubmesh12> _staleOpaqueBatchKeys = new(64);
    private readonly List<BlendedReferenceDraw> _blendedDraws = new(256);
    private readonly List<global::FalloutXbox360Utils.WorldSpatialCell> _candidateCells = new();

    // Candidates returned by the per-cell spatial broadphase before exact sphere/frustum cull.
    private readonly List<RenderableReference> _cullCandidateScratch = new(256);

    // 4-pre Item A — survivors of the per-cell cull pass, reused across cells. The render
    // loop now runs cull and mesh-resolve as two passes per cell so we can wrap each phase
    // in a single Stopwatch range instead of timing per REFR (which was costing ~0.2 ms
    // per frame at 5K REFRs just for the GetTimestamp calls).
    private readonly List<RenderableReference> _cullSurvivorScratch = new(256);

    // 4-pre Item B — per-frame memoization: MeshId → resolved CachedNifMesh12 (or null
    // when the cache hasn't uploaded it yet this frame). Cleared at the top of each
    // Render() so per-frame inserts (which may evict older entries) only affect entries
    // we haven't yet read. Cuts the cull loop's per-REFR string-key dict lookup
    // (~80 ns × 5000 REFRs ≈ 0.4 ms) down to a uint-keyed lookup (~25 ns) after the
    // first sighting of each MeshId.
    private readonly Dictionary<uint, CachedNifMesh12?> _resolvedMeshesThisFrame = new(256);
    private readonly bool _disableReferenceFrustum =
        EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.DisableReferenceFrustum);

    // A5 distance-LOD is now OFF by default — it was removing things the user wanted to see.
    // Opt back in with FALLOUT_VIEWER_REFERENCE_DISTANCE_LOD=1 once the bounds are trusted.
    private readonly bool _enableDistanceLod =
        EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.ReferenceDistanceLod);

    private Dictionary<(int gx, int gy), CellRecord>? _cells;
    private global::FalloutXbox360Utils.WorldSpatialIndex? _spatialIndex;
    private global::FalloutXbox360Utils.WorldRenderCache? _renderCache;
    private int _opaqueBatchFrameId;
    private bool _disposed;

    public ReferenceRenderer12(
        GpuDevice12 gpu,
        GpuCommandRecorder12 recorder,
        GpuRingBuffer12 ringBuffer,
        GpuRootSignature12 rootSignature,
        GpuDescriptorHeapAllocator12 cbvSrvUavHeap,
        ReferenceMeshCache12 meshCache)
    {
        _gpu = gpu;
        _recorder = recorder;
        _ringBuffer = ringBuffer;
        _rootSignature = rootSignature;
        _cbvSrvUavHeap = cbvSrvUavHeap;
        _meshCache = meshCache;

        _blendedVsBytecode = CompileEmbeddedShader("reference.vert.hlsl", "main", "vs_5_1");
        var instancedVsBytecode = CompileEmbeddedShader("reference_instanced.vert.hlsl", "main", "vs_5_1");
        _psBytecode = CompileEmbeddedShader("reference.frag.hlsl", "main", "ps_5_1");
        _opaqueBackPso = CreatePipelineState(instancedVsBytecode, _psBytecode, doubleSided: false, blendAttachment: null,
            depthWriteEnabled: true);
        _opaqueDoublePso = CreatePipelineState(instancedVsBytecode, _psBytecode, doubleSided: true, blendAttachment: null,
            depthWriteEnabled: true);
    }

    public int ReferencesDrawnLastFrame { get; private set; }
    public global::FalloutXbox360Utils.WorldRenderStats LastStats { get; } = new();
    public bool DetailedProfilingEnabled { get; set; }
    public bool ShowInitiallyDisabled { get; set; }

    private bool _streamingThrottled = true;

    /// <summary>
    ///     When <c>false</c>, the per-frame mesh-streaming budget (upload count + time + bytes, decode
    ///     starts + concurrency) is lifted so a single <see cref="Render"/> loads everything visible
    ///     instead of the live loop's smoothness-preserving trickle. Used by the on-demand top-down
    ///     overlay, which has no framerate target; the live 60fps loop leaves it <c>true</c>.
    ///     Forwards to the shared <see cref="ReferenceMeshCache12.StreamingThrottled"/>.
    /// </summary>
    public bool StreamingThrottled
    {
        get => _streamingThrottled;
        set
        {
            _streamingThrottled = value;
            _meshCache.StreamingThrottled = value;
        }
    }

    public void LoadData(
        global::FalloutXbox360Utils.WorldRenderCache renderCache,
        Dictionary<(int gx, int gy), CellRecord> cells,
        global::FalloutXbox360Utils.WorldSpatialIndex? spatialIndex)
    {
        _renderCache = renderCache;
        _cells = cells;
        _spatialIndex = spatialIndex;
    }

    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder)
    {
        ReferencesDrawnLastFrame = 0;
        LastStats.Reset();
        _meshCache.ResetFrameStats();
        if (_cells is null || _cells.Count == 0 || _renderCache is null) return 0;

        var started = StartTiming();
        var cmd = _recorder.CommandList;
        var frameIndex = _recorder.FrameIndex;

        var segmentStarted = StartTiming();
        var perFrameAlloc = _ringBuffer.Allocate(frameIndex, PerFrameByteSize, GpuRingBuffer12.CbAlignment);
        unsafe { *(Matrix4x4*)perFrameAlloc.CpuPtr = viewProj; }

        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, perFrameAlloc.GpuAddress);
        // 4a — bindless: bind the unbounded SRV table once per frame to heap slot 0
        // (the persistent region's start). All bindless `textures[index]` accesses by the
        // PS resolve through this single binding for the rest of the frame.
        cmd.SetGraphicsRootDescriptorTable(GpuRootSignature12.Slots.BindlessSrvTable, _cbvSrvUavHeap.BindlessHeapStartGpu);
        LastStats.ReferenceStateSetupMilliseconds = ElapsedMilliseconds(segmentStarted);

        var drawn = 0;
        var throttled = _streamingThrottled;
        var uploadBudget = throttled ? MaxNewUploadsPerFrame : int.MaxValue;
        var uploadTimeBudget = new FrameUploadTimeBudget(MaxUploadMillisecondsPerFrame);
        var cylinderRadius = cylinder.Radius;
        var cylinderX = cylinder.Position.X;
        var cylinderY = cylinder.Position.Y;
        var hasFrustum = !_disableReferenceFrustum;
        var frustum = hasFrustum ? Frustum.FromViewProjection(viewProj) : default;
        Frustum? broadphaseFrustum = hasFrustum ? frustum : null;

        double cullMs = 0;
        double meshUploadMs = 0;
        double cbUpdateMs = 0;
        double srvBindMs = 0;
        double drawCallMs = 0;
        int cellsVisited = 0;
        int candidates = 0;
        int culled = 0;
        int missingMeshes = 0;
        int texturePending = 0;
        int referencesWithReadyMesh = 0;
        int submeshDraws = 0;
        int srvBinds = 0;

        BeginOpaqueBatches();
        _blendedDraws.Clear();
        _resolvedMeshesThisFrame.Clear();

        foreach (var cell in EnumerateVisibleCells(cylinder))
        {
            var cullStarted = StartTiming();
            _cullCandidateScratch.Clear();
            var totalPlacements = _renderCache.QueryPlacementCandidates(
                cell,
                cylinderX,
                cylinderY,
                cylinderRadius,
                broadphaseFrustum,
                FrustumCullMargin,
                _cullCandidateScratch);
            if (totalPlacements == 0)
            {
                cullMs += ElapsedMilliseconds(cullStarted);
                continue;
            }

            cellsVisited++;
            if (_cullCandidateScratch.Count == 0)
            {
                cullMs += ElapsedMilliseconds(cullStarted);
                continue;
            }
            candidates += _cullCandidateScratch.Count;

            // 4-pre Item A — two-pass per cell so each phase needs only ONE Stopwatch
            // range instead of two per REFR. Survivors of the cull pass land in
            // _cullSurvivorScratch; the mesh-resolve pass reads from there. Preserves
            // both `cullMs` and `meshUploadMs` HUD fields with the same semantics
            // (cull time = bounds + frustum tests; mesh time = cache hit lookup + batch
            // additions). At 5K REFRs the Stopwatch.GetTimestamp() overhead drops from
            // ~0.2 ms (10K samples) to ~0.01 ms (4 samples per visited cell × 300 cells).
            _cullSurvivorScratch.Clear();

            var smallPropCutoff = cylinderRadius * SmallPropDistanceFraction;
            var smallPropCutoffSq = smallPropCutoff * smallPropCutoff;
            foreach (var r in _cullCandidateScratch)
            {
                // Hide initially-disabled REFRs unless the toggle is on (default off = hidden),
                // matching the 2D viewer. Counted as culled so the HUD reflects the filter.
                if (!ShowInitiallyDisabled && r.IsInitiallyDisabled)
                {
                    culled++;
                    continue;
                }
                var dx = r.BoundsCenter.X - cylinderX;
                var dy = r.BoundsCenter.Y - cylinderY;
                var distSq = dx * dx + dy * dy;
                var maxDist = cylinderRadius + r.BoundsRadius;
                var rejected = distSq > maxDist * maxDist;
                // A5 — distance LOD: drop small props (clutter — barrels, debris, rocks) well
                // beyond the near field. Shrinks the high-view-distance working set (fewer
                // decodes/uploads/draws). Large props (BoundsRadius >= threshold: cars, shacks,
                // buildings) are never distance-culled so landmark-scale geometry stays visible to
                // the far plane. Trade-off, opted in: small objects fade out in the distance.
                if (_enableDistanceLod && !rejected && r.BoundsRadius < SmallPropLodRadius && distSq > smallPropCutoffSq)
                {
                    rejected = true;
                }
                if (!rejected && hasFrustum)
                {
                    rejected = !frustum.IntersectsSphere(r.BoundsCenter, r.BoundsRadius + FrustumCullMargin);
                }
                if (rejected) culled++;
                else _cullSurvivorScratch.Add(r);
            }
            cullMs += ElapsedMilliseconds(cullStarted);

            var meshStarted = StartTiming();
            for (var si = 0; si < _cullSurvivorScratch.Count; si++)
            {
                var r = _cullSurvivorScratch[si];
                // 4-pre Item B — per-frame memoized resolve. First sighting of a MeshId
                // pays one string-keyed _meshCache.GetOrUpload (+ potential mid-frame Insert
                // + LRU touch); every later REFR with the same MeshId hits this map
                // directly (uint-keyed). At 5K REFRs / ~300 unique meshes that drops the
                // cull-loop's mesh-lookup cost ~3× steady-state.
                if (!_resolvedMeshesThisFrame.TryGetValue(r.MeshId, out var mesh))
                {
                    // Stop *starting* new GPU uploads once the per-frame time budget is spent;
                    // GetOrUpload still queues background decodes and returns already-resident
                    // meshes, so the remainder simply appears over the next frame(s). Skipped when
                    // unthrottled (overlay) so one pass uploads everything that has decoded.
                    if (throttled && uploadBudget > 0 && uploadTimeBudget.IsExpired)
                    {
                        uploadBudget = 0;
                    }
                    // Nearest-first decode priority: squared XY distance from the view point, so a
                    // dense area's foreground meshes decode before its far edge (the queue persists
                    // across frames). Cheap — only computed on the first sighting of each MeshId.
                    var pdx = r.BoundsCenter.X - cylinderX;
                    var pdy = r.BoundsCenter.Y - cylinderY;
                    mesh = _meshCache.GetOrUpload(r.ModelPath, ref uploadBudget, pdx * pdx + pdy * pdy);
                    _resolvedMeshesThisFrame[r.MeshId] = mesh;
                }
                if (mesh is null)
                {
                    missingMeshes++;
                    continue;
                }
                if (!mesh.TexturesReady)
                {
                    texturePending++;
                    continue;
                }

                var anySubmeshDrawn = false;
                foreach (var sub in mesh.Submeshes)
                {
                    if (sub.AlphaRenderMode == NifAlphaRenderMode.Blend)
                    {
                        var worldCenter = Vector3.Transform(sub.LocalBoundsCenter, r.WorldMatrix);
                        _blendedDraws.Add(new BlendedReferenceDraw(
                            r.WorldMatrix,
                            sub,
                            Vector3.DistanceSquared(worldCenter, cylinder.Position),
                            sub.AlphaState,
                            sub.RenderState,
                            sub.TextureState));
                        anySubmeshDrawn = true;
                        continue;
                    }

                    var pso = sub.DoubleSided ? _opaqueDoublePso : _opaqueBackPso;
                    var batch = GetOpaqueBatch(sub, pso);
                    // Only the world matrix is per-instance. Material/texture state
                    // (AlphaState/RenderState/TextureState + bindless TexIndices) is identical
                    // across the whole batch — it comes from the submesh, which IS the batch key
                    // — so it is uploaded once per batch via the InstanceDraw CBV at draw time
                    // instead of being copied into every instance record.
                    batch.Instances.Add(r.WorldMatrix);
                    anySubmeshDrawn = true;
                }

                if (anySubmeshDrawn)
                {
                    referencesWithReadyMesh++;
                    drawn++;
                }
            }
            meshUploadMs += ElapsedMilliseconds(meshStarted);
        }

        ID3D12PipelineState? currentPso = null;
        DrawOpaqueBatches(cmd, frameIndex, ref currentPso, ref cbUpdateMs, ref srvBindMs, ref drawCallMs, ref srvBinds, ref submeshDraws);
        DrawBlended(cmd, frameIndex, ref currentPso, ref cbUpdateMs, ref drawCallMs, ref submeshDraws);

        ReferencesDrawnLastFrame = drawn;
        LastStats.ReferenceCellsVisited = cellsVisited;
        LastStats.ReferenceCandidates = candidates;
        LastStats.ReferenceCulled = culled;
        LastStats.ReferenceMeshMissing = missingMeshes;
        LastStats.ReferenceTexturePending = texturePending;
        LastStats.ReferenceDrawn = referencesWithReadyMesh;
        LastStats.ReferenceSubmeshDraws = submeshDraws;
        LastStats.ReferenceSrvBinds = srvBinds;
        LastStats.ReferenceMeshCacheMisses = _meshCache.FrameCacheMisses;
        LastStats.ReferenceDecodeRequests = _meshCache.FrameDecodeRequests;
        LastStats.ReferenceQueuedDecodes = _meshCache.FrameQueuedDecodes;
        LastStats.ReferenceDecodeStarts = _meshCache.FrameDecodeStarts;
        LastStats.ReferenceActiveDecodes = _meshCache.FrameActiveDecodes;
        LastStats.ReferenceCpuDecodedMeshCacheHits = _meshCache.FrameCpuDecodedCacheHits;
        LastStats.ReferenceCpuDecodedMeshCacheMisses = _meshCache.FrameCpuDecodedCacheMisses;
        LastStats.ReferenceCpuDecodedMeshNegativeHits = _meshCache.FrameCpuDecodedNegativeHits;
        LastStats.ReferenceGpuUploads = _meshCache.FrameGpuUploads;
        LastStats.ReferenceUploadByteBudgetDeferrals = _meshCache.FrameByteBudgetDeferrals;
        LastStats.ReferenceCompressedTextureUploads = _meshCache.FrameCompressedTextureUploads;
        LastStats.ReferenceRgbaTextureUploads = _meshCache.FrameRgbaTextureUploads;
        LastStats.ReferenceTextureQueuedResolves = _meshCache.FrameQueuedTextureResolves;
        LastStats.ReferenceTextureActiveResolves = _meshCache.FrameActiveTextureResolves;
        LastStats.ReferenceTexturePendingResolves = _meshCache.PendingTextureResolves;
        LastStats.ReferenceTexturePendingUploads = _meshCache.PendingTextureUploads;
        LastStats.ReferenceCullMilliseconds = cullMs;
        LastStats.ReferenceMeshUploadMilliseconds = meshUploadMs;
        LastStats.ReferenceCbUpdateMilliseconds = cbUpdateMs;
        LastStats.ReferenceSrvBindMilliseconds = srvBindMs;
        LastStats.ReferenceDrawCallMilliseconds = drawCallMs;
        LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
        return drawn;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var pso in _blendPsos.Values)
        {
            pso.Dispose();
        }
        _blendPsos.Clear();
        _opaqueDoublePso.Dispose();
        _opaqueBackPso.Dispose();
    }

    private IEnumerable<CellRecord> EnumerateVisibleCells(VisibilityCylinder cylinder)
    {
        if (_spatialIndex is not null)
        {
            _spatialIndex.QueryCellsInRadius(
                cylinder.Position.X,
                -cylinder.Position.Y,
                cylinder.Radius,
                _candidateCells);
            foreach (var candidate in _candidateCells)
            {
                yield return candidate.Cell;
            }
            yield break;
        }

        foreach (var (key, cell) in _cells!)
        {
            if (cylinder.ContainsCell(key.gx, key.gy))
            {
                yield return cell;
            }
        }
    }

    private void DrawOpaqueBatches(
        ID3D12GraphicsCommandList cmd,
        int frameIndex,
        ref ID3D12PipelineState? currentPso,
        ref double cbUpdateMs,
        ref double srvBindMs,
        ref double drawCallMs,
        ref int srvBinds,
        ref int submeshDraws)
    {
        var totalInstances = 0;
        var activeBatchCount = 0;
        foreach (var batchState in _activeOpaqueBatches)
        {
            var count = batchState.Instances.Count;
            if (count == 0) continue;
            totalInstances += count;
            activeBatchCount++;
        }
        if (totalInstances == 0) return;

        var instanceStride = (uint)Marshal.SizeOf<Matrix4x4>();
        var instanceBytes = instanceStride * (uint)totalInstances;
        var instanceAlloc = _ringBuffer.Allocate(frameIndex, instanceBytes + instanceStride - 1, alignment: 16);
        var instanceByteOffset = AlignUp(instanceAlloc.ByteOffset, instanceStride);
        var instanceCpuPtr = instanceAlloc.CpuPtr + (int)(instanceByteOffset - instanceAlloc.ByteOffset);

        var offset = 0;
        unsafe
        {
            // Bulk-copy each batch's world matrices in one memcpy (CollectionsMarshal.AsSpan ->
            // Span.CopyTo) rather than per-element struct assignment. Only the 64-byte matrix is
            // copied now; per-batch material moves into the InstanceDraw CBV below.
            var span = new Span<Matrix4x4>((void*)instanceCpuPtr, totalInstances);
            foreach (var batchState in _activeOpaqueBatches)
            {
                var worlds = batchState.Instances;
                if (worlds.Count == 0) continue;
                CollectionsMarshal.AsSpan(worlds).CopyTo(span.Slice(offset, worlds.Count));
                offset += worlds.Count;
            }
        }

        var instanceGpuAddress = instanceAlloc.GpuAddress + (instanceByteOffset - instanceAlloc.ByteOffset);
        BindReferenceInstanceBuffer(cmd, instanceGpuAddress, ref srvBinds, ref srvBindMs);

        var startInstance = 0u;
        foreach (var batchState in _activeOpaqueBatches)
        {
            var batch = batchState.Instances;
            if (batch.Count == 0) continue;

            if (!ReferenceEquals(currentPso, batchState.Pso))
            {
                cmd.SetPipelineState(batchState.Pso);
                currentPso = batchState.Pso;
            }

            var cbStarted = StartTiming();
            // Per-batch material/texture state pulled from the submesh (the batch key) — these
            // are identical for every instance in the batch, so they live here, not per instance.
            var sub = batchState.Submesh;
            var instanceDraw = new InstanceDrawConstants(
                sub.AlphaState,
                sub.RenderState,
                sub.TextureState,
                new TexIndexQuad(sub.Diffuse.BindlessIndex, sub.Normal.BindlessIndex, 0, 0),
                startInstance);
            var instanceDrawAlloc = _ringBuffer.Allocate(frameIndex, InstanceDrawByteSize, GpuRingBuffer12.CbAlignment);
            unsafe { *(InstanceDrawConstants*)instanceDrawAlloc.CpuPtr = instanceDraw; }
            cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerDrawCbv, instanceDrawAlloc.GpuAddress);
            cbUpdateMs += ElapsedMilliseconds(cbStarted);

            var drawStarted = StartTiming();
            cmd.IASetVertexBuffers(0, batchState.Submesh.VertexBufferView);
            cmd.IASetIndexBuffer(batchState.Submesh.IndexBufferView);
            cmd.DrawIndexedInstanced((uint)batchState.Submesh.IndexCount, (uint)batch.Count, 0, 0, 0);
            drawCallMs += ElapsedMilliseconds(drawStarted);
            startInstance += (uint)batch.Count;
            submeshDraws++;
            LastStats.ReferenceInstancedDraws++;
            LastStats.ReferenceInstances += batch.Count;
        }

        LastStats.ReferenceBatches = activeBatchCount;
    }

    private void DrawBlended(
        ID3D12GraphicsCommandList cmd,
        int frameIndex,
        ref ID3D12PipelineState? currentPso,
        ref double cbUpdateMs,
        ref double drawCallMs,
        ref int submeshDraws)
    {
        if (_blendedDraws.Count == 0) return;

        _blendedDraws.Sort(static (a, b) => b.DistanceSquared.CompareTo(a.DistanceSquared));
        foreach (var draw in _blendedDraws)
        {
            var pso = GetBlendPipeline(
                draw.Submesh.SrcBlendMode,
                draw.Submesh.DstBlendMode,
                draw.Submesh.DoubleSided);
            DrawBlendedSubmesh(
                cmd,
                frameIndex,
                draw,
                pso,
                ref currentPso,
                ref cbUpdateMs,
                ref drawCallMs);
            submeshDraws++;
            LastStats.ReferenceBlendedDraws++;
        }
    }

    private void DrawBlendedSubmesh(
        ID3D12GraphicsCommandList cmd,
        int frameIndex,
        BlendedReferenceDraw draw,
        ID3D12PipelineState pso,
        ref ID3D12PipelineState? currentPso,
        ref double cbUpdateMs,
        ref double drawCallMs)
    {
        if (!ReferenceEquals(currentPso, pso))
        {
            cmd.SetPipelineState(pso);
            currentPso = pso;
        }

        var cbStarted = StartTiming();
        var perDraw = new PerDrawConstants
        {
            World = draw.World,
            AlphaState = draw.AlphaState,
            RenderState = draw.RenderState,
            TextureState = draw.TextureState,
            // 4a — bindless TexIndices on the blended draw path. Same convention as the
            // instanced path: .x = diffuse slot, .y = normal slot.
            TexIndices = new TexIndexQuad(
                draw.Submesh.Diffuse.BindlessIndex,
                draw.Submesh.Normal.BindlessIndex,
                0,
                0),
        };
        var perDrawAlloc = _ringBuffer.Allocate(frameIndex, PerDrawByteSize, GpuRingBuffer12.CbAlignment);
        unsafe { *(PerDrawConstants*)perDrawAlloc.CpuPtr = perDraw; }
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerDrawCbv, perDrawAlloc.GpuAddress);
        cbUpdateMs += ElapsedMilliseconds(cbStarted);

        var drawStarted = StartTiming();
        cmd.IASetVertexBuffers(0, draw.Submesh.VertexBufferView);
        cmd.IASetIndexBuffer(draw.Submesh.IndexBufferView);
        cmd.DrawIndexedInstanced((uint)draw.Submesh.IndexCount, 1, 0, 0, 0);
        drawCallMs += ElapsedMilliseconds(drawStarted);
    }

    private void BindReferenceInstanceBuffer(
        ID3D12GraphicsCommandList cmd,
        ulong instanceGpuAddress,
        ref int srvBinds,
        ref double srvBindMs)
    {
        var srvStarted = StartTiming();
        cmd.SetGraphicsRootShaderResourceView((uint)GpuRootSignature12.Slots.ReferenceInstanceSrv, instanceGpuAddress);
        srvBinds++;
        srvBindMs += ElapsedMilliseconds(srvStarted);
    }

    private ID3D12PipelineState GetBlendPipeline(byte srcBlendMode, byte dstBlendMode, bool doubleSided)
    {
        var key = new BlendPipelineKey(srcBlendMode, dstBlendMode, doubleSided);
        if (_blendPsos.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var rtBlend = new D12.RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = NifD3D12BlendMapper.ResolveBlendFactor(srcBlendMode),
            DestinationBlend = NifD3D12BlendMapper.ResolveBlendFactor(dstBlendMode),
            BlendOperation = D12.BlendOperation.Add,
            SourceBlendAlpha = D12.Blend.One,
            DestinationBlendAlpha = D12.Blend.One,
            BlendOperationAlpha = D12.BlendOperation.Max,
            RenderTargetWriteMask = D12.ColorWriteEnable.All
        };

        var pso = CreatePipelineState(_blendedVsBytecode, _psBytecode, doubleSided, rtBlend, depthWriteEnabled: false);
        _blendPsos[key] = pso;
        return pso;
    }

    private ID3D12PipelineState CreatePipelineState(
        byte[] vsBytecode,
        byte[] psBytecode,
        bool doubleSided,
        D12.RenderTargetBlendDescription? blendAttachment,
        bool depthWriteEnabled)
    {
        var rasterizer = new D12.RasterizerDescription
        {
            FillMode = D12.FillMode.Solid,
            CullMode = doubleSided ? D12.CullMode.None : D12.CullMode.Back,
            FrontCounterClockwise = true,
            DepthClipEnable = true,
        };

        var depth = new D12.DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = depthWriteEnabled ? D12.DepthWriteMask.All : D12.DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.LessEqual,
            StencilEnable = false,
        };

        var blend = new D12.BlendDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false,
        };
        blend.RenderTarget[0] = blendAttachment ?? new D12.RenderTargetBlendDescription
        {
            BlendEnable = false,
            SourceBlend = D12.Blend.One,
            DestinationBlend = D12.Blend.Zero,
            BlendOperation = D12.BlendOperation.Add,
            SourceBlendAlpha = D12.Blend.One,
            DestinationBlendAlpha = D12.Blend.Zero,
            BlendOperationAlpha = D12.BlendOperation.Add,
            RenderTargetWriteMask = D12.ColorWriteEnable.All,
        };

        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = _rootSignature.RootSignature,
            VertexShader = vsBytecode,
            PixelShader = psBytecode,
            BlendState = blend,
            RasterizerState = rasterizer,
            DepthStencilState = depth,
            InputLayout = new InputLayoutDescription(GpuMeshBufferFactory12.InputElements),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { Format.B8G8R8A8_UNorm },
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
            SampleMask = uint.MaxValue,
        };
        return _gpu.Device.CreateGraphicsPipelineState(psoDesc);
    }

    private OpaqueBatchState GetOpaqueBatch(CachedSubmesh12 submesh, ID3D12PipelineState pso)
    {
        if (!_opaqueBatches.TryGetValue(submesh, out var batch))
        {
            batch = new OpaqueBatchState(submesh, pso);
            _opaqueBatches.Add(submesh, batch);
        }

        if (batch.LastTouchedFrame != _opaqueBatchFrameId)
        {
            batch.LastTouchedFrame = _opaqueBatchFrameId;
            _activeOpaqueBatches.Add(batch);
        }

        return batch;
    }

    private void BeginOpaqueBatches()
    {
        unchecked
        {
            _opaqueBatchFrameId++;
        }

        foreach (var batch in _activeOpaqueBatches)
        {
            batch.Instances.Clear();
        }
        _activeOpaqueBatches.Clear();

        if (_opaqueBatchFrameId % OpaqueBatchPruneIntervalFrames == 0)
        {
            PruneStaleOpaqueBatches();
        }
    }

    private void PruneStaleOpaqueBatches()
    {
        var staleBeforeFrame = _opaqueBatchFrameId - OpaqueBatchStaleFrameWindow;
        foreach (var (key, batch) in _opaqueBatches)
        {
            if (batch.LastTouchedFrame < staleBeforeFrame)
            {
                _staleOpaqueBatchKeys.Add(key);
            }
        }

        foreach (var key in _staleOpaqueBatchKeys)
        {
            _opaqueBatches.Remove(key);
        }
        _staleOpaqueBatchKeys.Clear();
    }

    private long StartTiming() => DetailedProfilingEnabled ? Stopwatch.GetTimestamp() : 0;

    private static double ElapsedMilliseconds(long started) =>
        started == 0 ? 0 : Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static int ParsePositiveIntEnvironment(string name, int defaultValue, int min, int max)
    {
        var raw = EnvironmentVariables.Get(name);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }

    private static double ParsePositiveDoubleEnvironment(string name, double defaultValue, double min, double max)
    {
        var raw = EnvironmentVariables.Get(name);
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }

    private static uint AlignUp(uint value, uint alignment) =>
        alignment == 0 ? value : ((value + alignment - 1) / alignment) * alignment;

    private static byte[] CompileEmbeddedShader(string name, string entryPoint, string profile)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Embedded shader resource not found: {name}");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();

        var shaderFlags = source.Contains("textures[]", StringComparison.Ordinal)
            ? EnableUnboundedDescriptorTables
            : ShaderFlags.None;

        var result = Compiler.Compile(
            source,
            Array.Empty<ShaderMacro>(),
            include: null!,
            entryPoint,
            sourceName: name,
            profile,
            shaderFlags,
            EffectFlags.None,
            out Blob? bytecode, out Blob? errors);

        if (result.Failure || bytecode is null)
        {
            var errorText = errors?.AsString() ?? "(no error blob)";
            errors?.Dispose();
            bytecode?.Dispose();
            throw new InvalidOperationException($"HLSL compile failed for {name} ({profile}): {errorText}");
        }

        errors?.Dispose();
        try { return bytecode.AsBytes().ToArray(); }
        finally { bytecode.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerDrawConstants
    {
        public Matrix4x4 World;
        public Vector4 AlphaState;
        public Vector4 RenderState;
        public Vector4 TextureState;
        // 4a — bindless TexIndices for the (non-instanced) blended draw path. Kept here so the
        // PS interface stays uniform (it expects vTexIndices regardless of which VS produced it).
        // TextureState + TexIndices bring this to 128 bytes total.
        public TexIndexQuad TexIndices;
    }

    /// <summary>
    ///     Per-batch (per <c>DrawIndexedInstanced</c>) constants for the instanced opaque path,
    ///     matching the <c>InstanceDraw</c> cbuffer in <c>reference_instanced.vert.hlsl</c>.
    ///     Material/texture state is identical across a batch (it comes from the submesh, the
    ///     batch key), so it is uploaded once here instead of per instance. <see cref="InstanceBase" />
    ///     is the start offset of this batch's world matrices inside the shared instance buffer.
    ///     `.x` of <see cref="TexIndices" /> is the diffuse bindless slot, `.y` the normal slot.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct InstanceDrawConstants(
        Vector4 AlphaState,
        Vector4 RenderState,
        Vector4 TextureState,
        TexIndexQuad TexIndices,
        uint InstanceBase,
        uint Padding0 = 0,
        uint Padding1 = 0,
        uint Padding2 = 0);

    /// <summary>
    ///     Packed 4-uint TexIndices field. C# has no <c>uint4</c> primitive, so we lay out
    ///     four contiguous uints with explicit <see cref="LayoutKind.Sequential" /> so the
    ///     C# struct blits cleanly into the HLSL <c>uint4</c>. <see cref="X" /> = diffuse,
    ///     <see cref="Y" /> = normal, <see cref="Z" />/<see cref="W" /> reserved (per-instance
    ///     emissive / detail slots for future extension).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct TexIndexQuad(uint X, uint Y, uint Z, uint W);

    private readonly record struct BlendPipelineKey(byte SrcBlendMode, byte DstBlendMode, bool DoubleSided);

    private sealed class OpaqueBatchState(CachedSubmesh12 submesh, ID3D12PipelineState pso)
    {
        public CachedSubmesh12 Submesh { get; } = submesh;
        public ID3D12PipelineState Pso { get; } = pso;

        /// <summary>Per-instance world matrices for this batch (the only per-instance data;
        /// material/texture state is per-batch and lives in the InstanceDraw CBV at draw time).</summary>
        public List<Matrix4x4> Instances { get; } = new(16);
        public int LastTouchedFrame { get; set; }
    }

    private readonly record struct BlendedReferenceDraw(
        Matrix4x4 World,
        CachedSubmesh12 Submesh,
        float DistanceSquared,
        Vector4 AlphaState,
        Vector4 RenderState,
        Vector4 TextureState);
}
#endif
