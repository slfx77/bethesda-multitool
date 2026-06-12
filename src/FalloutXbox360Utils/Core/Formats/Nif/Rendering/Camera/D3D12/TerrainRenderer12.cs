#if WINDOWS_GUI
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using FalloutXbox360Utils.Core.Diagnostics;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;
using FalloutXbox360Utils.Core.Resources;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D12 = Vortice.Direct3D12;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>
///     v3 Pass 4 Step 2c — D3D12 port of <c>TerrainRenderer</c>. Renders textured
///     heightmap terrain via per-cell vertex buffers + per-quadrant draws against a shared
///     index buffer.
///     <para>
///         Mirrors the D3D11 render-loop shape: gather visible cells via spatial index,
///         sort closest-first, lazily build + upload per-cell vertex buffers under a per-
///         frame budget, then issue 4 quadrant draws per cell with per-quadrant constants
///         + 7 SRVs (4 diffuse + 3 opacity) bound through the shared root signature.
///     </para>
/// </summary>
internal sealed class TerrainRenderer12 : Abstractions.ITerrainRenderer
{
    private const uint PerFrameByteSize = 64;     // float4x4 viewProj
    private const uint PerDrawByteSize = 64;      // uint4[4] = 16 terrain texture bindless indices
    private const uint PerModeByteSize = 16;      // float4 (debugMode.x, uvScale, pad, pad)
    private const ShaderFlags EnableUnboundedDescriptorTables = (ShaderFlags)0x00100000;
    // No per-frame COUNT cap by default — the wall-clock time budget below is the pacer, so the
    // per-frame build cost is bounded by frame-TIME rather than a fixed cell count (a count cap
    // couples terrain load rate to FPS; time-budgeting keeps the per-frame cost FPS-independent).
    private const int MaxNewUploadsPerFrame = int.MaxValue;

    // Wall-clock ceiling on render-thread cell build/upload work per frame. The primary pacer:
    // bounds the movement-stutter spike when many new cells enter view at high render distance by
    // capping *how long* building may take. Tuned against MeshBuildUploadMilliseconds.
    private const double MaxMeshBuildMillisecondsPerFrame = 2.0;

    // Async cell build runs off the render thread, but it still allocates enough to compete with
    // mesh decode and trigger visible GC pauses. Keep the default conservative and tune via env
    // when profiling throughput on a specific machine.
    private static readonly int MaxConcurrentBuildTasks = ParsePositiveIntEnvironment(
        EnvironmentVariables.Viewer.TerrainBuildConcurrency,
        defaultValue: 2,
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
    private readonly ushort[] _sharedIndexData;
    private ID3D12Resource? _sharedIndexBuffer;
    private IndexBufferView _sharedIbv;

    private LruCache<(int gx, int gy), CachedCellMesh12> _meshCache = CreateMeshCache(MinCacheCapacity);
    private readonly HashSet<(int gx, int gy)> _knownUnusableCells = new();
    private readonly List<VisibleCell> _visibleScratch = new();
    private readonly List<VisibleCell> _missingVisibleScratch = new();
    private readonly HashSet<(int gx, int gy)> _missingVisibleKeys = new();
    private readonly List<global::FalloutXbox360Utils.WorldSpatialCell> _candidateScratch = new();
    private readonly GpuMeshUploader.GpuVertex[] _vertexScratch =
        new GpuMeshUploader.GpuVertex[TerrainMeshBuilder.VertexCount];
    private readonly Vector4[] _blendWeightScratch =
        new Vector4[TerrainMeshBuilder.VertexCount * CellTerrainTextureSet.SlotVectors];

    // Async cell-build bookkeeping. _buildQueue / _queuedOrBuilding / _frameBuildStarts and the
    // whole upload path are touched ONLY on the render thread (LoadData and Render run on the same
    // UI/composition thread). _buildResults, _loadGeneration, _activeBuildTasks and _buildTasks are
    // shared with background build tasks and guarded as marked.
    private readonly Queue<(int gx, int gy)> _buildQueue = new();
    private readonly HashSet<(int gx, int gy)> _queuedOrBuilding = new();
    private readonly List<(int gx, int gy)> _pruneScratch = new();
    private readonly object _buildTaskListLock = new();
    private readonly List<Task> _buildTasks = new();
    private readonly object _buildResultsLock = new();             // guards _buildResults + _loadGeneration
    private readonly Dictionary<(int gx, int gy), BuiltCellCpuData> _buildResults = new();
    private int _activeBuildTasks;
    private int _frameBuildStarts;
    private int _loadGeneration;

    private Dictionary<(int gx, int gy), CellRecord>? _cells;
    private global::FalloutXbox360Utils.WorldSpatialIndex? _spatialIndex;
    private global::FalloutXbox360Utils.WorldRenderCache? _renderCache;
    private bool _vclrOnlyMode;
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

        var vsBytecode = CompileEmbeddedShader("terrain_textured.vert.hlsl", "main", "vs_5_1");
        var psBytecode = CompileEmbeddedShader("terrain_textured.frag.hlsl", "main", "ps_5_1");

        var rasterizer = new D12.RasterizerDescription
        {
            FillMode = D12.FillMode.Solid,
            CullMode = D12.CullMode.Back,
            FrontCounterClockwise = true,
            DepthClipEnable = true,
        };

        var depth = new D12.DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = D12.DepthWriteMask.All,
            DepthFunc = ComparisonFunction.LessEqual,
            StencilEnable = false,
        };

        var blend = new D12.BlendDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false,
        };
        blend.RenderTarget[0] = new D12.RenderTargetBlendDescription
        {
            BlendEnable = false,
            RenderTargetWriteMask = D12.ColorWriteEnable.All,
        };

        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = rootSignature.RootSignature,
            VertexShader = vsBytecode,
            PixelShader = psBytecode,
            BlendState = blend,
            RasterizerState = rasterizer,
            DepthStencilState = depth,
            InputLayout = new InputLayoutDescription(TerrainInputElements),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { Format.B8G8R8A8_UNorm },
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
            SampleMask = uint.MaxValue,
        };
        _pso = gpu.Device.CreateGraphicsPipelineState(psoDesc);

        // Depth-only PSO: same vertex path + depth state, but no pixel shader and no render
        // targets, so it writes only the depth buffer. Used by the top-down overlay pre-pass.
        var depthOnlyPsoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = rootSignature.RootSignature,
            VertexShader = vsBytecode,
            BlendState = blend,
            RasterizerState = rasterizer,
            DepthStencilState = depth,
            InputLayout = new InputLayoutDescription(TerrainInputElements),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = Array.Empty<Format>(),
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
            SampleMask = uint.MaxValue,
        };
        _depthOnlyPso = gpu.Device.CreateGraphicsPipelineState(depthOnlyPsoDesc);

        _sharedIndexData = TerrainMeshBuilder.BuildSharedIndexBufferData();
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
        _depthOnlyPso.Dispose();
        _pso.Dispose();
    }

    private void DrainBuildTasksForDispose()
    {
        Task[] pending;
        lock (_buildTaskListLock)
        {
            pending = _buildTasks.Where(static t => !t.IsCompleted).ToArray();
        }

        if (pending.Length == 0)
        {
            return;
        }

        Log.Info("TerrainRenderer12: waiting for {0} build task(s) during dispose.", pending.Length);
        try
        {
            Task.WaitAll(pending);
        }
        catch (AggregateException ex)
        {
            foreach (var inner in ex.Flatten().InnerExceptions)
            {
                Log.Warn("TerrainRenderer12: build task faulted during dispose: {0}", inner.Message);
            }
        }

        PruneCompletedBuildTasks();
    }

    public int CellCount => _spatialIndex?.CellCount ?? _cells?.Count ?? 0;
    public global::FalloutXbox360Utils.WorldRenderStats LastStats { get; } = new();
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

    private int EffectiveMaxBuildStartsPerFrame =>
        StreamingThrottled ? MaxBuildStartsPerFrame : int.MaxValue;

    private int EffectiveMaxConcurrentBuildTasks =>
        StreamingThrottled ? MaxConcurrentBuildTasks : Math.Max(MaxConcurrentBuildTasks, UnthrottledMaxConcurrentBuildTasks);

    public void SetVclrOnlyMode(bool on) => _vclrOnlyMode = on;

    /// <summary>
    ///     Render-thread-only LRU of per-cell terrain meshes (replaces the bespoke
    ///     CellMeshLruCache): evicted/replaced entries are disposed, and the cache reports its
    ///     entry counts to the resource registry under the "terrain-cells" tag.
    /// </summary>
    private static LruCache<(int gx, int gy), CachedCellMesh12> CreateMeshCache(int capacity) =>
        new LruCache<(int gx, int gy), CachedCellMesh12>(
                "CellMeshLru",
                ResourceCategory.GpuResident,
                maxEntries: capacity,
                onEvicted: static (_, mesh) => mesh.Dispose())
            .RegisterWith(ResourceRegistry.Instance, "terrain-cells");

    public void LoadData(Dictionary<(int gx, int gy), CellRecord> cells)
        => LoadData(cells, spatialIndex: null, renderCache: null);

    public void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        global::FalloutXbox360Utils.WorldSpatialIndex? spatialIndex,
        global::FalloutXbox360Utils.WorldRenderCache? renderCache)
    {
        _meshCache.Dispose();
        var capacity = Math.Max(MinCacheCapacity, cells.Count + CacheHeadroom);
        _meshCache = CreateMeshCache(capacity);
        _knownUnusableCells.Clear();
        _cells = cells;
        _spatialIndex = spatialIndex;
        _renderCache = renderCache;

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
                Parallel.ForEach(cells, pair =>
                {
                    var captured = pair;
                    _renderCache.GetOrBuildTerrainTextureSet(
                        captured.Value,
                        () => BuildCellTextureSet(captured.Key, captured.Value, cells));
                });
            }
            else
            {
                foreach (var pair in cells)
                {
                    var captured = pair;
                    _renderCache.GetOrBuildTerrainTextureSet(
                        captured.Value,
                        () => BuildCellTextureSet(captured.Key, captured.Value, cells));
                }
            }
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

        var cmd = _recorder.CommandList;
        var frameIndex = _recorder.FrameIndex;

        var segmentStarted = StartTiming();

        // Per-frame CB (b0) — viewProj.
        var perFrameAlloc = _ringBuffer.Allocate(frameIndex, PerFrameByteSize, GpuRingBuffer12.CbAlignment);
        unsafe { *(Matrix4x4*)perFrameAlloc.CpuPtr = viewProj; }

        // Per-mode CB (b2): x = VCLR-only debug, y = diffuse UV scale (formerly in the
        // per-quadrant CB, now per-frame since every cell uses the same scale). z/w padding.
        var perModeAlloc = _ringBuffer.Allocate(frameIndex, PerModeByteSize, GpuRingBuffer12.CbAlignment);
        unsafe { *(Vector4*)perModeAlloc.CpuPtr = new Vector4(_vclrOnlyMode ? 1f : 0f, DefaultDiffuseUvScale, 0f, 0f); }

        cmd.SetPipelineState(pso);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        EnsureSharedIndexBuffer(cmd);
        cmd.IASetIndexBuffer(_sharedIbv);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, perFrameAlloc.GpuAddress);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerModeCbv, perModeAlloc.GpuAddress);
        cmd.SetGraphicsRootDescriptorTable(GpuRootSignature12.Slots.BindlessSrvTable, _cbvSrvUavHeap.BindlessHeapStartGpu);

        LastStats.StateSetupMilliseconds = ElapsedMilliseconds(segmentStarted);
        segmentStarted = StartTiming();

        // Pass 1a — gather visible cells.
        _visibleScratch.Clear();
        _missingVisibleScratch.Clear();
        _missingVisibleKeys.Clear();
        var camX = cylinder.Position.X;
        var camY = cylinder.Position.Y;
        if (_spatialIndex is not null)
        {
            _spatialIndex.QueryCellsInRadius(camX, -camY, cylinder.Radius, _candidateScratch);
            foreach (var candidate in _candidateScratch)
            {
                var center = candidate.CenterCanvas;
                var dx = camX - center.X;
                var dy = -camY - center.Y;
                _visibleScratch.Add(new VisibleCell(candidate.Key, candidate.Cell, dx * dx + dy * dy));
            }
        }
        else
        {
            foreach (var (key, cell) in _cells!)
            {
                if (!cylinder.ContainsCell(key.gx, key.gy)) continue;
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
        var uploadTimeBudget = new FrameUploadTimeBudget(MaxMeshBuildMillisecondsPerFrame);
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
    ///     Engine-accurate per-cell draw: binds the cell's 4 cell-wide diffuse bindless indices
    ///     (resolved once at mesh-build time via <see cref="CellTerrainTextureSet" />) and issues a
    ///     single DrawIndexedInstanced for the full 33×33 mesh. The per-vertex blend weights
    ///     bound at vertex slot 1 carry all per-cell variation, so the prior 4-quadrant
    ///     sub-draws + per-quadrant constant buffer are gone.
    /// </summary>
    private void DrawCell(ID3D12GraphicsCommandList cmd, CachedCellMesh12 entry)
    {
        var perDrawAlloc = _ringBuffer.Allocate(_recorder.FrameIndex, PerDrawByteSize, GpuRingBuffer12.CbAlignment);
        unsafe { *(TerrainTextureIndices*)perDrawAlloc.CpuPtr = entry.TextureIndices; }
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerDrawCbv, perDrawAlloc.GpuAddress);

        cmd.DrawIndexedInstanced(
            indexCountPerInstance: (uint)TerrainMeshBuilder.IndexCount,
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
                _knownUnusableCells.Add(key);
                return null;
            }
            if (uploadBudget <= 0) return null; // keep the result; upload on a later frame

            RemoveBuildResult(key);
            _queuedOrBuilding.Remove(key);
            var entry = UploadBuiltCell(key, cpu);
            if (entry is null)
            {
                _knownUnusableCells.Add(key);
                return null;
            }
            uploadBudget--;
            LastStats.NewUploads++;
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

    private void StartQueuedBuilds()
    {
        while (_frameBuildStarts < EffectiveMaxBuildStartsPerFrame &&
               Volatile.Read(ref _activeBuildTasks) < EffectiveMaxConcurrentBuildTasks &&
               _buildQueue.Count > 0)
        {
            var key = _buildQueue.Dequeue();

            // Drop stale queue entries: the cell got cached/unusable some other way, was already
            // dequeued, or has scrolled out of view since it was enqueued (a prior frame).
            if (!_queuedOrBuilding.Contains(key) ||
                _meshCache.ContainsKey(key) ||
                _knownUnusableCells.Contains(key) ||
                !_missingVisibleKeys.Contains(key))
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
                StoreBuildResult(key, BuildCellCpu(key, cell, cells, renderCache, generation));
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

        lock (_buildTaskListLock)
        {
            _buildTasks.Add(task);
        }
    }

    /// <summary>
    ///     Background-thread CPU build: heightmap → vertices, neighbor-aware texture set, and the
    ///     remapped blend weights. Allocates its OWN vertex + blend-weight arrays (never the shared
    ///     scratch) so concurrent builds can't corrupt each other — the load-bearing correctness
    ///     constraint. Touches only the captured <paramref name="cells" /> snapshot, the immutable
    ///     <see cref="CellRecord" />, and the thread-safe <see cref="WorldRenderCache" />.
    /// </summary>
    private static BuiltCellCpuData BuildCellCpu(
        (int gx, int gy) key,
        CellRecord cell,
        Dictionary<(int gx, int gy), CellRecord>? cells,
        global::FalloutXbox360Utils.WorldRenderCache? renderCache,
        int generation)
    {
        var vertices = new GpuMeshUploader.GpuVertex[TerrainMeshBuilder.VertexCount];
        if (!TerrainMeshBuilder.TryBuildVertices(cell, vertices, renderCache))
        {
            return BuiltCellCpuData.Failed(generation);
        }

        var textureSet = renderCache is not null
            ? renderCache.GetOrBuildTerrainTextureSet(cell, () => BuildCellTextureSet(key, cell, cells))
            : BuildCellTextureSet(key, cell, cells);

        var blendWeights = new Vector4[TerrainMeshBuilder.VertexCount * CellTerrainTextureSet.SlotVectors];
        PopulateBlendWeights(textureSet, blendWeights);
        return new BuiltCellCpuData(vertices, blendWeights, textureSet, Unusable: false, generation);
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
        lock (_buildTaskListLock)
        {
            for (var i = _buildTasks.Count - 1; i >= 0; i--)
            {
                if (_buildTasks[i].IsCompleted)
                {
                    _buildTasks.RemoveAt(i);
                }
            }
        }
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
                if (!_missingVisibleKeys.Contains(k))
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

            var textureIndices = ResolveSlotTextureIndices(cpu.TextureSet);

            var entry = new CachedCellMesh12
            {
                VertexBuffer = vb,
                BlendWeightBuffer = blendBuffer,
                TextureIndices = textureIndices,
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
                ? _renderCache.GetOrBuildTerrainTextureSet(cell, () => BuildCellTextureSet(key, cell, _cells))
                : BuildCellTextureSet(key, cell, _cells);
            PopulateBlendWeights(textureSet, _blendWeightScratch);
            var blendBuffer = GpuMeshBufferFactory12.CreateDefaultBuffer(
                _gpu,
                _recorder.CommandList,
                _deletionQueue,
                _blendWeightScratch,
                ResourceStates.VertexAndConstantBuffer);

            var textureIndices = ResolveSlotTextureIndices(textureSet);

            var entry = new CachedCellMesh12
            {
                VertexBuffer = vb,
                BlendWeightBuffer = blendBuffer,
                TextureIndices = textureIndices,
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
    ///     Build a <see cref="CellTerrainTextureSet" /> for this cell, including the cardinal
    ///     neighbor edges so the cross-cell blend extends into this cell's edge vertices.
    ///     Returns null when neither this cell nor any neighbor has LAND texture data — the
    ///     downstream code paints the cell as engine-default in that case.
    /// </summary>
    private static CellTerrainTextureSet? BuildCellTextureSet(
        (int gx, int gy) key,
        CellRecord cell,
        Dictionary<(int gx, int gy), CellRecord>? cells)
    {
        var layers = cell.LandVisualData?.TextureLayers;

        IReadOnlyList<LandTextureLayer>? eastN = null, westN = null, northN = null, southN = null;
        if (cells is not null)
        {
            eastN = NeighborLayers(cells, key.gx + 1, key.gy);
            westN = NeighborLayers(cells, key.gx - 1, key.gy);
            northN = NeighborLayers(cells, key.gx, key.gy + 1);
            southN = NeighborLayers(cells, key.gx, key.gy - 1);
        }

        var hasOwnLayers = layers is { Count: > 0 };
        var hasRealNeighbor = IsRealLayerList(eastN) || IsRealLayerList(westN)
                           || IsRealLayerList(northN) || IsRealLayerList(southN);

        CellLayerWeightTable? table = null;
        if (hasOwnLayers)
        {
            table = CellLayerWeightTable.Build(layers!, eastN, westN, northN, southN);
        }
        else if (hasRealNeighbor)
        {
            table = CellLayerWeightTable.Build(
                s_engineDefaultSyntheticLayers, eastN, westN, northN, southN);
        }

        return CellTerrainTextureSet.Project(table);
    }

    private static IReadOnlyList<LandTextureLayer>? NeighborLayers(
        Dictionary<(int gx, int gy), CellRecord> cells, int gx, int gy)
    {
        if (!cells.TryGetValue((gx, gy), out var neighbor)) return null;
        var layers = neighbor.LandVisualData?.TextureLayers;
        if (layers is not { Count: > 0 }) return s_engineDefaultSyntheticLayers;
        return layers;
    }

    private static bool IsRealLayerList(IReadOnlyList<LandTextureLayer>? layers)
        => layers is { Count: > 0 } && !ReferenceEquals(layers, s_engineDefaultSyntheticLayers);

    private static readonly IReadOnlyList<LandTextureLayer> s_engineDefaultSyntheticLayers =
    [
        new LandTextureLayer { Kind = LandTextureLayerKind.Base, TextureFormId = CellLayerWeightTable.EngineDefaultSentinelFormId, Quadrant = 0 },
        new LandTextureLayer { Kind = LandTextureLayerKind.Base, TextureFormId = CellLayerWeightTable.EngineDefaultSentinelFormId, Quadrant = 1 },
        new LandTextureLayer { Kind = LandTextureLayerKind.Base, TextureFormId = CellLayerWeightTable.EngineDefaultSentinelFormId, Quadrant = 2 },
        new LandTextureLayer { Kind = LandTextureLayerKind.Base, TextureFormId = CellLayerWeightTable.EngineDefaultSentinelFormId, Quadrant = 3 },
    ];

    /// <summary>
    ///     Pack <paramref name="set" />'s per-vertex Vector4 weights into the scratch array,
    ///     remapping from the weight-table row order (vy=0 north) to the mesh-builder row
    ///     order (j=0 south, since the mesh emits world-Y growing northward from originY).
    /// </summary>
    private static void PopulateBlendWeights(CellTerrainTextureSet? set, Vector4[] dest)
    {
        if (set is null)
        {
            Array.Clear(dest);
            return;
        }
        const int vectors = CellTerrainTextureSet.SlotVectors;
        for (var j = 0; j < CellLayerWeightTable.CellVertexCount; j++)
        {
            var cellVy = CellLayerWeightTable.CellVertexCount - 1 - j;
            for (var i = 0; i < CellLayerWeightTable.CellVertexCount; i++)
            {
                var meshIdx = j * CellLayerWeightTable.CellVertexCount + i;
                var tableIdx = cellVy * CellLayerWeightTable.CellVertexCount + i;
                for (var k = 0; k < vectors; k++)
                {
                    dest[meshIdx * vectors + k] = set.VertexWeights[tableIdx * vectors + k];
                }
            }
        }
    }

    /// <summary>
    ///     Resolve the 4 cell-wide diffuse bindless texture indices the terrain shader samples.
    ///     The engine-default sentinel routes to DirtWasteland01; empty slots also fall back there
    ///     so all 4 sample positions remain valid when fewer than 4 LTEXs are assigned.
    /// </summary>
    private TerrainTextureIndices ResolveSlotTextureIndices(CellTerrainTextureSet? set)
    {
        var active = set?.ActiveSlotCount ?? 0;
        var indices = new TerrainTextureIndices();
        unsafe
        {
            for (var slot = 0; slot < CellTerrainTextureSet.MaxSlots; slot++)
            {
                var entry = slot < active && set!.SlotFormIds[slot] != CellLayerWeightTable.EngineDefaultSentinelFormId
                    ? _textureResolver.Resolve(set.SlotFormIds[slot])
                    : _textureResolver.EngineDefault;
                indices.Index[slot] = entry.BindlessIndex;
            }
        }
        return indices;
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
            out Blob? bytecode,
            out Blob? errors);

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

    private readonly record struct VisibleCell((int gx, int gy) Key, CellRecord Cell, float DistSq);

    /// <summary>
    ///     Per-cell bindless diffuse texture indices for the 16 blend slots, laid out as
    ///     <c>uint4[4]</c> to match the terrain fragment shader's <c>uTextureIndices[4]</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct TerrainTextureIndices
    {
        public fixed uint Index[CellTerrainTextureSet.MaxSlots];
    }

    /// <summary>
    ///     CPU-only product of a background cell build. Holds freshly-allocated per-task arrays (so
    ///     concurrent builds never share scratch) and the resolved texture set — but NO
    ///     <see cref="ID3D12Resource" />; GPU buffers are created on the render thread in
    ///     <see cref="UploadBuiltCell" />. <see cref="Generation" /> tags the worldspace the build
    ///     ran against so <see cref="StoreBuildResult" /> can drop results from a stale LoadData.
    /// </summary>
    private sealed record BuiltCellCpuData(
        GpuMeshUploader.GpuVertex[]? Vertices,
        Vector4[]? BlendWeights,
        CellTerrainTextureSet? TextureSet,
        bool Unusable,
        int Generation)
    {
        public static BuiltCellCpuData Failed(int generation) => new(null, null, null, true, generation);
    }

    private sealed class CachedCellMesh12 : IDisposable
    {
        public required ID3D12Resource VertexBuffer { get; init; }
        public required ID3D12Resource BlendWeightBuffer { get; init; }
        public required TerrainTextureIndices TextureIndices { get; init; }
        public required GpuDeletionQueue12 DeletionQueue { get; init; }

        // Route through the deletion queue so LRU eviction can't release a buffer that the
        // GPU is still consuming from the previous frame's command list. Textures are owned by
        // TerrainTextureResolver12 and referenced through stable bindless indices.
        public void Dispose()
        {
            DeletionQueue.EnqueueDispose(VertexBuffer);
            DeletionQueue.EnqueueDispose(BlendWeightBuffer);
        }
    }
}
#endif
