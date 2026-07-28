#if WINDOWS_GUI
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Abstractions;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Resources;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D12 = Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     v3 parity — D3D12 navmesh overlay. The 3D analog of the 2D
///     <c>WorldMapNavMeshOverlayRenderer</c>: draws every visible cell's NAVM triangles as a
///     translucent green fill plus a brighter wireframe edge pass. Geometry is parsed once per
///     cell via the shared <see cref="NavMeshGeometry" /> and memoized in a CPU-side LRU; the
///     visible set is concatenated into ONE combined vertex/index buffer pair that is rebuilt only
///     when the visible cell-key set changes (cell-boundary crossings, toggles, worldspace switch).
///     Oblivion authors a pathgrid in nearly every exterior cell, so the old per-cell draw path
///     issued 1–2k tiny draws per frame and its GPU LRU (cap 1024) thrashed create/destroy work
///     every frame once the visible set outgrew it — this path draws twice per frame total.
///     <para>
///         Reuses the <c>cellgrid</c> shaders (position-only vertex + viewProj/color CB at b0,
///         passthrough fragment). Fill + edge PSOs exist in a scene-target flavor (HDR/MSAA — the
///         export path composites there) and an LDR flavor drawn AFTER the tonemap resolve so the
///         diagnostic is not eye-adapted, bloomed, or tonemapped with the scene. R32 indices
///         because the combined vertex count far exceeds 65 535.
///     </para>
/// </summary>
internal sealed class NavMeshRenderer12 : Abstractions.INavMeshRenderer
{
    private const uint UniformsByteSize = 80; // float4x4 viewProj (64) + float4 color (16)
    private const uint VertexStride = 12;      // sizeof(Vector3)
    private const int CacheCapacity = 8192;    // CPU geometry (~10 KB/cell), not GPU buffers

    /// <summary>
    ///     Combined-buffer ceiling (~24 MB VB at 12 B/vertex). Cells beyond it are dropped for the
    ///     frame set — like <c>CollisionDebugRenderer12.MaxLineVertices</c>, a debug overlay bound;
    ///     move closer or lower the render distance to see the remainder.
    /// </summary>
    private const int MaxCombinedVertices = 2_000_000;

    // Match the 2D overlay's colors (Win2D ARGB → RGBA float).
    private static readonly Vector4 FillColor = new(80f / 255f, 220f / 255f, 120f / 255f, 70f / 255f);
    private static readonly Vector4 EdgeColor = new(150f / 255f, 1f, 180f / 255f, 200f / 255f);

    private readonly GpuDevice12 _gpu;
    private readonly GpuCommandRecorder12 _recorder;
    private readonly GpuRingBuffer12 _ringBuffer;
    private readonly GpuRootSignature12 _rootSignature;
    private readonly GpuDeletionQueue12 _deletionQueue;
    private readonly ID3D12PipelineState _fillPso;
    private readonly ID3D12PipelineState _edgePso;
    private readonly ID3D12PipelineState _ldrFillPso;
    private readonly ID3D12PipelineState _ldrEdgePso;

    private LruCache<(int gx, int gy), CellNavGeometry> _meshCache = CreateMeshCache();
    private readonly HashSet<(int gx, int gy)> _knownUnusableCells = new();
    private readonly List<global::BethesdaMultitool.WorldSpatialCell> _candidateScratch = new();
    private readonly List<(int gx, int gy)> _visibleKeyScratch = new();
    private readonly List<CellNavGeometry> _visibleGeometryScratch = new();
    private readonly HashSet<(int gx, int gy)> _lastVisibleKeys = new();
    private readonly List<Vector3> _combineVertexScratch = new();
    private readonly List<uint> _combineIndexScratch = new();

    private ID3D12Resource? _combinedVertexBuffer;
    private ID3D12Resource? _combinedIndexBuffer;
    private uint _combinedIndexCount;
    private int _combinedCellCount;

    private IReadOnlyDictionary<uint, List<NavMeshRecord>>? _navMeshesByCell;
    private Dictionary<(int gx, int gy), CellRecord>? _cells;
    private global::BethesdaMultitool.WorldSpatialIndex? _spatialIndex;
    private bool _disposed;

    public NavMeshRenderer12(
        GpuDevice12 gpu,
        GpuCommandRecorder12 recorder,
        GpuRingBuffer12 ringBuffer,
        GpuRootSignature12 rootSignature,
        GpuDeletionQueue12 deletionQueue)
    {
        _gpu = gpu;
        _recorder = recorder;
        _ringBuffer = ringBuffer;
        _rootSignature = rootSignature;
        _deletionQueue = deletionQueue;

        var vsBytecode = CompileEmbeddedShader("cellgrid.vert.hlsl", "main", "vs_5_1");
        var psBytecode = CompileEmbeddedShader("cellgrid.frag.hlsl", "main", "ps_5_1");

        var inputElements = new[]
        {
            new InputElementDescription("TEXCOORD", 0, Format.R32G32B32_Float, 0, 0)
        };

        // NAVM is a diagnostic overlay: it must remain visible through terrain and references.
        // Depth testing hid valid triangles whenever authored navmesh sat slightly below the
        // rendered ground (the common case this overlay is meant to expose). The fill + edge draw
        // order below still keeps navmesh edges on top of its own translucent fill.
        var depth = new D12.DepthStencilDescription
        {
            DepthEnable = false,
            DepthWriteMask = D12.DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.Always,
            StencilEnable = false,
        };

        var blend = new D12.BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
        blend.RenderTarget[0] = new D12.RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = D12.Blend.SourceAlpha,
            DestinationBlend = D12.Blend.InverseSourceAlpha,
            BlendOperation = D12.BlendOperation.Add,
            SourceBlendAlpha = D12.Blend.One,
            DestinationBlendAlpha = D12.Blend.Zero,
            BlendOperationAlpha = D12.BlendOperation.Add,
            RenderTargetWriteMask = D12.ColorWriteEnable.All,
        };

        _fillPso = CreatePso(vsBytecode, psBytecode, inputElements, depth, blend, D12.FillMode.Solid, ldr: false);
        _edgePso = CreatePso(vsBytecode, psBytecode, inputElements, depth, blend, D12.FillMode.Wireframe, ldr: false);
        _ldrFillPso = CreatePso(vsBytecode, psBytecode, inputElements, depth, blend, D12.FillMode.Solid, ldr: true);
        _ldrEdgePso = CreatePso(vsBytecode, psBytecode, inputElements, depth, blend, D12.FillMode.Wireframe, ldr: true);
    }

    private ID3D12PipelineState CreatePso(
        byte[] vs, byte[] ps, InputElementDescription[] inputElements,
        D12.DepthStencilDescription depth, D12.BlendDescription blend, D12.FillMode fillMode, bool ldr)
    {
        var msaa = !ldr && _gpu.SceneSampleCount > 1;
        var rasterizer = new D12.RasterizerDescription
        {
            FillMode = fillMode,
            CullMode = D12.CullMode.None, // navmesh triangles aren't consistently wound
            FrontCounterClockwise = true,
            DepthClipEnable = true,
            MultisampleEnable = msaa,
            // Depth is disabled for this diagnostic overlay, so no coplanar bias is required.
            DepthBias = 0,
            DepthBiasClamp = 0f,
            SlopeScaledDepthBias = 0f,
            // Fixed-function line AA is what aliased incorrectly on HDR monitors: its alpha-coverage
            // gradient is distorted by DWM's SDR->HDR mapping of the SDR backbuffer. Under MSAA the
            // edge is antialiased by multisample coverage (resolved before present), which is robust
            // to the display curve; only fall back to fixed-function line AA when MSAA is unavailable.
            AntialiasedLineEnable = !msaa && fillMode == D12.FillMode.Wireframe,
        };

        // The LDR flavor draws into the post-tonemap back buffer after ResolveTo (same pattern as
        // CollisionDebugRenderer12): single-sample LDR format, no depth buffer bound.
        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = _rootSignature.RootSignature,
            VertexShader = vs,
            PixelShader = ps,
            BlendState = blend,
            RasterizerState = rasterizer,
            DepthStencilState = depth,
            InputLayout = new InputLayoutDescription(inputElements),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[]
            {
                ldr ? Gpu.D3D12.GpuSceneFormats.LdrOutput : Gpu.D3D12.GpuSceneFormats.SceneColor
            },
            DepthStencilFormat = ldr ? Format.Unknown : Format.D32_Float,
            SampleDescription = new SampleDescription(ldr ? 1u : (uint)_gpu.SceneSampleCount, 0),
            SampleMask = uint.MaxValue,
        };
        return _gpu.Device.CreateGraphicsPipelineState(psoDesc);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _meshCache.Dispose();
        ReleaseCombinedBuffers();
        _fillPso.Dispose();
        _edgePso.Dispose();
        _ldrFillPso.Dispose();
        _ldrEdgePso.Dispose();
    }

    /// <summary>Render-thread-only LRU of per-cell CPU navmesh geometry (managed arrays; eviction
    /// only costs a cheap re-parse at the next visible-set rebuild).</summary>
    private static LruCache<(int gx, int gy), CellNavGeometry> CreateMeshCache() =>
        new LruCache<(int gx, int gy), CellNavGeometry>(
                "CellMeshLru",
                ResourceCategory.CpuCache,
                maxEntries: CacheCapacity)
            .RegisterWith(ResourceRegistry.Instance, "navmesh-cells");

    private void ReleaseCombinedBuffers()
    {
        if (_combinedVertexBuffer is not null) _deletionQueue.EnqueueDispose(_combinedVertexBuffer);
        if (_combinedIndexBuffer is not null) _deletionQueue.EnqueueDispose(_combinedIndexBuffer);
        _combinedVertexBuffer = null;
        _combinedIndexBuffer = null;
        _combinedIndexCount = 0;
        _combinedCellCount = 0;
        _lastVisibleKeys.Clear();
    }

    public global::BethesdaMultitool.WorldRenderStats LastStats { get; } = new();
    public bool DetailedProfilingEnabled { get; set; }

    public void LoadData(
        IReadOnlyDictionary<uint, List<NavMeshRecord>> navMeshesByCell,
        Dictionary<(int gx, int gy), CellRecord> cells,
        global::BethesdaMultitool.WorldSpatialIndex? spatialIndex)
    {
        _meshCache.Dispose();
        _meshCache = CreateMeshCache();
        _knownUnusableCells.Clear();
        ReleaseCombinedBuffers();
        _navMeshesByCell = navMeshesByCell;
        _cells = cells;
        _spatialIndex = spatialIndex;
    }

    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder) =>
        Render(viewProj, cylinder, ldrTarget: false);

    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder, bool ldrTarget)
    {
        LastStats.Reset();
        if (_navMeshesByCell is null || _navMeshesByCell.Count == 0) return 0;

        var started = StartTiming();
        var cmd = _recorder.CommandList;
        var frameIndex = _recorder.FrameIndex;

        // Gather visible cells that have usable navmesh geometry (CPU memo; no GPU work here).
        _visibleKeyScratch.Clear();
        _visibleGeometryScratch.Clear();
        GatherVisible(cylinder);
        if (_visibleKeyScratch.Count == 0)
        {
            LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
            return 0;
        }

        // Rebuild the combined buffers only when the visible KEY set changed; a stationary camera
        // (or one moving inside the same cell) draws with zero build work.
        if (VisibleSetChanged())
        {
            RebuildCombinedBuffers(cmd);
        }

        if (_combinedIndexCount == 0 || _combinedVertexBuffer is null || _combinedIndexBuffer is null)
        {
            LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
            return 0;
        }

        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cmd.IASetVertexBuffers(0, new VertexBufferView
        {
            BufferLocation = _combinedVertexBuffer.GPUVirtualAddress,
            SizeInBytes = (uint)_combinedVertexBuffer.Description.Width,
            StrideInBytes = VertexStride,
        });
        cmd.IASetIndexBuffer(new IndexBufferView
        {
            BufferLocation = _combinedIndexBuffer.GPUVirtualAddress,
            SizeInBytes = (uint)_combinedIndexBuffer.Description.Width,
            Format = Format.R32_UInt,
        });

        // Two passes share the combined buffers: solid fill, then wireframe edges. One CB per pass
        // (viewProj + color) bound at b0; ONE draw per pass regardless of visible cell count.
        DrawPass(cmd, frameIndex, viewProj, FillColor, ldrTarget ? _ldrFillPso : _fillPso);
        DrawPass(cmd, frameIndex, viewProj, EdgeColor, ldrTarget ? _ldrEdgePso : _edgePso);

        // The HUD's nav count keeps its established meaning: visible navmesh-bearing cells.
        LastStats.WireframeDraws = _combinedCellCount;
        LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
        return _combinedCellCount;
    }

    private void DrawPass(
        ID3D12GraphicsCommandList cmd, int frameIndex, Matrix4x4 viewProj, Vector4 color, ID3D12PipelineState pso)
    {
        // Soft-fail on ring exhaustion — skip this overlay pass for the frame rather than throwing.
        if (!_ringBuffer.TryAllocate(frameIndex, UniformsByteSize, out var cbAlloc, GpuRingBuffer12.CbAlignment))
        {
            return;
        }
        unsafe { *(NavMeshUniforms*)cbAlloc.CpuPtr = new NavMeshUniforms { ViewProj = viewProj, Color = color }; }

        cmd.SetPipelineState(pso);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, cbAlloc.GpuAddress);
        cmd.DrawIndexedInstanced(_combinedIndexCount, 1, 0, 0, 0);
    }

    private bool VisibleSetChanged()
    {
        if (_visibleKeyScratch.Count != _lastVisibleKeys.Count) return true;
        foreach (var key in _visibleKeyScratch)
        {
            if (!_lastVisibleKeys.Contains(key)) return true;
        }

        return false;
    }

    private void RebuildCombinedBuffers(ID3D12GraphicsCommandList cmd)
    {
        _lastVisibleKeys.Clear();
        foreach (var key in _visibleKeyScratch)
        {
            _lastVisibleKeys.Add(key);
        }

        if (_combinedVertexBuffer is not null) _deletionQueue.EnqueueDispose(_combinedVertexBuffer);
        if (_combinedIndexBuffer is not null) _deletionQueue.EnqueueDispose(_combinedIndexBuffer);
        _combinedVertexBuffer = null;
        _combinedIndexBuffer = null;
        _combinedIndexCount = 0;
        _combinedCellCount = 0;

        _combineVertexScratch.Clear();
        _combineIndexScratch.Clear();
        foreach (var geometry in _visibleGeometryScratch)
        {
            if (_combineVertexScratch.Count + geometry.Vertices.Length > MaxCombinedVertices) break;
            var baseIndex = (uint)_combineVertexScratch.Count;
            _combineVertexScratch.AddRange(geometry.Vertices);
            foreach (var index in geometry.Indices)
            {
                _combineIndexScratch.Add(baseIndex + index);
            }

            _combinedCellCount++;
        }

        if (_combineVertexScratch.Count == 0 || _combineIndexScratch.Count == 0)
        {
            _combinedCellCount = 0;
            return;
        }

        _combinedVertexBuffer = GpuMeshBufferFactory12.CreateDefaultBuffer<Vector3>(
            _gpu, cmd, _deletionQueue,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_combineVertexScratch),
            ResourceStates.VertexAndConstantBuffer);
        _combinedIndexBuffer = GpuMeshBufferFactory12.CreateDefaultBuffer<uint>(
            _gpu, cmd, _deletionQueue,
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_combineIndexScratch),
            ResourceStates.IndexBuffer);
        _combinedIndexCount = (uint)_combineIndexScratch.Count;
    }

    private void GatherVisible(VisibilityCylinder cylinder)
    {
        if (_spatialIndex is not null)
        {
            _spatialIndex.QueryCellsInRadius(cylinder.Position.X, -cylinder.Position.Y, cylinder.Radius, _candidateScratch);
            foreach (var candidate in _candidateScratch)
            {
                AddIfBuilt(candidate.Key, candidate.Cell);
            }
            return;
        }

        if (_cells is null) return;
        foreach (var (key, cell) in _cells)
        {
            if (cylinder.ContainsCell(key.gx, key.gy)) AddIfBuilt(key, cell);
        }
    }

    private void AddIfBuilt((int gx, int gy) key, CellRecord cell)
    {
        if (_knownUnusableCells.Contains(key)) return;
        if (_meshCache.TryGet(key, out var cached))
        {
            _visibleKeyScratch.Add(key);
            _visibleGeometryScratch.Add(cached);
            return;
        }

        var built = TryBuildCellGeometry(cell);
        if (built is null)
        {
            _knownUnusableCells.Add(key);
            return;
        }
        _meshCache.Set(key, built);
        _visibleKeyScratch.Add(key);
        _visibleGeometryScratch.Add(built);
    }

    private CellNavGeometry? TryBuildCellGeometry(CellRecord cell)
    {
        if (_navMeshesByCell is null || !_navMeshesByCell.TryGetValue(cell.FormId, out var list) || list.Count == 0)
        {
            return null;
        }

        var verts = new List<Vector3>(256);
        var indices = new List<uint>(512);
        foreach (var nm in list)
        {
            var geom = NavMeshGeometry.TryParse(nm);
            if (geom is null) continue;
            var baseIndex = (uint)verts.Count;
            verts.AddRange(geom.Vertices);
            foreach (var (a, b, c) in geom.Triangles)
            {
                if (a >= geom.Vertices.Length || b >= geom.Vertices.Length || c >= geom.Vertices.Length) continue;
                indices.Add(baseIndex + a);
                indices.Add(baseIndex + b);
                indices.Add(baseIndex + c);
            }
        }

        if (verts.Count == 0 || indices.Count == 0) return null;

        return new CellNavGeometry
        {
            Vertices = [.. verts],
            Indices = [.. indices],
        };
    }

    private long StartTiming() => DetailedProfilingEnabled ? Stopwatch.GetTimestamp() : 0;

    private static double ElapsedMilliseconds(long started) =>
        started == 0 ? 0 : Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    /// <summary>
    ///     Forwards to the one shared compiler. The private copy this replaces used the short
    ///     <c>Compiler.Compile</c> overload and so passed NO shader flags at all — harmless for
    ///     cellgrid.*, which declares no unbounded array, but one more variant of a decision that is
    ///     now made once in <see cref="GpuShaderCompiler12" />. It also means cellgrid.* is compiled
    ///     once per process now rather than three times (here, SelectionHighlight, CellGridDebug).
    /// </summary>
    private static byte[] CompileEmbeddedShader(string name, string entryPoint, string profile) =>
        GpuShaderCompiler12.Compile(name, entryPoint, profile);

    [StructLayout(LayoutKind.Sequential)]
    private struct NavMeshUniforms
    {
        public Matrix4x4 ViewProj;
        public Vector4 Color;
    }

    /// <summary>One cell's parsed navmesh geometry (managed arrays; ~10 KB for a typical pathgrid cell).</summary>
    private sealed class CellNavGeometry
    {
        public required Vector3[] Vertices { get; init; }
        public required uint[] Indices { get; init; }
    }
}
#endif
