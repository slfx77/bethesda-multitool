#if WINDOWS_GUI
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;
using FalloutXbox360Utils.Core.Orchestration;
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
    // "Dist" loads a SQUARE of half-extent cylinderRadius (Chebyshev). The per-cell sub-cell broadphase
    // is a circular radius query, so widen its radius by √2 to reach the square's corners; the exact
    // per-REFR Chebyshev test below then trims back to the square (the broadphase only over-includes).
    private const float SquareBroadphaseFactor = 1.41422f;
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

    // Profiler fix #4 — cached cull survivors. The render loop runs the cull (broadphase + per-REFR
    // sphere/frustum tests) and the mesh-resolve/batch build as two SEPARATE passes. When the camera
    // pose, the mesh-bounds generation (grows as meshes resolve and tighten their cull spheres), and
    // the visibility filters are all unchanged frame-to-frame, the cull is SKIPPED and this survivor
    // list is reused — static / settled frames otherwise re-test ~15k candidates every frame (the
    // dominant late-frame cost). The resolve+batch pass still runs each frame so streamed-in meshes
    // appear. Persists across frames (it's the cache); cleared only on a fresh cull or LoadData.
    private readonly List<RenderableReference> _cachedCullSurvivors = new(2048);
    private bool _cullCacheValid;
    private Matrix4x4 _cullCacheViewProj;
    private int _cullCacheMeshRadiusCount;
    private bool _cullCacheShowMarkers;
    private bool _cullCacheShowImposters;
    private bool _cullCacheShowDisabled;
    private int _cullCacheCellsVisited;
    private int _cullCacheCandidates;
    private int _cullCacheCulled;

    // 4-pre Item B — per-frame memoization: MeshId → resolved CachedNifMesh12 (or null
    // when the cache hasn't uploaded it yet this frame). Cleared at the top of each
    // Render() so per-frame inserts (which may evict older entries) only affect entries
    // we haven't yet read. Cuts the cull loop's per-REFR string-key dict lookup
    // (~80 ns × 5000 REFRs ≈ 0.4 ms) down to a uint-keyed lookup (~25 ns) after the
    // first sighting of each MeshId.
    private readonly Dictionary<uint, CachedNifMesh12?> _resolvedMeshesThisFrame = new(256);

    // MeshId → the mesh's local bounding-sphere radius (around the NIF origin), recorded the first
    // time a mesh resolves. Lets the per-REFR cull use the ACTUAL geometry extent (conservative)
    // instead of the OBND/256 fallback, so large meshes stop popping at screen edges / near camera.
    // Persists across frames + worldspaces (radius is static per NIF); bounded by unique-mesh count.
    private readonly Dictionary<uint, float> _meshLocalRadius = new(512);

    // Placed-NIF water planes (WaterShaderProperty geometry diverted out of the drawable submesh set
    // at upload) accumulated as their meshes resolve, deduped per REFR FormId, and handed to
    // WaterRenderer12 each frame. Each plane is static (mesh AABB × placement), so it is computed
    // exactly once; both are reset on LoadData.
    private readonly List<NifWaterPlane> _nifWaterPlanes = new();
    private readonly HashSet<uint> _nifWaterPlaneSeen = new();
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

    // 3D-8: when Render(deferBlended: true) is used, the blended (transparent) reference submeshes are
    // NOT drawn inside Render — the caller invokes RenderBlendedDeferred() after the water pass so water
    // never paints over them. We stash the per-frame CBV address + frame index because the water pass
    // rebinds PerFrameCbv (its own uniforms) and the DSV, which RenderBlendedDeferred must re-establish.
    private ulong _deferredPerFrameCbvAddress;
    private int _deferredFrameIndex;

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

    /// <summary>
    ///     When <c>false</c> (default), engine "marker" objects (XMarker/heading, map/travel/door
    ///     markers, encounter/idle markers) are culled — matching the game, which never renders them
    ///     in play. Driven by <c>RenderableReference.IsMarker</c> (model-path classification).
    /// </summary>
    public bool ShowMarkers { get; set; }

    /// <summary>
    ///     When <c>false</c> (default), imposter references (low-detail distant LOD stand-ins) are
    ///     culled. In a full-detail render the whole worldspace is loaded, so the real STAT/SCOL
    ///     geometry each imposter stands in for is already present — matching the engine, which hides
    ///     the imposter once that geometry's region loads. Driven by <c>RenderableReference.IsImposter</c>.
    /// </summary>
    public bool ShowImposters { get; set; }

    // Per-category visibility filter. References whose RenderableReference.Category is in this set are
    // culled (e.g. activators off by default in the 3D view; the 2D legend's hidden set for the
    // top-down overlay). Mutated only via SetHiddenCategories, which invalidates the cull cache.
    private readonly HashSet<PlacedObjectCategory> _hiddenCategories = [];

    /// <summary>
    ///     Replaces the set of <see cref="PlacedObjectCategory" /> values whose references are hidden.
    ///     Invalidates the cull cache so the next frame re-filters. Pass an empty set to show all.
    /// </summary>
    public void SetHiddenCategories(IEnumerable<PlacedObjectCategory> hidden)
    {
        _hiddenCategories.Clear();
        foreach (var category in hidden) _hiddenCategories.Add(category);
        _cullCacheValid = false;
    }

    /// <summary>
    ///     Number of per-draw allocations that didn't fit the shared ring buffer this frame and were
    ///     skipped (instead of throwing + abandoning the whole frame). Non-zero means the scene is
    ///     denser than the ring can hold in one frame — raise <c>FALLOUT_VIEWER_RING_BUFFER_MB</c>.
    ///     Surfaced in the HUD so the soft cap is visible rather than silent.
    /// </summary>
    public int LastFrameDrawsTruncated { get; private set; }

    /// <summary>
    ///     World-space water planes detected on placed-reference NIFs (cave/pool/reflecting-pool
    ///     water). Accumulates as those references' meshes stream in; the host hands this to
    ///     <see cref="WaterRenderer12.SetNifWaterPlanes" /> so placed water renders with the real
    ///     water shader instead of a flat slab. The list reference is stable across a worldspace load
    ///     (cleared, not reallocated, in <see cref="LoadData" />).
    /// </summary>
    public IReadOnlyList<NifWaterPlane> NifWaterPlanes => _nifWaterPlanes;

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
        // The reference set + spatial index changed — the cached cull survivors are stale.
        _cullCacheValid = false;
        // Drop the accumulated NIF water planes so they don't leak across worldspace loads. Cleared
        // (not reallocated) so the reference the host handed WaterRenderer12 stays valid.
        _nifWaterPlanes.Clear();
        _nifWaterPlaneSeen.Clear();

        // Size the resident-mesh LRU to this worldspace's distinct-mesh working set so it holds the
        // whole set rather than thrashing (evict → re-decode → re-upload) when the count exceeds the
        // default cap. No-op when the capacity env knob pins a fixed cap (auto-size off).
        _meshCache.SetMeshCapacity(
            Core.Resources.ReferenceMeshCapacityPlanner.Plan(CountUniqueMeshPaths(cells)));
    }

    // O(total placements), one-time per worldspace load — negligible next to the rest of load.
    // OrdinalIgnoreCase matches the mesh LRU's key comparer so the count tracks real cache keys.
    private static int CountUniqueMeshPaths(Dictionary<(int gx, int gy), CellRecord> cells)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in cells.Values)
        {
            foreach (var placed in cell.PlacedObjects)
            {
                if (!string.IsNullOrEmpty(placed.ModelPath))
                {
                    seen.Add(placed.ModelPath);
                }
            }
        }

        return seen.Count;
    }

    // IWorldRenderer entry point — draws opaque + blended inline (no deferral). Used by the 2D
    // top-down overlay, which has no water pass.
    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder)
        => Render(viewProj, cylinder, deferBlended: false);

    /// <param name="deferBlended">
    ///     When true, the blended (transparent) reference submeshes are NOT drawn in this call — the
    ///     caller must invoke <see cref="RenderBlendedDeferred" /> after the water pass so water does
    ///     not paint over them (3D-8). False draws opaque + blended inline.
    /// </param>
    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder, bool deferBlended)
    {
        ReferencesDrawnLastFrame = 0;
        LastFrameDrawsTruncated = 0;
        LastStats.Reset();
        _meshCache.ResetFrameStats();
        if (_cells is null || _cells.Count == 0 || _renderCache is null) return 0;

        var started = StartTiming();
        var cmd = _recorder.CommandList;
        var frameIndex = _recorder.FrameIndex;

        var segmentStarted = StartTiming();
        var perFrameAlloc = _ringBuffer.Allocate(frameIndex, PerFrameByteSize, GpuRingBuffer12.CbAlignment);
        unsafe { *(Matrix4x4*)perFrameAlloc.CpuPtr = viewProj; }
        _deferredPerFrameCbvAddress = perFrameAlloc.GpuAddress;
        _deferredFrameIndex = frameIndex;

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
        var uploadTimeBudget = new FrameTimeBudget(MaxUploadMillisecondsPerFrame);
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

        // === Cull pass (or cache reuse) ===
        // The cull (per-cell broadphase + per-REFR filter/sphere/frustum tests) is reused from the
        // previous frame when the camera pose, the mesh-bounds generation (the count of resolved
        // meshes — each new resolve can tighten a cull sphere), and the visibility filters are ALL
        // unchanged. The viewProj is byte-identical frame-to-frame for a still camera, so equality is
        // exact. This skips re-testing ~15k candidates on static / settled frames (profiler fix #4);
        // active streaming keeps re-culling (cheap relative to decode) until the working set settles.
        var cullCacheValid =
            _cullCacheValid
            && _cullCacheViewProj == viewProj
            && _cullCacheMeshRadiusCount == _meshLocalRadius.Count
            && _cullCacheShowMarkers == ShowMarkers
            && _cullCacheShowImposters == ShowImposters
            && _cullCacheShowDisabled == ShowInitiallyDisabled;

        if (cullCacheValid)
        {
            // Restore the cull HUD counters so they don't read zero while the survivor set is reused.
            cellsVisited = _cullCacheCellsVisited;
            candidates = _cullCacheCandidates;
            culled = _cullCacheCulled;
        }
        else
        {
            var cullStarted = StartTiming();
            _cachedCullSurvivors.Clear();
            var smallPropCutoff = cylinderRadius * SmallPropDistanceFraction;
            var smallPropCutoffSq = smallPropCutoff * smallPropCutoff;
            foreach (var cell in EnumerateVisibleCells(cylinder))
            {
                _cullCandidateScratch.Clear();
                var totalPlacements = _renderCache.QueryPlacementCandidates(
                    cell,
                    cylinderX,
                    cylinderY,
                    cylinderRadius * SquareBroadphaseFactor, // reach the square footprint's corners; exact test trims
                    broadphaseFrustum,
                    FrustumCullMargin,
                    _cullCandidateScratch);
                if (totalPlacements == 0 || _cullCandidateScratch.Count == 0) continue;

                cellsVisited++;
                candidates += _cullCandidateScratch.Count;
                foreach (var r in _cullCandidateScratch)
                {
                    // Hide initially-disabled REFRs unless the toggle is on (default off = hidden),
                    // matching the 2D viewer. Counted as culled so the HUD reflects the filter.
                    if (!ShowInitiallyDisabled && r.IsInitiallyDisabled)
                    {
                        culled++;
                        continue;
                    }
                    // Hide engine marker objects (XMarker, map/travel markers, etc.) unless toggled on.
                    // The game strips these in play; we match that for a clean render.
                    if (!ShowMarkers && r.IsMarker)
                    {
                        culled++;
                        continue;
                    }
                    // Hide imposter LOD stand-ins. Their real geometry (STAT/SCOL) lives elsewhere in
                    // the same worldspace (placed at a different origin, not exactly co-located), so in
                    // this full-detail render — where the whole worldspace is loaded — every imposter is
                    // redundant and the engine would have hidden it. Toggle back via ShowImposters.
                    if (!ShowImposters && r.IsImposter)
                    {
                        culled++;
                        continue;
                    }
                    // Per-category visibility (activators off by default; 2D legend toggles for the
                    // top-down overlay). The set is empty in the common case so this is one HashSet
                    // count check per candidate.
                    if (_hiddenCategories.Count > 0 && _hiddenCategories.Contains(r.Category))
                    {
                        culled++;
                        continue;
                    }
                    // Cull bounds: once a mesh is resident, use its ACTUAL local bounding sphere (radius
                    // around the NIF origin, scaled into world) instead of the OBND-or-256-fallback baked
                    // at LoadData. The mesh sphere is conservative (contains all geometry) and centered at
                    // the placement point, so large meshes (highways/buildings) with a missing/tiny OBND no
                    // longer get culled at the screen edge or when the camera is close. Falls back to the
                    // OBND sphere until the mesh first resolves (it then self-corrects on later frames).
                    Vector3 cullCenter;
                    float cullRadius;
                    if (_meshLocalRadius.TryGetValue(r.MeshId, out var localRadius))
                    {
                        cullCenter = r.WorldMatrix.Translation;
                        var basisX = new Vector3(r.WorldMatrix.M11, r.WorldMatrix.M12, r.WorldMatrix.M13);
                        cullRadius = localRadius * basisX.Length(); // uniform-scale assumption (FNV refs)
                    }
                    else
                    {
                        cullCenter = r.BoundsCenter;
                        cullRadius = r.BoundsRadius;
                    }
                    var dx = cullCenter.X - cylinderX;
                    var dy = cullCenter.Y - cylinderY;
                    var distSq = dx * dx + dy * dy; // retained for the small-prop distance LOD below
                    var maxDist = cylinderRadius + cullRadius;
                    // Square ("Dist") footprint: reject iff the object falls outside the half-extent box
                    // along either axis (Chebyshev), so references load as a square matching the terrain/
                    // grid footprint instead of a circle.
                    var rejected = MathF.Abs(dx) > maxDist || MathF.Abs(dy) > maxDist;
                    // A5 — distance LOD: drop small props (clutter — barrels, debris, rocks) well
                    // beyond the near field. Shrinks the high-view-distance working set (fewer
                    // decodes/uploads/draws). Large props (cullRadius >= threshold: cars, shacks,
                    // buildings) are never distance-culled so landmark-scale geometry stays visible to
                    // the far plane. Trade-off, opted in: small objects fade out in the distance.
                    if (_enableDistanceLod && !rejected && cullRadius < SmallPropLodRadius && distSq > smallPropCutoffSq)
                    {
                        rejected = true;
                    }
                    if (!rejected && hasFrustum)
                    {
                        rejected = !frustum.IntersectsSphere(cullCenter, cullRadius + FrustumCullMargin);
                    }
                    if (rejected) culled++;
                    else _cachedCullSurvivors.Add(r);
                }
            }
            cullMs = ElapsedMilliseconds(cullStarted);

            // Snapshot the cache key. MeshRadiusCount is captured BEFORE the resolve pass (below) adds
            // this frame's newly-resolved radii, so the next frame re-culls iff a new mesh resolved.
            _cullCacheValid = true;
            _cullCacheViewProj = viewProj;
            _cullCacheMeshRadiusCount = _meshLocalRadius.Count;
            _cullCacheShowMarkers = ShowMarkers;
            _cullCacheShowImposters = ShowImposters;
            _cullCacheShowDisabled = ShowInitiallyDisabled;
            _cullCacheCellsVisited = cellsVisited;
            _cullCacheCandidates = candidates;
            _cullCacheCulled = culled;
        }

        // === Resolve + batch pass (always runs — decoded meshes stream in across frames) ===
        var meshStarted = StartTiming();
        for (var si = 0; si < _cachedCullSurvivors.Count; si++)
        {
            var r = _cachedCullSurvivors[si];
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
            // Record the resident mesh's true local radius so the NEXT frame's cull uses real
            // geometry extent for this MeshId. A new key here grows _meshLocalRadius.Count, which
            // invalidates the cull cache next frame so the tighter bounds are applied.
            _meshLocalRadius[r.MeshId] = mesh.LocalBoundsRadius;
            // Placed water planes need no textures — emit them as soon as the mesh resolves, once per
            // REFR (the seen-set dedups the per-frame resolve). Transforms the mesh-local water AABB(s)
            // by this placement's world matrix into world-space footprints for WaterRenderer12.
            if (mesh.WaterPlanesLocal.Count > 0 && _nifWaterPlaneSeen.Add(r.FormId))
            {
                AccumulateNifWaterPlanes(r.WorldMatrix, mesh.WaterPlanesLocal);
            }
            if (!mesh.TexturesReady)
            {
                texturePending++;
                continue;
            }

            var anySubmeshDrawn = false;
            foreach (var sub in mesh.Submeshes)
            {
                // Billboards must take the per-draw blended path even when opaque: the instanced
                // opaque path has no per-draw matrix, but a billboard needs a unique camera-facing
                // world matrix per placement.
                if (sub.AlphaRenderMode == NifAlphaRenderMode.Blend || sub.IsBillboard)
                {
                    var worldCenter = Vector3.Transform(sub.LocalBoundsCenter, r.WorldMatrix);
                    var world = sub.IsBillboard
                        ? BuildBillboardWorld(sub.LocalBoundsCenter, r.WorldMatrix, worldCenter, cylinder.Position)
                        : r.WorldMatrix;
                    _blendedDraws.Add(new BlendedReferenceDraw(
                        world,
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
        meshUploadMs = ElapsedMilliseconds(meshStarted);

        ID3D12PipelineState? currentPso = null;
        DrawOpaqueBatches(cmd, frameIndex, ref currentPso, ref cbUpdateMs, ref srvBindMs, ref drawCallMs, ref srvBinds, ref submeshDraws);
        // 3D-8: blended submeshes draw now (inline, e.g. top-down overlay) or are deferred to after the
        // water pass via RenderBlendedDeferred() so water never paints over transparent meshes.
        if (!deferBlended)
        {
            DrawBlended(cmd, frameIndex, ref currentPso, ref cbUpdateMs, ref drawCallMs, ref submeshDraws);
        }

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

    /// <summary>
    ///     3D-8: draws the blended (transparent) reference submeshes accumulated by the most recent
    ///     <see cref="Render" /> with <c>deferBlended: true</c>. Called AFTER the water pass so water
    ///     never paints over transparent meshes. The water pass rebinds <c>PerFrameCbv</c> to its own
    ///     uniforms (and may change topology), so this re-establishes the reference per-frame state
    ///     before issuing the blended draws. The DSV is bound by the frame loop, so blended draws stay
    ///     depth-tested against the opaque scene depth.
    /// </summary>
    public void RenderBlendedDeferred()
    {
        if (_blendedDraws.Count == 0) return;
        var cmd = _recorder.CommandList;

        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, _deferredPerFrameCbvAddress);
        cmd.SetGraphicsRootDescriptorTable(GpuRootSignature12.Slots.BindlessSrvTable, _cbvSrvUavHeap.BindlessHeapStartGpu);

        ID3D12PipelineState? currentPso = null;
        double cbUpdateMs = 0, drawCallMs = 0;
        var submeshDraws = 0;
        DrawBlended(cmd, _deferredFrameIndex, ref currentPso, ref cbUpdateMs, ref drawCallMs, ref submeshDraws);

        LastStats.ReferenceSubmeshDraws += submeshDraws;
        LastStats.ReferenceCbUpdateMilliseconds += cbUpdateMs;
        LastStats.ReferenceDrawCallMilliseconds += drawCallMs;
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

    // Transforms each mesh-local water-plane AABB by the placement world matrix into a world-space
    // axis-aligned footprint and appends it as a NifWaterPlane. All 8 local corners are transformed
    // and re-bounded so a rotated/scaled placement still yields a correct world AABB; the water
    // renderer draws a flat quad at the mid-Z over the rectangular XY footprint (separate X/Y extents).
    private void AccumulateNifWaterPlanes(Matrix4x4 world, IReadOnlyList<(Vector3 Min, Vector3 Max)> localPlanes)
    {
        for (var pi = 0; pi < localPlanes.Count; pi++)
        {
            var (min, max) = localPlanes[pi];
            var wMin = new Vector3(float.PositiveInfinity);
            var wMax = new Vector3(float.NegativeInfinity);
            for (var c = 0; c < 8; c++)
            {
                var corner = new Vector3(
                    (c & 1) == 0 ? min.X : max.X,
                    (c & 2) == 0 ? min.Y : max.Y,
                    (c & 4) == 0 ? min.Z : max.Z);
                var w = Vector3.Transform(corner, world);
                wMin = Vector3.Min(wMin, w);
                wMax = Vector3.Max(wMax, w);
            }

            // Keep the X and Y extents separate so a non-square plane (e.g. NVCleanWater1x4)
            // covers its true rectangle instead of a max-side square that overhangs the short axis.
            var footprintX = wMax.X - wMin.X;
            var footprintY = wMax.Y - wMin.Y;
            if (!float.IsFinite(footprintX) || !float.IsFinite(footprintY) ||
                footprintX <= 0f || footprintY <= 0f)
            {
                continue;
            }

            _nifWaterPlanes.Add(new NifWaterPlane(
                new Vector2(wMin.X, wMin.Y),
                (wMin.Z + wMax.Z) * 0.5f,
                new Vector2(footprintX, footprintY)));
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
        // Soft-fail instead of throwing: if the whole instance buffer can't fit this frame, skip the
        // opaque instanced pass (blended draws + later layers still present) rather than abandoning
        // the frame and blanking every model. The ring is sized so this only bites the densest
        // unthrottled top-down windows.
        if (!_ringBuffer.TryAllocate(frameIndex, instanceBytes + instanceStride - 1, out var instanceAlloc, alignment: 16))
        {
            LastFrameDrawsTruncated += totalInstances;
            return;
        }
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
            if (!_ringBuffer.TryAllocate(frameIndex, InstanceDrawByteSize, out var instanceDrawAlloc, GpuRingBuffer12.CbAlignment))
            {
                LastFrameDrawsTruncated += batch.Count;
                break;
            }
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
        for (var i = 0; i < _blendedDraws.Count; i++)
        {
            var draw = _blendedDraws[i];
            var pso = GetBlendPipeline(
                draw.Submesh.SrcBlendMode,
                draw.Submesh.DstBlendMode,
                draw.Submesh.DoubleSided);
            if (!DrawBlendedSubmesh(
                cmd,
                frameIndex,
                draw,
                pso,
                ref currentPso,
                ref cbUpdateMs,
                ref drawCallMs))
            {
                // Ring slot full — stop adding blended draws and present what fit (sorted
                // back-to-front, so the nearest survive). Count the rest as truncated.
                LastFrameDrawsTruncated += _blendedDraws.Count - i;
                break;
            }
            submeshDraws++;
            LastStats.ReferenceBlendedDraws++;
        }
    }

    /// <summary>Returns <c>false</c> when the ring slot is full so the caller stops the blended pass.</summary>
    private bool DrawBlendedSubmesh(
        ID3D12GraphicsCommandList cmd,
        int frameIndex,
        BlendedReferenceDraw draw,
        ID3D12PipelineState pso,
        ref ID3D12PipelineState? currentPso,
        ref double cbUpdateMs,
        ref double drawCallMs)
    {
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
        // Allocate before mutating PSO state so a soft-fail leaves the command list consistent.
        if (!_ringBuffer.TryAllocate(frameIndex, PerDrawByteSize, out var perDrawAlloc, GpuRingBuffer12.CbAlignment))
        {
            return false;
        }
        if (!ReferenceEquals(currentPso, pso))
        {
            cmd.SetPipelineState(pso);
            currentPso = pso;
        }
        unsafe { *(PerDrawConstants*)perDrawAlloc.CpuPtr = perDraw; }
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerDrawCbv, perDrawAlloc.GpuAddress);
        cbUpdateMs += ElapsedMilliseconds(cbStarted);

        var drawStarted = StartTiming();
        cmd.IASetVertexBuffers(0, draw.Submesh.VertexBufferView);
        cmd.IASetIndexBuffer(draw.Submesh.IndexBufferView);
        cmd.DrawIndexedInstanced((uint)draw.Submesh.IndexCount, 1, 0, 0, 0);
        drawCallMs += ElapsedMilliseconds(drawStarted);
        return true;
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
            // Antialias triangle edges on the multisampled scene RT (no-op when scene isn't MSAA).
            MultisampleEnable = _gpu.SceneSampleCount > 1,
        };

        var depth = new D12.DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = depthWriteEnabled ? D12.DepthWriteMask.All : D12.DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.GreaterEqual, // reversed-Z (near→1, far→0); depth clear = 0
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
            SampleDescription = new SampleDescription((uint)_gpu.SceneSampleCount, 0),
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

    /// <summary>
    ///     Builds a cylindrical (billboardUp / ROTATE_ABOUT_UP) camera-facing world matrix for a
    ///     submesh that sat under a <c>NiBillboardNode</c>. The quad spins about the world up axis (Z)
    ///     to face the camera and ignores the REFR placement rotation — only the placement's world
    ///     position and uniform scale are kept. The bake already dropped the billboard node's own
    ///     rotation (NifSceneGraphWalker.WalkNode), so the quad sits at <paramref name="worldCenter" />
    ///     in its authored local orientation, re-aimable here.
    ///     <para>
    ///         Convention: the quad's local +Y is assumed to be its facing axis, hence the
    ///         <c>-π/2</c> term that maps +Y onto the horizontal camera direction. If the smoke renders
    ///         90° off or back-facing in the GUI, drop the <c>-π/2</c> (local +X facing) or flip the
    ///         sign — fast to iterate once visible.
    ///     </para>
    /// </summary>
    private static Matrix4x4 BuildBillboardWorld(
        Vector3 localBoundsCenter, Matrix4x4 world, Vector3 worldCenter, Vector3 cameraPosition)
    {
        var toCam = cameraPosition - worldCenter;
        toCam.Z = 0f;
        var facing = toCam.LengthSquared() > 1e-6f ? Vector3.Normalize(toCam) : Vector3.UnitX;
        var angle = MathF.Atan2(facing.Y, facing.X) - MathF.PI / 2f;
        // Uniform scale = length of the placement matrix's first row (X axis).
        var scale = new Vector3(world.M11, world.M12, world.M13).Length();
        var rs = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationZ(angle);
        // Anchor the quad so its local bounds center maps to the placement's world position.
        rs.Translation = worldCenter - Vector3.TransformNormal(localBoundsCenter, rs);
        return rs;
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
