#if WINDOWS_GUI
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D12 = Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>
///     Debug overlay that draws each visible placed reference's walk-mode <see cref="CollisionMesh" />
///     as a wireframe cage (the Havok <c>bhk*</c> collision when present, else the visual-mesh fallback),
///     so the user can visually compare collision against the rendered meshes. Off by default; toggled
///     from the Visibility menu.
///     <para>
///         Reuses the cellgrid line shaders + PSO and draws one <c>DrawInstanced</c> line list. Each
///         collision triangle contributes three edges (6 line vertices). The transformed line list lives
///         in a content-addressed persistent upload buffer, so an unchanged view reuses it across frames;
///         only the small view/projection constant remains in the per-frame ring. Depth is DISABLED so the
///         cage stays visible through the meshes it overlays. It is drawn into the LDR back buffer after
///         tonemapping, keeping this editor diagnostic out of scene bloom/exposure. References are globally
///         distance-sorted; warm meshes draw immediately and a bounded cold-path warmup offers the nearest
///         misses to the shared reference cache.
///     </para>
/// </summary>
internal sealed class CollisionDebugRenderer12 : IDisposable
{
    private const uint UniformsByteSize = 80; // float4x4 (64) + float4 color (16)
    private const uint VertexStride = 12;     // sizeof(Vector3)
    private const int MaxColdWarmupRequestsPerFrame = 2;

    // Cap on line vertices retained in the persistent wireframe buffer (each 12 B → ~6 MB at the cap).
    // A dense area's collision cages fit comfortably; beyond this we stop adding (debug overlay — move
    // closer to see the rest).
    private const int MaxLineVertices = 500_000;

    private readonly GpuDevice12 _gpu;
    private readonly GpuCommandRecorder12 _recorder;
    private readonly GpuRingBuffer12 _ringBuffer;
    private readonly ID3D12PipelineState _scenePso;
    private readonly ID3D12PipelineState _ldrPso;
    private readonly CollisionWireframeGeometryCache<ID3D12Resource> _geometryCache;
    private readonly CollisionReferencePriorityResolver _priorityResolver = new();
    private readonly ReferenceEnabledOverrideStore _enabledOverrides;

    private readonly List<global::BethesdaMultitool.WorldSpatialCell> _visibleCellScratch = [];
    private readonly List<CollisionReferenceCandidate> _candidateScratch = [];
    private readonly List<CollisionWireframeInstance> _instanceScratch = [];
    private Func<string, CollisionMesh?>? _collisionResolver;
    private Func<string, float, CollisionMesh?>? _collisionWarmup;
    private IReadOnlyCollection<uint>? _xespDisabledRefs;
    private bool _showDisabled;
    private bool _disposed;

    /// <summary>Spatial index for the loaded worldspace; set via <see cref="LoadData" />.</summary>
    public global::BethesdaMultitool.WorldSpatialIndex? SpatialIndex { get; private set; }

    /// <summary>Resolves a model path to its cached walk-mode collision mesh (null when not warm).</summary>
    public Func<string, CollisionMesh?>? CollisionResolver
    {
        get => _collisionResolver;
        set
        {
            if (ReferenceEquals(_collisionResolver, value)) return;
            _collisionResolver = value;
            _geometryCache.Invalidate();
        }
    }

    /// <summary>
    ///     Offers a cold model path to the reference streaming cache at the supplied squared XY
    ///     distance. At most <see cref="MaxColdWarmupRequestsPerFrame" /> unique paths are offered.
    /// </summary>
    public Func<string, float, CollisionMesh?>? CollisionWarmup
    {
        get => _collisionWarmup;
        set
        {
            if (ReferenceEquals(_collisionWarmup, value)) return;
            _collisionWarmup = value;
            _geometryCache.Invalidate();
        }
    }

    /// <summary>Mirror the viewer's "show initially-disabled" toggle so the overlay matches the scene.</summary>
    public bool ShowDisabled
    {
        get => _showDisabled;
        set
        {
            if (_showDisabled == value) return;
            _showDisabled = value;
            _geometryCache.Invalidate();
        }
    }

    /// <summary>Resolved XESP-disabled placed FormIDs, shared with the main scene's authored-state bake.</summary>
    public IReadOnlyCollection<uint>? XespDisabledRefs
    {
        get => _xespDisabledRefs;
        set
        {
            if (ReferenceEquals(_xespDisabledRefs, value)) return;
            _xespDisabledRefs = value;
            _geometryCache.Invalidate();
        }
    }

    public CollisionDebugRenderer12(
        GpuDevice12 gpu,
        GpuCommandRecorder12 recorder,
        GpuRingBuffer12 ringBuffer,
        GpuRootSignature12 rootSignature,
        GpuDeletionQueue12 deletionQueue,
        ReferenceEnabledOverrideStore? enabledOverrides = null)
    {
        _gpu = gpu;
        _recorder = recorder;
        _ringBuffer = ringBuffer;
        _enabledOverrides = enabledOverrides ?? new ReferenceEnabledOverrideStore();
        _geometryCache = new CollisionWireframeGeometryCache<ID3D12Resource>(
            UploadGeometry,
            deletionQueue.EnqueueDispose,
            MaxLineVertices);

        var vsBytecode = CompileEmbeddedShader("cellgrid.vert.hlsl", "main", "vs_5_1");
        var psBytecode = CompileEmbeddedShader("cellgrid.frag.hlsl", "main", "ps_5_1");

        var inputElements = new[]
        {
            new InputElementDescription("TEXCOORD", 0, Format.R32G32B32_Float, 0, 0)
        };

        // Depth DISABLED: the collision cage is a comparison overlay — it must stay visible through the
        // meshes it sits inside (you're checking the cage matches/spans the visible geometry), like the
        // selection highlight. It never writes depth, so later layers are unaffected.
        var depth = new D12.DepthStencilDescription
        {
            DepthEnable = false,
            DepthWriteMask = D12.DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.Always,
            StencilEnable = false
        };

        var rasterizer = new D12.RasterizerDescription
        {
            FillMode = D12.FillMode.Solid,
            CullMode = D12.CullMode.None,
            FrontCounterClockwise = true,
            DepthClipEnable = true,
            // The post-tonemap back buffer is single-sample. Fixed-function line AA keeps the
            // diagnostic readable without routing it through the scene's MSAA resolve/bloom path.
            MultisampleEnable = false,
            AntialiasedLineEnable = true,
        };

        var blend = new D12.BlendDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false,
        };
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

        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = rootSignature.RootSignature,
            VertexShader = vsBytecode,
            PixelShader = psBytecode,
            BlendState = blend,
            RasterizerState = rasterizer,
            DepthStencilState = depth,
            InputLayout = new InputLayoutDescription(inputElements),
            PrimitiveTopologyType = PrimitiveTopologyType.Line,
            RenderTargetFormats = new[] { Gpu.D3D12.GpuSceneFormats.LdrOutput },
            DepthStencilFormat = Format.Unknown,
            SampleDescription = new SampleDescription(1, 0),
            SampleMask = uint.MaxValue,
        };
        _ldrPso = gpu.Device.CreateGraphicsPipelineState(psoDesc);

        // Projection exports still composite the optional cage into their HDR/MSAA scene target
        // before readback. Keep a matching scene PSO for that path; the live viewer selects the
        // single-sample LDR PSO above after tonemapping.
        var sceneMsaa = gpu.SceneSampleCount > 1;
        rasterizer.MultisampleEnable = sceneMsaa;
        rasterizer.AntialiasedLineEnable = !sceneMsaa;
        psoDesc.RasterizerState = rasterizer;
        psoDesc.RenderTargetFormats = new[] { Gpu.D3D12.GpuSceneFormats.SceneColor };
        psoDesc.DepthStencilFormat = Format.D32_Float;
        psoDesc.SampleDescription = new SampleDescription((uint)gpu.SceneSampleCount, 0);
        _scenePso = gpu.Device.CreateGraphicsPipelineState(psoDesc);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _geometryCache.Dispose();
        _scenePso.Dispose();
        _ldrPso.Dispose();
    }

    /// <summary>Binds the loaded worldspace's spatial index (placed-ref source for the overlay).</summary>
    public void LoadData(global::BethesdaMultitool.WorldSpatialIndex? spatialIndex)
    {
        _geometryCache.Invalidate();
        SpatialIndex = spatialIndex;
    }

    /// <summary>
    ///     Draws the collision wireframe nearest-first within the visibility cylinder. Returns the
    ///     number of references drawn (0 when nothing is available / in view).
    /// </summary>
    public int Render(
        Matrix4x4 viewProj, VisibilityCylinder cylinder,
        Vector3 renderOrigin = default, bool ldrTarget = false)
    {
        if (_disposed || SpatialIndex is null || _collisionResolver is null) return 0;

        _candidateScratch.Clear();
        SpatialIndex.QueryCellsInRadius(cylinder.Position.X, -cylinder.Position.Y, cylinder.Radius, _visibleCellScratch);

        // QueryCellsInRadius is row-major and Cell.PlacedObjects retains source order. Preserve that as
        // the final tie-break, but rank every eligible REFR globally by its own XY distance below. A cell-
        // only sort still lets a far corner of the camera's cell consume the cap before a near REFR in the
        // adjacent cell.
        var sourceOrder = 0;
        foreach (var spatialCell in _visibleCellScratch)
        {
            foreach (var p in spatialCell.Cell.PlacedObjects)
            {
                var candidateSourceOrder = sourceOrder++;
                if (string.IsNullOrEmpty(p.ModelPath)) continue;
                var authoredDisabled = p.IsInitiallyDisabled || _xespDisabledRefs?.Contains(p.FormId) == true;
                if (!_enabledOverrides.IsVisible(p.FormId, authoredDisabled, _showDisabled)) continue;
                if (p.RecordType is "ACHR" or "ACRE") continue; // skinned actors carry no static collision
                if (RenderableReference.IsMarkerModelPath(p.ModelPath) ||
                    RenderableReference.IsImposterModelPath(p.ModelPath) ||
                    RenderableReference.IsLodDuplicateBaseEditorId(p.BaseEditorId)) continue;

                var world = PlacedReferenceTransform.ComposeWorldMatrix(
                    p.X, p.Y, p.Z, p.RotX, p.RotY, p.RotZ, p.Scale);
                // Match the main scene's camera-relative frame. Keeping absolute ~50k+ world
                // coordinates in this CPU-built line buffer made the cage wobble against the
                // camera-relative visual mesh as view rotation amplified float cancellation.
                world.M41 -= renderOrigin.X;
                world.M42 -= renderOrigin.Y;
                world.M43 -= renderOrigin.Z;
                var dx = p.X - cylinder.Position.X;
                var dy = p.Y - cylinder.Position.Y;
                _candidateScratch.Add(new CollisionReferenceCandidate(
                    p.ModelPath!, p.FormId, dx * dx + dy * dy, world, candidateSourceOrder));
            }
        }

        _priorityResolver.Resolve(
            _candidateScratch,
            _collisionResolver,
            _collisionWarmup,
            MaxColdWarmupRequestsPerFrame,
            MaxLineVertices,
            _instanceScratch);
        var cached = _geometryCache.Resolve(_instanceScratch);
        if (cached.LineVertexCount < 2 || cached.Buffer is null) return 0;

        var frameIndex = _recorder.FrameIndex;
        var cmd = _recorder.CommandList;

        // Only the changing view/projection constants remain transient. Geometry is persistent until
        // the ordered visible collision content changes or LoadData/property invalidation retires it.
        if (!_ringBuffer.TryAllocate(frameIndex, UniformsByteSize, out var cbAlloc, GpuRingBuffer12.CbAlignment))
        {
            return 0;
        }
        var uniforms = new CollisionUniforms
        {
            ViewProj = viewProj,
            LineColor = new Vector4(0.2f, 1.0f, 0.35f, 0.85f) // bright green, distinct from grid/navmesh
        };
        unsafe { *(CollisionUniforms*)cbAlloc.CpuPtr = uniforms; }

        cmd.SetPipelineState(ldrTarget ? _ldrPso : _scenePso);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.LineList);
        cmd.IASetVertexBuffers(0, new VertexBufferView
        {
            BufferLocation = cached.Buffer.GPUVirtualAddress,
            SizeInBytes = (uint)(cached.LineVertexCount * VertexStride),
            StrideInBytes = VertexStride,
        });
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, cbAlloc.GpuAddress);
        cmd.DrawInstanced((uint)cached.LineVertexCount, 1, 0, 0);

        return cached.ReferencesDrawn;
    }

    private ID3D12Resource UploadGeometry(Vector3[] vertices, int count)
    {
        return GpuMeshBufferFactory12.CreateUploadBuffer<Vector3>(_gpu, vertices.AsSpan(0, count));
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
    private struct CollisionUniforms
    {
        public Matrix4x4 ViewProj;
        public Vector4 LineColor;
    }
}
#endif
