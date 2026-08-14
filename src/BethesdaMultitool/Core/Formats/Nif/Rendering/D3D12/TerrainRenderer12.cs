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
    private static readonly int MaxConcurrentBuildTasks = ParsePositiveIntEnvironment(
        EnvironmentVariables.Viewer.TerrainBuildConcurrency,
        defaultValue: Math.Clamp(Environment.ProcessorCount / 4, 4, 8),
        min: 1,
        max: 16);

    private static readonly int MaxBuildStartsPerFrame = ParsePositiveIntEnvironment(
        EnvironmentVariables.Viewer.TerrainBuildStartsPerFrame,
        defaultValue: MaxConcurrentBuildTasks,
        min: 1,
        max: MaxConcurrentBuildTasks);

    private const int MinCacheCapacity = 1024;
    private const int CacheHeadroom = 256;

    public const float DefaultDiffuseUvScale = 1f / 512f;

    /// <summary>
    ///     Input layout matching the engine-accurate terrain shader: slot 0 is the shared
    ///     <see cref="GpuMeshUploader.GpuVertex" /> (6 attributes, TEXCOORD0..5); slot 1 is
    ///     the per-cell <see cref="CellTerrainTextureSet" /> blend-weight stream — four float4s
    ///     per vertex (16 layer weights, TEXCOORD6..9), uploaded as an independent buffer per
    ///     cached cell. Sixteen weights match the 2D blit's per-pixel layer ceiling, so the 3D
    ///     terrain blend is now non-lossy.
    /// </summary>
    private static readonly InputElementDescription[] TerrainInputElements =
    [
        new("TEXCOORD", 0, Format.R32G32B32_Float,    0, 0), // aPosition
        new("TEXCOORD", 1, Format.R32G32B32_Float,   12, 0), // aNormal
        new("TEXCOORD", 2, Format.R32G32_Float,      24, 0), // aTexCoord (unused on this shader)
        new("TEXCOORD", 3, Format.R32G32B32A32_Float, 32, 0), // aVertexColor
        new("TEXCOORD", 4, Format.R32G32B32_Float,   48, 0), // aTangent (unused)
        new("TEXCOORD", 5, Format.R32G32B32_Float,   60, 0), // aBitangent (unused)
        new("TEXCOORD", 6, Format.R32G32B32A32_Float,  0, 1), // aLayerWeights0 (slot 1, slots 0..3)
        new("TEXCOORD", 7, Format.R32G32B32A32_Float, 16, 1), // aLayerWeights1 (slots 4..7)
        new("TEXCOORD", 8, Format.R32G32B32A32_Float, 32, 1), // aLayerWeights2 (slots 8..11)
        new("TEXCOORD", 9, Format.R32G32B32A32_Float, 48, 1)  // aLayerWeights3 (slots 12..15)
    ];

    private static readonly Comparison<VisibleCell> ByDistanceAscending =
        (a, b) => a.DistSq.CompareTo(b.DistSq);

    private static readonly Logger Log = Logger.Instance;

    private readonly GpuDevice12 _gpu;
    private readonly GpuCommandRecorder12 _recorder;
    private readonly GpuRingBuffer12 _ringBuffer;
    private readonly GpuDescriptorHeapAllocator12 _cbvSrvUavHeap;
    private readonly GpuDeletionQueue12 _deletionQueue;
    private readonly TerrainTextureResolver12 _textureResolver;
    private readonly ID3D12PipelineState _pso;
    // Depth-only variant of _pso (same VS + depth state, no pixel shader, no render targets). Used
    // by the 2D map's top-down overlay to lay down terrain depth so placed references are occluded
    // by the ground without painting terrain color over the 2D map's own terrain layer.
    private readonly ID3D12PipelineState _depthOnlyPso;
    // Sun-shadow variant: depth-only like above but single-sample (the shadow map is never MSAA),
    // no culling (back faces of grazing slopes must still occlude), and negatively depth-biased
    // for the reversed-Z acne fix — matching the reference renderer's shadow PSOs.
    private readonly ID3D12PipelineState _shadowDepthPso;
    // Per-worldspace LAND grid resolution. Defaults to 33×33 (Fallout/Oblivion/Skyrim + runtime DMP);
    // a Morrowind worldspace load bumps this to 65×65. The shared index buffer + scratch arrays are
    // rebuilt in LoadData when the grid size changes, and DrawCell issues _indexCount per cell.
    private int _gridSize = TerrainConstants.LandGridSize;
    private TerrainTriangleTopology _triangleTopology = TerrainTriangleTopology.FixedSouthEastNorthWest;
    private int _indexCount = TerrainMeshBuilder.IndexCount;
    private ushort[] _sharedIndexData;
    private ID3D12Resource? _sharedIndexBuffer;
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
    private readonly List<global::BethesdaMultitool.WorldSpatialCell> _candidateScratch = new();
    private GpuMeshUploader.GpuVertex[] _vertexScratch =
        new GpuMeshUploader.GpuVertex[TerrainMeshBuilder.VertexCount];
    private Vector4[] _blendWeightScratch =
        new Vector4[TerrainMeshBuilder.VertexCount * CellTerrainTextureSet.SlotVectors];

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
    private global::BethesdaMultitool.WorldSpatialIndex? _spatialIndex;
    private global::BethesdaMultitool.WorldRenderCache? _renderCache;
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

        var vsBytecode = TerrainPipelineFactory12.CompileEmbeddedShader("terrain_textured.vert.hlsl", "main", "vs_5_1");
        var psBytecode = TerrainPipelineFactory12.CompileEmbeddedShader("terrain_textured.frag.hlsl", "main", "ps_5_1");

        (_pso, _depthOnlyPso) = TerrainPipelineFactory12.BuildPipelineStates(
            gpu, rootSignature, vsBytecode, psBytecode, TerrainInputElements);
        _shadowDepthPso = TerrainPipelineFactory12.BuildShadowPipelineState(
            gpu, rootSignature, vsBytecode, TerrainInputElements);

        _sharedIndexData = TerrainMeshBuilder.BuildSharedIndexBufferData();
        _meshCache = CreateMeshCache(MinCacheCapacity);
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
        _shadowDepthPso.Dispose();
        _depthOnlyPso.Dispose();
        _pso.Dispose();
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
    public global::BethesdaMultitool.WorldRenderStats LastStats { get; } = new();
    public bool DetailedProfilingEnabled { get; set; }

    // Concurrency ceiling used when streaming is unthrottled (the on-demand top-down overlay
    // depth pre-pass, which has no framerate target).
    private static readonly int UnthrottledMaxConcurrentBuildTasks = Math.Clamp(Environment.ProcessorCount, 4, 16);

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
    private LruCache<(int gx, int gy), CachedCellMesh12> CreateMeshCache(int capacity) =>
        new LruCache<(int gx, int gy), CachedCellMesh12>(
                "CellMeshLru",
                ResourceCategory.GpuResident,
                maxEntries: capacity,
                onEvicted: (_, mesh) =>
                {
                    mesh.Dispose();
                    // A cell leaving residency changes what the shadow pass can draw: bump the
                    // content version so the throttled shadow re-render heals the vanished
                    // caster instead of the published cascades keeping its stale depth.
                    unchecked { ContentVersion++; }
                })
            .RegisterWith(ResourceRegistry.Instance, "terrain-cells");

    public void LoadData(Dictionary<(int gx, int gy), CellRecord> cells)
        => LoadData(cells, spatialIndex: null, renderCache: null);

    public void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        global::BethesdaMultitool.WorldSpatialIndex? spatialIndex,
        global::BethesdaMultitool.WorldRenderCache? renderCache)
    {
        _meshCache.Dispose();
        var capacity = Math.Max(MinCacheCapacity, cells.Count + CacheHeadroom);
        _meshCache = CreateMeshCache(capacity);
        _knownUnusableCells.Clear();
        _cellBuildFailures.Clear();
        _cells = cells;
        _spatialIndex = spatialIndex;
        _renderCache = renderCache;

        // Adopt this worldspace's LAND grid resolution (33×33 Fallout-family, 65×65 Morrowind). When
        // it changes, rebuild the shared index buffer + per-cell scratch so the index/vertex/blend
        // stream lengths all match the new grid. All cells of one worldspace share a single grid size.
        EnsureGridSize(cells);

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

        // 4-pre Item C — warm the per-cell texture-set cache so the cold-cell mesh-build
        // burst (16 cells/frame budget) no longer pays the 4 neighbor lookups + per-quadrant
        // layer-weight sort each time. Costs ~0.05 ms × cells.Count at LoadData (~250 ms
        // for a 5000-cell worldspace), folded into the existing ~1–2 s worldspace load.
        if (_renderCache is not null)
        {
            if (cells.Count >= 512)
            {
                var gridSize = _gridSize;
                Parallel.ForEach(cells, pair =>
                {
                    var captured = pair;
                    _renderCache.GetOrBuildTerrainTextureSet(
                        captured.Value,
                        () => TerrainCellCpuBuilder.BuildCellTextureSet(captured.Key, captured.Value, cells, gridSize));
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
        }

        if (gridChanged)
        {
            var vertexCount = TerrainMeshBuilder.VertexCountFor(gridSize);
            _vertexScratch = new GpuMeshUploader.GpuVertex[vertexCount];
            _blendWeightScratch = new Vector4[vertexCount * CellTerrainTextureSet.SlotVectors];
        }
    }

    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder)
        => RenderInternal(viewProj, cylinder, _pso);

    /// <summary>
    ///     Depth-only render: lays terrain depth into the bound depth buffer without writing color.
    ///     Used by the 2D map's top-down "Rendered models" overlay so placed references depth-test
    ///     against the ground and partially-buried meshes are clipped, while the 2D map keeps its
    ///     own terrain layer underneath. Shares the cell mesh cache + async build path with
    ///     <see cref="Render" />.
    /// </summary>
    public int RenderDepthOnly(Matrix4x4 viewProj, VisibilityCylinder cylinder)
        => RenderInternal(viewProj, cylinder, _depthOnlyPso);

    /// <summary>Bumped whenever a terrain cell mesh becomes newly GPU-resident — part of the sun
    /// shadow map's cache key, so streamed-in hills re-render the map (terrain CASTS shadows).</summary>
    public int ContentVersion { get; private set; }

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
    /// </summary>
    public int RenderShadowDepth(Matrix4x4 lightViewProj, VisibilityCylinder cylinder)
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
            return 0; // the host disables this cleared cascade and retries without a lit coverage hole
        }
        unsafe
        {
            *(Matrix4x4*)perFrameAlloc.CpuPtr = lightViewProj;
            // b2 is read by the terrain VS (UV scale feeds an output the null-PS pass ignores,
            // but the register must still be validly bound — earlier passes rebind this slot).
            *(Vector4*)perModeAlloc.CpuPtr = new Vector4(0f, DefaultDiffuseUvScale, 0f, 0f);
        }

        cmd.SetPipelineState(_shadowDepthPso);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        EnsureSharedIndexBuffer(cmd);
        cmd.IASetIndexBuffer(_sharedIbv);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, perFrameAlloc.GpuAddress);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerModeCbv, perModeAlloc.GpuAddress);

        var vertexStride = (uint)Marshal.SizeOf<GpuMeshUploader.GpuVertex>();
        const uint blendWeightStride = 64; // SlotVectors (4) × sizeof(Vector4)
        var drawn = 0;
        foreach (var key in EnumerateCellKeysInCylinder(cylinder))
        {
            if (!_meshCache.TryPeek(key, out var entry))
            {
                continue;
            }

            cmd.IASetVertexBuffers(0, new VertexBufferView
            {
                BufferLocation = entry.VertexBuffer.GPUVirtualAddress,
                SizeInBytes = (uint)entry.VertexBuffer.Description.Width,
                StrideInBytes = vertexStride,
            });
            cmd.IASetVertexBuffers(1, new VertexBufferView
            {
                BufferLocation = entry.BlendWeightBuffer.GPUVirtualAddress,
                SizeInBytes = (uint)entry.BlendWeightBuffer.Description.Width,
                StrideInBytes = blendWeightStride,
            });
            // No per-draw CB: b1 carries the PS's texture indices and this PSO has no PS — skipping
            // it saves one ring allocation per cell per cascade (the old streaming path's real ring
            // pressure) and keeps LastStats untouched.
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

    private int RenderInternal(Matrix4x4 viewProj, VisibilityCylinder cylinder, ID3D12PipelineState pso)
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

        var segmentStarted = StartTiming();

        // Per-frame CB (b0) — viewProj. Soft-fail on ring exhaustion (dense whole-map frames):
        // skip the terrain pass this frame instead of throwing and killing the render loop.
        if (!_ringBuffer.TryAllocate(frameIndex, PerFrameByteSize, out var perFrameAlloc, GpuRingBuffer12.CbAlignment))
        {
            LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
            return 0;
        }
        unsafe { *(Matrix4x4*)perFrameAlloc.CpuPtr = viewProj; }

        // Per-mode CB (b2): x = show diffuse textures (1/0), y = diffuse UV scale (formerly in the
        // per-quadrant CB, now per-frame since every cell uses the same scale), z = apply VCLR tint
        // (1/0), w = FNV terrain normals (1/0). Textures off + VCLR on reproduces the old VCLR-only
        // debug look and also leaves the geometric LAND normal untouched.
        if (!_ringBuffer.TryAllocate(frameIndex, PerModeByteSize, out var perModeAlloc, GpuRingBuffer12.CbAlignment))
        {
            LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
            return 0;
        }
        unsafe
        {
            *(Vector4*)perModeAlloc.CpuPtr = new Vector4(
                _showTextures ? 1f : 0f,
                DefaultDiffuseUvScale,
                _showVertexColors ? 1f : 0f,
                _textureResolver.LandscapeNormalMappingEnabled ? 1f : 0f);
        }

        cmd.SetPipelineState(pso);
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
        var vertexStride = (uint)Marshal.SizeOf<GpuMeshUploader.GpuVertex>();
        const uint blendWeightStride = 64; // SlotVectors (4) × sizeof(Vector4)
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

            // Slot 0 = shared GpuVertex; slot 1 = per-cell blend weights for the engine-
            // accurate weighted sum. Vortice doesn't expose a 2-buffer IASetVertexBuffers
            // overload directly; the per-slot calls below are cheap and stay readable.
            cmd.IASetVertexBuffers(0, new VertexBufferView
            {
                BufferLocation = entry.VertexBuffer.GPUVirtualAddress,
                SizeInBytes = (uint)entry.VertexBuffer.Description.Width,
                StrideInBytes = vertexStride,
            });
            cmd.IASetVertexBuffers(1, new VertexBufferView
            {
                BufferLocation = entry.BlendWeightBuffer.GPUVirtualAddress,
                SizeInBytes = (uint)entry.BlendWeightBuffer.Description.Width,
                StrideInBytes = blendWeightStride,
            });

            var cellDrawStarted = StartTiming();
            DrawCell(cmd, entry);
            LastStats.QuadrantDrawMilliseconds += ElapsedMilliseconds(cellDrawStarted);
            drawn++;
        }
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
        Math.Max(Math.Abs(key.gx - _retainCameraCell.gx), Math.Abs(key.gy - _retainCameraCell.gy))
            <= _retainRadiusCells;

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
                StoreBuildResult(key, TerrainCellCpuBuilder.BuildCellCpu(key, cell, cells, renderCache, gridSize, generation));
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
            var vb = GpuMeshBufferFactory12.CreateDefaultBuffer(
                _gpu,
                _recorder.CommandList,
                _deletionQueue,
                cpu.Vertices!,
                ResourceStates.VertexAndConstantBuffer);
            var blendBuffer = GpuMeshBufferFactory12.CreateDefaultBuffer(
                _gpu,
                _recorder.CommandList,
                _deletionQueue,
                cpu.BlendWeights!,
                ResourceStates.VertexAndConstantBuffer);

            var textureIndices = ResolveSlotTextureIndices(cpu.TextureSet, out var normalTextureEntries);

            var entry = new CachedCellMesh12
            {
                VertexBuffer = vb,
                BlendWeightBuffer = blendBuffer,
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
            if (!TerrainMeshBuilder.TryBuildVertices(cell, _vertexScratch, _renderCache))
            {
                _knownUnusableCells.Add(key);
                return null;
            }

            var vb = GpuMeshBufferFactory12.CreateDefaultBuffer(
                _gpu,
                _recorder.CommandList,
                _deletionQueue,
                _vertexScratch,
                ResourceStates.VertexAndConstantBuffer);

            var textureSet = _renderCache is not null
                ? _renderCache.GetOrBuildTerrainTextureSet(cell, () => TerrainCellCpuBuilder.BuildCellTextureSet(key, cell, _cells, _gridSize))
                : TerrainCellCpuBuilder.BuildCellTextureSet(key, cell, _cells, _gridSize);
            TerrainCellCpuBuilder.PopulateBlendWeights(textureSet, _blendWeightScratch, _gridSize);
            var blendBuffer = GpuMeshBufferFactory12.CreateDefaultBuffer(
                _gpu,
                _recorder.CommandList,
                _deletionQueue,
                _blendWeightScratch,
                ResourceStates.VertexAndConstantBuffer);

            var textureIndices = ResolveSlotTextureIndices(textureSet, out var normalTextureEntries);

            var entry = new CachedCellMesh12
            {
                VertexBuffer = vb,
                BlendWeightBuffer = blendBuffer,
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

    private static int ParsePositiveIntEnvironment(string name, int defaultValue, int min, int max)
    {
        var raw = EnvironmentVariables.Get(name);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }

    private readonly record struct VisibleCell((int gx, int gy) Key, CellRecord Cell, float DistSq);
}
#endif
