#if WINDOWS_GUI
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Games;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D12 = Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>
///     v3 Pass 4 Step 2e — D3D12 port of <c>WaterRenderer</c>. Renders one
///     alpha-blended flat quad per visible cell whose water height resolves, plus authored
///     WaterShaderProperty triangles from placed NIFs. No vertex buffer: <c>water.vert.hlsl</c>
///     reads six world-space vertices from each structured-buffer packet via <c>SV_VertexID</c>.
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
    // + 3 layers(48) + depthParams(16) + renderOrigin(16) + 3 FO4 float4s(48)
    private const uint UniformsByteSize = 304; // WaterFrameUniforms incl. the trailing FO4 constants

    // Sentinel meaning "no resolved NNAM normal map" — the shader then uses a procedural ripple
    // normal so proto/test worldspaces with no water texture still animate.
    private const uint NoNormalMap = 0xFFFFFFFF;

    // Per-game water config (noise tile size, depth tie-break bias, no-WATR fallback tints, shader
    // variant). These were FNV-specific constants hardcoded here; they now live in WaterProfile so they
    // stop being silently universal. Defaults to the FNV profile (the renderer is built before the ESM
    // loads); SetGame swaps in the loaded game's profile. FNV/FO3 → identical values → byte-identical.
    private WaterProfile _waterProfile = WaterProfile.Fnv;

    private readonly GpuDevice12 _gpu;
    private readonly GpuCommandRecorder12 _recorder;
    private readonly GpuRingBuffer12 _ringBuffer;
    private readonly GpuDescriptorHeapAllocator12 _cbvSrvUavHeap;
    private readonly GpuPersistentDescriptorAllocator12 _persistentSrvs;
    private readonly GpuDeletionQueue12 _deletionQueue;
    // _pso: depth-test variant (DSV bound, hardware depth test) used when no scene-depth SRV is
    // available. _psoDepthSample: depth-test OFF (no DSV) used when the host hands us the scene
    // depth as an SRV — occlusion is then done in-shader via the sampled depth (FNV WATER000 path).
    // The Oblivion pair is the same HLSL compiled with OBLIVION_WATER (view-angle body + single sun
    // specular — see WaterShaderVariant.OblivionWater000), and the FO4 pair with FO4_WATER (the
    // disassembled BSWaterShader math — see WaterShaderVariant.Fo4Water); both selected by the
    // profile at draw time.
    private readonly ID3D12PipelineState _pso;
    private readonly ID3D12PipelineState _psoDepthSample;
    private readonly ID3D12PipelineState _psoOblivion;
    private readonly ID3D12PipelineState _psoOblivionDepthSample;
    private readonly ID3D12PipelineState _psoFo4;
    private readonly ID3D12PipelineState _psoFo4DepthSample;
    private readonly ID3D12PipelineState _psoMorrowind;
    private readonly ID3D12PipelineState _psoMorrowindDepthSample;

    // Morrowind surface animation: the bindless indices of textures\water\water00-31.dds, resolved
    // by the host at load; Render picks the frame from elapsed time × the profile's SurfaceFrameFps
    // and passes it in the NNAM slot (the MorrowindWater shader samples it as a tiled diffuse).
    private uint[]? _morrowindSurfaceFrames;

    private readonly List<global::BethesdaMultitool.WorldWaterCell> _waterCells = new();
    private readonly List<global::BethesdaMultitool.WorldWaterCell> _visibleWaterScratch = new();
    // Placed-NIF water geometry (cave/pool/reflecting-pool water embedded in REFR meshes). Owned by
    // ReferenceRenderer12 (which accumulates them as those references' meshes stream in) and handed
    // here once per frame via SetNifWaterPlanes; culled into _visibleNifScratch each Render and drawn
    // with the same shader/appearance as cell water.
    private IReadOnlyList<NifWaterGeometry> _nifWaterPlanes = Array.Empty<NifWaterGeometry>();
    private readonly List<NifWaterGeometry> _visibleNifScratch = new();
    private float? _worldspaceDefaultWaterHeight;
    private BethesdaMultitool.Core.Formats.Esm.Models.Records.World.WaterAppearance? _appearance;
    private uint _noiseBindlessIndex = NoNormalMap;
    private uint _depthBindlessIndex = NoNormalMap; // scene depth SRV; NoNormalMap => proxy + depth-test PSO
    private float _depthNear = 16f;
    private float _depthFar = 1f;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private global::BethesdaMultitool.WorldSpatialIndex? _spatialIndex;

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
            // Antialias edges on the multisampled scene RT (no-op when scene isn't MSAA).
            MultisampleEnable = gpu.SceneSampleCount > 1,
        };

        // Read depth so terrain occludes submerged water; don't write depth so layer
        // order (terrain → references → water → wireframe) stays sane.
        var depth = new D12.DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = D12.DepthWriteMask.Zero,
            // reversed-Z (near→1, far→0; depth clear = 0). GreaterEqual (not Greater) so water WINS a
            // coplanar tie with the terrain beneath it (3D-2 z-fighting): at a shoreline where the water
            // plane and the land mesh resolve to the same depth, Greater would reject the water fragment
            // and the land would flicker through; GreaterEqual draws the water. Water writes no depth, so
            // letting it pass ties has no knock-on effect on later passes. (This hardware-test PSO is the
            // no-scene-depth-SRV fallback; the depth-sample PSO does the same tie-break in the shader.)
            DepthFunc = ComparisonFunction.GreaterEqual,
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
            // The swapchain is premultiplied-alpha (GpuSwapChainSurface12), so the scene-RT alpha channel
            // is composited to the screen — a pass that drops it below the opaque scene's 1.0 makes that
            // region render see-through over the WinUI background. Water blends its COLOR translucently
            // (above) but must PRESERVE the destination alpha. Was DestinationBlendAlpha=Zero, which
            // OVERWROTE the underlying opaque 1.0 with water's <1 alpha and turned the water surface — and
            // any geometry it overlaps (partially-submerged rocks) — transparent. Max against the opaque
            // 1.0 underneath keeps the surface opaque to the compositor, matching the reference renderer.
            SourceBlendAlpha = D12.Blend.One,
            DestinationBlendAlpha = D12.Blend.One,
            BlendOperationAlpha = D12.BlendOperation.Max,
            RenderTargetWriteMask = D12.ColorWriteEnable.All,
        };

        var psOblivionBytecode = CompileEmbeddedShader(
            "water.frag.hlsl", "main", "ps_5_1", new ShaderMacro("OBLIVION_WATER", "1"));
        var psFo4Bytecode = CompileEmbeddedShader(
            "water.frag.hlsl", "main", "ps_5_1", new ShaderMacro("FO4_WATER", "1"));
        var psMorrowindBytecode = CompileEmbeddedShader(
            "water.frag.hlsl", "main", "ps_5_1", new ShaderMacro("MORROWIND_WATER", "1"));

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
            RenderTargetFormats = new[] { Gpu.D3D12.GpuSceneFormats.SceneColor },
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription((uint)gpu.SceneSampleCount, 0),
            SampleMask = uint.MaxValue,
        };
        _pso = gpu.Device.CreateGraphicsPipelineState(psoDesc);
        psoDesc.PixelShader = psOblivionBytecode;
        _psoOblivion = gpu.Device.CreateGraphicsPipelineState(psoDesc);
        psoDesc.PixelShader = psFo4Bytecode;
        _psoFo4 = gpu.Device.CreateGraphicsPipelineState(psoDesc);
        psoDesc.PixelShader = psMorrowindBytecode;
        _psoMorrowind = gpu.Device.CreateGraphicsPipelineState(psoDesc);
        psoDesc.PixelShader = psBytecode;

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
        psoDesc.PixelShader = psOblivionBytecode;
        _psoOblivionDepthSample = gpu.Device.CreateGraphicsPipelineState(psoDesc);
        psoDesc.PixelShader = psFo4Bytecode;
        _psoFo4DepthSample = gpu.Device.CreateGraphicsPipelineState(psoDesc);
        psoDesc.PixelShader = psMorrowindBytecode;
        _psoMorrowindDepthSample = gpu.Device.CreateGraphicsPipelineState(psoDesc);
    }

    public global::BethesdaMultitool.WorldRenderStats LastStats { get; } = new();
    public bool DetailedProfilingEnabled { get; set; }

    public void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        float? worldspaceDefaultWaterHeight)
        => LoadData(cells, worldspaceDefaultWaterHeight, spatialIndex: null);

    public void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        float? worldspaceDefaultWaterHeight,
        global::BethesdaMultitool.WorldSpatialIndex? spatialIndex)
        => LoadData(cells, worldspaceDefaultWaterHeight, spatialIndex, appearance: null);

    public void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        float? worldspaceDefaultWaterHeight,
        global::BethesdaMultitool.WorldSpatialIndex? spatialIndex,
        BethesdaMultitool.Core.Formats.Esm.Models.Records.World.WaterAppearance? appearance)
        => LoadData(cells, worldspaceDefaultWaterHeight, spatialIndex, appearance, normalMapBindlessIndex: null);

    public void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        float? worldspaceDefaultWaterHeight,
        global::BethesdaMultitool.WorldSpatialIndex? spatialIndex,
        BethesdaMultitool.Core.Formats.Esm.Models.Records.World.WaterAppearance? appearance,
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
                    _waterCells.Add(new global::BethesdaMultitool.WorldWaterCell(
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

    /// <summary>Selects the per-game <see cref="WaterProfile" /> (shader variant + tuning) for the loaded
    /// game. Call before <see cref="Render(Matrix4x4, VisibilityCylinder, Vector3)" /> on each
    /// worldspace/interior load. FNV/FO3 resolve to the FNV profile (byte-identical); every other game
    /// falls back to it until its own water shader is reverse-engineered (binary-RE-only policy).</summary>
    public void SetGame(BethesdaGame game) => _waterProfile = WaterProfile.ForGame(game);

    /// <summary>
    ///     Supplies the bindless indices of Morrowind's animated surface frames
    ///     (<c>textures\water\water00–31.dds</c>), resolved by the host at worldspace load.
    ///     <see cref="Render(Matrix4x4, VisibilityCylinder, Vector3)" /> cycles them at the profile's
    ///     <see cref="WaterProfile.SurfaceFrameFps" />. Null/empty → the MorrowindWater shader falls
    ///     back to the profile's flat default tint. No-op for other games' profiles.
    /// </summary>
    public void SetMorrowindSurfaceFrames(uint[]? frameBindlessIndices) =>
        _morrowindSurfaceFrames = frameBindlessIndices;

    /// <summary>
    ///     Supplies world-space authored water geometry detected on placed-reference NIFs (cave/pool water).
    ///     The list is owned by <see cref="ReferenceRenderer12" /> and grows as those references'
    ///     meshes stream in; this renderer reads it each frame and draws the visible surfaces with the
    ///     same shader as cell water. Cheap reference assignment — call once per frame before Render.
    /// </summary>
    public void SetNifWaterPlanes(IReadOnlyList<NifWaterGeometry> planes) => _nifWaterPlanes = planes;

    /// <summary>IWorldRenderer entry — absolute path (zero render origin).</summary>
    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder) => Render(viewProj, cylinder, default);

    /// <summary>Draws the visible cell-water grid + authored NIF water geometry; returns the surface count.</summary>
    /// <param name="renderOrigin">
    ///     The world-space point <paramref name="viewProj" /> treats as its origin (the same value the
    ///     scene binds as the camera-relative render origin). The VS subtracts it from each water vertex
    ///     before projection so far-from-origin water keeps float32 depth precision (the absolute path
    ///     shimmered the water edge as the camera rotated). Absolute callers (top-down, export) use the
    ///     two-arg overload — a no-op. vWorldPos stays absolute for the PS's noise-UV/view-vector anchoring.
    /// </param>
    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder, Vector3 renderOrigin)
    {
        LastStats.Reset();
        if (_waterCells.Count == 0 && _nifWaterPlanes.Count == 0) return 0;

        var started = StartTiming();
        var cmd = _recorder.CommandList;
        var frameIndex = _recorder.FrameIndex;

        var segmentStarted = StartTiming();
        var cellVisible = GatherVisibleWater(cylinder);
        var nifVisible = GatherVisibleNifPlanes(cylinder);
        var visibleSurfaces = cellVisible + nifVisible;
        LastStats.VisibleCandidates = visibleSurfaces;
        LastStats.VisibleGatherMilliseconds = ElapsedMilliseconds(segmentStarted);
        if (visibleSurfaces == 0)
        {
            LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
            return 0;
        }

        // One six-vertex packet holds a cell quad (two triangles) or up to two authored NIF
        // triangles. Indices choose the exact source positions; an odd final triangle gets one
        // degenerate companion, which rasterizes no fragments.
        var nifPacketCount = 0;
        for (var i = 0; i < nifVisible; i++)
        {
            nifPacketCount = checked(nifPacketCount + _visibleNifScratch[i].TrianglePacketCount);
        }
        var instanceCount = checked(cellVisible + nifPacketCount);

        segmentStarted = StartTiming();
        EnsureInstanceCapacity(instanceCount);
        LastStats.ResourceResizeMilliseconds = ElapsedMilliseconds(segmentStarted);

        segmentStarted = StartTiming();
        for (var i = 0; i < cellVisible; i++)
        {
            var water = _visibleWaterScratch[i];
            // OriginXY/FootprintSize are grid-derived for exteriors and AABB-derived for interiors
            // (see WorldSpatialIndex.BuildInterior). This remains the existing cell-water quad path;
            // only its two triangles are materialized into the unified six-vertex packet.
            _instanceScratch[i] = CreateQuadPacket(
                water.OriginXY,
                water.Height,
                new Vector2(water.FootprintSize));
        }

        // Placed NIF water follows the cell-water packets in the same buffer and uses the same DNAM
        // appearance. Unlike the old AABB slab, every packet dereferences the authored indices and
        // carries the exact transformed vertex positions (including distinct per-vertex heights).
        var packetCursor = cellVisible;
        for (var j = 0; j < nifVisible; j++)
        {
            var geometry = _visibleNifScratch[j];
            for (var packetIndex = 0; packetIndex < geometry.TrianglePacketCount; packetIndex++)
            {
                var packet = geometry.GetTrianglePacket(packetIndex);
                _instanceScratch[packetCursor++] = CreateTrianglePacket(
                    packet.Vertex0,
                    packet.Vertex1,
                    packet.Vertex2,
                    packet.Vertex3,
                    packet.Vertex4,
                    packet.Vertex5);
            }
        }
        LastStats.InstanceBuildMilliseconds = ElapsedMilliseconds(segmentStarted);

        segmentStarted = StartTiming();
        // Per-frame CB (b0) — viewProj + DNAM colors + (camera pos, elapsed time) for the
        // procedural ripple/Fresnel/specular shading.
        var elapsedSeconds = (float)Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds;
        if (!_ringBuffer.TryAllocate(frameIndex, UniformsByteSize, out var perFrameAlloc, GpuRingBuffer12.CbAlignment))
        {
            // Frame ring slot exhausted by the earlier scene passes (dense whole-map views). Skip
            // the water pass this frame — the next frame retries — instead of throwing, which
            // killed the whole render loop (the water pass runs LAST, so it hits the wall first).
            LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
            return 0;
        }
        var surface = _appearance?.Surface ?? BethesdaMultitool.Core.Formats.Esm.Models.Records.World.WaterSurfaceParams.Default;
        // MorrowindWater repurposes the NNAM slot as the CURRENT animated diffuse frame (the FF
        // engine cycles water00-31 at SurfaceFPS) and the opacity slot as [Water] World Alpha —
        // TES3 has no WATR records, so nothing else feeds these.
        var noiseIndex = _noiseBindlessIndex;
        var waterOpacity = surface.Opacity;
        if (_waterProfile.ShaderVariant == WaterShaderVariant.MorrowindWater)
        {
            waterOpacity = _waterProfile.SurfaceAlpha;
            if (_morrowindSurfaceFrames is { Length: > 0 } frames)
            {
                var frame = (int)(elapsedSeconds * _waterProfile.SurfaceFrameFps) % frames.Length;
                noiseIndex = frames[frame];
            }
        }
        unsafe
        {
            *(WaterFrameUniforms*)perFrameAlloc.CpuPtr = new WaterFrameUniforms
            {
                ViewProj = viewProj,
                Shallow = ColorToVector4(_appearance?.Shallow, _waterProfile.DefaultShallow),
                Deep = ColorToVector4(_appearance?.Deep, _waterProfile.DefaultDeep),
                Reflection = ColorToVector4(_appearance?.Reflection, _waterProfile.DefaultReflection),
                CamPosTime = new Vector4(cylinder.Position, elapsedSeconds),
                // x = NNAM bindless index (NoNormalMap → procedural ripple); y = world units/tile.
                NoiseIndex = noiseIndex,
                NoiseTiling = _waterProfile.NoiseTilingWorldUnits,
                NoiseScale = surface.NoiseScale,
                WaterOpacity = waterOpacity,
                Surface0 = new Vector4(surface.NormalsUvScale, surface.FresnelAmount, surface.ReflectivityAmount, surface.Shininess),
                // .w carries the lava flag (OBLIV-2): 1 = render as emissive, Fresnel-free lava (Oblivion
                // Deadlands lava planes) instead of reflective water. Was an unused spare.
                Surface1 = new Vector4(surface.SunPower, surface.DepthFalloffStart, surface.DepthFalloffEnd,
                    _appearance?.IsLava == true ? 1f : 0f),
                Layer1 = LayerToVector4(surface.Layer1),
                Layer2 = LayerToVector4(surface.Layer2),
                Layer3 = LayerToVector4(surface.Layer3),
                // Scene-depth source: x = depth SRV bindless index (NoNormalMap => view-angle proxy),
                // y/z = camera near/far (bit-reinterpreted; the shader asfloat()s them to linearize
                // the sampled depth into the real water-column thickness for the DepthFalloff fade).
                DepthIndex = _depthBindlessIndex,
                DepthNear = _depthNear,
                DepthFar = _depthFar,
                DepthTieBias = _waterProfile.DepthTieBiasWorldUnits,
                RenderOrigin = new Vector4(renderOrigin, 0f),
                // FO4-only constants (see WaterShaderVariant.Fo4Water); zero-defaults for other games.
                Fo4Spec = new Vector4(
                    surface.SunSpecularMagnitude, surface.SiltAmount, surface.ShallowAlpha, surface.DeepAlpha),
                Fo4Ranges = new Vector4(
                    surface.ColorShallowRange, surface.ColorDeepRange,
                    surface.AlphaShallowRange, surface.AlphaDeepRange),
                Fo4DarkSilt = ColorToVector4(_appearance?.DarkSilt, Vector3.Zero) with
                {
                    W = surface.DepthAmount,
                },
            };
        }

        // Copy CPU instance scratch into the persistent-mapped UPLOAD buffer.
        UploadInstances(instanceCount);
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
        var depthSample = _depthBindlessIndex != NoNormalMap;
        cmd.SetPipelineState(_waterProfile.ShaderVariant switch
        {
            WaterShaderVariant.OblivionWater000 => depthSample ? _psoOblivionDepthSample : _psoOblivion,
            WaterShaderVariant.Fo4Water => depthSample ? _psoFo4DepthSample : _psoFo4,
            WaterShaderVariant.MorrowindWater => depthSample ? _psoMorrowindDepthSample : _psoMorrowind,
            _ => depthSample ? _psoDepthSample : _pso,
        });
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, perFrameAlloc.GpuAddress);
        cmd.SetGraphicsRootDescriptorTable(GpuRootSignature12.Slots.SrvTable, srvAlloc.Gpu);
        // Bind the shared bindless texture table (space1, t0..) at the heap head so the PS can
        // sample the resolved NNAM normal map via NonUniformResourceIndex(uNoiseParams.x).
        cmd.SetGraphicsRootDescriptorTable(GpuRootSignature12.Slots.BindlessSrvTable, _cbvSrvUavHeap.BindlessHeapStartGpu);

        // Six vertices per packet: a cell quad or one/two authored NIF triangles.
        cmd.DrawInstanced(6, (uint)instanceCount, 0, 0);
        LastStats.DrawCallMilliseconds = ElapsedMilliseconds(segmentStarted);
        LastStats.WaterDraws = visibleSurfaces;
        LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
        return visibleSurfaces;
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
        _psoOblivion.Dispose();
        _psoOblivionDepthSample.Dispose();
        _psoFo4.Dispose();
        _psoFo4DepthSample.Dispose();
        _psoMorrowind.Dispose();
        _psoMorrowindDepthSample.Dispose();
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

    // NIF water geometry carries absolute world-space positions (from the REFR placement), so it culls
    // directly against the cylinder's game-space position — no canvas-Y flip (that flip is only for the
    // spatial-index buckets). Bounds are conservative only; the authored triangles remain unchanged.
    // Small list (only water-bearing refs), so linear.
    private int GatherVisibleNifPlanes(VisibilityCylinder cylinder)
    {
        _visibleNifScratch.Clear();
        if (_nifWaterPlanes.Count == 0) return 0;

        var center = new Vector2(cylinder.Position.X, cylinder.Position.Y);
        foreach (var geometry in _nifWaterPlanes)
        {
            if (geometry.IntersectsXY(center, cylinder.Radius))
            {
                _visibleNifScratch.Add(geometry);
            }
        }
        return _visibleNifScratch.Count;
    }

    private float? ResolveWaterHeight(CellRecord cell)
    {
        if (cell.WaterHeight is float cellHeight && !WorldHeightNormalizer.IsNoWaterSentinel(cellHeight))
            return cellHeight;
        if (_worldspaceDefaultWaterHeight is float worldHeight && !WorldHeightNormalizer.IsNoWaterSentinel(worldHeight))
            return worldHeight;
        return null;
    }

    private static WaterInstance CreateQuadPacket(Vector2 origin, float height, Vector2 footprint)
    {
        var v00 = new Vector3(origin, height);
        var v10 = new Vector3(origin.X + footprint.X, origin.Y, height);
        var v01 = new Vector3(origin.X, origin.Y + footprint.Y, height);
        var v11 = new Vector3(origin + footprint, height);
        return CreateTrianglePacket(v00, v10, v01, v01, v10, v11);
    }

    private static WaterInstance CreateTrianglePacket(
        Vector3 v0,
        Vector3 v1,
        Vector3 v2,
        Vector3 v3,
        Vector3 v4,
        Vector3 v5) =>
        new()
        {
            Vertex0 = new Vector4(v0, 1f),
            Vertex1 = new Vector4(v1, 1f),
            Vertex2 = new Vector4(v2, 1f),
            Vertex3 = new Vector4(v3, 1f),
            Vertex4 = new Vector4(v4, 1f),
            Vertex5 = new Vector4(v5, 1f),
        };

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

    private static byte[] CompileEmbeddedShader(
        string name, string entryPoint, string profile, params ShaderMacro[] defines)
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
            defines,
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
        BethesdaMultitool.Core.Formats.Esm.Models.Records.World.WaterNoiseLayer layer)
        => new(layer.UvScale, layer.WindDirDegrees, layer.WindSpeed, layer.AmpScale);

    [StructLayout(LayoutKind.Sequential)]
    private struct WaterInstance
    {
        // Two explicit triangles. Cell water writes its usual rectangular quad here; placed NIF
        // water writes authored indexed vertices. The matching HLSL layout is six float4 registers.
        // This is 96 bytes versus the old compact cell-only instance's 32 bytes; that modest visible-
        // cell upload cost keeps one buffer, one root-table binding, one VS, and one draw call instead
        // of adding a second geometry path / PSO family solely for the uncommon placed-water surfaces.
        public Vector4 Vertex0;
        public Vector4 Vertex1;
        public Vector4 Vertex2;
        public Vector4 Vertex3;
        public Vector4 Vertex4;
        public Vector4 Vertex5;
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
        public float NoiseScale; // DNAM fNoiseScale @96; shader reads asfloat(uNoiseParams.z)
        // WATR ANAM Opacity/100 (Oblivion VarAmounts.z fresnel/alpha floor; decompiled
        // FUN_004ed660 = ANAM byte / 100). Shader reads asfloat(uNoiseParams.w).
        public float WaterOpacity;
        // Engine-faithful WATR DNAM shading params (see WaterSurfaceParams).
        public Vector4 Surface0; // NormalsUvScale, FresnelAmount, ReflectivityAmount, Shininess
        public Vector4 Surface1; // SunPower, DepthFalloffStart, DepthFalloffEnd, spare
        public Vector4 Layer1;   // UvScale, WindDirDegrees, WindSpeed, AmpScale
        public Vector4 Layer2;
        public Vector4 Layer3;
        // uint4 uDepthParams: x = scene-depth SRV bindless index (0xFFFFFFFF = none/proxy),
        // y = near, z = far (both bit-reinterpreted floats), w = depth-occlusion tie-break bias
        // (world units, asfloat in the shader) so coplanar water wins over land (3D-2).
        public uint DepthIndex;
        public float DepthNear;
        public float DepthFar;
        public float DepthTieBias;
        // xyz = camera-relative render origin subtracted in the VS before projection (0 on absolute paths).
        public Vector4 RenderOrigin;
        // FO4_WATER-only constants (appended so every other variant's offsets are untouched):
        // Fo4Spec = (Sun Specular Magnitude, Silt Amount, Shallow Alpha, Deep Alpha);
        // Fo4Ranges = (Color Shallow/Deep Range, Alpha Shallow/Deep Range) in world units;
        // Fo4DarkSilt = DNAM silt Dark Color (the FO4 PS's unshadowed ambient add).
        public Vector4 Fo4Spec;
        public Vector4 Fo4Ranges;
        public Vector4 Fo4DarkSilt;
    }
}

#endif
