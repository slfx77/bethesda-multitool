#if WINDOWS_GUI
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
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
    private static readonly Logger Log = Logger.Instance;

    // viewProj(64) + 3 DNAM colors(48) + camPosTime(16) + noiseParams(16) + surface0/1(32)
    // + 3 layers(48) + depthParams(16) + renderOrigin(16) + 3 FO4 float4s(48)
    // + ordered normal indices(16) + 2 Oblivion DATA registers(32)
    // + 4 opt-in modern-water registers(64) = 416 bytes / 26 registers.
    private const uint UniformsByteSize = ModernWaterPipeline.FrameUniformByteSize;
    private const uint FnvNoiseDimension = 256;

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
    private readonly ID3D12RootSignature _sharedRootSignature;
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
    private readonly GraphicsPipelineStateDescription _depthPsoTemplate;
    private readonly GraphicsPipelineStateDescription _depthSamplePsoTemplate;

    // WATER-07 architectural replacement. These resources and shader permutations are created only
    // after an explicit opt-in on FO4/FO76. Any initialization or transient-prepass failure selects
    // the established _psoFo4 path for that frame.
    private ModernWaterResources12? _modernWater;
    private bool _modernWaterInitializationFailed;
    private uint _modernCubeBindlessIndex = NoNormalMap;
    private BethesdaGame _game = BethesdaGame.FalloutNewVegas;

    // WATER-08: FO3/FNV do not sample three procedural octaves in WATER000. The engine first
    // renders ISNOISESCROLLANDBLEND into a 256x256 scalar tile, then runs
    // ISNOISENORMALMAP over it and binds that finished normal tile to WATER000.
    private readonly ID3D12PipelineState _fnvNoiseScrollBlendPso;
    private readonly ID3D12PipelineState _fnvNoiseNormalPso;
    private readonly ID3D12Resource _fnvNoiseBlendTexture;
    private readonly ID3D12Resource _fnvNoiseNormalTexture;
    private readonly uint _fnvNoiseBlendBindlessIndex;
    private readonly uint _fnvNoiseNormalBindlessIndex;
    private bool _useFnvNoisePrepass;

    // Legacy water animation: bindless indices of textures\water\water00-31.dds, resolved by the
    // host at load. Morrowind samples the selected frame as diffuse; Oblivion WATER000 samples it
    // as its global animated normal. WATR TNAM is a separate per-water detail input.
    private uint[]? _legacyAnimatedFrames;

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
    private readonly uint[] _normalBindlessIndices = [NoNormalMap, NoNormalMap, NoNormalMap];
    private uint _oblivionDetailBindlessIndex = NoNormalMap;
    private string[] _telemetryMapPaths = [];
    private string[] _telemetryMapRoles = [];
    private bool[] _telemetryMapResolved = [];
    private uint _depthBindlessIndex = NoNormalMap; // scene depth SRV; NoNormalMap => proxy + depth-test PSO
    private float _depthNear = 16f;
    private float _depthFar = 1f;
    private int _depthSampleCount = 1;
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
        ValidateGpuLayouts();
        _gpu = gpu;
        _recorder = recorder;
        _ringBuffer = ringBuffer;
        _cbvSrvUavHeap = cbvSrvUavHeap;
        _persistentSrvs = new GpuPersistentDescriptorAllocator12(
            gpu,
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            capacity: 1);
        _deletionQueue = deletionQueue;
        _sharedRootSignature = rootSignature.RootSignature;

        var noiseScrollBlendBytecode = CompileEmbeddedShader(
            "water_noise.comp.hlsl", "mainScrollBlend", "cs_5_1");
        var noiseNormalBytecode = CompileEmbeddedShader(
            "water_noise.comp.hlsl", "mainNormal", "cs_5_1");
        _fnvNoiseScrollBlendPso = gpu.Device.CreateComputePipelineState(
            new ComputePipelineStateDescription
            {
                RootSignature = rootSignature.RootSignature,
                ComputeShader = noiseScrollBlendBytecode,
            });
        _fnvNoiseNormalPso = gpu.Device.CreateComputePipelineState(
            new ComputePipelineStateDescription
            {
                RootSignature = rootSignature.RootSignature,
                ComputeShader = noiseNormalBytecode,
            });

        var noiseTextureDescription = ResourceDescription.Texture2D(
            Format.R8G8B8A8_UNorm,
            FnvNoiseDimension,
            FnvNoiseDimension,
            1,
            1,
            1,
            0,
            ResourceFlags.AllowUnorderedAccess);
        _fnvNoiseBlendTexture = gpu.Device.CreateCommittedResource<ID3D12Resource>(
            new HeapProperties(HeapType.Default),
            HeapFlags.None,
            noiseTextureDescription,
            ResourceStates.NonPixelShaderResource);
        _fnvNoiseBlendTexture.Name = "FNV Water Noise Scroll+Blend";
        _fnvNoiseNormalTexture = gpu.Device.CreateCommittedResource<ID3D12Resource>(
            new HeapProperties(HeapType.Default),
            HeapFlags.None,
            noiseTextureDescription,
            ResourceStates.PixelShaderResource);
        _fnvNoiseNormalTexture.Name = "FNV Water Noise Normal";

        var noiseSrvDescription = new ShaderResourceViewDescription
        {
            Format = Format.R8G8B8A8_UNorm,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView
            {
                MostDetailedMip = 0,
                MipLevels = 1,
            },
        };
        var blendSrv = cbvSrvUavHeap.AllocatePersistent();
        _fnvNoiseBlendBindlessIndex = blendSrv.BindlessIndex;
        gpu.Device.CreateShaderResourceView(_fnvNoiseBlendTexture, noiseSrvDescription, blendSrv.Cpu);
        var normalSrv = cbvSrvUavHeap.AllocatePersistent();
        _fnvNoiseNormalBindlessIndex = normalSrv.BindlessIndex;
        gpu.Device.CreateShaderResourceView(_fnvNoiseNormalTexture, noiseSrvDescription, normalSrv.Cpu);

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
        _depthPsoTemplate = psoDesc;
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
        _depthSamplePsoTemplate = psoDesc;
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
    public bool ModernPipelineEnabled { get; set; }

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
        => LoadData(cells, worldspaceDefaultWaterHeight, spatialIndex, appearance,
            normalMapBindlessIndex is { } index ? new uint?[] { index } : null);

    public void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        float? worldspaceDefaultWaterHeight,
        global::BethesdaMultitool.WorldSpatialIndex? spatialIndex,
        BethesdaMultitool.Core.Formats.Esm.Models.Records.World.WaterAppearance? appearance,
        IReadOnlyList<uint?>? normalMapBindlessIndices)
    {
        _worldspaceDefaultWaterHeight = worldspaceDefaultWaterHeight;
        _spatialIndex = spatialIndex;
        _appearance = appearance;
        var first = NoNormalMap;
        if (normalMapBindlessIndices is not null)
        {
            for (var i = 0; i < normalMapBindlessIndices.Count; i++)
            {
                if (normalMapBindlessIndices[i] is { } resolved)
                {
                    first = resolved;
                    break;
                }
            }
        }
        _noiseBindlessIndex = first;
        for (var i = 0; i < _normalBindlessIndices.Length; i++)
        {
            _normalBindlessIndices[i] = normalMapBindlessIndices is not null && i < normalMapBindlessIndices.Count
                ? normalMapBindlessIndices[i] ?? first
                : first;
        }
        RefreshWaterMapTelemetry();
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

    public void SetSceneDepth(uint depthBindlessIndex, float near, float far, int sampleCount = 1)
    {
        if (depthBindlessIndex == NoNormalMap)
        {
            _depthBindlessIndex = NoNormalMap;
            _depthNear = 16f;
            _depthFar = 1f;
            _depthSampleCount = 1;
            return;
        }
        // The host transitions/unbinds depth based on whether it supplied a valid descriptor, so a
        // malformed enabled binding is a contract violation rather than a fail-soft fallback. Silently
        // changing its dimension could select Texture2D for a Texture2DMS descriptor (undefined D3D12),
        // while silently disabling it would make the renderer choose a DSV PSO after the host dropped DSV.
        if (!float.IsFinite(near) || near <= 0f)
            throw new ArgumentOutOfRangeException(nameof(near), near, "Depth near plane must be finite and positive.");
        if (!float.IsFinite(far) || far <= near)
            throw new ArgumentOutOfRangeException(nameof(far), far, "Depth far plane must be finite and greater than near.");
        if (sampleCount < 1)
            throw new ArgumentOutOfRangeException(nameof(sampleCount), sampleCount, "Depth sample count must be positive.");

        _depthBindlessIndex = depthBindlessIndex;
        _depthNear = near;
        _depthFar = far;
        _depthSampleCount = sampleCount;
    }

    public void SetModernCubeMap(uint? cubeMapBindlessIndex) =>
        _modernCubeBindlessIndex = cubeMapBindlessIndex ?? NoNormalMap;

    /// <summary>Selects the per-game <see cref="WaterProfile" /> (shader variant + tuning) for the loaded
    /// game. Call before <see cref="Render(Matrix4x4, VisibilityCylinder, Vector3)" /> on each
    /// worldspace/interior load. FNV/FO3 resolve to the FNV profile (byte-identical); every other game
    /// falls back to it until its own water shader is reverse-engineered (binary-RE-only policy).</summary>
    public void SetGame(BethesdaGame game)
    {
        _game = game;
        _waterProfile = WaterProfile.ForGame(game);
        // FO3 and FNV share the recovered ISNOISESCROLLANDBLEND -> ISNOISENORMALMAP chain.
        // Skyrim's evolved shader samples its three authored normals independently and must not be
        // routed through this classic single-NNAM prepass merely because its RT-free body math shares
        // the FNV profile.
        _useFnvNoisePrepass = game is BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas;
    }

    /// <summary>
    ///     Supplies the bindless indices of the legacy <c>water00–31.dds</c> animation, resolved by
    ///     the host at worldspace load. Morrowind interprets the selected texture as diffuse;
    ///     Oblivion WATER000 interprets it as NormalMap. Null/empty preserves the established fallback.
    /// </summary>
    public void SetLegacyAnimatedFrames(uint[]? frameBindlessIndices) =>
        _legacyAnimatedFrames = frameBindlessIndices;

    /// <summary>
    ///     Supplies TES4 WATR TNAM as WATER000's DetailMap. It is intentionally separate from the
    ///     global animated normal sequence: retail TNAM values are per-water textures and the
    ///     shipped DefaultWater record leaves TNAM empty.
    /// </summary>
    public void SetOblivionDetailTexture(uint? detailBindlessIndex)
    {
        _oblivionDetailBindlessIndex = detailBindlessIndex ?? NoNormalMap;
        RefreshWaterMapTelemetry();
    }

    private void RefreshWaterMapTelemetry()
    {
        var paths = new List<string>();
        var roles = new List<string>();
        var resolved = new List<bool>();
        var normals = _appearance?.NormalTextures;
        if (normals is { Count: > 0 })
        {
            for (var i = 0; i < normals.Count; i++)
            {
                paths.Add(normals[i]);
                roles.Add($"normal-{i + 1}");
                resolved.Add(i < _normalBindlessIndices.Length &&
                             _normalBindlessIndices[i] != NoNormalMap);
            }
        }
        else if (_appearance?.NoiseTexture is { Length: > 0 } noise)
        {
            paths.Add(noise);
            roles.Add("noise-or-normal-1");
            resolved.Add(_noiseBindlessIndex != NoNormalMap);
        }

        if (_appearance?.SurfaceTexture is { Length: > 0 } detail)
        {
            paths.Add(detail);
            roles.Add("oblivion-detail");
            resolved.Add(_oblivionDetailBindlessIndex != NoNormalMap);
        }

        _telemetryMapPaths = paths.ToArray();
        _telemetryMapRoles = roles.ToArray();
        _telemetryMapResolved = resolved.ToArray();
    }

    /// <summary>
    ///     Supplies world-space authored water geometry detected on placed-reference NIFs (cave/pool water).
    ///     The list is owned by <see cref="ReferenceRenderer12" /> and grows as those references'
    ///     meshes stream in; this renderer reads it each frame and draws the visible surfaces with the
    ///     same shader as cell water. Cheap reference assignment — call once per frame before Render.
    /// </summary>
    public void SetNifWaterPlanes(IReadOnlyList<NifWaterGeometry> planes) => _nifWaterPlanes = planes;

    private bool TryEnsureModernWater(out ModernWaterResources12? resources)
    {
        resources = _modernWater;
        if (resources is not null) return true;
        if (_modernWaterInitializationFailed) return false;

        try
        {
            resources = ModernWaterResources12.Create(
                _gpu,
                _sharedRootSignature,
                _cbvSrvUavHeap,
                _depthPsoTemplate,
                _depthSamplePsoTemplate);
            _modernWater = resources;
            Log.Info("[Water] Initialized opt-in FO4/FO76 dynamic-water resources.");
            return true;
        }
        catch (Exception ex)
        {
            _modernWaterInitializationFailed = true;
            Log.Warn(
                "[Water] Opt-in modern-water initialization failed; preserving stand-in pipeline: {0}",
                ex.Message);
            resources = null;
            return false;
        }
    }

    private unsafe bool RecordModernWaterPrepasses(
        ID3D12GraphicsCommandList cmd,
        int frameIndex,
        float elapsedSeconds,
        WaterSurfaceParams surface,
        ModernWaterResources12 resources)
    {
        if (!_ringBuffer.TryAllocate(
                frameIndex,
                ModernWaterPipeline.PrepassUniformByteSize,
                out var constants,
                GpuRingBuffer12.CbAlignment))
        {
            return false;
        }

        var layer1 = surface.Layer1;
        var layer2 = surface.Layer2;
        var layer3 = surface.Layer3;
        if (!surface.HasAuthoredNoiseLayers)
        {
            // FO76's short DNAM omits FO4's layer block. Equal, stationary unit layers are the
            // neutral composite; do not reuse unrelated classic-water defaults as modern authorship.
            layer1 = new WaterNoiseLayer(1f, 0f, 0f, 1f / 3f);
            layer2 = layer1;
            layer3 = layer1;
        }

        *(ModernWaterPrepassUniforms*)constants.CpuPtr = new ModernWaterPrepassUniforms
        {
            NormalIndex1 = _normalBindlessIndices[0],
            NormalIndex2 = _normalBindlessIndices[1],
            NormalIndex3 = _normalBindlessIndices[2],
            ShallowCoverage = ColorToVector4(_appearance?.Shallow, _waterProfile.DefaultShallow) with
            {
                W = surface.ShallowAlpha,
            },
            DeepCoverage = ColorToVector4(_appearance?.Deep, _waterProfile.DefaultDeep) with
            {
                W = surface.DeepAlpha,
            },
            Layer1 = LayerToVector4(layer1),
            Layer2 = LayerToVector4(layer2),
            Layer3 = LayerToVector4(layer3),
            NormalParams = new Vector4(
                surface.NormalsUvScale,
                surface.NormalMagnitude,
                surface.DepthAmount,
                elapsedSeconds),
            Ranges = new Vector4(
                surface.ColorShallowRange,
                surface.ColorDeepRange,
                surface.AlphaShallowRange,
                surface.AlphaDeepRange),
        };

        GpuDescriptorHeapAllocator12.DescriptorAllocation uavs;
        try
        {
            uavs = _cbvSrvUavHeap.Allocate((uint)resources.Outputs.Length);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        var uavCpu = uavs.Cpu;
        for (var i = 0; i < resources.Outputs.Length; i++)
        {
            _gpu.Device.CreateUnorderedAccessView(resources.Outputs[i], null, null, uavCpu);
            uavCpu.Ptr += uavs.DescriptorSize;
        }

        cmd.SetComputeRootSignature(_sharedRootSignature);
        cmd.SetComputeRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, constants.GpuAddress);
        cmd.SetComputeRootDescriptorTable(
            GpuRootSignature12.Slots.BindlessSrvTable,
            _cbvSrvUavHeap.BindlessHeapStartGpu);

        var uavGpu = uavs.Gpu;
        for (var i = 0; i < resources.Outputs.Length; i++)
        {
            var output = resources.Outputs[i];
            cmd.ResourceBarrierTransition(
                output,
                ModernWaterPipeline.DynamicReadState,
                ModernWaterPipeline.DynamicWriteState);
            cmd.SetPipelineState(resources.ComputePipelines[i]);
            cmd.SetComputeRootDescriptorTable(GpuRootSignature12.Slots.WaterNoiseUavTable, uavGpu);
            var width = i == ModernWaterResources12.DepthLutOutput
                ? ModernWaterPipeline.DepthLutWidth
                : ModernWaterPipeline.SurfaceDimension;
            var height = i == ModernWaterResources12.DepthLutOutput
                ? ModernWaterPipeline.DepthLutHeight
                : ModernWaterPipeline.SurfaceDimension;
            cmd.Dispatch((width + 7) / 8, (height + 7) / 8, 1);
            cmd.ResourceBarrierTransition(
                output,
                ModernWaterPipeline.DynamicWriteState,
                ModernWaterPipeline.DynamicReadState);
            uavGpu.Ptr += uavs.DescriptorSize;
        }

        return true;
    }

    /// <summary>
    ///     Records the recovered FO3/FNV noise construction without rebinding the graphics render
    ///     targets: ISNOISESCROLLANDBLEND writes the scalar tile, ISNOISENORMALMAP consumes it and
    ///     writes the normal tile. The shared root signature has independent compute/graphics binding
    ///     state, so the scene's graphics atmosphere CB remains intact across these dispatches.
    /// </summary>
    private unsafe bool RecordFnvNoisePrepass(
        ID3D12GraphicsCommandList cmd,
        int frameIndex,
        float elapsedSeconds,
        uint sourceIndex,
        WaterSurfaceParams surface)
    {
        if (!_ringBuffer.TryAllocate(
                frameIndex,
                (uint)Marshal.SizeOf<FnvNoiseUniforms>(),
                out var constants,
                GpuRingBuffer12.CbAlignment))
        {
            return false;
        }

        *(FnvNoiseUniforms*)constants.CpuPtr = new FnvNoiseUniforms
        {
            SourceIndex = sourceIndex,
            BlendIndex = _fnvNoiseBlendBindlessIndex,
            NoiseScale = surface.NoiseScale,
            TexScaleAmplitude = new Vector4(
                RecoveredNoiseTexScale(surface.Layer1.UvScale),
                RecoveredNoiseTexScale(surface.Layer2.UvScale),
                RecoveredNoiseTexScale(surface.Layer3.UvScale),
                0f),
            Scroll0 = new Vector4(RecoveredNoiseScroll(surface.Layer1, elapsedSeconds), 0f, 0f),
            Scroll1 = new Vector4(RecoveredNoiseScroll(surface.Layer2, elapsedSeconds), 0f, 0f),
            Scroll2 = new Vector4(RecoveredNoiseScroll(surface.Layer3, elapsedSeconds), 0f, 0f),
            Amplitude = new Vector4(
                surface.Layer1.AmpScale,
                surface.Layer2.AmpScale,
                surface.Layer3.AmpScale,
                0f),
        };

        var uavs = _cbvSrvUavHeap.Allocate(2);
        var blendUavCpu = uavs.Cpu;
        var normalUavCpu = uavs.Cpu;
        normalUavCpu.Ptr += uavs.DescriptorSize;
        _gpu.Device.CreateUnorderedAccessView(_fnvNoiseBlendTexture, null, null, blendUavCpu);
        _gpu.Device.CreateUnorderedAccessView(_fnvNoiseNormalTexture, null, null, normalUavCpu);

        var blendUavGpu = uavs.Gpu;
        var normalUavGpu = uavs.Gpu;
        normalUavGpu.Ptr += uavs.DescriptorSize;

        cmd.SetComputeRootSignature(_sharedRootSignature);
        cmd.SetComputeRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, constants.GpuAddress);
        cmd.SetComputeRootDescriptorTable(
            GpuRootSignature12.Slots.BindlessSrvTable,
            _cbvSrvUavHeap.BindlessHeapStartGpu);

        cmd.ResourceBarrierTransition(
            _fnvNoiseBlendTexture,
            ResourceStates.NonPixelShaderResource,
            ResourceStates.UnorderedAccess);
        cmd.SetPipelineState(_fnvNoiseScrollBlendPso);
        cmd.SetComputeRootDescriptorTable(GpuRootSignature12.Slots.WaterNoiseUavTable, blendUavGpu);
        cmd.Dispatch(FnvNoiseDimension / 8, FnvNoiseDimension / 8, 1);
        cmd.ResourceBarrierTransition(
            _fnvNoiseBlendTexture,
            ResourceStates.UnorderedAccess,
            ResourceStates.NonPixelShaderResource);

        cmd.ResourceBarrierTransition(
            _fnvNoiseNormalTexture,
            ResourceStates.PixelShaderResource,
            ResourceStates.UnorderedAccess);
        cmd.SetPipelineState(_fnvNoiseNormalPso);
        cmd.SetComputeRootDescriptorTable(GpuRootSignature12.Slots.WaterNoiseUavTable, normalUavGpu);
        cmd.Dispatch(FnvNoiseDimension / 8, FnvNoiseDimension / 8, 1);
        cmd.ResourceBarrierTransition(
            _fnvNoiseNormalTexture,
            ResourceStates.UnorderedAccess,
            ResourceStates.PixelShaderResource);
        return true;
    }

    private static float RecoveredNoiseTexScale(float authoredScale) =>
        MathF.Max(1f, MathF.Ceiling(authoredScale * 0.01f));

    private static Vector2 RecoveredNoiseScroll(WaterNoiseLayer layer, float elapsedSeconds)
    {
        // TESWaterSystem::UpdateWaterNoise accumulates X with sin(direction) and Y with
        // cos(direction): the DNAM angle is compass-style (0 degrees points +Y), not the prior
        // mathematical-angle cos/sin interpretation. Sampling wraps, so retain only the fraction
        // to keep long-running sessions numerically stable.
        var radians = layer.WindDirDegrees * (MathF.PI / 180f);
        var distance = layer.WindSpeed * elapsedSeconds;
        return new Vector2(
            WrapUnit(MathF.Sin(radians) * distance),
            WrapUnit(MathF.Cos(radians) * distance));
    }

    private static float WrapUnit(float value) => value - MathF.Floor(value);

    /// <summary>IWorldRenderer entry — absolute path (zero render origin).</summary>
    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder) =>
        RenderCore(viewProj, cylinder, default, animationTimeSeconds: null);

    /// <summary>Draws the visible cell-water grid + authored NIF water geometry; returns the surface count.</summary>
    /// <param name="renderOrigin">
    ///     The world-space point <paramref name="viewProj" /> treats as its origin (the same value the
    ///     scene binds as the camera-relative render origin). The VS subtracts it from each water vertex
    ///     before projection so far-from-origin water keeps float32 depth precision (the absolute path
    ///     shimmered the water edge as the camera rotated). Absolute callers (top-down, export) use the
    ///     two-arg overload — a no-op. vWorldPos stays absolute for the PS's noise-UV/view-vector anchoring.
    /// </param>
    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder, Vector3 renderOrigin) =>
        RenderCore(viewProj, cylinder, renderOrigin, animationTimeSeconds: null);

    /// <summary>
    ///     Capture-only deterministic entry point. Uses one authored clock for legacy frame selection,
    ///     noise scrolling, and shader time instead of renderer construction time.
    /// </summary>
    internal int RenderAtTime(
        Matrix4x4 viewProj,
        VisibilityCylinder cylinder,
        Vector3 renderOrigin,
        float animationTimeSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(animationTimeSeconds);
        if (!float.IsFinite(animationTimeSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(animationTimeSeconds), animationTimeSeconds, "Animation time must be finite.");
        }

        return RenderCore(viewProj, cylinder, renderOrigin, animationTimeSeconds);
    }

    private int RenderCore(
        Matrix4x4 viewProj,
        VisibilityCylinder cylinder,
        Vector3 renderOrigin,
        float? animationTimeSeconds)
    {
        LastStats.Reset();
        var elapsedSeconds = animationTimeSeconds ??
                             (float)Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds;
        LastStats.WaterAnimationSeconds = elapsedSeconds;
        LastStats.WaterAnimationFps = _waterProfile.SurfaceFrameFps;
        LastStats.WaterMapPaths = _telemetryMapPaths;
        LastStats.WaterMapRoles = _telemetryMapRoles;
        LastStats.WaterMapResolved = _telemetryMapResolved;
        LastStats.WaterTelemetryUnavailableReason = _telemetryMapPaths.Length == 0
            ? _appearance is null
                ? "no WATR appearance resolved; the shader uses profile colors and a procedural normal fallback"
                : "the active WATR appearance carries no authored texture path; a procedural normal fallback is used"
            : null;
        LastStats.WaterPipeline = ModernWaterPipeline.TelemetryName(
            _game,
            explicitlyEnabled: false,
            resourcesReady: false,
            prepassesRecorded: false);
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
        if (!_ringBuffer.TryAllocate(frameIndex, UniformsByteSize, out var perFrameAlloc, GpuRingBuffer12.CbAlignment))
        {
            // Frame ring slot exhausted by the earlier scene passes (dense whole-map views). Skip
            // the water pass this frame — the next frame retries — instead of throwing, which
            // killed the whole render loop (the water pass runs LAST, so it hits the wall first).
            LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
            return 0;
        }
        var surface = _appearance?.Surface ?? BethesdaMultitool.Core.Formats.Esm.Models.Records.World.WaterSurfaceParams.Default;
        // Morrowind and Oblivion both cycle the global water00-31 animation at the shipped 12 FPS.
        // Morrowind repurposes the NNAM slot as diffuse; Oblivion WATER000 consumes it as NormalMap.
        var noiseIndex = _noiseBindlessIndex;
        if (_legacyAnimatedFrames is { Length: > 0 } animatedFrames &&
            _waterProfile.SurfaceFrameFps > 0f &&
            _waterProfile.ShaderVariant is WaterShaderVariant.MorrowindWater or WaterShaderVariant.OblivionWater000)
        {
            var animationFrame = LegacyWaterAnimation.SelectFrame(
                elapsedSeconds, _waterProfile.SurfaceFrameFps, animatedFrames.Length);
            LastStats.WaterAnimationFrame = animationFrame;
            noiseIndex = animatedFrames[animationFrame];
        }
        var usedFnvNoisePrepass = false;
        if (_useFnvNoisePrepass && noiseIndex != NoNormalMap)
        {
            usedFnvNoisePrepass = RecordFnvNoisePrepass(
                cmd, frameIndex, elapsedSeconds, noiseIndex, surface);
            if (usedFnvNoisePrepass)
            {
                noiseIndex = _fnvNoiseNormalBindlessIndex;
            }
        }

        var modernTechnique = ModernWaterPipeline.SelectTechnique(
            hasDepthLut: _depthBindlessIndex != NoNormalMap,
            hasCubemap: _modernCubeBindlessIndex != NoNormalMap);
        ModernWaterResources12? modernResources = null;
        var modernResourcesReady = false;
        var modernPrepassesRecorded = false;
        if (ModernWaterPipeline.ShouldUse(ModernPipelineEnabled, _game))
        {
            modernResourcesReady = TryEnsureModernWater(out modernResources);
            if (modernResourcesReady && modernResources is not null)
            {
                modernPrepassesRecorded = RecordModernWaterPrepasses(
                    cmd,
                    frameIndex,
                    elapsedSeconds,
                    surface,
                    modernResources);
            }
        }
        var useModernPipeline = modernResourcesReady && modernPrepassesRecorded && modernResources is not null;
        LastStats.WaterPipeline = ModernWaterPipeline.TelemetryName(
            _game,
            ModernPipelineEnabled,
            modernResourcesReady,
            modernPrepassesRecorded,
            modernTechnique);
        LastStats.WaterNoisePrepassUsed = usedFnvNoisePrepass;

        var waterOpacity = surface.Opacity;
        if (_waterProfile.ShaderVariant == WaterShaderVariant.MorrowindWater)
        {
            waterOpacity = _waterProfile.SurfaceAlpha;
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
                // xyz remains the camera-relative origin consumed by the VS. Spare w selects the
                // Texture2D (1) or Texture2DMS (>1) depth alias without growing the 416-byte CB.
                RenderOrigin = new Vector4(renderOrigin, _depthSampleCount),
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
                NormalIndex1 = _normalBindlessIndices[0],
                // Oblivion repurposes y as the separately-authored TNAM DetailMap. Other variants
                // retain y as the second ordered normal source (Skyrim/Creation water).
                NormalIndex2 = _waterProfile.ShaderVariant == WaterShaderVariant.OblivionWater000
                    ? _oblivionDetailBindlessIndex
                    : _normalBindlessIndices[1],
                NormalIndex3 = _normalBindlessIndices[2],
                // 1 = NoiseIndex already contains the recovered FO3/FNV two-pass composite.
                // 0 = sample authored layers directly (Skyrim/modern/fallback paths).
                NormalIndex4 = usedFnvNoisePrepass ? 1u : 0u,
                LegacySurface0 = new Vector4(
                    surface.WaveAmplitude, surface.WaveFrequency,
                    surface.ScrollXSpeed, surface.ScrollYSpeed),
                LegacySurface1 = new Vector4(
                    surface.FogNear, surface.FogFar, surface.TextureBlend, surface.WindVelocity),
                Modern = new ModernWaterFrameUniforms
                {
                    BodyIndex = modernResources?.BindlessIndices[ModernWaterResources12.BodyOutput] ?? NoNormalMap,
                    NormalIndex = modernResources?.BindlessIndices[ModernWaterResources12.NormalOutput] ?? NoNormalMap,
                    GlossIndex = modernResources?.BindlessIndices[ModernWaterResources12.GlossOutput] ?? NoNormalMap,
                    DepthLutIndex = modernResources?.BindlessIndices[ModernWaterResources12.DepthLutOutput] ?? NoNormalMap,
                    TechniqueId = (uint)modernTechnique,
                    CubeIndex = _modernCubeBindlessIndex,
                    MaxPointLights = ModernWaterPipeline.MaxPointLights,
                    // Unit gloss samples in the neutral t2 map reproduce these authored values
                    // through the recovered exp()/pi equations. Output alpha and alpha-test
                    // threshold are neutral because their SetupMaterial mappings remain open.
                    Params = new Vector4(
                        ModernWaterPipeline.GlossPowerScale(surface.SunPower),
                        ModernWaterPipeline.GlossAmplitudeScale(surface.SunSpecularMagnitude),
                        1f,
                        0f),
                    LightSilt = ColorToVector4(_appearance?.LightSilt, Vector3.Zero) with
                    {
                        W = surface.NormalMagnitude,
                    },
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
        // DEPTH_READ | PIXEL_SHADER_RESOURCE, so use the no-depth-test PSO (occlusion is done in-shader).
        // Otherwise the DSV is still bound → use the hardware-depth-test PSO.
        var depthSample = _depthBindlessIndex != NoNormalMap;
        LastStats.WaterTechnique = DescribeTechnique(
            _game,
            _waterProfile.ShaderVariant,
            useModernPipeline,
            modernTechnique,
            depthSample,
            _depthSampleCount);
        if (useModernPipeline)
        {
            cmd.SetPipelineState(depthSample ? modernResources!.PixelDepthSample : modernResources!.Pixel);
        }
        else
        {
            cmd.SetPipelineState(_waterProfile.ShaderVariant switch
            {
                WaterShaderVariant.OblivionWater000 => depthSample ? _psoOblivionDepthSample : _psoOblivion,
                WaterShaderVariant.Fo4Water => depthSample ? _psoFo4DepthSample : _psoFo4,
                WaterShaderVariant.MorrowindWater => depthSample ? _psoMorrowindDepthSample : _psoMorrowind,
                _ => depthSample ? _psoDepthSample : _pso,
            });
        }
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

    private static string DescribeTechnique(
        BethesdaGame game,
        WaterShaderVariant shaderVariant,
        bool modernPipeline,
        ModernWaterTechnique modernTechnique,
        bool sceneDepth,
        int sampleCount)
    {
        string shaderName;
        if (modernPipeline)
        {
            shaderName = $"modern-{modernTechnique}";
        }
        else if (shaderVariant == WaterShaderVariant.FnvWater000)
        {
            shaderName = game is BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas
                ? "FnvWater003RtFree"
                : $"{game}-ClassicRtFreeWaterStandIn";
        }
        else
        {
            shaderName = shaderVariant.ToString();
        }
        string depthName;
        if (sceneDepth)
        {
            depthName = sampleCount > 1
                ? $"scene-depth-msaa{sampleCount}x"
                : "scene-depth-1x";
        }
        else if (!modernPipeline &&
                 shaderVariant is WaterShaderVariant.FnvWater000 or WaterShaderVariant.Fo4Water)
        {
            depthName = "hardware-depth-view-angle-fallback";
        }
        else
        {
            depthName = "hardware-depth-no-scene-depth";
        }
        return $"{shaderName}-{depthName}";
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
        _fnvNoiseScrollBlendPso.Dispose();
        _fnvNoiseNormalPso.Dispose();
        _deletionQueue.EnqueueDispose(_fnvNoiseBlendTexture);
        _deletionQueue.EnqueueDispose(_fnvNoiseNormalTexture);
        _deletionQueue.EnqueueDispose(new PersistentSlotReturn(_cbvSrvUavHeap, _fnvNoiseBlendBindlessIndex));
        _deletionQueue.EnqueueDispose(new PersistentSlotReturn(_cbvSrvUavHeap, _fnvNoiseNormalBindlessIndex));
        _modernWater?.Dispose(_deletionQueue, _cbvSrvUavHeap);
        _modernWater = null;
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

    private static void ValidateGpuLayouts()
    {
        // One scalar register (indices/scale/padding) plus five float4 registers. The earlier
        // 5-register assertion omitted uAmplitude and disabled the entire D3D12 backend at startup
        // even though the CPU struct and HLSL both correctly occupy 96 bytes.
        const int expectedFnvNoiseUniformSize = 6 * 16;
        var fnvNoiseUniformSize = Marshal.SizeOf<FnvNoiseUniforms>();
        if (fnvNoiseUniformSize != expectedFnvNoiseUniformSize)
        {
            throw new InvalidOperationException(
                $"FNV water-noise constant-buffer layout drifted: CPU={fnvNoiseUniformSize} bytes, " +
                $"HLSL={expectedFnvNoiseUniformSize} bytes.");
        }

        var modernPrepassSize = Marshal.SizeOf<ModernWaterPrepassUniforms>();
        if (modernPrepassSize != (int)ModernWaterPipeline.PrepassUniformByteSize)
        {
            throw new InvalidOperationException(
                $"Modern water-prepass constant-buffer layout drifted: CPU={modernPrepassSize} bytes, " +
                $"HLSL={ModernWaterPipeline.PrepassUniformByteSize} bytes.");
        }

        const int expectedModernFrameTailSize = 4 * 16;
        var modernFrameTailSize = Marshal.SizeOf<ModernWaterFrameUniforms>();
        if (modernFrameTailSize != expectedModernFrameTailSize)
        {
            throw new InvalidOperationException(
                $"Modern water-frame tail layout drifted: CPU={modernFrameTailSize} bytes, " +
                $"HLSL={expectedModernFrameTailSize} bytes.");
        }

        var uniformSize = Marshal.SizeOf<WaterFrameUniforms>();
        if (uniformSize != (int)UniformsByteSize)
        {
            throw new InvalidOperationException(
                $"Water constant-buffer layout drifted: CPU={uniformSize} bytes, HLSL={UniformsByteSize} bytes.");
        }

        const int expectedInstanceSize = 6 * 16;
        var instanceSize = Marshal.SizeOf<WaterInstance>();
        if (instanceSize != expectedInstanceSize)
        {
            throw new InvalidOperationException(
                $"Water instance layout drifted: CPU={instanceSize} bytes, HLSL={expectedInstanceSize} bytes.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FnvNoiseUniforms
    {
        public uint SourceIndex;
        public uint BlendIndex;
        public float NoiseScale;
        public float Padding0;
        public Vector4 TexScaleAmplitude;
        public Vector4 Scroll0;
        public Vector4 Scroll1;
        public Vector4 Scroll2;
        public Vector4 Amplitude;
    }

    private sealed class ModernWaterResources12
    {
        internal const int BodyOutput = 0;
        internal const int NormalOutput = 1;
        internal const int GlossOutput = 2;
        internal const int DepthLutOutput = 3;

        private ModernWaterResources12(
            ID3D12PipelineState pixel,
            ID3D12PipelineState pixelDepthSample,
            ID3D12PipelineState[] computePipelines,
            ID3D12Resource[] outputs,
            uint[] bindlessIndices)
        {
            Pixel = pixel;
            PixelDepthSample = pixelDepthSample;
            ComputePipelines = computePipelines;
            Outputs = outputs;
            BindlessIndices = bindlessIndices;
        }

        internal ID3D12PipelineState Pixel { get; }
        internal ID3D12PipelineState PixelDepthSample { get; }
        internal ID3D12PipelineState[] ComputePipelines { get; }
        internal ID3D12Resource[] Outputs { get; }
        internal uint[] BindlessIndices { get; }

        internal static ModernWaterResources12 Create(
            GpuDevice12 gpu,
            ID3D12RootSignature rootSignature,
            GpuDescriptorHeapAllocator12 heap,
            GraphicsPipelineStateDescription depthTemplate,
            GraphicsPipelineStateDescription depthSampleTemplate)
        {
            ID3D12PipelineState? pixel = null;
            ID3D12PipelineState? pixelDepthSample = null;
            var compute = new List<ID3D12PipelineState>(4);
            var outputs = new List<ID3D12Resource>(4);
            var slots = new List<uint>(4);
            try
            {
                var modernPixelBytecode = CompileEmbeddedShader(
                    "water.frag.hlsl",
                    "main",
                    "ps_5_1",
                    new ShaderMacro("FO4_WATER", "1"),
                    new ShaderMacro("FO4_WATER_ARCHITECTURAL", "1"));
                var pixelDescription = depthTemplate;
                pixelDescription.PixelShader = modernPixelBytecode;
                pixel = gpu.Device.CreateGraphicsPipelineState(pixelDescription);
                var pixelDepthDescription = depthSampleTemplate;
                pixelDepthDescription.PixelShader = modernPixelBytecode;
                pixelDepthSample = gpu.Device.CreateGraphicsPipelineState(pixelDepthDescription);

                string[] entryPoints =
                [
                    "mainBodyCoverage",
                    "mainNormal",
                    "mainGloss",
                    "mainDepthLut",
                ];
                foreach (var entryPoint in entryPoints)
                {
                    var bytecode = CompileEmbeddedShader(
                        "water_modern.comp.hlsl", entryPoint, "cs_5_1");
                    compute.Add(gpu.Device.CreateComputePipelineState(
                        new ComputePipelineStateDescription
                        {
                            RootSignature = rootSignature,
                            ComputeShader = bytecode,
                        }));
                }

                for (var i = 0; i < entryPoints.Length; i++)
                {
                    var width = i == DepthLutOutput
                        ? ModernWaterPipeline.DepthLutWidth
                        : ModernWaterPipeline.SurfaceDimension;
                    var height = i == DepthLutOutput
                        ? ModernWaterPipeline.DepthLutHeight
                        : ModernWaterPipeline.SurfaceDimension;
                    var output = gpu.Device.CreateCommittedResource<ID3D12Resource>(
                        new HeapProperties(HeapType.Default),
                        HeapFlags.None,
                        ResourceDescription.Texture2D(
                            Format.R16G16B16A16_Float,
                            width,
                            height,
                            1,
                            1,
                            1,
                            0,
                            ResourceFlags.AllowUnorderedAccess),
                        ModernWaterPipeline.DynamicReadState);
                    output.Name = i switch
                    {
                        BodyOutput => "Modern Water Body+Coverage",
                        NormalOutput => "Modern Water Composite Normal",
                        GlossOutput => "Modern Water Gloss+Flow",
                        _ => "Modern Water Depth LUT",
                    };
                    outputs.Add(output);

                    var allocation = heap.AllocatePersistent();
                    slots.Add(allocation.BindlessIndex);
                    gpu.Device.CreateShaderResourceView(
                        output,
                        new ShaderResourceViewDescription
                        {
                            Format = Format.R16G16B16A16_Float,
                            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                            Shader4ComponentMapping = ShaderComponentMapping.Default,
                            Texture2D = new Texture2DShaderResourceView
                            {
                                MostDetailedMip = 0,
                                MipLevels = 1,
                            },
                        },
                        allocation.Cpu);
                }

                return new ModernWaterResources12(
                    pixel,
                    pixelDepthSample,
                    compute.ToArray(),
                    outputs.ToArray(),
                    slots.ToArray());
            }
            catch
            {
                pixel?.Dispose();
                pixelDepthSample?.Dispose();
                foreach (var pipeline in compute) pipeline.Dispose();
                foreach (var output in outputs) output.Dispose();
                foreach (var slot in slots) heap.FreePersistent(slot);
                throw;
            }
        }

        internal void Dispose(
            GpuDeletionQueue12 deletionQueue,
            GpuDescriptorHeapAllocator12 heap)
        {
            Pixel.Dispose();
            PixelDepthSample.Dispose();
            foreach (var pipeline in ComputePipelines) pipeline.Dispose();
            foreach (var output in Outputs) deletionQueue.EnqueueDispose(output);
            foreach (var slot in BindlessIndices)
            {
                deletionQueue.EnqueueDispose(new PersistentSlotReturn(heap, slot));
            }
        }
    }

    /// <summary>Returns a bindless descriptor only after the GPU has drained the prepass draws that
    /// may still index it.</summary>
    private sealed class PersistentSlotReturn(GpuDescriptorHeapAllocator12 heap, uint slot) : IDisposable
    {
        public void Dispose() => heap.FreePersistent(slot);
    }

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
        // xyz = camera-relative render origin subtracted in the VS before projection (0 on absolute paths);
        // w = scene-depth sample count (1 selects Texture2D, >1 selects Texture2DMS in the PS).
        public Vector4 RenderOrigin;
        // FO4_WATER-only constants (appended so every other variant's offsets are untouched):
        // Fo4Spec = (Sun Specular Magnitude, Silt Amount, Shallow Alpha, Deep Alpha);
        // Fo4Ranges = (Color Shallow/Deep Range, Alpha Shallow/Deep Range) in world units;
        // Fo4DarkSilt = DNAM silt Dark Color (the FO4 PS's unshadowed ambient add).
        public Vector4 Fo4Spec;
        public Vector4 Fo4Ranges;
        public Vector4 Fo4DarkSilt;
        // Ordered water-normal sources. Skyrim authors three inputs; FNV/FO3 repeat the first index
        // into all three slots. Oblivion uses NormalIndex1 for the selected global animation frame and
        // repurposes NormalIndex2 for the distinct WATR TNAM DetailMap.
        public uint NormalIndex1;
        public uint NormalIndex2;
        public uint NormalIndex3;
        public uint NormalIndex4;
        // Oblivion WATR DATA: wave amplitude/frequency + direct XY scroll, then WATR fog near/far,
        // texture-detail blend, and wind velocity. Kept at the tail so earlier variants retain offsets.
        public Vector4 LegacySurface0;
        public Vector4 LegacySurface1;
        // WATER-07 opt-in FO4/FO76 architecture. Four generated Texture2D indices, recovered
        // technique/cubemap/light-loop controls, recovered gloss scales + neutral unresolved alpha
        // constants, then retained LightSilt/normal magnitude. Appended for legacy layout stability.
        public ModernWaterFrameUniforms Modern;
    }
}

#endif
