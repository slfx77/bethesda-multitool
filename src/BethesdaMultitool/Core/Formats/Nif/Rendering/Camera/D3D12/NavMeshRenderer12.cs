#if WINDOWS_GUI
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Resources;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D12 = Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>
///     v3 parity — D3D12 navmesh overlay. The 3D analog of the 2D
///     <c>WorldMapNavMeshOverlayRenderer</c>: draws every visible cell's NAVM triangles as a
///     translucent green fill plus a brighter wireframe edge pass. Geometry is parsed once per
///     <see cref="NavMeshRecord" /> via the shared <see cref="NavMeshGeometry" /> and the combined
///     per-cell mesh is cached in an LRU keyed by grid coordinate (static data, built once).
///     <para>
///         Reuses the <c>cellgrid</c> shaders (position-only vertex + viewProj/color CB at b0,
///         passthrough fragment). Two PSOs share the cached vertex/index buffers: a solid fill
///         (alpha-blended, depth-read/no-write so terrain occludes submerged navmesh) and a
///         wireframe edge pass. R32 indices because a cell's combined navmesh vertex count can
///         exceed 65 535.
///     </para>
/// </summary>
internal sealed class NavMeshRenderer12 : Abstractions.INavMeshRenderer
{
    private const uint UniformsByteSize = 80; // float4x4 viewProj (64) + float4 color (16)
    private const uint VertexStride = 12;      // sizeof(Vector3)
    private const int CacheCapacity = 1024;

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

    private LruCache<(int gx, int gy), CachedNavMesh12> _meshCache = CreateMeshCache();
    private readonly HashSet<(int gx, int gy)> _knownUnusableCells = new();
    private readonly List<global::BethesdaMultitool.WorldSpatialCell> _candidateScratch = new();
    private readonly List<CachedNavMesh12> _visibleScratch = new();

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

        // Depth-read / no-write so terrain + refs (which wrote depth) occlude submerged navmesh,
        // but the overlay itself doesn't perturb the depth buffer for later layers. Reversed-Z
        // (near→1, far→0) so the test is GreaterEqual; a depth bias (in CreatePso's rasterizer)
        // pulls the overlay toward the camera so it wins coplanar ties against terrain (no
        // z-fighting) while real geometry in front still occludes it.
        var depth = new D12.DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = D12.DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.GreaterEqual,
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

        _fillPso = CreatePso(vsBytecode, psBytecode, inputElements, depth, blend, D12.FillMode.Solid);
        _edgePso = CreatePso(vsBytecode, psBytecode, inputElements, depth, blend, D12.FillMode.Wireframe);
    }

    private ID3D12PipelineState CreatePso(
        byte[] vs, byte[] ps, InputElementDescription[] inputElements,
        D12.DepthStencilDescription depth, D12.BlendDescription blend, D12.FillMode fillMode)
    {
        var msaa = _gpu.SceneSampleCount > 1;
        var rasterizer = new D12.RasterizerDescription
        {
            FillMode = fillMode,
            CullMode = D12.CullMode.None, // navmesh triangles aren't consistently wound
            FrontCounterClockwise = true,
            DepthClipEnable = true,
            MultisampleEnable = msaa,
            // Priority over coplanar terrain/water (no z-fighting) without poking through walls.
            // Under reversed-Z a POSITIVE bias moves the fragment TOWARD the camera (larger depth),
            // so the navmesh wins the GreaterEqual tie against the surface it sits on. The
            // slope-scaled term carries sloped terrain; the constant handles flat coplanar ground.
            // Read-only depth (DepthWriteMask.Zero) so this never corrupts later layers' occlusion.
            DepthBias = 2000,
            DepthBiasClamp = 0f,
            SlopeScaledDepthBias = 2f,
            // Fixed-function line AA is what aliased incorrectly on HDR monitors: its alpha-coverage
            // gradient is distorted by DWM's SDR->HDR mapping of the SDR backbuffer. Under MSAA the
            // edge is antialiased by multisample coverage (resolved before present), which is robust
            // to the display curve; only fall back to fixed-function line AA when MSAA is unavailable.
            AntialiasedLineEnable = !msaa && fillMode == D12.FillMode.Wireframe,
        };

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
            RenderTargetFormats = new[] { Gpu.D3D12.GpuSceneFormats.SceneColor },
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription((uint)_gpu.SceneSampleCount, 0),
            SampleMask = uint.MaxValue,
        };
        return _gpu.Device.CreateGraphicsPipelineState(psoDesc);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _meshCache.Dispose();
        _fillPso.Dispose();
        _edgePso.Dispose();
    }

    /// <summary>Render-thread-only LRU of per-cell navmesh geometry; evicted entries are disposed.</summary>
    private static LruCache<(int gx, int gy), CachedNavMesh12> CreateMeshCache() =>
        new LruCache<(int gx, int gy), CachedNavMesh12>(
                "CellMeshLru",
                ResourceCategory.GpuResident,
                maxEntries: CacheCapacity,
                onEvicted: static (_, mesh) => mesh.Dispose())
            .RegisterWith(ResourceRegistry.Instance, "navmesh-cells");

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
        _navMeshesByCell = navMeshesByCell;
        _cells = cells;
        _spatialIndex = spatialIndex;
    }

    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder)
    {
        LastStats.Reset();
        if (_navMeshesByCell is null || _navMeshesByCell.Count == 0) return 0;

        var started = StartTiming();
        var cmd = _recorder.CommandList;
        var frameIndex = _recorder.FrameIndex;

        // Gather visible cells that have a usable (built) navmesh.
        _visibleScratch.Clear();
        GatherVisible(cylinder);
        if (_visibleScratch.Count == 0)
        {
            LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
            return 0;
        }

        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        // Two passes share the cached buffers: solid fill, then wireframe edges. One CB per pass
        // (viewProj + color) bound at b0; per-pass PSO bound once.
        DrawPass(cmd, frameIndex, viewProj, FillColor, _fillPso);
        DrawPass(cmd, frameIndex, viewProj, EdgeColor, _edgePso);

        LastStats.WireframeDraws = _visibleScratch.Count;
        LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
        return _visibleScratch.Count;
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

        foreach (var mesh in _visibleScratch)
        {
            cmd.IASetVertexBuffers(0, new VertexBufferView
            {
                BufferLocation = mesh.VertexBuffer.GPUVirtualAddress,
                SizeInBytes = (uint)mesh.VertexBuffer.Description.Width,
                StrideInBytes = VertexStride,
            });
            cmd.IASetIndexBuffer(new IndexBufferView
            {
                BufferLocation = mesh.IndexBuffer.GPUVirtualAddress,
                SizeInBytes = (uint)mesh.IndexBuffer.Description.Width,
                Format = Format.R32_UInt,
            });
            cmd.DrawIndexedInstanced(mesh.IndexCount, 1, 0, 0, 0);
        }
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
            _visibleScratch.Add(cached);
            return;
        }

        var built = TryBuildCellMesh(cell);
        if (built is null)
        {
            _knownUnusableCells.Add(key);
            return;
        }
        _meshCache.Set(key, built);
        _visibleScratch.Add(built);
    }

    private CachedNavMesh12? TryBuildCellMesh(CellRecord cell)
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

        var cmd = _recorder.CommandList;
        var vb = GpuMeshBufferFactory12.CreateDefaultBuffer<Vector3>(
            _gpu, cmd, _deletionQueue, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(verts),
            ResourceStates.VertexAndConstantBuffer);
        var ib = GpuMeshBufferFactory12.CreateDefaultBuffer<uint>(
            _gpu, cmd, _deletionQueue, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(indices),
            ResourceStates.IndexBuffer);

        return new CachedNavMesh12
        {
            VertexBuffer = vb,
            IndexBuffer = ib,
            IndexCount = (uint)indices.Count,
            DeletionQueue = _deletionQueue,
        };
    }

    private long StartTiming() => DetailedProfilingEnabled ? Stopwatch.GetTimestamp() : 0;

    private static double ElapsedMilliseconds(long started) =>
        started == 0 ? 0 : Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static byte[] CompileEmbeddedShader(string name, string entryPoint, string profile)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Embedded shader resource not found: {name}");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();

        var result = Compiler.Compile(source, entryPoint, sourceName: name, profile,
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
    private struct NavMeshUniforms
    {
        public Matrix4x4 ViewProj;
        public Vector4 Color;
    }

    private sealed class CachedNavMesh12 : IDisposable
    {
        public required ID3D12Resource VertexBuffer { get; init; }
        public required ID3D12Resource IndexBuffer { get; init; }
        public required uint IndexCount { get; init; }
        public required GpuDeletionQueue12 DeletionQueue { get; init; }

        public void Dispose()
        {
            DeletionQueue.EnqueueDispose(VertexBuffer);
            DeletionQueue.EnqueueDispose(IndexBuffer);
        }
    }
}
#endif
