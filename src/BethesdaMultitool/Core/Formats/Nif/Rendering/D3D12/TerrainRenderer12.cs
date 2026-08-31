#if WINDOWS_GUI
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Terrain;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Abstractions;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Orchestration;
using BethesdaMultitool.Core.Resources;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     v3 Pass 4 Step 2c — D3D12 port of <c>TerrainRenderer</c>. Renders textured
///     heightmap terrain via per-cell vertex buffers + per-quadrant draws against a shared
///     index buffer.
///     <para>
///         The render loop gathers visible cells via the spatial index, sorts closest-first,
///         lazily builds + uploads per-cell vertex buffers under a per-
///         frame budget, then issue 4 quadrant draws per cell with per-quadrant constants
///         + 7 SRVs (4 diffuse + 3 opacity) bound through the shared root signature.
///     </para>
/// </summary>
internal sealed class TerrainRenderer12 : Abstractions.ITerrainRenderer
{
    private const uint PerFrameByteSize = 64;     // float4x4 viewProj
    // b1 preserves the original 16 diffuse indices at byte 0, then appends 16 FNV normal indices
    // and a uint4 of decode metadata (x = 16-slot BC5 mask). Both the old 64 bytes and the new
    // 144 bytes occupy one 256-byte-aligned ring slot, so draw capacity is unchanged.
    private const uint PerDrawByteSize = 144;     // uint4[4] diffuse + uint4[4] normal + uint4 metadata
    private const uint PerModeByteSize = 16;      // float4 (textures, uvScale, VCLR, FNV normals)
    // No per-frame COUNT cap by default — the wall-clock time budget below is the pacer, so the
    // per-frame build cost is bounded by frame-TIME rather than a fixed cell count (a count cap
    // couples terrain load rate to FPS; time-budgeting keeps the per-frame cost FPS-independent).
    private const int MaxNewUploadsPerFrame = int.MaxValue;

    // Wall-clock ceiling on render-thread cell build/upload work per frame. The primary pacer:
    // bounds the movement-stutter spike when many new cells enter view at high render distance by
    // capping *how long* building may take. Tuned against MeshBuildUploadMilliseconds. Raised from
    // 2.0 → 3.0 so a zoomed-out view (hundreds of visible cells) fills its edges noticeably faster
    // without a perceptible per-frame hitch.
    private const double MaxMeshBuildMillisecondsPerFrame = 3.0;

    // Async cell build runs off the render thread, but it still allocates enough to compete with
    // mesh decode and trigger visible GC pauses. Default scales gently with cores (cores/4, clamped
    // 4..8): terrain builds are the binding constraint on whole-map fills (~4 workers × 100-250ms
    // per cell), so high-core machines get a bit more parallelism while staying GC-guarded. Tune
    // via env (down on GC-bound machines, up to 16).
    private static readonly int MaxConcurrentBuildTasks =
        Core.Orchestration.ConcurrencyPolicy
            .Fixed(Core.Orchestration.CpuBudget.Interactive()
                .Claim(Core.Orchestration.CpuWorkload.TerrainCellBuild))
            .WithEnvironmentOverride(EnvironmentVariables.Viewer.TerrainBuildConcurrency, 1, 16)
            .Resolve();

    private static readonly int MaxBuildStartsPerFrame = EnvironmentVariables.GetClampedInt(
        EnvironmentVariables.Viewer.TerrainBuildStartsPerFrame,
        defaultValue: MaxConcurrentBuildTasks,
        min: 1,
        max: MaxConcurrentBuildTasks);

    private const int MinCacheCapacity = 1024;
    private const int CacheHeadroom = 256;

    /// <summary>
    ///     Retain radius the byte-budget PLAN sizes its floor for. Deliberately modest: the plan
    ///     floor is a sizing aid, not the correctness guarantee — the per-frame sweep pins the LIVE
    ///     retain + shadow rings whatever their size, and when the live ring outgrows the plan the
    ///     sweep simply stops short of its target (over budget, logged) rather than evict anything
    ///     visible. Planning for the largest configurable radius instead would let the floor
    ///     swallow the whole budget and the bound would never engage.
    /// </summary>
    private const int PlanRetainRadiusCells = 10;

    public const float DefaultDiffuseUvScale = 1f / 512f;

    /// <summary>
    ///     Terrain diffuse repeats per exterior cell. 8 reproduces the long-standing 1/512 scale on the
    ///     4096-unit Fallout/Skyrim grid, which is where that constant came from — but expressed against
    ///     the cell it makes sense on every game. Starfield's cell is 100 units, so the fixed 1/512 gave
    ///     it ~0.2 repeats per cell: a ~41x magnified texture that reads as flat colour even when the
    ///     right image loads.
    /// </summary>
    private const float DiffuseTileRepeatsPerCell = 8f;

    /// <summary>Texture-space scale for the active game's cell size (see <see cref="DiffuseTileRepeatsPerCell" />).</summary>
    private float DiffuseUvScale
    {
        get
        {
            var cellSize = _textureResolver.CellWorldSize;
            return cellSize > 0f ? DiffuseTileRepeatsPerCell / cellSize : DefaultDiffuseUvScale;
        }
    }

    private static readonly Comparison<VisibleCell> ByDistanceAscending =
        (a, b) => a.DistSq.CompareTo(b.DistSq);

    private static readonly Logger Log = Logger.Instance;

    private readonly GpuDevice12 _gpu;
    private readonly GpuCommandRecorder12 _recorder;
    private readonly GpuRingBuffer12 _ringBuffer;
    private readonly GpuDescriptorHeapAllocator12 _cbvSrvUavHeap;
    private readonly GpuDeletionQueue12 _deletionQueue;
    private readonly TerrainTextureResolver12 _textureResolver;
    private readonly GpuRootSignature12 _rootSignature;
    // Colour PSOs indexed by the cell's blend-quad count (1..MaxBlendQuads; slot 0 stays null). A
    // cell uploads only the layer-weight quads its slot count reaches, and the vertex shader has to
    // declare exactly that many inputs, so the permutation is a property of the CELL rather than of
    // the pass. Created on demand — a worldspace typically uses one or two widths, and building all
    // four up front would put three or four unnecessary shader compiles into every startup.
    private readonly ID3D12PipelineState?[] _colorPsoByQuads =
        new ID3D12PipelineState?[TerrainVertexLayout.MaxBlendQuads + 1];
    // Mirror-winding twins of the above for the water-reflection pass (a mirrored viewProj flips
    // screen-space winding; without the twin every terrain triangle would back-face cull).
    private readonly ID3D12PipelineState?[] _mirrorPsoByQuads =
        new ID3D12PipelineState?[TerrainVertexLayout.MaxBlendQuads + 1];
    // Depth-only variant (same VS path + depth state, no pixel shader, no render targets). Used by
    // the 2D map's top-down overlay to lay down terrain depth so placed references are occluded by
    // the ground without painting terrain color over the 2D map's own terrain layer. Built from the
    // ZERO-quad permutation: it discards the layer weights, so it now fetches none.
    private readonly ID3D12PipelineState _depthOnlyPso;
    // Sun-shadow variant: depth-only like above but single-sample (the shadow map is never MSAA),
    // no culling (back faces of grazing slopes must still occlude), and negatively depth-biased
    // for the reversed-Z acne fix — matching the reference renderer's shadow PSOs. Zero-quad too,
    // which matters most here: the cascades redraw the caster ring several times a frame.
    private readonly ID3D12PipelineState _shadowDepthPso;
    // Per-worldspace LAND grid resolution. Defaults to 33×33 (Fallout/Oblivion/Skyrim + runtime DMP);
    // a Morrowind worldspace load bumps this to 65×65. The shared index buffer + scratch arrays are
    // rebuilt in LoadData when the grid size changes, and DrawCell issues _indexCount per cell.
    private int _gridSize = TerrainConstants.LandGridSize;
    private TerrainTriangleTopology _triangleTopology = TerrainTriangleTopology.FixedSouthEastNorthWest;
    private int _indexCount = TerrainMeshBuilder.IndexCount;
    private ushort[] _sharedIndexData;
    private ID3D12Resource? _sharedIndexBuffer;
    private IDisposable? _sharedIndexFootprint;

    // Per-cell geometry lives here rather than in two committed buffers per cell: D3D12's 64 KiB
    // committed-buffer rounding is 43% of a 33-grid cell, and the arena pays it once per 16 MiB
    // block instead. DEFAULT heap (device-local) because terrain is re-read by the IA in the colour,
    // depth-only, shadow and mirror passes every frame.
    private readonly GpuTerrainArena12 _terrainArena;

    // Planned terrain byte budget (0 = no bound: VRAM unknown), enforced by EnforceCellByteBudget.
    private long _cellByteBudget;
    // Caster ring of the most recent shadow replay (+1 cell margin) — pinned by the sweep because
    // shadow draws deliberately never bump recency.
    private VisibilityCylinder? _shadowPinCylinder;
    // True while the budget sweep evicts, so onEvicted defers its ContentVersion bump to one
    // per-burst increment instead of laddering the shadow re-render per cell.
    private bool _suppressEvictionContentBump;
    // Scratch for the sweep's farthest-first ranking (render-thread only, reused across frames).
    private readonly List<((int gx, int gy) Key, int Distance)> _evictScratch = [];
    // One log line per worldspace when the visible set alone exceeds the budget.
    private bool _budgetUnreachableWarned;
    private IndexBufferView _sharedIbv;

    private LruCache<(int gx, int gy), CachedCellMesh12> _meshCache;
    private readonly HashSet<(int gx, int gy)> _knownUnusableCells = new();

    // Transient build/upload failures get retried before a cell is permanently blacklisted for
    // the worldspace session — one BSA/IO hiccup must not hole a cell until reload.
    private readonly Dictionary<(int gx, int gy), int> _cellBuildFailures = new();
    private const int MaxCellBuildFailures = 3;

    // Camera cell + retain radius snapshot from the current frame's gather: completed CPU builds
    // and queue entries within this Chebyshev band survive a one-frame boundary exit under fast
    // movement instead of being pruned and rebuilt from scratch.
    private (int gx, int gy) _retainCameraCell;
    private int _retainRadiusCells;
    private readonly List<VisibleCell> _visibleScratch = new();
    private readonly List<VisibleCell> _missingVisibleScratch = new();
    private readonly HashSet<(int gx, int gy)> _missingVisibleKeys = new();
    private readonly List<global::BethesdaMultitool.Core.WorldData.WorldSpatialCell> _candidateScratch = new();
    private TerrainVertex[] _vertexScratch =
        new TerrainVertex[TerrainMeshBuilder.VertexCount];
    // UNORM16 layer weights, MaxSlots per vertex — the wire format, which the input assembler
    // presents to the shader as four float4s. Not Vector4[]: that is the CPU-side authoring model.
    private ushort[] _blendWeightScratch =
        new ushort[TerrainMeshBuilder.VertexCount * CellTerrainTextureSet.MaxSlots];

    // Async cell-build bookkeeping. _buildQueue / _queuedOrBuilding / _frameBuildStarts and the
    // whole upload path are touched ONLY on the render thread (LoadData and Render run on the same
    // UI/composition thread). _buildResults, _loadGeneration, _activeBuildTasks and _buildTasks are
    // shared with background build tasks and guarded as marked.
    private readonly Queue<(int gx, int gy)> _buildQueue = new();
    private readonly HashSet<(int gx, int gy)> _queuedOrBuilding = new();
    private readonly List<(int gx, int gy)> _pruneScratch = new();
    private readonly InFlightTaskTracker _buildTasks = new(nameof(TerrainRenderer12));
    private readonly object _buildResultsLock = new();             // guards _buildResults + _loadGeneration
    private readonly Dictionary<(int gx, int gy), BuiltCellCpuData> _buildResults = new();
    private int _activeBuildTasks;
    private int _frameBuildStarts;
    private int _loadGeneration;

    private Dictionary<(int gx, int gy), CellRecord>? _cells;
    private global::BethesdaMultitool.Core.WorldData.WorldSpatialIndex? _spatialIndex;
    private global::BethesdaMultitool.Core.WorldData.WorldRenderCache? _renderCache;
    private bool _showTextures = true;
    private bool _showVertexColors = true;
    private bool _disposed;

    public TerrainRenderer12(
        GpuDevice12 gpu,
        GpuCommandRecorder12 recorder,
        GpuRingBuffer12 ringBuffer,
        GpuRootSignature12 rootSignature,
        GpuDescriptorHeapAllocator12 cbvSrvUavHeap,
        GpuDeletionQueue12 deletionQueue,
        TerrainTextureResolver12 textureResolver)
    {
        _gpu = gpu;
        _recorder = recorder;
        _ringBuffer = ringBuffer;
        _cbvSrvUavHeap = cbvSrvUavHeap;
        _deletionQueue = deletionQueue;
        _textureResolver = textureResolver;
        _rootSignature = rootSignature;

        // The two depth passes are the only PSOs built eagerly: they are quad-count 0, so one vertex
        // shader covers every cell no matter how many layers it paints, and both are wanted before
        // the first cell exists. The colour and mirror PSOs arrive with their first cell.
        var depthVsBytecode = TerrainPipelineFactory12.CompileVertexShader(0);
        var depthElements = TerrainVertexLayout.ElementsFor(0);
        _depthOnlyPso = TerrainPipelineFactory12.BuildDepthOnlyPipelineState(
            gpu, rootSignature, depthVsBytecode, depthElements);
        _shadowDepthPso = TerrainPipelineFactory12.BuildShadowPipelineState(
            gpu, rootSignature, depthVsBytecode, depthElements);

        _sharedIndexData = TerrainMeshBuilder.BuildSharedIndexBufferData();
        _terrainArena = new GpuTerrainArena12(gpu)
            .RegisterWith(ResourceRegistry.Instance, "terrain-geometry");
        _meshCache = CreateMeshCache(MinCacheCapacity, byteBudget: 0); // re-created with a plan at LoadData
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Drain any in-flight background builds before tearing down GPU/cache state they could
        // still be feeding. Their results land in _buildResults (gen-checked) and are harmless.
        DrainBuildTasksForDispose();

        _meshCache.Dispose();
        if (_sharedIndexBuffer is not null)
        {
            _deletionQueue.EnqueueDispose(_sharedIndexBuffer);
        }
        _sharedIndexFootprint?.Dispose();
        _sharedIndexFootprint = null;
        // After _meshCache.Dispose() above: eviction enqueues each cell's deferred arena free, so
        // the arena must outlive that cascade. Its blocks are released here regardless.
        _terrainArena.Dispose();
        _shadowDepthPso.Dispose();
        _depthOnlyPso.Dispose();
        for (var quads = 0; quads < _colorPsoByQuads.Length; quads++)
        {
            _colorPsoByQuads[quads]?.Dispose();
            _mirrorPsoByQuads[quads]?.Dispose();
            _colorPsoByQuads[quads] = null;
            _mirrorPsoByQuads[quads] = null;
        }
    }

    /// <summary>
    ///     The colour PSO for a cell of <paramref name="blendQuadCount" /> layer-weight quads,
    ///     building it (and its mirror twin) on first use.
    ///     <para>
    ///         Reached from cell upload rather than from the draw loop, so the compile lands on the
    ///         path that is already time-budgeted and never mid-draw. The FXC compile itself has
    ///         usually already happened on the background build task that produced the cell — see
    ///         <see cref="TerrainPipelineFactory12.PrecompileBlendPermutation" /> — leaving only
    ///         <c>CreateGraphicsPipelineState</c> here.
    ///     </para>
    /// </summary>
    private ID3D12PipelineState EnsureBlendPipelines(int blendQuadCount)
    {
        var quads = Math.Clamp(blendQuadCount, 1, TerrainVertexLayout.MaxBlendQuads);
        if (_colorPsoByQuads[quads] is { } existing)
        {
            return existing;
        }

        var elements = TerrainVertexLayout.ElementsFor(quads);
        var (pso, mirrorPso) = TerrainPipelineFactory12.BuildColorPipelineStates(
            _gpu,
            _rootSignature,
            TerrainPipelineFactory12.CompileVertexShader(quads),
            TerrainPipelineFactory12.CompilePixelShader(quads),
            elements);
        _colorPsoByQuads[quads] = pso;
        _mirrorPsoByQuads[quads] = mirrorPso;
        Log.Info("TerrainRenderer12: built terrain pipeline for {0}-quad cells ({1} layer weights).",
            quads, quads * TerrainBlendWeightPacking.SlotsPerQuad);
        return pso;
    }

    private void DrainBuildTasksForDispose()
    {
        var pending = _buildTasks.PendingCount;
        if (pending > 0)
        {
            Log.Info("TerrainRenderer12: waiting for {0} build task(s) during dispose.", pending);
        }

        // Non-pumping drain (dispose runs on the UI thread); faulted tasks are observed + logged.
        _buildTasks.WaitForDrainLogged();
    }

    public int CellCount => _spatialIndex?.CellCount ?? _cells?.Count ?? 0;
    public global::BethesdaMultitool.Core.WorldData.WorldRenderStats LastStats { get; } = new();
    public bool DetailedProfilingEnabled { get; set; }

    // Concurrency ceiling used when streaming is unthrottled (the on-demand top-down overlay
    // depth pre-pass, which has no framerate target).
    private static readonly int UnthrottledMaxConcurrentBuildTasks =
        Core.Orchestration.CpuBudget.Bulk().Claim(Core.Orchestration.CpuWorkload.TerrainCellBuild);

    /// <summary>
    ///     When <c>false</c>, the per-frame cell build/upload budget (count + time + build starts +
    ///     concurrency) is lifted so a few back-to-back renders build the whole visible terrain — used
    ///     by the top-down overlay's depth pre-pass so ground occlusion converges in lockstep with the
    ///     (also-unthrottled) reference meshes. The live 60fps loop leaves this <c>true</c>.
    /// </summary>
    public bool StreamingThrottled { get; set; } = true;

    // Time-based pace (StreamingFrameBudgetScaler): per-frame allowances are calibrated for 60fps,
    // so a heavy whole-map frame (10-20fps once thousands of cells draw) otherwise collapses the
    // fill rate exactly when the most cells are pending — the "terrain loads slower and slower the
    // further out it gets" compounding crawl. Scaling by measured frame duration keeps the fill
    // THROUGHPUT constant; the reference streaming path already paces this way.
    private double _frameBudgetScale = 1.0;
    private long _lastFrameTimestamp;

    /// <summary>EMA of the observed frame duration that drives the streaming budget scale. Smoothed
    /// because the raw previous frame is partly a RESULT of this budget — see StreamingFrameBudgetScaler.</summary>
    private double _smoothedFrameSeconds;

    private int EffectiveMaxBuildStartsPerFrame =>
        StreamingThrottled
            ? Core.Resources.StreamingFrameBudgetScaler.ScaleCount(MaxBuildStartsPerFrame, _frameBudgetScale)
            : int.MaxValue;

    private int EffectiveMaxConcurrentBuildTasks =>
        StreamingThrottled ? MaxConcurrentBuildTasks : Math.Max(MaxConcurrentBuildTasks, UnthrottledMaxConcurrentBuildTasks);

    public void SetDebugModes(bool showTextures, bool showVertexColors)
    {
        _showTextures = showTextures;
        _showVertexColors = showVertexColors;
    }

    /// <summary>
    ///     Render-thread-only LRU of per-cell terrain meshes (replaces the bespoke
    ///     CellMeshLruCache): evicted/replaced entries are disposed, and the cache reports its
    ///     entry counts to the resource registry under the "terrain-cells" tag.
    /// </summary>
    private LruCache<(int gx, int gy), CachedCellMesh12> CreateMeshCache(int capacity, long byteBudget) =>
        new LruCache<(int gx, int gy), CachedCellMesh12>(
                "CellMeshLru",
                ResourceCategory.GpuResident,
                maxEntries: capacity,
                // The LRU's own byte cap is a BACKSTOP at 1.25× the plan, not the enforcer. The
                // per-frame distance sweep (EnforceCellByteBudget) is the enforcer: it knows camera
                // distance and pins the retain + shadow-caster rings, whereas this cascade is blind
                // LRU order — and shadow draws deliberately TryPeek (no recency bump), so an active
                // up-sun caster can sit at the LRU tail. With the headroom, the cascade fires only
                // if the sweep somehow failed to keep us under plan.
                maxBytes: byteBudget > 0
                    ? (long)(byteBudget * TerrainCellResidencyPolicy.BackstopHeadroom)
                    : null,
                // Real bytes, not the 1-byte-per-entry default. This cache owns its two committed
                // DEFAULT-heap buffers outright — nothing else accounts for them — so the size is
                // reported rather than attributed, and it is what the byte budget is enforced
                // against. Known at Set time (unlike the reference-mesh cache, whose nodes are
                // populated after an async decode), so no UpdateSize is needed here.
                sizeOf: static (_, mesh) => mesh.ByteSize,
                onEvicted: (_, mesh) =>
                {
                    mesh.Dispose();
                    // A cell leaving residency changes what the shadow pass can draw: bump the
                    // content version so the throttled shadow re-render heals the vanished
                    // caster instead of the published cascades keeping its stale depth. The
                    // budget sweep suppresses this and bumps ONCE per burst — hundreds of
                    // per-evict bumps in one frame would ladder the cascade re-render storm.
                    if (!_suppressEvictionContentBump)
                    {
                        unchecked { ContentVersion++; }
                    }
                })
            .WithSegment(Core.Diagnostics.GpuMemorySegment.Local)
            .RegisterWith(ResourceRegistry.Instance, "terrain-cells");

    public void LoadData(Dictionary<(int gx, int gy), CellRecord> cells)
        => LoadData(cells, spatialIndex: null, renderCache: null);

    public void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        global::BethesdaMultitool.Core.WorldData.WorldSpatialIndex? spatialIndex,
        global::BethesdaMultitool.Core.WorldData.WorldRenderCache? renderCache)
    {
        _meshCache.Dispose();
        _cells = cells;
        _spatialIndex = spatialIndex;
        _renderCache = renderCache;

        // Adopt this worldspace's LAND grid resolution (33×33 Fallout-family, 65×65 Morrowind,
        // 129×129 FO76/Starfield) BEFORE sizing the cache: the byte budget is a function of the
        // per-cell GPU cost, which is a function of the grid. When it changes, rebuild the shared
        // index buffer + per-cell scratch so the index/vertex/blend stream lengths all match the
        // new grid. All cells of one worldspace share a single grid size.
        EnsureGridSize(cells);

        // Plan the terrain byte budget from the adapter's REAL local budget. 0 (unknown VRAM —
        // WARP/iGPU, failed query) means no byte bound, i.e. exactly the pre-budget behaviour;
        // treating an unknown budget as zero would evict everything on the machines least able to
        // afford the rebuild churn. Entry capacity alone bounds nothing: it is sized at or above
        // the worldspace's cell count, which on Appalachia makes ~90 GiB of terrain eligible.
        _cellByteBudget = _gpu.TryQueryLocalVideoMemoryMb(out _, out var vramBudgetMb) && vramBudgetMb > 0
            ? TerrainCellResidencyPolicy.PlanBudgetBytes(
                vramBudgetMb * 1024L * 1024L, _gridSize, cells.Count, PlanRetainRadiusCells)
            : 0;
        if (_cellByteBudget > 0)
        {
            Log.Info(
                "TerrainRenderer12: cell byte budget {0:N0} MB for {1:N0} cells at grid {2} (VRAM budget {3:N0} MB)",
                _cellByteBudget / (1024 * 1024), cells.Count, _gridSize, vramBudgetMb);
        }

        var capacity = Math.Max(MinCacheCapacity, cells.Count + CacheHeadroom);
        _meshCache = CreateMeshCache(capacity, _cellByteBudget);
        _knownUnusableCells.Clear();
        _cellBuildFailures.Clear();

        // Bump the build generation so any in-flight task started against the previous worldspace
        // drops its result instead of inserting a cell built from the old _cells/_meshCache. Clear
        // the render-thread queue state too; in-flight tasks keep running but become no-ops.
        lock (_buildResultsLock)
        {
            _loadGeneration++;
            _buildResults.Clear();
        }
        _buildQueue.Clear();
        _queuedOrBuilding.Clear();

        // Warm the per-cell texture-set cache up front so the cold-cell mesh-build burst
        // (16 cells/frame budget) does not pay the 4 neighbor lookups + per-quadrant
        // layer-weight sort per cell. Costs ~0.05 ms × cells.Count at LoadData (~250 ms
        // for a 5000-cell worldspace), folded into the existing ~1–2 s worldspace load.
        // Only when the resulting resident set fits the budget — see TerrainTextureSetWarmPolicy;
        // a 129-grid worldspace the size of Appalachia would want ~42 GB of it.
        if (_renderCache is not null && !TerrainTextureSetWarmPolicy.ShouldWarm(_gridSize, cells.Count))
        {
            Log.Info(
                "TerrainRenderer12: texture-set warm skipped for {0:N0} cells at grid {1} " +
                "({2:N0} MB > {3:N0} MB budget) — sets build on demand.",
                cells.Count, _gridSize,
                TerrainTextureSetWarmPolicy.EstimateWarmBytes(_gridSize, cells.Count) / (1024 * 1024),
                TerrainTextureSetWarmPolicy.MaxWarmBytes / (1024 * 1024));
        }
        else if (_renderCache is not null)
        {
            if (cells.Count >= 512)
            {
                var gridSize = _gridSize;
                // NOT Parallel.ForEach: LoadData runs on the STA UI thread during a worldspace
                // switch, and Parallel blocks the caller through a COM PUMPING wait that can
                // fail-fast the process via XAML reentrancy (0xc000027b) — the same mechanism that
                // crashed the per-frame reference cull. Bounded worker count also stops the warm
                // from claiming every core while the UI thread is trying to paint.
                Core.Orchestration.NonPumpingParallel.ForEach(
                    [.. cells],
                    Core.Orchestration.ConcurrencyPolicy.CoresMinusOne.Resolve(),
                    pair =>
                    {
                        var captured = pair;
                        _renderCache.GetOrBuildTerrainTextureSet(
                            captured.Value,
                            () => TerrainCellCpuBuilder.BuildCellTextureSet(
                                captured.Key, captured.Value, cells, gridSize));
                    });
            }
            else
            {
                foreach (var pair in cells)
                {
                    var captured = pair;
                    _renderCache.GetOrBuildTerrainTextureSet(
                        captured.Value,
                        () => TerrainCellCpuBuilder.BuildCellTextureSet(captured.Key, captured.Value, cells, _gridSize));
                }
            }
        }
    }

    /// <summary>
    ///     Determine the worldspace's LAND grid resolution and game-specific triangle topology.
    ///     When either differs, rebuild the shared index buffer lazily from new CPU data; grid-size
    ///     changes also resize the per-cell build scratch arrays.
    /// </summary>
    private void EnsureGridSize(Dictionary<(int gx, int gy), CellRecord> cells)
    {
        var gridSize = TerrainConstants.LandGridSize;
        foreach (var cell in cells.Values)
        {
            if (cell.Heightmap?.ExactHeights is { } exact)
            {
                gridSize = exact.GetLength(0);
                break;
            }
        }

        var topology = _renderCache is null
            ? TerrainTriangleTopology.FixedSouthEastNorthWest
            : TerrainSurfaceTopology.ForGame(_renderCache.Game);
        var gridChanged = gridSize != _gridSize;
        if (!gridChanged && topology == _triangleTopology)
        {
            return;
        }

        _gridSize = gridSize;
        _triangleTopology = topology;
        _indexCount = TerrainMeshBuilder.IndexCountFor(gridSize);
        _sharedIndexData = TerrainMeshBuilder.BuildSharedIndexBufferData(gridSize, topology);

        // Drop the GPU index buffer so EnsureSharedIndexBuffer recreates it from the new data on the
        // next render (we have no command list here). Route the old one through the deletion queue.
        if (_sharedIndexBuffer is not null)
        {
            _deletionQueue.EnqueueDispose(_sharedIndexBuffer);
            _sharedIndexBuffer = null;
            _sharedIndexFootprint?.Dispose();
            _sharedIndexFootprint = null;
        }

        if (gridChanged)
        {
            var vertexCount = TerrainMeshBuilder.VertexCountFor(gridSize);
            _vertexScratch = new TerrainVertex[vertexCount];
            _blendWeightScratch = new ushort[vertexCount * CellTerrainTextureSet.MaxSlots];
        }
    }

    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder)
        => RenderInternal(viewProj, cylinder, colorPass: true, renderOrigin: Vector3.Zero);

    /// <summary>
    ///     Camera-relative main color render. <paramref name="renderOrigin" /> is the exact absolute
    ///     world origin subtracted by the terrain vertex shader before <paramref name="viewProj" />;
    ///     carrying it here keeps CPU world-space cell bounds in the same coordinate frame.
    /// </summary>
    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder, Vector3 renderOrigin)
        => RenderInternal(viewProj, cylinder, colorPass: true, renderOrigin);

    /// <summary>
    ///     Depth-only render: lays terrain depth into the bound depth buffer without writing color.
    ///     Used by the 2D map's top-down "Rendered models" overlay so placed references depth-test
    ///     against the ground and partially-buried meshes are clipped, while the 2D map keeps its
    ///     own terrain layer underneath. Shares the cell mesh cache + async build path with the
    ///     main color render.
    /// </summary>
    public int RenderDepthOnly(Matrix4x4 viewProj, VisibilityCylinder cylinder)
        => RenderInternal(viewProj, cylinder, colorPass: false, renderOrigin: Vector3.Zero);

    /// <summary>Bumped whenever a terrain cell mesh becomes newly GPU-resident — part of the sun
    /// shadow map's cache key, so streamed-in hills re-render the map (terrain CASTS shadows).</summary>
    public int ContentVersion { get; private set; }

    /// <summary>
    ///     True when the latest <see cref="RenderShadowDepth" /> reached an authoritative result,
    ///     including the valid zero-cell cases where no terrain is loaded at all or no RESIDENT cell
    ///     intersects the cascade's cylinder. False only when the gather could not run (ring
    ///     allocation failed).
    ///     <para>
    ///         The <c>int</c> return alone cannot carry this: "0 cells" is emitted by three different
    ///         paths and only two of them are answers. Without the distinction the host cannot tell an
    ///         empty cascade it may CACHE from a failed one it must RETRY — the twin of
    ///         <c>ReferenceRenderer12.LastShadowReplayCompleted</c> on the reference side.
    ///     </para>
    /// </summary>
    public bool LastShadowReplayCompleted { get; private set; }

    /// <summary>
    ///     Sun-shadow depth pass: draws the ALREADY-RESIDENT terrain cells into the (already bound)
    ///     shadow map from the light's view with the 1-sample no-cull shadow PSO — terrain casts
    ///     shadows onto itself and onto placed references (hillsides shading valleys).
    ///     <para>
    ///         Deliberately streaming-free, unlike <see cref="RenderInternal" />: it never builds or
    ///         uploads cells (missing casters heal via <see cref="ContentVersion" /> once the MAIN
    ///         pass streams them in), never touches the self-measured frame timestamp (per-cascade
    ///         calls at frame end made the main pass measure a near-zero "frame duration", flooring
    ///         its time-based streaming budget — the "terrain loads slower with shadows on" report),
    ///         never overwrites <see cref="LastStats" />, and peeks the mesh cache without bumping
    ///         recency (shadow reads of far cells must not push the camera's own cells toward
    ///         eviction).
    ///     </para>
    ///     Returns the count of resident cells drawn. See <see cref="LastShadowReplayCompleted" /> to
    ///     distinguish an authoritative zero from a gather that could not run.
    /// </summary>
    public int RenderShadowDepth(Matrix4x4 lightViewProj, VisibilityCylinder cylinder)
    {
        LastShadowReplayCompleted = false;
        // Record the caster ring (plus a one-cell margin) so the byte-budget sweep pins it. Shadow
        // draws deliberately never bump recency — without this pin, actively-shadowing up-sun cells
        // outside the camera cylinder would be the sweep's FIRST victims, and an evicted caster
        // cannot rebuild until the camera turns toward it (build admission is camera-strict).
        _shadowPinCylinder = new VisibilityCylinder(
            cylinder.Position, cylinder.Radius + WorldGridConstants.CellSize);
        if ((_spatialIndex is null || _spatialIndex.CellCount == 0) &&
            (_cells is null || _cells.Count == 0))
        {
            // No terrain exists to draw — an authoritative zero, not a failure.
            LastShadowReplayCompleted = true;
            return 0;
        }

        var cmd = _recorder.CommandList;
        if (!_ringBuffer.TryAllocate(
                _recorder.FrameIndex, PerFrameByteSize, out var perFrameAlloc, GpuRingBuffer12.CbAlignment) ||
            !_ringBuffer.TryAllocate(
                _recorder.FrameIndex, PerModeByteSize, out var perModeAlloc, GpuRingBuffer12.CbAlignment))
        {
            return 0; // the host disables this cleared cascade and retries without a lit coverage hole
        }
        unsafe
        {
            *(Matrix4x4*)perFrameAlloc.CpuPtr = lightViewProj;
            // b2 is read by the terrain VS (UV scale feeds an output the null-PS pass ignores,
            // but the register must still be validly bound — earlier passes rebind this slot).
            *(Vector4*)perModeAlloc.CpuPtr = new Vector4(0f, DiffuseUvScale, 0f, 0f);
        }

        cmd.SetPipelineState(_shadowDepthPso);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        EnsureSharedIndexBuffer(cmd);
        cmd.IASetIndexBuffer(_sharedIbv);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, perFrameAlloc.GpuAddress);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerModeCbv, perModeAlloc.GpuAddress);

        var drawn = 0;
        foreach (var key in EnumerateCellKeysInCylinder(cylinder))
        {
            if (!_meshCache.TryPeek(key, out var entry))
            {
                continue;
            }

            // Slot 1 stays unbound: this PSO's zero-quad vertex shader declares no layer weights.
            // The cascades redraw the caster ring several times a frame, so the weights this pass
            // used to fetch and discard were the largest single piece of wasted IA bandwidth here.
            cmd.IASetVertexBuffers(0, entry.VertexView());
            BindCellGrid(cmd, entry);
            // No per-draw CB: b1 carries the PS's texture indices and this PSO has no PS — skipping
            // it saves one ring allocation per cell per cascade (the old streaming path's real ring
            // pressure) and keeps LastStats untouched.
            cmd.DrawIndexedInstanced((uint)_indexCount, 1, 0, 0, 0);
            drawn++;
        }

        // The gather ran to completion; `drawn == 0` here means no RESIDENT cell intersected the
        // cylinder, which is an answer the host may cache.
        LastShadowReplayCompleted = true;
        return drawn;
    }

    /// <summary>
    ///     Water-reflection mirror render: draws the ALREADY-RESIDENT terrain cells in full color
    ///     with the mirrored view-projection and the winding-flipped PSO. Streaming-free like
    ///     <see cref="RenderShadowDepth" /> (never builds/uploads cells, never touches the frame
    ///     timestamp or <c>LastStats</c>, peeks the mesh cache without bumping recency) — missing
    ///     mirror content heals as the MAIN pass streams cells in. The caller has already bound the
    ///     reflection RTV/DSV and the mirror-pass b3 atmosphere CB (clip plane + mirrored shading
    ///     camera). Returns the resident cells drawn.
    /// </summary>
    public int RenderMirror(Matrix4x4 mirrorViewProj, VisibilityCylinder cylinder)
    {
        if ((_spatialIndex is null || _spatialIndex.CellCount == 0) &&
            (_cells is null || _cells.Count == 0))
        {
            return 0;
        }

        var cmd = _recorder.CommandList;
        if (!_ringBuffer.TryAllocate(
                _recorder.FrameIndex, PerFrameByteSize, out var perFrameAlloc, GpuRingBuffer12.CbAlignment) ||
            !_ringBuffer.TryAllocate(
                _recorder.FrameIndex, PerModeByteSize, out var perModeAlloc, GpuRingBuffer12.CbAlignment))
        {
            return 0; // soft-skip: the reflection keeps its sky content this frame
        }
        unsafe
        {
            *(Matrix4x4*)perFrameAlloc.CpuPtr = mirrorViewProj;
            // Same per-mode lanes as the main color pass: textures on, UV scale, vertex colors on,
            // FNV normals off (the mirror is a lighting-faithful but detail-light view).
            *(Vector4*)perModeAlloc.CpuPtr = new Vector4(1f, DiffuseUvScale, 1f, 0f);
        }

        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        EnsureSharedIndexBuffer(cmd);
        cmd.IASetIndexBuffer(_sharedIbv);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, perFrameAlloc.GpuAddress);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerModeCbv, perModeAlloc.GpuAddress);

        var boundQuads = -1;
        var drawn = 0;
        foreach (var key in EnumerateCellKeysInCylinder(cylinder))
        {
            if (!_meshCache.TryPeek(key, out var entry))
            {
                continue;
            }

            if (!_ringBuffer.TryAllocate(
                    _recorder.FrameIndex, PerDrawByteSize, out var perDrawAlloc, GpuRingBuffer12.CbAlignment))
            {
                break; // partial mirror beats none; the main pass is unaffected
            }
            var textureIndices = entry.TextureIndices;
            unsafe
            {
                textureIndices.NormalDecodeMetadata[0] = BuildNormalBc5Mask(entry.NormalTextureEntries);
                *(TerrainTextureIndices*)perDrawAlloc.CpuPtr = textureIndices;
            }
            cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerDrawCbv, perDrawAlloc.GpuAddress);

            // Mirror PSOs are built alongside their colour twins, so a cell that has been drawn in
            // colour already has one; EnsureBlendPipelines covers the case where the mirror pass
            // reaches a cell first (a reflection can see terrain the camera cannot).
            if (boundQuads != entry.BlendQuadCount)
            {
                boundQuads = entry.BlendQuadCount;
                EnsureBlendPipelines(boundQuads);
                cmd.SetPipelineState(_mirrorPsoByQuads[Math.Clamp(
                    boundQuads, 1, TerrainVertexLayout.MaxBlendQuads)]!);
            }

            cmd.IASetVertexBuffers(0, entry.VertexView());
            cmd.IASetVertexBuffers(1, entry.BlendWeightView());
            BindCellGrid(cmd, entry);
            cmd.DrawIndexedInstanced((uint)_indexCount, 1, 0, 0, 0);
            drawn++;
        }

        return drawn;
    }

    /// <summary>Cell keys within the cylinder, via the spatial index when present (matching the
    /// gather in <see cref="RenderInternal" />) or the raw cell dictionary otherwise.</summary>
    private IEnumerable<(int gx, int gy)> EnumerateCellKeysInCylinder(VisibilityCylinder cylinder)
    {
        if (_spatialIndex is not null)
        {
            _spatialIndex.QueryCellsInRadius(
                cylinder.Position.X, -cylinder.Position.Y, cylinder.Radius, _candidateScratch);
            foreach (var candidate in _candidateScratch)
            {
                yield return candidate.Key;
            }
        }
        else
        {
            foreach (var key in _cells!.Keys)
            {
                if (cylinder.ContainsCell(key.gx, key.gy))
                {
                    yield return key;
                }
            }
        }
    }

    private int RenderInternal(
        Matrix4x4 viewProj,
        VisibilityCylinder cylinder,
        bool colorPass,
        Vector3 renderOrigin)
    {
        if ((_spatialIndex is null || _spatialIndex.CellCount == 0) &&
            (_cells is null || _cells.Count == 0))
        {
            LastStats.Reset();
            return 0;
        }

        var started = StartTiming();
        LastStats.Reset();
        _textureResolver.ResetFrameStats();

        // Self-measured frame duration → this frame's time-based streaming scale. A back-to-back
        // second call in the same frame (depth prepass) measures ~0 and floors at 1.0 — allowances
        // never shrink below the 60fps calibration.
        // Driven from a SMOOTHED frame time so one hitch cannot license the burst that sustains it.
        var nowTimestamp = Stopwatch.GetTimestamp();
        if (_lastFrameTimestamp == 0)
        {
            _frameBudgetScale = 1.0;
        }
        else
        {
            _smoothedFrameSeconds = Core.Resources.StreamingFrameBudgetScaler.SmoothFrameSeconds(
                _smoothedFrameSeconds,
                Stopwatch.GetElapsedTime(_lastFrameTimestamp, nowTimestamp).TotalSeconds);
            _frameBudgetScale = Core.Resources.StreamingFrameBudgetScaler.Scale(_smoothedFrameSeconds);
        }

        _lastFrameTimestamp = nowTimestamp;

        var cmd = _recorder.CommandList;
        var frameIndex = _recorder.FrameIndex;
        // Main color/depth draw rejection only. A malformed matrix produces null and therefore
        // fails open for every cell; streaming admission below remains cylinder-driven.
        var drawFrustum = TerrainCellDrawCulling.CreateFrustum(viewProj, renderOrigin);
        LastStats.TerrainFrustumCullingActive = drawFrustum.HasValue;

        var segmentStarted = StartTiming();

        // Per-frame CB (b0) — viewProj. Soft-fail on ring exhaustion (dense whole-map frames):
        // skip the terrain pass this frame instead of throwing and killing the render loop.
        if (!_ringBuffer.TryAllocate(frameIndex, PerFrameByteSize, out var perFrameAlloc, GpuRingBuffer12.CbAlignment))
        {
            LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
            return 0;
        }
        unsafe { *(Matrix4x4*)perFrameAlloc.CpuPtr = viewProj; }

        // Per-mode CB (b2): x = show diffuse textures (1/0), y = diffuse UV scale (carried
        // per-frame rather than per-quadrant because every cell shares one scale), z = apply VCLR
        // tint (1/0), w = FNV terrain normals (1/0). Textures off + VCLR on gives a VCLR-only
        // debug view and leaves the geometric LAND normal untouched.
        if (!_ringBuffer.TryAllocate(frameIndex, PerModeByteSize, out var perModeAlloc, GpuRingBuffer12.CbAlignment))
        {
            LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
            return 0;
        }
        unsafe
        {
            *(Vector4*)perModeAlloc.CpuPtr = new Vector4(
                _showTextures ? 1f : 0f,
                DiffuseUvScale,
                _showVertexColors ? 1f : 0f,
                _textureResolver.LandscapeNormalMappingEnabled ? 1f : 0f);
        }

        // The depth-only pass runs one zero-quad PSO for every cell. The colour pass cannot: each
        // cell's PSO is chosen by its own blend-quad count, inside the draw loop below.
        if (!colorPass)
        {
            cmd.SetPipelineState(_depthOnlyPso);
        }

        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        EnsureSharedIndexBuffer(cmd);
        cmd.IASetIndexBuffer(_sharedIbv);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, perFrameAlloc.GpuAddress);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerModeCbv, perModeAlloc.GpuAddress);
        GpuRootSignature12.SetGraphicsBindlessTables(cmd, _cbvSrvUavHeap.BindlessHeapStartGpu);

        LastStats.StateSetupMilliseconds = ElapsedMilliseconds(segmentStarted);
        segmentStarted = StartTiming();

        // Pass 1a — gather visible cells.
        _visibleScratch.Clear();
        _missingVisibleScratch.Clear();
        _missingVisibleKeys.Clear();
        var camX = cylinder.Position.X;
        var camY = cylinder.Position.Y;
        // Exit hysteresis: gather one extra cell ring beyond the streaming square, but cells in
        // that margin draw ONLY when already cached — new builds are admitted strictly inside the
        // un-margined radius. Without this, the draw set is recomputed from scratch each frame
        // with a strict boundary test while the far plane extends PAST the square, so any camera
        // motion away from a boundary cell popped that fully visible cell off screen in a single
        // frame (the "cell unloads while moving" report; the mesh stays cached throughout).
        var gatherRadius = cylinder.Radius + WorldGridConstants.CellSize;
        _retainCameraCell = (
            (int)MathF.Floor(camX / WorldGridConstants.CellSize),
            (int)MathF.Floor(camY / WorldGridConstants.CellSize));
        _retainRadiusCells = (int)MathF.Ceiling(cylinder.Radius / WorldGridConstants.CellSize) + 2;
        if (_spatialIndex is not null)
        {
            var half = WorldGridConstants.CellSize * 0.5f;
            _spatialIndex.QueryCellsInRadius(camX, -camY, gatherRadius, _candidateScratch);
            foreach (var candidate in _candidateScratch)
            {
                var center = candidate.CenterCanvas;
                // Same closest-point Chebyshev test as QueryCellsInRadius, at the strict radius.
                var closestX = Math.Clamp(camX, center.X - half, center.X + half);
                var closestY = Math.Clamp(-camY, center.Y - half, center.Y + half);
                var insideAdmit = MathF.Abs(camX - closestX) < cylinder.Radius &&
                                  MathF.Abs(-camY - closestY) < cylinder.Radius;
                if (!insideAdmit && !_meshCache.ContainsKey(candidate.Key))
                {
                    continue; // margin ring is draw-only: never admits new builds
                }
                var dx = camX - center.X;
                var dy = -camY - center.Y;
                _visibleScratch.Add(new VisibleCell(candidate.Key, candidate.Cell, dx * dx + dy * dy));
            }
        }
        else
        {
            var marginCylinder = new VisibilityCylinder(cylinder.Position, gatherRadius);
            foreach (var (key, cell) in _cells!)
            {
                var insideAdmit = cylinder.ContainsCell(key.gx, key.gy);
                if (!insideAdmit &&
                    !(marginCylinder.ContainsCell(key.gx, key.gy) && _meshCache.ContainsKey(key)))
                {
                    continue;
                }
                var cellCenterX = (key.gx + 0.5f) * WorldGridConstants.CellSize;
                var cellCenterY = (key.gy + 0.5f) * WorldGridConstants.CellSize;
                var dx = camX - cellCenterX;
                var dy = camY - cellCenterY;
                _visibleScratch.Add(new VisibleCell(key, cell, dx * dx + dy * dy));
            }
        }

        LastStats.VisibleCandidates = _visibleScratch.Count;
        LastStats.VisibleGatherMilliseconds = ElapsedMilliseconds(segmentStarted);

        // Enforce the planned byte budget now that this frame's retain state is fresh. Runs BEFORE
        // this frame's builds, so residency can exceed the plan by at most one frame of uploads —
        // well inside the LRU cascade's 1.25× backstop headroom.
        EnforceCellByteBudget();

        segmentStarted = StartTiming();
        foreach (var vc in _visibleScratch)
        {
            if (_meshCache.ContainsKey(vc.Key) || _knownUnusableCells.Contains(vc.Key))
            {
                continue;
            }

            _missingVisibleScratch.Add(vc);
            _missingVisibleKeys.Add(vc.Key);
        }

        if (_missingVisibleScratch.Count > 1)
        {
            _missingVisibleScratch.Sort(ByDistanceAscending);
        }
        LastStats.VisibleSortMilliseconds = ElapsedMilliseconds(segmentStarted);

        // Pass 1b — drain ready async builds + draw. Missing cells whose CPU build is ready get
        // their GPU buffers created here (render thread); the rest are enqueued for background
        // build. Only missing cells are distance-sorted so the upload budget is spent nearest-first.
        segmentStarted = StartTiming();
        _frameBuildStarts = 0;
        PruneCompletedBuildTasks();
        var throttled = StreamingThrottled;
        var uploadBudget = throttled ? MaxNewUploadsPerFrame : int.MaxValue;
        // Wall-clock build/upload slice, scaled by the measured frame duration (see
        // _frameBudgetScale): a fixed 3ms inside a 100ms whole-map frame was a 3% duty cycle,
        // throttling the fill hardest exactly when the frame is busiest.
        var uploadTimeBudget = new FrameTimeBudget(MaxMeshBuildMillisecondsPerFrame * _frameBudgetScale);
        // Last blend-quad width bound on this pass, so a run of same-width cells (the normal case —
        // neighbouring cells paint the same land textures) costs one SetPipelineState, not one each.
        var boundQuads = -1;
        var drawn = 0;
        if (_missingVisibleScratch.Count > 0)
        {
            foreach (var vc in _missingVisibleScratch)
            {
                // Stop building new cells once the per-frame time budget is spent. uploadBudget
                // is captured by TryDrawVisibleCell, so zeroing it makes GetOrUploadMesh skip the
                // build for the remaining misses; already-cached cells (the second loop) still draw.
                if (throttled && uploadBudget > 0 && uploadTimeBudget.IsExpired)
                {
                    uploadBudget = 0;
                }
                TryDrawVisibleCell(vc);
            }

            foreach (var vc in _visibleScratch)
            {
                if (!_missingVisibleKeys.Contains(vc.Key))
                {
                    TryDrawVisibleCell(vc);
                }
            }
        }
        else
        {
            foreach (var vc in _visibleScratch)
            {
                TryDrawVisibleCell(vc);
            }
        }

        // Kick off background builds for cells enqueued this frame (nearest-first via the
        // distance-sorted missing loop), then drop any completed results for cells that have
        // since left view so _buildResults stays bounded. Both read _missingVisibleKeys, so they
        // must run before it is cleared.
        StartQueuedBuilds();
        PruneInvisibleBuildResults();

        _missingVisibleKeys.Clear();
        LastStats.DrawLoopMilliseconds = ElapsedMilliseconds(segmentStarted);

        LastStats.TerrainDraws = drawn;
        LastStats.TextureCacheMisses = _textureResolver.FrameCacheMisses;
        LastStats.TextureCompressedUploads = _textureResolver.FrameCompressedTextureUploads;
        LastStats.TextureRgbaFallbackUploads = _textureResolver.FrameRgbaTextureUploads;
        LastStats.TextureQueuedResolves = _textureResolver.FrameQueuedTextureResolves;
        LastStats.TextureActiveResolves = _textureResolver.FrameActiveTextureResolves;
        LastStats.TexturePendingResolves = _textureResolver.PendingTextureResolves;
        LastStats.TexturePendingUploads = _textureResolver.PendingTextureUploads;
        LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
        return drawn;

        void TryDrawVisibleCell(VisibleCell vc)
        {
            var entry = GetOrUploadMesh(vc.Key, vc.Cell, ref uploadBudget);
            if (entry is null) return;

            // Deliberately AFTER GetOrUploadMesh: every cylinder-admitted cell keeps exactly the
            // same build/upload/cache-recency behavior. This rejects only the main color/depth draw;
            // RenderShadowDepth and RenderMirror retain their own cylinder semantics.
            if (!TerrainCellDrawCulling.ShouldDraw(drawFrustum, entry.Grid, entry.HeightBounds))
            {
                LastStats.TerrainFrustumRejected++;
                return;
            }

            // Slot 0 = TerrainVertex; slot 1 = per-cell blend weights for the engine-accurate
            // weighted sum. Vortice doesn't expose a 2-buffer IASetVertexBuffers overload directly;
            // the per-slot calls below are cheap and stay readable. The depth-only pass reads no
            // weights at all, so it binds neither slot 1 nor a per-width PSO.
            if (colorPass && boundQuads != entry.BlendQuadCount)
            {
                boundQuads = entry.BlendQuadCount;
                cmd.SetPipelineState(EnsureBlendPipelines(boundQuads));
            }

            cmd.IASetVertexBuffers(0, entry.VertexView());
            if (colorPass)
            {
                cmd.IASetVertexBuffers(1, entry.BlendWeightView());
            }

            BindCellGrid(cmd, entry);

            var cellDrawStarted = StartTiming();
            DrawCell(cmd, entry);
            LastStats.QuadrantDrawMilliseconds += ElapsedMilliseconds(cellDrawStarted);
            drawn++;
        }
    }

    /// <summary>
    ///     Hands the cell's grid to the vertex shader as root constants at <c>b4</c>. Mandatory on
    ///     EVERY terrain draw, in every pass: <see cref="TerrainVertex" /> carries no X or Y, so a
    ///     draw that skipped this would place the cell on whatever grid the previous draw left in the
    ///     root arguments — silently, and most visibly as two cells stacked on the same ground.
    /// </summary>
    private static void BindCellGrid(ID3D12GraphicsCommandList cmd, CachedCellMesh12 entry)
    {
        Span<uint> constants =
        [
            BitConverter.SingleToUInt32Bits(entry.Grid.OriginX),
            BitConverter.SingleToUInt32Bits(entry.Grid.OriginY),
            BitConverter.SingleToUInt32Bits(entry.Grid.Spacing),
            (uint)entry.Grid.GridSize
        ];
        cmd.SetGraphicsRoot32BitConstants(GpuRootSignature12.Slots.TerrainCellGridConstants, constants, 0);
    }

    private void EnsureSharedIndexBuffer(ID3D12GraphicsCommandList cmd)
    {
        if (_sharedIndexBuffer is not null)
        {
            return;
        }

        _sharedIndexBuffer = GpuMeshBufferFactory12.CreateDefaultBuffer(
            _gpu,
            cmd,
            _deletionQueue,
            _sharedIndexData,
            ResourceStates.IndexBuffer);
        _sharedIbv = GpuMeshBufferFactory12.IndexBufferViewOf(_sharedIndexBuffer);
        // One per worldspace (replaced on grid-size change); small but device-local and previously
        // uncounted like every other fixed allocation.
        _sharedIndexFootprint?.Dispose();
        _sharedIndexFootprint = Gpu.D3D12.GpuFixedFootprintTracker12.LocalInstance.Add(
            "terrain-shared-ib",
            Gpu.GpuResourceFootprint.CommittedBufferBytes((long)_sharedIndexBuffer.Description.Width));
    }

    /// <summary>
    ///     Engine-accurate per-cell draw: binds the cell's diffuse and FNV normal bindless indices
    ///     (resolved once at mesh-build time via <see cref="CellTerrainTextureSet" />) and issues a
    ///     single DrawIndexedInstanced for the full 33×33 mesh. The per-vertex blend weights
    ///     bound at vertex slot 1 carry all per-cell variation, so the prior 4-quadrant
    ///     sub-draws + per-quadrant constant buffer are gone.
    /// </summary>
    private void DrawCell(ID3D12GraphicsCommandList cmd, CachedCellMesh12 entry)
    {
        // Soft-fail rather than throw: if the shared ring slot is full, skip this cell's draw and
        // present what fit instead of abandoning the frame. Terrain is drawn first and bounded by
        // the cylinder radius, so this rarely bites, but it keeps the frame from blanking.
        if (!_ringBuffer.TryAllocate(_recorder.FrameIndex, PerDrawByteSize, out var perDrawAlloc, GpuRingBuffer12.CbAlignment))
        {
            LastStats.TerrainDrawsTruncated++; // surfaced on the HUD: a skip here reads as a missing cell
            return;
        }
        var textureIndices = entry.TextureIndices;
        unsafe
        {
            // Entry metadata is deliberately read at draw time. Cold normal-map misses initially
            // expose the RGB flat placeholder (decode mode None); an asynchronously promoted Xbox
            // ATI2/BC5 texture changes the same stable Entry to Bc5ReconstructZ without rebuilding
            // this cell or changing its bindless index.
            textureIndices.NormalDecodeMetadata[0] = BuildNormalBc5Mask(entry.NormalTextureEntries);
            *(TerrainTextureIndices*)perDrawAlloc.CpuPtr = textureIndices;
        }
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerDrawCbv, perDrawAlloc.GpuAddress);

        cmd.DrawIndexedInstanced(
            indexCountPerInstance: (uint)_indexCount,
            instanceCount: 1,
            startIndexLocation: 0,
            baseVertexLocation: 0,
            startInstanceLocation: 0);
        LastStats.TerrainQuadrantDraws++;  // stat name kept; now = one draw per cell
    }

    private CachedCellMesh12? GetOrUploadMesh((int gx, int gy) key, CellRecord cell, ref int uploadBudget)
    {
        if (_meshCache.TryGet(key, out var existing)) return existing;
        if (_knownUnusableCells.Contains(key)) return null;

        // No WorldRenderCache (tests / standalone) → keep the simple synchronous build. The async
        // path needs the cache both for the warm texture-set lookups and as the thread-safe
        // dependency the background build tasks read.
        if (_renderCache is null)
        {
            if (uploadBudget <= 0) return null;
            var builtSync = TryBuildAndCacheSync(key, cell);
            if (builtSync is null) return null;
            uploadBudget--;
            LastStats.NewUploads++;
            unchecked { ContentVersion++; } // new resident cell → sun shadow map re-renders
            return builtSync;
        }

        // Background build ready? Peek (don't remove) so a budget-deferred result survives to the
        // next frame instead of being discarded.
        if (TryPeekBuildResult(key, out var cpu))
        {
            if (cpu.Unusable)
            {
                RemoveBuildResult(key);
                _queuedOrBuilding.Remove(key);
                MarkCellBuildFailure(key);
                return null;
            }
            if (uploadBudget <= 0) return null; // keep the result; upload on a later frame

            RemoveBuildResult(key);
            _queuedOrBuilding.Remove(key);
            var entry = UploadBuiltCell(key, cpu);
            if (entry is null)
            {
                MarkCellBuildFailure(key);
                return null;
            }
            uploadBudget--;
            LastStats.NewUploads++;
            unchecked { ContentVersion++; } // new resident cell → sun shadow map re-renders
            return entry;
        }

        // Not built yet — make sure it's queued for a background build.
        EnqueueBuild(key);
        return null;
    }

    private void EnqueueBuild((int gx, int gy) key)
    {
        if (_queuedOrBuilding.Contains(key)) return;
        _buildQueue.Enqueue(key);
        _queuedOrBuilding.Add(key);
    }

    /// <summary>Counts a failed build/upload; the cell is blacklisted for the worldspace session
    /// only after <see cref="MaxCellBuildFailures" /> attempts, so a transient BSA/IO exception
    /// gets retried on a later frame instead of permanently holing the cell.</summary>
    private void MarkCellBuildFailure((int gx, int gy) key)
    {
        var failures = _cellBuildFailures.GetValueOrDefault(key) + 1;
        _cellBuildFailures[key] = failures;
        if (failures >= MaxCellBuildFailures)
        {
            _knownUnusableCells.Add(key);
        }
    }

    private bool WithinRetainMargin((int gx, int gy) key) =>
        ChebyshevFromCamera(key) <= _retainRadiusCells;

    private int ChebyshevFromCamera((int gx, int gy) key) =>
        Math.Max(Math.Abs(key.gx - _retainCameraCell.gx), Math.Abs(key.gy - _retainCameraCell.gy));

    /// <summary>
    ///     True when <paramref name="key" /> is inside the most recent shadow replay's caster ring.
    ///     Those cells cast into the published cascades but are read via <c>TryPeek</c> (no recency
    ///     bump by design — shadow reads of far cells must not push the camera's own cells toward
    ///     eviction), so LRU order alone would rank an actively-shadowing caster as coldest.
    /// </summary>
    private bool WithinShadowPin((int gx, int gy) key)
    {
        if (_shadowPinCylinder is not { } cylinder)
        {
            return false;
        }

        var centerX = (key.gx + 0.5f) * WorldGridConstants.CellSize;
        var centerY = (key.gy + 0.5f) * WorldGridConstants.CellSize;
        var half = WorldGridConstants.CellSize * 0.5f;
        // Same closest-point Chebyshev test the gather uses (canvas Y is negated world Y).
        var closestX = Math.Clamp(cylinder.Position.X, centerX - half, centerX + half);
        var closestY = Math.Clamp(-cylinder.Position.Y, centerY - half, centerY + half);
        return MathF.Abs(cylinder.Position.X - closestX) < cylinder.Radius &&
               MathF.Abs(-cylinder.Position.Y - closestY) < cylinder.Radius;
    }

    /// <summary>
    ///     Evicts resident cells FARTHEST-FIRST until the planned byte budget is met, never touching
    ///     a cell inside the retain ring or the shadow-caster ring.
    ///     <para>
    ///         Distance rather than LRU because recency is only a proxy for "will be needed again",
    ///         and it fails on the most ordinary motion there is: face one way for a while, then turn
    ///         around — the cells you are about to look at are precisely the LRU tail. Chebyshev
    ///         distance from the camera cell is the actual predictor for terrain.
    ///     </para>
    ///     <para>
    ///         Falling short of the target is a legitimate outcome: if everything left is pinned, the
    ///         visible world is simply larger than the budget, and under a no-LOD policy the answer
    ///         is to run over budget (logged once per worldspace) rather than punch a hole in what
    ///         the camera can see.
    ///     </para>
    /// </summary>
    private void EnforceCellByteBudget()
    {
        if (_cellByteBudget <= 0 || _meshCache.EstimatedBytes <= _cellByteBudget)
        {
            return;
        }

        _evictScratch.Clear();
        foreach (var key in _meshCache.SnapshotKeys())
        {
            if (WithinRetainMargin(key) || WithinShadowPin(key))
            {
                continue; // pinned: visible, or casting into the published cascades
            }

            _evictScratch.Add((key, ChebyshevFromCamera(key)));
        }

        if (_evictScratch.Count == 0)
        {
            WarnBudgetUnreachable();
            return;
        }

        _evictScratch.Sort(static (a, b) => b.Distance.CompareTo(a.Distance)); // farthest first
        var evicted = 0;
        _suppressEvictionContentBump = true;
        try
        {
            foreach (var (key, _) in _evictScratch)
            {
                if (_meshCache.EstimatedBytes <= _cellByteBudget)
                {
                    break;
                }

                _meshCache.Evict(key);
                evicted++;
            }
        }
        finally
        {
            _suppressEvictionContentBump = false;
        }

        if (evicted > 0)
        {
            // ONE bump for the whole burst: the published cascades need re-rendering once, and a
            // per-cell bump would ladder the throttled shadow re-render into a storm.
            unchecked { ContentVersion++; }
            LastStats.CellsEvictedForBudget += evicted;
        }

        if (_meshCache.EstimatedBytes > _cellByteBudget)
        {
            WarnBudgetUnreachable();
        }
    }

    private void WarnBudgetUnreachable()
    {
        if (_budgetUnreachableWarned)
        {
            return;
        }

        _budgetUnreachableWarned = true;
        Log.Info(
            "TerrainRenderer12: visible terrain ({0:N0} MB resident) exceeds the {1:N0} MB budget — " +
            "every remaining cell is inside the retain or shadow-caster ring. Running over budget " +
            "rather than evicting visible terrain.",
            _meshCache.EstimatedBytes / (1024 * 1024), _cellByteBudget / (1024 * 1024));
    }

    private void StartQueuedBuilds()
    {
        while (_frameBuildStarts < EffectiveMaxBuildStartsPerFrame &&
               Volatile.Read(ref _activeBuildTasks) < EffectiveMaxConcurrentBuildTasks &&
               _buildQueue.Count > 0)
        {
            var key = _buildQueue.Dequeue();

            // Drop stale queue entries: the cell got cached/unusable some other way, was already
            // dequeued, or has scrolled well out of view since it was enqueued (a prior frame).
            // The retain margin keeps entries that merely oscillated across the streaming-square
            // boundary for a frame under fast movement — dropping those forced full rebuilds
            // exactly when build throughput was scarcest.
            if (!_queuedOrBuilding.Contains(key) ||
                _meshCache.ContainsKey(key) ||
                _knownUnusableCells.Contains(key) ||
                (!_missingVisibleKeys.Contains(key) && !WithinRetainMargin(key)))
            {
                _queuedOrBuilding.Remove(key);
                continue;
            }
            if (_cells is null || !_cells.TryGetValue(key, out var cell))
            {
                _queuedOrBuilding.Remove(key);
                continue;
            }

            StartBuildTask(key, cell);
            _frameBuildStarts++;
        }
    }

    private void StartBuildTask((int gx, int gy) key, CellRecord cell)
    {
        // Snapshot the worldspace-scoped dependencies so a concurrent LoadData can't swap them out
        // from under a running task; the generation tag makes the result a no-op if it does.
        var cells = _cells;
        var renderCache = _renderCache;
        var gridSize = _gridSize;
        int generation;
        lock (_buildResultsLock)
        {
            generation = _loadGeneration;
        }

        Interlocked.Increment(ref _activeBuildTasks);
        var task = Task.Run(() =>
        {
            try
            {
                var built = TerrainCellCpuBuilder.BuildCellCpu(key, cell, cells, renderCache, gridSize, generation);
                if (!built.Unusable)
                {
                    // Warm the bytecode for this cell's blend width here, off the render thread. The
                    // render thread then only has to create the pipeline object when the cell
                    // uploads, instead of running FXC inside a frame.
                    TerrainPipelineFactory12.PrecompileBlendPermutation(built.BlendQuadCount);
                }

                StoreBuildResult(key, built);
            }
            catch (Exception ex)
            {
                Log.Warn("TerrainRenderer12: cell build failed for {0},{1}: {2}", key.gx, key.gy, ex.Message);
                StoreBuildResult(key, BuiltCellCpuData.Failed(generation));
            }
            finally
            {
                Interlocked.Decrement(ref _activeBuildTasks);
            }
        });

        _buildTasks.Add(task);
    }

    private void StoreBuildResult((int gx, int gy) key, BuiltCellCpuData data)
    {
        lock (_buildResultsLock)
        {
            if (data.Generation != _loadGeneration) return; // result built against a prior worldspace — drop
            _buildResults[key] = data;
        }
    }

    private bool TryPeekBuildResult((int gx, int gy) key, [MaybeNullWhen(false)] out BuiltCellCpuData data)
    {
        lock (_buildResultsLock)
        {
            return _buildResults.TryGetValue(key, out data);
        }
    }

    private void RemoveBuildResult((int gx, int gy) key)
    {
        lock (_buildResultsLock)
        {
            _buildResults.Remove(key);
        }
    }

    private void PruneCompletedBuildTasks()
    {
        _buildTasks.PruneCompleted();
    }

    /// <summary>
    ///     Drop completed CPU builds for cells no longer visible-missing this frame so a fast
    ///     flythrough can't accumulate built-but-never-uploaded results. Budget-deferred cells stay
    ///     in <see cref="_missingVisibleKeys" /> and are preserved; they upload on a later frame.
    /// </summary>
    private void PruneInvisibleBuildResults()
    {
        lock (_buildResultsLock)
        {
            if (_buildResults.Count == 0) return;
            _pruneScratch.Clear();
            foreach (var k in _buildResults.Keys)
            {
                // Retain completed builds within the margin band of the camera: a one-frame
                // boundary exit under fast/diagonal movement must not discard a finished CPU
                // build (memory stays bounded by the margin band).
                if (!_missingVisibleKeys.Contains(k) && !WithinRetainMargin(k))
                {
                    _pruneScratch.Add(k);
                }
            }
            foreach (var k in _pruneScratch)
            {
                _buildResults.Remove(k);
                _queuedOrBuilding.Remove(k);
            }
        }
    }

    /// <summary>
    ///     Render-thread upload of an already-built cell: creates the two GPU buffers from the
    ///     per-task CPU arrays, resolves the 4 diffuse SRVs (NOT thread-safe — render thread only),
    ///     and inserts into the LRU cache. The analog of <c>ReferenceMeshCache12.UploadDecodedMesh</c>.
    /// </summary>
    private CachedCellMesh12? UploadBuiltCell((int gx, int gy) key, BuiltCellCpuData cpu)
    {
        var started = StartTiming();
        var success = false;
        try
        {
            // Before the arena range exists, so a PSO failure cannot strand an allocation — and
            // because a cell must never enter the cache without the pipeline its width needs.
            EnsureBlendPipelines(cpu.BlendQuadCount);
            var geometry = _terrainArena.Upload(
                _recorder.CommandList,
                _deletionQueue,
                MemoryMarshal.AsBytes<TerrainVertex>(cpu.Vertices!),
                MemoryMarshal.AsBytes<ushort>(cpu.BlendWeights!),
                debugTag: null);

            var textureIndices = ResolveSlotTextureIndices(cpu.TextureSet, out var normalTextureEntries);

            var entry = new CachedCellMesh12
            {
                Geometry = geometry,
                Grid = cpu.Grid,
                HeightBounds = cpu.HeightBounds,
                BlendQuadCount = cpu.BlendQuadCount,
                Arena = _terrainArena,
                TextureIndices = textureIndices,
                NormalTextureEntries = normalTextureEntries,
                DeletionQueue = _deletionQueue,
            };
            _meshCache.Set(key, entry);
            success = true;
            return entry;
        }
        finally
        {
            var elapsed = ElapsedMilliseconds(started);
            LastStats.MeshBuildUploadMilliseconds += elapsed;
            if (RendererProfilerTrace.IsEnabled)
            {
                RendererProfilerTrace.Event("resource-event", new Dictionary<string, object?>
                {
                    ["resource"] = "terrain-mesh",
                    ["phase"] = "upload",
                    ["source"] = "async-built-cell",
                    ["gridX"] = key.gx,
                    ["gridY"] = key.gy,
                    ["elapsedMs"] = elapsed,
                    ["success"] = success
                });
            }
        }
    }

    /// <summary>
    ///     Synchronous build retained for the no-WorldRenderCache case (tests / standalone). Uses
    ///     the shared single-threaded scratch arrays; never runs concurrently with a build task.
    /// </summary>
    private CachedCellMesh12? TryBuildAndCacheSync((int gx, int gy) key, CellRecord cell)
    {
        var started = StartTiming();
        var success = false;
        try
        {
            if (!TerrainMeshBuilder.TryBuildVertices(cell, _vertexScratch, _renderCache, out var grid))
            {
                _knownUnusableCells.Add(key);
                return null;
            }

            var textureSet = _renderCache is not null
                ? _renderCache.GetOrBuildTerrainTextureSet(cell, () => TerrainCellCpuBuilder.BuildCellTextureSet(key, cell, _cells, _gridSize))
                : TerrainCellCpuBuilder.BuildCellTextureSet(key, cell, _cells, _gridSize);
            // The scratch array is sized for the widest cell; this cell uploads only the prefix its
            // own quad count occupies, so the tail from a previously-built wider cell is not sent.
            var blendQuadCount = TerrainBlendWeightPacking.QuadCountFor(textureSet?.ActiveSlotCount ?? 0);
            var blendUshorts = _vertexScratch.Length * blendQuadCount * TerrainBlendWeightPacking.SlotsPerQuad;
            TerrainCellCpuBuilder.PopulateBlendWeights(textureSet, _blendWeightScratch, _gridSize, blendQuadCount);
            EnsureBlendPipelines(blendQuadCount);
            // One arena range for both streams — so the blend weights must be populated BEFORE the
            // upload (the old code created the vertex buffer first, then built the weights).
            var geometry = _terrainArena.Upload(
                _recorder.CommandList,
                _deletionQueue,
                MemoryMarshal.AsBytes<TerrainVertex>(_vertexScratch),
                MemoryMarshal.AsBytes<ushort>(_blendWeightScratch.AsSpan(0, blendUshorts)),
                debugTag: null);

            var textureIndices = ResolveSlotTextureIndices(textureSet, out var normalTextureEntries);
            var heightBounds = TerrainCellHeightBounds.FromVertices(_vertexScratch);

            var entry = new CachedCellMesh12
            {
                Geometry = geometry,
                Grid = grid,
                HeightBounds = heightBounds,
                BlendQuadCount = blendQuadCount,
                Arena = _terrainArena,
                TextureIndices = textureIndices,
                NormalTextureEntries = normalTextureEntries,
                DeletionQueue = _deletionQueue,
            };
            _meshCache.Set(key, entry);
            success = true;
            return entry;
        }
        finally
        {
            var elapsed = ElapsedMilliseconds(started);
            LastStats.MeshBuildUploadMilliseconds += elapsed;
            if (RendererProfilerTrace.IsEnabled)
            {
                RendererProfilerTrace.Event("resource-event", new Dictionary<string, object?>
                {
                    ["resource"] = "terrain-mesh",
                    ["phase"] = "build-upload",
                    ["source"] = "sync-cell",
                    ["gridX"] = key.gx,
                    ["gridY"] = key.gy,
                    ["elapsedMs"] = elapsed,
                    ["success"] = success
                });
            }
        }
    }

    /// <summary>
    ///     Resolve the cell-wide diffuse and normal bindless texture indices the terrain shader
    ///     samples. The engine-default sentinel routes to DirtWasteland01 and its paired normal.
    ///     Missing authored normals use uint.MaxValue, which the shader maps to an exact flat normal.
    ///     Non-FNV games fill only that sentinel and remain on their old geometric-normal path.
    /// </summary>
    private TerrainTextureIndices ResolveSlotTextureIndices(
        CellTerrainTextureSet? set,
        out GpuTextureCache12.Entry?[]? normalTextureEntries)
    {
        var active = set?.ActiveSlotCount ?? 0;
        var indices = new TerrainTextureIndices();
        normalTextureEntries = _textureResolver.LandscapeNormalMappingEnabled
            ? new GpuTextureCache12.Entry?[CellTerrainTextureSet.MaxSlots]
            : null;
        unsafe
        {
            for (var slot = 0; slot < CellTerrainTextureSet.MaxSlots; slot++)
            {
                var hasAuthoredLayer = slot < active &&
                    set!.SlotFormIds[slot] != CellLayerWeightTable.EngineDefaultSentinelFormId;
                var entry = hasAuthoredLayer
                    ? _textureResolver.Resolve(set!.SlotFormIds[slot])
                    : _textureResolver.EngineDefault;
                indices.Index[slot] = entry.BindlessIndex;

                if (!_textureResolver.LandscapeNormalMappingEnabled)
                {
                    indices.NormalIndex[slot] = uint.MaxValue;
                    continue;
                }

                var normalEntry = hasAuthoredLayer
                    ? _textureResolver.ResolveLandscapeNormal(set!.SlotFormIds[slot])
                    : _textureResolver.EngineDefaultNormal;
                indices.NormalIndex[slot] = normalEntry?.BindlessIndex ?? uint.MaxValue;
                normalTextureEntries![slot] = normalEntry;
            }
        }
        return indices;
    }

    private static uint BuildNormalBc5Mask(GpuTextureCache12.Entry?[]? normalTextureEntries)
    {
        if (normalTextureEntries is null)
        {
            return 0;
        }

        uint mask = 0;
        var count = Math.Min(normalTextureEntries.Length, CellTerrainTextureSet.MaxSlots);
        for (var slot = 0; slot < count; slot++)
        {
            if (normalTextureEntries[slot]?.NormalDecodeMode == GpuNormalDecodeMode.Bc5ReconstructZ)
            {
                mask |= 1u << slot;
            }
        }

        return mask;
    }

    private long StartTiming() => DetailedProfilingEnabled ? Stopwatch.GetTimestamp() : 0;

    private static double ElapsedMilliseconds(long started) =>
        started == 0 ? 0 : Stopwatch.GetElapsedTime(started).TotalMilliseconds;


    private readonly record struct VisibleCell((int gx, int gy) Key, CellRecord Cell, float DistSq);
}
#endif
