#if WINDOWS_GUI
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D12 = Vortice.Direct3D12;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>
///     v3 Pass 4 Step 2e — D3D12 port of <c>WaterRenderer</c>. Renders one
///     alpha-blended flat quad per visible cell whose water height resolves. No vertex
///     buffer: <c>water.vert.hlsl</c> expands the 6 quad corners from <c>SV_VertexID</c>
///     and a per-instance structured-buffer entry.
///     <para>
///         Single PSO (no double-sided variants — water is always rendered with
///         CullMode.None). One root CBV at b0 for the viewProj, one structured-buffer
///         SRV at t0 read by the VS (visible to all shader stages per
///         <see cref="GpuRootSignature12" /> slot 3).
///     </para>
/// </summary>
internal sealed class WaterRenderer12 : Abstractions.IWaterRenderer
{
    // viewProj(64) + 3 DNAM colors(48) + camPosTime(16) + noiseParams(16) + surface0/1(32)
    // + 3 layers(48) + depthParams(16)
    private const uint UniformsByteSize = 240;

    // Sentinel meaning "no resolved NNAM normal map" — the shader then uses a procedural ripple
    // normal so proto/test worldspaces with no water texture still animate.
    private const uint NoNormalMap = 0xFFFFFFFF;

    // World units per NNAM tile. FNV cells are 4096 units; ~2 tiles/cell reads as gentle swell.
    private const uint NoiseTilingWorldUnits = 2048;

    // Fallback tints when the worldspace has no resolvable WATR appearance (DNAM colors).
    private static readonly Vector3 DefaultShallow = new(0.12f, 0.24f, 0.32f);
    private static readonly Vector3 DefaultDeep = new(0.03f, 0.09f, 0.16f);
    private static readonly Vector3 DefaultReflection = new(0.22f, 0.32f, 0.40f);

    private readonly GpuDevice12 _gpu;
    private readonly GpuCommandRecorder12 _recorder;
    private readonly GpuRingBuffer12 _ringBuffer;
    private readonly GpuDescriptorHeapAllocator12 _cbvSrvUavHeap;
    private readonly GpuPersistentDescriptorAllocator12 _persistentSrvs;
    private readonly GpuDeletionQueue12 _deletionQueue;
    // _pso: depth-test variant (DSV bound, hardware depth test) used when no scene-depth SRV is
    // available. _psoDepthSample: depth-test OFF (no DSV) used when the host hands us the scene
    // depth as an SRV — occlusion is then done in-shader via the sampled depth (FNV WATER000 path).
    private readonly ID3D12PipelineState _pso;
    private readonly ID3D12PipelineState _psoDepthSample;

    private readonly List<global::FalloutXbox360Utils.WorldWaterCell> _waterCells = new();
    private readonly List<global::FalloutXbox360Utils.WorldWaterCell> _visibleWaterScratch = new();
    private float? _worldspaceDefaultWaterHeight;
    private FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World.WaterAppearance? _appearance;
    private uint _noiseBindlessIndex = NoNormalMap;
    private uint _depthBindlessIndex = NoNormalMap; // scene depth SRV; NoNormalMap => proxy + depth-test PSO
    private float _depthNear = 16f;
    private float _depthFar = 1f;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private global::FalloutXbox360Utils.WorldSpatialIndex? _spatialIndex;

    // Persistent-mapped UPLOAD-heap structured buffer. Resized when the visible water cell
    // count exceeds capacity. Stays mapped for its lifetime — UPLOAD-heap resources can.
    private ID3D12Resource? _instanceBuffer;
    private IntPtr _instanceMapped;
    private CpuDescriptorHandle _instanceSrvPersistent;
    private bool _instanceSrvAllocated;
    private int _instanceCapacity;
    private WaterInstance[] _instanceScratch = [];
    private bool _disposed;

    public WaterRenderer12(
        GpuDevice12 gpu,
        GpuCommandRecorder12 recorder,
        GpuRingBuffer12 ringBuffer,
        GpuRootSignature12 rootSignature,
        GpuDescriptorHeapAllocator12 cbvSrvUavHeap,
        GpuDeletionQueue12 deletionQueue)
    {
        _gpu = gpu;
        _recorder = recorder;
        _ringBuffer = ringBuffer;
        _cbvSrvUavHeap = cbvSrvUavHeap;
        _persistentSrvs = new GpuPersistentDescriptorAllocator12(
            gpu,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            capacity: 1);
        _deletionQueue = deletionQueue;

        var vsBytecode = CompileEmbeddedShader("water.vert.hlsl", "main", "vs_5_1");
        var psBytecode = CompileEmbeddedShader("water.frag.hlsl", "main", "ps_5_1");

        var rasterizer = new D12.RasterizerDescription
        {
            FillMode = D12.FillMode.Solid,
            CullMode = D12.CullMode.None, // flat plane, both faces
            FrontCounterClockwise = true,
            DepthClipEnable = true,
        };

        // Read depth so terrain occludes submerged water; don't write depth so layer
        // order (terrain → references → water → wireframe) stays sane.
        var depth = new D12.DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = D12.DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.Less,
            StencilEnable = false,
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
            InputLayout = new InputLayoutDescription(Array.Empty<InputElementDescription>()),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { Format.B8G8R8A8_UNorm },
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
            SampleMask = uint.MaxValue,
        };
        _pso = gpu.Device.CreateGraphicsPipelineState(psoDesc);

        // Depth-sample variant: no hardware depth test and no DSV bound, so the scene depth buffer
        // can be read as an SRV during this pass. Occlusion is done in the shader (discard where the
        // sampled scene geometry is nearer than the water fragment). Blend/raster match _pso.
        psoDesc.DepthStencilState = new D12.DepthStencilDescription
        {
            DepthEnable = false,
            DepthWriteMask = D12.DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.Always,
            StencilEnable = false,
        };
        psoDesc.DepthStencilFormat = Format.Unknown;
        _psoDepthSample = gpu.Device.CreateGraphicsPipelineState(psoDesc);
    }

    public global::FalloutXbox360Utils.WorldRenderStats LastStats { get; } = new();
    public bool DetailedProfilingEnabled { get; set; }

    public void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        float? worldspaceDefaultWaterHeight)
        => LoadData(cells, worldspaceDefaultWaterHeight, spatialIndex: null);

    public void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        float? worldspaceDefaultWaterHeight,
        global::FalloutXbox360Utils.WorldSpatialIndex? spatialIndex)
        => LoadData(cells, worldspaceDefaultWaterHeight, spatialIndex, appearance: null);

    public void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        float? worldspaceDefaultWaterHeight,
        global::FalloutXbox360Utils.WorldSpatialIndex? spatialIndex,
        FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World.WaterAppearance? appearance)
        => LoadData(cells, worldspaceDefaultWaterHeight, spatialIndex, appearance, normalMapBindlessIndex: null);

    public void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        float? worldspaceDefaultWaterHeight,
        global::FalloutXbox360Utils.WorldSpatialIndex? spatialIndex,
        FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World.WaterAppearance? appearance,
        uint? normalMapBindlessIndex)
    {
        _worldspaceDefaultWaterHeight = worldspaceDefaultWaterHeight;
        _spatialIndex = spatialIndex;
        _appearance = appearance;
        _noiseBindlessIndex = normalMapBindlessIndex ?? NoNormalMap;
        _waterCells.Clear();

        if (spatialIndex is not null)
        {
            _waterCells.AddRange(spatialIndex.WaterCells);
        }
        else
        {
            foreach (var (key, cell) in cells)
            {
                if (ResolveWaterHeight(cell) is float z)
                {
                    _waterCells.Add(new global::FalloutXbox360Utils.WorldWaterCell(
                        key,
                        cell,
                        z,
                        new Vector2(key.gx * WorldGridConstants.CellSize, key.gy * WorldGridConstants.CellSize),
                        WorldGridConstants.CellSize));
                }
            }
        }

        EnsureInstanceCapacity(_waterCells.Count);
    }

    public void SetSceneDepth(uint depthBindlessIndex, float near, float far)
    {
        _depthBindlessIndex = depthBindlessIndex;
        _depthNear = near;
        _depthFar = far;
    }

    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder)
    {
        LastStats.Reset();
        if (_waterCells.Count == 0) return 0;

        var started = StartTiming();
        var cmd = _recorder.CommandList;
        var frameIndex = _recorder.FrameIndex;

        var segmentStarted = StartTiming();
        var visible = GatherVisibleWater(cylinder);
        LastStats.VisibleCandidates = visible;
        LastStats.VisibleGatherMilliseconds = ElapsedMilliseconds(segmentStarted);
        if (visible == 0)
        {
            LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
            return 0;
        }

        segmentStarted = StartTiming();
        EnsureInstanceCapacity(visible);
        LastStats.ResourceResizeMilliseconds = ElapsedMilliseconds(segmentStarted);

        segmentStarted = StartTiming();
        for (var i = 0; i < visible; i++)
        {
            var water = _visibleWaterScratch[i];
            _instanceScratch[i] = new WaterInstance
            {
                // OriginXY/FootprintSize are grid-derived for exteriors and AABB-derived for
                // interiors (see WorldSpatialIndex.BuildInterior), so this is grid-agnostic.
                CellOriginAndFootprint = new Vector4(
                    water.OriginXY.X,
                    water.OriginXY.Y,
                    water.Height,
                    water.FootprintSize),
            };
        }
        LastStats.InstanceBuildMilliseconds = ElapsedMilliseconds(segmentStarted);

        segmentStarted = StartTiming();
        // Per-frame CB (b0) — viewProj + DNAM colors + (camera pos, elapsed time) for the
        // procedural ripple/Fresnel/specular shading.
        var elapsedSeconds = (float)Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds;
        var perFrameAlloc = _ringBuffer.Allocate(frameIndex, UniformsByteSize, GpuRingBuffer12.CbAlignment);
        var surface = _appearance?.Surface ?? FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World.WaterSurfaceParams.Default;
        unsafe
        {
            *(WaterFrameUniforms*)perFrameAlloc.CpuPtr = new WaterFrameUniforms
            {
                ViewProj = viewProj,
                Shallow = ColorToVector4(_appearance?.Shallow, DefaultShallow),
                Deep = ColorToVector4(_appearance?.Deep, DefaultDeep),
                Reflection = ColorToVector4(_appearance?.Reflection, DefaultReflection),
                CamPosTime = new Vector4(cylinder.Position, elapsedSeconds),
                // x = NNAM bindless index (NoNormalMap → procedural ripple); y = world units/tile.
                NoiseIndex = _noiseBindlessIndex,
                NoiseTiling = NoiseTilingWorldUnits,
                NoisePad0 = 0,
                NoisePad1 = 0,
                Surface0 = new Vector4(surface.NormalsUvScale, surface.FresnelAmount, surface.ReflectivityAmount, surface.Shininess),
                Surface1 = new Vector4(surface.SunPower, surface.DepthFalloffStart, surface.DepthFalloffEnd, 0f),
                Layer1 = LayerToVector4(surface.Layer1),
                Layer2 = LayerToVector4(surface.Layer2),
                Layer3 = LayerToVector4(surface.Layer3),
                // Scene-depth source: x = depth SRV bindless index (NoNormalMap => view-angle proxy),
                // y/z = camera near/far (bit-reinterpreted; the shader asfloat()s them to linearize
                // the sampled depth into the real water-column thickness for the DepthFalloff fade).
                DepthIndex = _depthBindlessIndex,
                DepthNear = _depthNear,
                DepthFar = _depthFar,
                DepthPad = 0,
            };
        }

        // Copy CPU instance scratch into the persistent-mapped UPLOAD buffer.
        UploadInstances(visible);
        LastStats.GpuUploadMilliseconds = ElapsedMilliseconds(segmentStarted);

        segmentStarted = StartTiming();
        // Copy the persistent instance-buffer SRV into this frame's shader-visible heap slot.
        var srvAlloc = _cbvSrvUavHeap.Allocate(1);
        _gpu.Device.CopyDescriptorsSimple(
            1,
            srvAlloc.Cpu,
            _instanceSrvPersistent,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        // With a scene-depth SRV the host has unbound the DSV + transitioned depth to
        // PIXEL_SHADER_RESOURCE, so use the no-depth-test PSO (occlusion is done in-shader).
        // Otherwise the DSV is still bound → use the hardware-depth-test PSO.
        cmd.SetPipelineState(_depthBindlessIndex != NoNormalMap ? _psoDepthSample : _pso);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, perFrameAlloc.GpuAddress);
        cmd.SetGraphicsRootDescriptorTable(GpuRootSignature12.Slots.SrvTable, srvAlloc.Gpu);
        // Bind the shared bindless texture table (space1, t0..) at the heap head so the PS can
        // sample the resolved NNAM normal map via NonUniformResourceIndex(uNoiseParams.x).
        cmd.SetGraphicsRootDescriptorTable(GpuRootSignature12.Slots.BindlessSrvTable, _cbvSrvUavHeap.BindlessHeapStartGpu);

        // 6 vertices per quad, `visible` instances.
        cmd.DrawInstanced(6, (uint)visible, 0, 0);
        LastStats.DrawCallMilliseconds = ElapsedMilliseconds(segmentStarted);
        LastStats.WaterDraws = visible;
        LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
        return visible;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_instanceBuffer is not null)
        {
            _instanceBuffer.Unmap(0, null);
            _deletionQueue.EnqueueDispose(_instanceBuffer);
            _instanceBuffer = null;
        }
        _persistentSrvs.Dispose();
        _pso.Dispose();
        _psoDepthSample.Dispose();
    }

    private int GatherVisibleWater(VisibilityCylinder cylinder)
    {
        _visibleWaterScratch.Clear();
        if (_spatialIndex is not null)
        {
            _spatialIndex.QueryWaterCellsInRadius(
                cylinder.Position.X,
                -cylinder.Position.Y,
                cylinder.Radius,
                _visibleWaterScratch);
            return _visibleWaterScratch.Count;
        }

        foreach (var water in _waterCells)
        {
            var key = water.Key;
            if (cylinder.ContainsCell(key.gx, key.gy))
            {
                _visibleWaterScratch.Add(water);
            }
        }
        return _visibleWaterScratch.Count;
    }

    private float? ResolveWaterHeight(CellRecord cell)
    {
        if (cell.WaterHeight is float cellHeight && !WorldHeightNormalizer.IsNoWaterSentinel(cellHeight))
            return cellHeight;
        if (_worldspaceDefaultWaterHeight is float worldHeight && !WorldHeightNormalizer.IsNoWaterSentinel(worldHeight))
            return worldHeight;
        return null;
    }

    private unsafe void UploadInstances(int instanceCount)
    {
        if (_instanceBuffer is null || instanceCount == 0 || _instanceMapped == IntPtr.Zero) return;
        var byteCount = (uint)(instanceCount * Marshal.SizeOf<WaterInstance>());
        fixed (WaterInstance* src = _instanceScratch)
        {
            System.Runtime.CompilerServices.Unsafe.CopyBlockUnaligned(
                destination: (void*)_instanceMapped,
                source: src,
                byteCount: byteCount);
        }
    }

    private unsafe void EnsureInstanceCapacity(int requested)
    {
        if (requested <= _instanceCapacity && _instanceBuffer is not null) return;

        if (_instanceBuffer is not null)
        {
            _instanceBuffer.Unmap(0, null);
            _deletionQueue.EnqueueDispose(_instanceBuffer);
            _instanceBuffer = null;
            _instanceMapped = IntPtr.Zero;
        }

        var capacity = Math.Max(1, requested);
        _instanceScratch = new WaterInstance[capacity];
        var stride = (uint)Marshal.SizeOf<WaterInstance>();
        var byteWidth = (ulong)capacity * stride;

        _instanceBuffer = _gpu.Device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.UploadHeapProperties,
            HeapFlags.None,
            ResourceDescription.Buffer(byteWidth),
            ResourceStates.GenericRead,
            optimizedClearValue: null);

        void* cpuPtr = null;
        _instanceBuffer.Map(0, &cpuPtr).CheckError();
        _instanceMapped = (IntPtr)cpuPtr;
        _instanceCapacity = capacity;
        UpdateInstanceSrv();
    }

    private void UpdateInstanceSrv()
    {
        if (_instanceBuffer is null)
        {
            return;
        }

        if (!_instanceSrvAllocated)
        {
            _instanceSrvPersistent = _persistentSrvs.Allocate();
            _instanceSrvAllocated = true;
        }

        var srvDesc = new ShaderResourceViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Buffer,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Buffer = new BufferShaderResourceView
            {
                FirstElement = 0,
                NumElements = (uint)_instanceCapacity,
                StructureByteStride = (uint)Marshal.SizeOf<WaterInstance>(),
                Flags = BufferShaderResourceViewFlags.None,
            },
        };
        _gpu.Device.CreateShaderResourceView(_instanceBuffer, srvDesc, _instanceSrvPersistent);
    }

    private long StartTiming() => DetailedProfilingEnabled ? Stopwatch.GetTimestamp() : 0;

    private static double ElapsedMilliseconds(long started) =>
        started == 0 ? 0 : Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    // D3DCOMPILE_ENABLE_UNBOUNDED_DESCRIPTOR_TABLES — required by fxc/D3DCompile for any shader
    // that declares an unbounded resource array (e.g. `Texture2D gWaterTextures[] : register(t0, space1)`).
    // Mirrors the flag TerrainRenderer12/ReferenceRenderer12 set for their bindless shaders; without it
    // the compile fails X3596 and the whole D3D12 backend init aborts.
    private const ShaderFlags EnableUnboundedDescriptorTables = (ShaderFlags)0x00100000;

    private static byte[] CompileEmbeddedShader(string name, string entryPoint, string profile)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Embedded shader resource not found: {name}");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();

        // Detect the unbounded-array declaration by its shape (`[] : register`) rather than a fixed
        // variable name — water's array is `gWaterTextures[]`, so the name-specific `textures[]` check
        // the other renderers use would miss it.
        var shaderFlags = source.Contains("[] : register", StringComparison.Ordinal)
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

    private static Vector4 ColorToVector4((byte R, byte G, byte B)? color, Vector3 fallback)
    {
        if (color is not { } c) return new Vector4(fallback, 1f);
        return new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, 1f);
    }

    private static Vector4 LayerToVector4(
        FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World.WaterNoiseLayer layer)
        => new(layer.UvScale, layer.WindDirDegrees, layer.WindSpeed, layer.AmpScale);

    [StructLayout(LayoutKind.Sequential)]
    private struct WaterInstance
    {
        public Vector4 CellOriginAndFootprint; // xy = origin, z = water height, w = footprint size
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaterFrameUniforms
    {
        public Matrix4x4 ViewProj;
        public Vector4 Shallow;
        public Vector4 Deep;
        public Vector4 Reflection;
        public Vector4 CamPosTime;
        // A uint4 register (HLSL uNoiseParams): x = NNAM bindless index, y = world units/tile.
        // Kept as raw uints so the index reaches the shader bit-exact (no float reinterpretation).
        public uint NoiseIndex;
        public uint NoiseTiling;
        public uint NoisePad0;
        public uint NoisePad1;
        // Engine-faithful WATR DNAM shading params (see WaterSurfaceParams).
        public Vector4 Surface0; // NormalsUvScale, FresnelAmount, ReflectivityAmount, Shininess
        public Vector4 Surface1; // SunPower, DepthFalloffStart, DepthFalloffEnd, spare
        public Vector4 Layer1;   // UvScale, WindDirDegrees, WindSpeed, AmpScale
        public Vector4 Layer2;
        public Vector4 Layer3;
        // uint4 uDepthParams: x = scene-depth SRV bindless index (0xFFFFFFFF = none/proxy),
        // y = near, z = far (both bit-reinterpreted floats), w = spare.
        public uint DepthIndex;
        public float DepthNear;
        public float DepthFar;
        public uint DepthPad;
    }
}
#endif
