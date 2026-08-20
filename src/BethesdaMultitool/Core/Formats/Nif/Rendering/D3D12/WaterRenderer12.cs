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
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using BethesdaMultitool.Core.Games;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D12 = Vortice.Direct3D12;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

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
internal sealed class WaterRenderer12 : Abstractions.IWaterRenderer,
    BethesdaMultitool.Core.Formats.Nif.Rendering.Water.IWaterHeightProbe
{
    private static readonly Logger Log = Logger.Instance;

    // viewProj(64) + 3 DNAM colors(48) + camPosTime(16) + noiseParams(16) + surface0/1(32)
    // + 3 layers(48) + depthParams(16) + renderOrigin(16) + 3 FO4 float4s(48)
    // + ordered normal indices(16) + 2 Oblivion DATA registers(32)
    // + 4 opt-in modern-water registers(64) = the established 416-byte / 26-register prefix.
    // WATER001 appends snapshot/plane + recovered refraction registers (32) without moving any
    // existing offset: 448 bytes / 28 registers.
    private const uint UniformsByteSize = ModernWaterPipeline.FrameUniformByteSize;
    private const uint FnvWater001UniformByteSize = 2 * 16;
    // Planar sky-reflection register (uWaterReflection), appended after the WATER001 tail, plus
    // the mirror pass's view-projection matrix + scene-content flag register (uReflectionViewProj
    // + uReflectionParams) for the Oblivion WATER007 projective arm.
    private const uint WaterReflectionUniformByteSize = 1 * 16 + 64 + 16;
    private const uint WaterFrameUniformsByteSize =
        UniformsByteSize + FnvWater001UniformByteSize + WaterReflectionUniformByteSize;
    private const uint FnvNoiseDimension = 256;

    // Full chain for the 256² noise-NORMAL tile (256..1 = 9 levels). The authored NNAM the retail
    // shader samples ships with mips; a mipless regenerated tile aliases into per-frame glint
    // shimmer at grazing views (the fixed-pose water flicker). The scroll+blend tile stays
    // single-mip — only the matched-resolution Sobel pass ever reads it.
    private const int FnvNoiseMipCount = 9;

    /// <summary>Mip chain on the noise-normal prepass target. "0" restores the mipless tile —
    /// the A/B lever for the fixed-pose water flicker.</summary>
    private static readonly bool FnvNoiseMipsEnabled =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.WaterNoiseMips) != "0";

    /// <summary>The engine's hardcoded terminal default water: TESDataHandler::GenerateDefaultObjects
    /// resolves WATR FormID 0x18 ("DefaultWater") and every GetWaterType chain ends there when XCWT
    /// and NAM2 both yield nothing.</summary>
    private const uint EngineDefaultWaterFormId = 0x18;

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
    // _pso: depth-test variant (writable DSV bound) used when no scene-depth SRV is available.
    // _psoDepthSample: same hardware GreaterEqual test against the host's READ-ONLY DSV while the
    // scene depth is simultaneously the shader's SRV (depth fade, FNV WATER000 path); its pixel
    // shader is the WATER_HARDWARE_OCCLUSION compile (manual occlusion clip dropped).
    // The Oblivion pair compiles water_oblivion.frag.hlsl (view-angle body + single sun
    // specular — see WaterShaderVariant.OblivionWater000), and the FO4 pair water_fo4.frag.hlsl
    // (the disassembled BSWaterShader math — see WaterShaderVariant.Fo4Water); both selected by
    // the profile (WaterProfile.PixelShaderFile) at draw time. _psoFlat is the odd one out: a
    // single PSO drawing water_flat.frag.hlsl for games with no recovered shader at all.
    private readonly ID3D12PipelineState _pso;
    private readonly ID3D12PipelineState _psoDepthSample;
    private readonly ID3D12PipelineState _psoFnvWater001DepthSample;
    private readonly ID3D12PipelineState _psoOblivion;
    private readonly ID3D12PipelineState _psoOblivionDepthSample;
    private readonly ID3D12PipelineState _psoFo4;
    private readonly ID3D12PipelineState _psoFo4DepthSample;
    private readonly ID3D12PipelineState _psoMorrowind;
    private readonly ID3D12PipelineState _psoMorrowindDepthSample;
    // The flat stand-in has NO depth-sample twin, unlike every pair above: it reads no scene depth,
    // so it has no occlusion clip to compile out and the two compiles would be byte-identical. One
    // PSO serves both host depth states — the depth-stencil state is the same in either template.
    private readonly ID3D12PipelineState _psoFlat;
    private readonly GraphicsPipelineStateDescription _depthPsoTemplate;
    private readonly GraphicsPipelineStateDescription _depthSamplePsoTemplate;

    // WATER-07 architectural replacement. These resources and shader permutations are created only
    // after an explicit opt-in on FO4/FO76. Any initialization or transient-prepass failure selects
    // the established _psoFo4 path for that frame.
    private ModernWaterResources12? _modernWater;
    private bool _modernWaterInitializationFailed;
    private uint _modernCubeBindlessIndex = NoNormalMap;
    private BethesdaGame _game = GameProfiles.DefaultGame;

    // WATER-08: FO3/FNV do not sample three procedural octaves in WATER000. The engine first
    // renders ISNOISESCROLLANDBLEND into a 256x256 scalar tile, then runs
    // ISNOISENORMALMAP over it and binds that finished normal tile to WATER000.
    private readonly ID3D12PipelineState _fnvNoiseScrollBlendPso;
    private readonly ID3D12PipelineState _fnvNoiseNormalPso;
    // Box-average + renormalize downsampler that fills the normal tile's mip chain after the
    // Sobel pass (see FnvNoiseMipCount). One single-mip SRV per SOURCE level feeds each step.
    private readonly ID3D12PipelineState _fnvNoiseDownsamplePso;

    /// <summary>
    ///     One noise tile set PER VISIBLE WATR MATERIAL. A single shared tile forced the prepass to
    ///     re-record before every run the depth sort produced — measured 349 prepasses (each 10
    ///     dispatches + 9 constant buffers + 10 descriptors) for SIX materials at Lake Mead, because
    ///     the materials interleave by depth and each run evicted the last one's normals. Worse, it
    ///     made the tile's contents an ORDERING invariant: correctness depended on every draw being
    ///     immediately preceded by its own prepass, so any run that failed to re-record silently
    ///     sampled another material's normals. Per-material tiles make the binding a stable fact for
    ///     the whole frame — one prepass each, recorded up front, and the uniforms CB can then cache
    ///     the resolved index without that coupling.
    /// </summary>
    private const int FnvNoiseMaterialSlots = 8;
    private readonly FnvNoiseTileSet[] _fnvNoiseTiles;
    private bool _useFnvNoisePrepass;

    // Legacy water animation: bindless indices of textures\water\water00-31.dds, resolved by the
    // host at load. Morrowind samples the selected frame as diffuse; Oblivion WATER000 samples it
    // as its global animated normal. WATR TNAM is a separate per-water detail input.
    private uint[]? _legacyAnimatedFrames;

    private readonly List<global::BethesdaMultitool.Core.WorldData.WorldWaterCell> _waterCells = new();
    private readonly List<global::BethesdaMultitool.Core.WorldData.WorldWaterCell> _visibleWaterScratch = new();
    // Point-query index over _waterCells; see RebuildWaterHeightLookup / TryGetWaterHeightAt.
    private readonly Dictionary<(int gx, int gy), float> _waterHeightByGrid = new();
    private readonly List<global::BethesdaMultitool.Core.WorldData.WorldWaterCell> _irregularWaterCells = new();
    // Placed-NIF water geometry (cave/pool/reflecting-pool water embedded in REFR meshes). Owned by
    // ReferenceRenderer12 (which retains ownership while their meshes stream in) and handed here once
    // per frame via SetNifWaterPlanes. Its stable publication dynamically removes hidden owners; this
    // renderer culls the remaining surfaces into _visibleNifScratch and draws them like cell water.
    private IReadOnlyList<NifWaterGeometry> _nifWaterPlanes = Array.Empty<NifWaterGeometry>();
    private readonly List<NifWaterGeometry> _visibleNifScratch = new();
    private readonly List<FnvNifWaterDrawBatch> _fnvNifDrawBatches = new();
    // Per-frame (cell, effective WATR, view depth) triples so the unstable cell sort compares
    // precomputed fields instead of re-deriving identity/depth per comparison.
    private readonly List<(global::BethesdaMultitool.Core.WorldData.WorldWaterCell Cell, uint FormId, float Depth)>
        _fnvWaterSortScratch = new();
    private float? _worldspaceDefaultWaterHeight;
    private BethesdaMultitool.Core.Formats.Esm.Models.Records.World.WaterAppearance? _appearance;
    private uint _noiseBindlessIndex = NoNormalMap;
    // Planar sky-reflection target (see SetWaterReflection). Persists across frames; the host
    // re-supplies it each frame and passes null when the feature is off or the pass failed.
    private uint _reflectionBindlessIndex = NoNormalMap;
    private uint _reflectionSceneWidth;
    private uint _reflectionSceneHeight;

    /// <summary>UV offset per unit of ripple-normal XY applied to the reflection lookup — what makes
    /// waves visible in the reflection. Small: the reflection must stay recognisably the sky.</summary>
    private const float ReflectionDistortionScale = 0.04f;
    private readonly uint[] _normalBindlessIndices = [NoNormalMap, NoNormalMap, NoNormalMap];
    // FNV can author a different XCWT on each generated CELL.  The old camera-cell-global
    // material binding made every visible tile change color and scroll direction when the camera
    // crossed a CELL boundary.  Keep the resolved material/texture binding for every retained WATR
    // so visible generated water can be emitted in per-WATR draw batches.  Other games retain the
    // established single-material path.
    private readonly Dictionary<uint, FnvWaterMaterialBinding> _fnvWaterMaterials = new();
    private readonly List<FnvWaterCellDrawBatch> _fnvWaterCellDrawBatches = new();
    private readonly HashSet<uint> _fnvVisibleWaterTypeScratch = new();
    private uint _oblivionDetailBindlessIndex = NoNormalMap;
    private string[] _telemetryMapPaths = [];
    private string[] _telemetryMapRoles = [];
    private bool[] _telemetryMapResolved = [];
    private uint _depthBindlessIndex = NoNormalMap; // scene depth SRV; NoNormalMap => proxy + depth-test PSO
    private float _depthNear = 16f;
    private float _depthFar = 1f;
    private int _depthSampleCount = 1;
    private uint? _fnvWater001SelectedWaterFormId;
    private uint? _fnvWater001WorldspaceDefaultWaterFormId;
    private bool _fnvWater001HasWaterTypeContext;
    private FnvWater001Preflight _fnvWater001PendingPreflight =
        FnvWater001Preflight.Fallback(FnvWater001FallbackReason.SnapshotUnavailable);
    private FnvWater001SnapshotDescriptor _fnvWater001Snapshot;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private global::BethesdaMultitool.Core.WorldData.WorldSpatialIndex? _spatialIndex;

    // Persistent-mapped UPLOAD-heap structured buffer, FRAME-SLOTTED: FramesInFlight slots of
    // _instanceCapacity packets each, written and SRV-windowed at the recorder's FrameIndex. A
    // single-slot buffer violated the recorder contract (frame-keyed resources MUST index off
    // FrameIndex): the CPU memcpy overwrote packets the previous frame's in-flight water draws
    // could still be reading — invisible while the bytes were identical, torn quads for one frame
    // whenever streaming shifted the packet array. Resized when the visible water cell count
    // exceeds capacity; stays mapped for its lifetime — UPLOAD-heap resources can.
    private ID3D12Resource? _instanceBuffer;
    private IntPtr _instanceMapped;
    private int _instanceCapacity;
    private WaterInstance[] _instanceScratch = [];
    private bool _disposed;

    private readonly record struct FnvWater001SnapshotDescriptor(uint BindlessIndex, uint Width, uint Height)
    {
        internal bool IsValid => BindlessIndex != NoNormalMap && Width > 0 && Height > 0;
    }

    internal readonly record struct FnvWaterMaterialBinding(
        BethesdaMultitool.Core.Formats.Esm.Models.Records.World.WaterAppearance? Appearance,
        IReadOnlyList<uint?>? NormalMapBindlessIndices);

    private readonly record struct ResolvedFnvWaterMaterial(
        BethesdaMultitool.Core.Formats.Esm.Models.Records.World.WaterAppearance? Appearance,
        uint NoiseIndex,
        uint NormalIndex1,
        uint NormalIndex2,
        uint NormalIndex3,
        // The WATR this material resolved from. Carried so a draw can amortize per-MATERIAL work
        // (the noise prepass and the frame uniforms CB) across the many runs the transparency
        // stream splits one material into — see _frameNoiseSlots / _frameMaterialCb.
        uint WaterFormId = 0u);

    private readonly record struct FnvWaterCellDrawBatch(
        uint WaterFormId,
        int StartInstance,
        int InstanceCount,
        ResolvedFnvWaterMaterial Material,
        float FarthestDepth);

    /// <summary>One contiguous per-WATR run of placed-NIF packets. Offsets are relative to the
    /// first NIF packet (the draw site adds <c>cellVisible</c>).</summary>
    private readonly record struct FnvNifWaterDrawBatch(
        uint WaterFormId,
        int StartPacket,
        int PacketCount,
        float FarthestDepth);

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
        var noiseDownsampleBytecode = CompileEmbeddedShader(
            "water_noise.comp.hlsl", "mainDownsample", "cs_5_1");
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
        _fnvNoiseDownsamplePso = gpu.Device.CreateComputePipelineState(
            new ComputePipelineStateDescription
            {
                RootSignature = rootSignature.RootSignature,
                ComputeShader = noiseDownsampleBytecode,
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
        // The normal tile carries the full chain (see FnvNoiseMipCount); with the switch off it
        // still allocates the chain but the SRVs below expose one level and the downsample never
        // runs — byte-identical to the previous single-mip behaviour.
        var noiseNormalTextureDescription = ResourceDescription.Texture2D(
            Format.R8G8B8A8_UNorm,
            FnvNoiseDimension,
            FnvNoiseDimension,
            1,
            FnvNoiseMipCount,
            1,
            0,
            ResourceFlags.AllowUnorderedAccess);
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
        // The main normal-tile SRV exposes the whole chain so the anisotropic water sampler
        // (s0, MaxLOD unbounded) starts mip-filtering the moment the levels hold data.
        var normalSrvDescription = noiseSrvDescription with
        {
            Texture2D = new Texture2DShaderResourceView
            {
                MostDetailedMip = 0,
                MipLevels = FnvNoiseMipsEnabled ? (uint)FnvNoiseMipCount : 1,
            },
        };

        _fnvNoiseTiles = new FnvNoiseTileSet[FnvNoiseMaterialSlots];
        for (var slot = 0; slot < FnvNoiseMaterialSlots; slot++)
        {
            var blendTexture = gpu.Device.CreateCommittedResource<ID3D12Resource>(
                new HeapProperties(HeapType.Default),
                HeapFlags.None,
                noiseTextureDescription,
                ResourceStates.NonPixelShaderResource);
            blendTexture.Name = $"FNV Water Noise Scroll+Blend {slot}";
            var normalTexture = gpu.Device.CreateCommittedResource<ID3D12Resource>(
                new HeapProperties(HeapType.Default),
                HeapFlags.None,
                noiseNormalTextureDescription,
                ResourceStates.PixelShaderResource);
            normalTexture.Name = $"FNV Water Noise Normal {slot}";

            var blendSrv = cbvSrvUavHeap.AllocatePersistent();
            gpu.Device.CreateShaderResourceView(blendTexture, noiseSrvDescription, blendSrv.Cpu);
            var normalSrv = cbvSrvUavHeap.AllocatePersistent();
            gpu.Device.CreateShaderResourceView(normalTexture, normalSrvDescription, normalSrv.Cpu);
            // One single-mip SRV per SOURCE level (0..N-2) feeds each downsample step; persistent
            // because the texture is.
            var mipSrvIndices = new uint[FnvNoiseMipCount - 1];
            for (var mip = 0; mip < FnvNoiseMipCount - 1; mip++)
            {
                var mipSrv = cbvSrvUavHeap.AllocatePersistent();
                mipSrvIndices[mip] = mipSrv.BindlessIndex;
                gpu.Device.CreateShaderResourceView(
                    normalTexture,
                    noiseSrvDescription with
                    {
                        Texture2D = new Texture2DShaderResourceView
                        {
                            MostDetailedMip = (uint)mip,
                            MipLevels = 1,
                        },
                    },
                    mipSrv.Cpu);
            }

            _fnvNoiseTiles[slot] = new FnvNoiseTileSet(
                blendTexture, normalTexture, blendSrv.BindlessIndex, normalSrv.BindlessIndex,
                mipSrvIndices);
        }

        var vsBytecode = CompileEmbeddedShader("water.vert.hlsl", "main", "vs_5_1");
        // Per-game water is a per-FILE axis (WaterProfile.PixelShaderFile); the only remaining
        // preprocessor axes are technique macros (WATER_HARDWARE_OCCLUSION below,
        // FO4_WATER_ARCHITECTURAL in ModernWaterResources12). Every variant file is compiled
        // eagerly here because the loaded game can change per LoadData without rebuilding PSOs.
        var psBytecode = CompileEmbeddedShader("water_fnv.frag.hlsl", "main", "ps_5_1");

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
            // Water blends its COLOR translucently (above) but must PRESERVE the destination alpha.
            // Was DestinationBlendAlpha=Zero, which OVERWROTE the underlying opaque 1.0 with water's
            // <1 alpha and turned the water surface — and any geometry it overlaps (partially-submerged
            // rocks) — transparent. HISTORICAL NOTE: that symptom was a downstream effect of the
            // composition swapchain being created PREMULTIPLIED, so scene-RT alpha reached the
            // compositor; the swapchain is now AlphaMode.Ignore (GpuSwapChainSurface12) and the live
            // view no longer composites scene alpha at all. Keep this Max-against-1.0 anyway: it is
            // still the correct alpha for the offscreen export/capture readback, which DOES consume
            // the alpha channel, and it keeps this pass consistent with the reference renderer.
            SourceBlendAlpha = D12.Blend.One,
            DestinationBlendAlpha = D12.Blend.One,
            BlendOperationAlpha = D12.BlendOperation.Max,
            RenderTargetWriteMask = D12.ColorWriteEnable.All,
        };

        var psOblivionBytecode = CompileEmbeddedShader(
            "water_oblivion.frag.hlsl", "main", "ps_5_1");
        var psFo4Bytecode = CompileEmbeddedShader(
            "water_fo4.frag.hlsl", "main", "ps_5_1");
        var psMorrowindBytecode = CompileEmbeddedShader(
            "water_morrowind.frag.hlsl", "main", "ps_5_1");
        // Un-recovered games (WaterProfile.Flat). Compiled once — see _psoFlat.
        var psFlatBytecode = CompileEmbeddedShader(
            "water_flat.frag.hlsl", "main", "ps_5_1");
        var psFnvWater001Bytecode = CompileEmbeddedShader(
            "water_fnv001.frag.hlsl", "main", "ps_5_1",
            new ShaderMacro("WATER_HARDWARE_OCCLUSION", "1"));

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
        psoDesc.PixelShader = psFlatBytecode;
        _psoFlat = gpu.Device.CreateGraphicsPipelineState(psoDesc);
        psoDesc.PixelShader = psBytecode;

        // Depth-sample variant: the scene depth buffer is bound BOTH as an SRV (depth-fade /
        // column math) and as a READ-ONLY DSV, so the same GreaterEqual hardware test as _pso
        // still rejects water behind opaque geometry — per sample, which antialiases water edges
        // at MSAA'd mesh silhouettes. The WATER_HARDWARE_OCCLUSION shader variants drop their
        // pixel-rate occlusion clip (binary keep/kill there left bright fringes around meshes in
        // front of water). Hosts must bind the read-only DSV while depth sits in
        // DepthRead | PixelShaderResource; depth state and formats intentionally match _pso.
        var psDepthSampleBytecode = CompileEmbeddedShader(
            "water_fnv.frag.hlsl", "main", "ps_5_1",
            new ShaderMacro("WATER_HARDWARE_OCCLUSION", "1"));
        var psOblivionDepthSampleBytecode = CompileEmbeddedShader(
            "water_oblivion.frag.hlsl", "main", "ps_5_1",
            new ShaderMacro("WATER_HARDWARE_OCCLUSION", "1"));
        var psFo4DepthSampleBytecode = CompileEmbeddedShader(
            "water_fo4.frag.hlsl", "main", "ps_5_1",
            new ShaderMacro("WATER_HARDWARE_OCCLUSION", "1"));
        var psMorrowindDepthSampleBytecode = CompileEmbeddedShader(
            "water_morrowind.frag.hlsl", "main", "ps_5_1",
            new ShaderMacro("WATER_HARDWARE_OCCLUSION", "1"));
        psoDesc.PixelShader = psDepthSampleBytecode;
        _depthSamplePsoTemplate = psoDesc;
        _psoDepthSample = gpu.Device.CreateGraphicsPipelineState(psoDesc);
        // WATER001 always consumes both scene depth and a separate single-sample opaque-scene
        // snapshot; like every depth-sample PSO it keeps the hardware GreaterEqual test through
        // the host's read-only DSV (its manual occlusion clip is compiled out above).
        psoDesc.PixelShader = psFnvWater001Bytecode;
        _psoFnvWater001DepthSample = gpu.Device.CreateGraphicsPipelineState(psoDesc);
        psoDesc.PixelShader = psOblivionDepthSampleBytecode;
        _psoOblivionDepthSample = gpu.Device.CreateGraphicsPipelineState(psoDesc);
        psoDesc.PixelShader = psFo4DepthSampleBytecode;
        _psoFo4DepthSample = gpu.Device.CreateGraphicsPipelineState(psoDesc);
        psoDesc.PixelShader = psMorrowindDepthSampleBytecode;
        _psoMorrowindDepthSample = gpu.Device.CreateGraphicsPipelineState(psoDesc);
    }

    public global::BethesdaMultitool.Core.WorldData.WorldRenderStats LastStats { get; } = new();
    public FnvWater001Preflight LastFnvWater001Decision { get; private set; } =
        FnvWater001Preflight.Fallback(FnvWater001FallbackReason.SnapshotUnavailable);
    public bool DetailedProfilingEnabled { get; set; }
    public bool ModernPipelineEnabled { get; set; }

    /// <summary>
    ///     True when the worldspace default water height was inherited from a WNAM parent (TES4 child
    ///     worldspaces): only cells flagging Has-Water then receive the default plane. Set alongside
    ///     <c>LoadData</c>; only consulted on the no-spatial-index fallback path (the spatial index
    ///     pre-resolves water with the same rule).
    /// </summary>
    public bool DefaultWaterRequiresCellHasWater { get; set; }

    public void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        float? worldspaceDefaultWaterHeight)
        => LoadData(cells, worldspaceDefaultWaterHeight, spatialIndex: null);

    public void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        float? worldspaceDefaultWaterHeight,
        global::BethesdaMultitool.Core.WorldData.WorldSpatialIndex? spatialIndex)
        => LoadData(cells, worldspaceDefaultWaterHeight, spatialIndex, appearance: null);

    public void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        float? worldspaceDefaultWaterHeight,
        global::BethesdaMultitool.Core.WorldData.WorldSpatialIndex? spatialIndex,
        BethesdaMultitool.Core.Formats.Esm.Models.Records.World.WaterAppearance? appearance)
        => LoadData(cells, worldspaceDefaultWaterHeight, spatialIndex, appearance, normalMapBindlessIndex: null);

    public void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        float? worldspaceDefaultWaterHeight,
        global::BethesdaMultitool.Core.WorldData.WorldSpatialIndex? spatialIndex,
        BethesdaMultitool.Core.Formats.Esm.Models.Records.World.WaterAppearance? appearance,
        uint? normalMapBindlessIndex)
        => LoadData(cells, worldspaceDefaultWaterHeight, spatialIndex, appearance,
            normalMapBindlessIndex is { } index ? new uint?[] { index } : null);

    public void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        float? worldspaceDefaultWaterHeight,
        global::BethesdaMultitool.Core.WorldData.WorldSpatialIndex? spatialIndex,
        BethesdaMultitool.Core.Formats.Esm.Models.Records.World.WaterAppearance? appearance,
        IReadOnlyList<uint?>? normalMapBindlessIndices)
    {
        _worldspaceDefaultWaterHeight = worldspaceDefaultWaterHeight;
        _spatialIndex = spatialIndex;
        SetAppearance(appearance, normalMapBindlessIndices);
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
                    _waterCells.Add(new global::BethesdaMultitool.Core.WorldData.WorldWaterCell(
                        key,
                        cell,
                        z,
                        new Vector2(key.gx * WorldGridConstants.CellSize, key.gy * WorldGridConstants.CellSize),
                        WorldGridConstants.CellSize));
                }
            }
        }

        RebuildWaterHeightLookup();
        EnsureInstanceCapacity(_waterCells.Count);
    }

    /// <summary>
    ///     Indexes the loaded water cells for O(1) point queries by world XY. Exterior water is one
    ///     cell-sized quad at a grid origin, so it keys directly by grid coordinate; interiors
    ///     (<c>WorldSpatialIndex.BuildInterior</c>) supply an arbitrary AABB-derived footprint and go
    ///     into a short linear list instead. Where two surfaces cover the same point, the HIGHER wins:
    ///     that is the one a camera above the water is looking through.
    /// </summary>
    private void RebuildWaterHeightLookup()
    {
        _waterHeightByGrid.Clear();
        _irregularWaterCells.Clear();
        foreach (var water in _waterCells)
        {
            if (!float.IsFinite(water.Height)) continue;

            var gridAligned =
                MathF.Abs(water.FootprintSize - WorldGridConstants.CellSize) < 0.5f &&
                MathF.Abs(water.OriginXY.X - water.Key.gx * WorldGridConstants.CellSize) < 0.5f &&
                MathF.Abs(water.OriginXY.Y - water.Key.gy * WorldGridConstants.CellSize) < 0.5f;
            if (gridAligned)
            {
                if (!_waterHeightByGrid.TryGetValue(water.Key, out var existing) || water.Height > existing)
                {
                    _waterHeightByGrid[water.Key] = water.Height;
                }
            }
            else
            {
                _irregularWaterCells.Add(water);
            }
        }
    }

    /// <summary>
    ///     The water surface height covering a world XY, or false where there is none.
    ///     <para>
    ///         This replaces an earlier single global plane taken as the MAXIMUM height over every
    ///         gathered water cell. That was unusable: the gather spans <c>renderDistance + 1</c> cells
    ///         with a Chebyshev test and no frustum or Z bound (~1225 cells at the default distance),
    ///         so one distant elevated body set the plane for the whole frame. Paired with a
    ///         camera-must-be-above-the-plane guard it disabled the below-water split entirely
    ///         wherever any visible water sat higher than the camera — measured at Lake Mead, the
    ///         local surface is 3000 while the global max was 5600, so a camera at Z≈3200 was treated
    ///         as submerged and every underwater decal composited on top of the water.
    ///     </para>
    /// </summary>
    public bool TryGetWaterHeightAt(float worldX, float worldY, out float height)
    {
        var key = (
            gx: (int)MathF.Floor(worldX / WorldGridConstants.CellSize),
            gy: (int)MathF.Floor(worldY / WorldGridConstants.CellSize));
        if (_waterHeightByGrid.TryGetValue(key, out height)) return true;

        height = float.NegativeInfinity;
        var found = false;
        foreach (var water in _irregularWaterCells)
        {
            if (worldX < water.OriginXY.X || worldX > water.OriginXY.X + water.FootprintSize ||
                worldY < water.OriginXY.Y || worldY > water.OriginXY.Y + water.FootprintSize)
            {
                continue;
            }

            height = MathF.Max(height, water.Height);
            found = true;
        }

        if (!found) height = 0f;
        return found;
    }

    /// <summary>
    ///     The mirror plane for the water-reflection pass: the exact surface height with the
    ///     LARGEST visible-cell coverage inside the cylinder (dominant plane), tie-broken toward
    ///     the surface nearest below the camera. False — callers keep sky-only reflection content —
    ///     when no cell water is visible or the camera is at/under every candidate surface (a
    ///     submerged camera has no above-water mirror). Exact float equality groups cells of one
    ///     authored body correctly (a lake's height repeats bit-identically); placed-NIF pools are
    ///     not counted and simply sample the dominant plane (documented v1 approximation).
    /// </summary>
    public bool TryGetDominantVisibleWaterPlaneHeight(
        in VisibilityCylinder cylinder, float cameraZ, out float height)
    {
        height = 0f;
        var radiusCells = cylinder.Radius / WorldGridConstants.CellSize;
        var camGx = cylinder.Position.X / WorldGridConstants.CellSize;
        var camGy = cylinder.Position.Y / WorldGridConstants.CellSize;
        Dictionary<float, int>? counts = null;
        foreach (var (key, cellHeight) in _waterHeightByGrid)
        {
            if (cellHeight >= cameraZ) continue; // mirror only exists above the surface
            var dx = key.gx + 0.5f - camGx;
            var dy = key.gy + 0.5f - camGy;
            if ((dx * dx) + (dy * dy) > radiusCells * radiusCells) continue;
            counts ??= new Dictionary<float, int>();
            counts[cellHeight] = counts.GetValueOrDefault(cellHeight) + 1;
        }

        foreach (var water in _irregularWaterCells)
        {
            if (water.Height >= cameraZ || !float.IsFinite(water.Height)) continue;
            var cx = water.OriginXY.X + (water.FootprintSize * 0.5f);
            var cy = water.OriginXY.Y + (water.FootprintSize * 0.5f);
            var dx = cx - cylinder.Position.X;
            var dy = cy - cylinder.Position.Y;
            if ((dx * dx) + (dy * dy) > cylinder.Radius * cylinder.Radius) continue;
            counts ??= new Dictionary<float, int>();
            counts[water.Height] = counts.GetValueOrDefault(water.Height) + 1;
        }

        if (counts is null) return false;

        var bestCount = -1;
        foreach (var (candidate, count) in counts)
        {
            // Dominant coverage wins; on a tie the surface NEAREST BELOW the camera (largest
            // height) is the one the player is standing beside.
            if (count > bestCount || (count == bestCount && candidate > height))
            {
                bestCount = count;
                height = candidate;
            }
        }

        return bestCount > 0;
    }

    /// <summary>
    ///     Rebinds only the WATR-derived material and its normal maps. Cell geometry, the spatial
    ///     index, and instance capacity remain untouched, so camera-cell XCWT changes do not rebuild
    ///     every visible water tile.
    /// </summary>
    public void SetAppearance(
        BethesdaMultitool.Core.Formats.Esm.Models.Records.World.WaterAppearance? appearance,
        IReadOnlyList<uint?>? normalMapBindlessIndices)
    {
        _appearance = appearance;
        // WATR identity is supplied separately because WaterAppearance intentionally carries only
        // decoded visual data. Clear it on every material rebind so WATER001 cannot reuse a stale
        // camera-cell XCWT/WRLD NAM2 decision.
        ClearFnvWater001TransientState(clearWaterTypeContext: true);
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
    }

    /// <summary>
    ///     Supplies the retained FNV WATR bindings used by generated CELL water.  A CELL's own XCWT
    ///     wins; a missing XCWT inherits the WRLD NAM2 supplied through
    ///     <see cref="SetFnvWater001WaterTypeContext"/>.  The catalog is copied so a worldspace reload
    ///     cannot mutate bindings while the GPU command list is being recorded.
    /// </summary>
    public void SetFnvWaterMaterialCatalog(
        IReadOnlyDictionary<uint, FnvWaterMaterialBinding>? materials)
    {
        _fnvWaterMaterials.Clear();
        if (materials is null) return;

        foreach (var (formId, material) in materials)
        {
            if (formId == 0) continue;
            _fnvWaterMaterials[formId] = new FnvWaterMaterialBinding(
                material.Appearance,
                material.NormalMapBindlessIndices?.ToArray());
        }
    }

    /// <summary>
    ///     Supplies the exact WATR identities needed by the bounded WATER001 preflight. A visible
    ///     CELL's non-zero XCWT is its effective type; otherwise it inherits
    ///     <paramref name="worldspaceDefaultWaterFormId" /> (WRLD NAM2). The selected identity is
    ///     retained for placed-NIF compatibility and one-material fixtures; generated CELL packets
    ///     resolve their exact identities through the per-WATR material catalog.
    /// </summary>
    public void SetFnvWater001WaterTypeContext(
        uint? selectedWaterFormId,
        uint? worldspaceDefaultWaterFormId)
    {
        _fnvWater001SelectedWaterFormId = selectedWaterFormId is > 0 ? selectedWaterFormId : null;
        _fnvWater001WorldspaceDefaultWaterFormId =
            worldspaceDefaultWaterFormId is > 0 ? worldspaceDefaultWaterFormId : null;
        _fnvWater001HasWaterTypeContext = true;
        _fnvWater001PendingPreflight =
            FnvWater001Preflight.Fallback(FnvWater001FallbackReason.SnapshotUnavailable);
        _fnvWater001Snapshot = default;
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
        // The host transitions depth and swaps to its read-only DSV based on whether it supplied a
        // valid descriptor, so a malformed enabled binding is a contract violation rather than a
        // fail-soft fallback. Silently changing its dimension could select Texture2D for a
        // Texture2DMS descriptor (undefined D3D12), while silently disabling it would make the
        // renderer choose the writable-DSV PSO while the host has depth in the read-only state.
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

    /// <summary>
    ///     Read-only resource preflight for the host. A positive result arms exactly one subsequent
    ///     <see cref="SetFnvWater001Snapshot" /> call; the renderer repeats the check and consumes the
    ///     descriptor on the next Render. Pass the same cylinder and perspective state as that draw.
    /// </summary>
    public FnvWater001Preflight GetFnvWater001Preflight(
        VisibilityCylinder cylinder,
        bool isPerspectiveProjection)
    {
        GatherVisibleWater(cylinder);
        var cells = InspectFnvWater001VisibleCells(_visibleWaterScratch);
        _fnvWater001PendingPreflight = EvaluateFnvWater001(
            _visibleWaterScratch,
            cells,
            cylinder.Position.Z,
            isPerspectiveProjection,
            requireSnapshot: false,
            snapshot: default);
        if (!_fnvWater001PendingPreflight.Candidate)
        {
            _fnvWater001Snapshot = default;
        }
        return _fnvWater001PendingPreflight;
    }

    /// <summary>
    ///     Whether any water will draw this frame, i.e. whether the deferred translucent reference list
    ///     is worth splitting around the surface at all. Water is drawn with <c>DepthWriteMask.Zero</c>,
    ///     so anything issued after it can never be occluded by it — ordering is the only lever, and a
    ///     submerged decal has to be issued before the surface to end up underneath it.
    ///     <para>
    ///         The split HEIGHT is no longer decided here. Each draw is now tested against the water
    ///         surface local to its own XY via <see cref="TryGetWaterHeightAt" />, because no single
    ///         global plane works: see that method for the measured failure of the previous
    ///         maximum-height plane.
    ///     </para>
    ///     <para>
    ///         Deliberately NOT derived from <see cref="FnvWater001Preflight" />. That contract decides
    ///         whether ONE plane equation can drive the WATER001 snapshot, so it declines on
    ///         <c>MixedVisiblePlaneHeights</c> — true in essentially every exterior view (measured: 1225
    ///         visible water cells over 6 distinct WATRs at Lake Mead), which left the partition dead
    ///         outdoors.
    ///     </para>
    /// </summary>
    public bool HasVisibleWaterToPartition(VisibilityCylinder cylinder)
    {
        var cellVisible = GatherVisibleWater(cylinder);
        var nifVisible = GatherVisibleNifPlanes(cylinder);
        return cellVisible + nifVisible > 0;
    }

    /// <summary>
    ///     Supplies a one-shot bindless SRV for a separate, single-sample SceneColor-format snapshot
    ///     captured after opaque terrain/references. The host must never pass the active scene RTV:
    ///     prepare/copy/resolve and transition the snapshot to PixelShaderResource first, then restore
    ///     it after water. Null clears the pending descriptor without drawing WATER001.
    /// </summary>
    public void SetFnvWater001Snapshot(uint? bindlessIndex, uint width, uint height)
    {
        if (bindlessIndex is null)
        {
            _fnvWater001Snapshot = default;
            return;
        }
        if (!_fnvWater001PendingPreflight.Candidate)
        {
            throw new InvalidOperationException(
                "A WATER001 opaque snapshot may only be supplied after a positive preflight.");
        }
        if (bindlessIndex == NoNormalMap)
            throw new ArgumentOutOfRangeException(nameof(bindlessIndex), "The null descriptor sentinel is not a snapshot SRV.");
        if (width == 0)
            throw new ArgumentOutOfRangeException(nameof(width), width, "Snapshot width must be positive.");
        if (height == 0)
            throw new ArgumentOutOfRangeException(nameof(height), height, "Snapshot height must be positive.");

        _fnvWater001Snapshot = new FnvWater001SnapshotDescriptor(bindlessIndex.Value, width, height);
    }

    /// <summary>
    ///     Supplies the planar sky-reflection target: the sky drawn with the view's Z axis mirrored,
    ///     resolved to a single-sample SceneColor texture. Replaces the 2-row gradient stand-in that
    ///     made water read flat (a gradient cannot carry cloud mottling, warm tint or sun glitter —
    ///     retail WATER000 samples a planar ReflectionMap RT here).
    ///     <para>
    ///         <paramref name="sceneWidth" />/<paramref name="sceneHeight" /> are the SCENE viewport
    ///         dimensions, not the target's: the lookup is a normalized screen-space UV, so the
    ///         target may be rendered at any resolution. Pass <c>null</c> to fall back to the gradient.
    ///     </para>
    /// </summary>
    public void SetWaterReflection(
        uint? bindlessIndex, uint sceneWidth, uint sceneHeight,
        Matrix4x4? reflectionViewProj = null)
    {
        _reflectionBindlessIndex = bindlessIndex is { } index && sceneWidth > 0 && sceneHeight > 0
            ? index
            : NoNormalMap;
        _reflectionSceneWidth = sceneWidth;
        _reflectionSceneHeight = sceneHeight;
        // Non-null marks SCENE content rendered with this mirrored view-projection (origin-
        // relative) — the Oblivion shader then takes its projective WATER007 arm instead of the
        // screen-UV sky lookup. Null keeps today's sky-only semantics for every variant.
        _reflectionViewProj = reflectionViewProj ?? Matrix4x4.Identity;
        _reflectionIsScene = reflectionViewProj is not null && _reflectionBindlessIndex != NoNormalMap;
    }

    private Matrix4x4 _reflectionViewProj = Matrix4x4.Identity;
    private bool _reflectionIsScene;

    public void SetModernCubeMap(uint? cubeMapBindlessIndex) =>
        _modernCubeBindlessIndex = cubeMapBindlessIndex ?? NoNormalMap;

    /// <summary>Selects the per-game <see cref="WaterProfile" /> (shader variant + tuning) for the loaded
    /// game. Call before <see cref="Render(Matrix4x4, VisibilityCylinder, Vector3)" /> on each
    /// worldspace/interior load. FNV/FO3 resolve to the FNV profile (byte-identical); a game with no
    /// reverse-engineered water shader resolves to the flat tinted plane (binary-RE-only policy).</summary>
    public void SetGame(BethesdaGame game)
    {
        _game = game;
        _waterProfile = WaterProfile.ForGame(game);
        _fnvWaterMaterials.Clear();
        _fnvWaterCellDrawBatches.Clear();
        ClearFnvWater001TransientState(clearWaterTypeContext: true);
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
    ///     Video-settings "Water ripples" toggle (retail <c>bUseWaterDisplacements:Water</c>; the
    ///     viewer maps it to the animated surface normal FIELD per the 2026-08-18 user ruling).
    ///     False substitutes the flat-normal placeholder for every ripple source — the surface
    ///     becomes a calm sheet with N=(0,0,1) — while the scroll/detail/lava clocks keep running.
    ///     Read per frame; changing it needs no rebuild.
    /// </summary>
    public bool RipplesEnabled { get; set; } = true;

    /// <summary>
    ///     Bindless index of the host texture cache's flat-normal placeholder (encoded (0,0,1)),
    ///     consumed only while <see cref="RipplesEnabled" /> is off. <see cref="NoNormalMap" />
    ///     until the host plumbs it; then ripples-off falls back to the procedural-ripple sentinel
    ///     being replaced, so the toggle can never black out the surface.
    /// </summary>
    public uint FlatNormalBindlessIndex { get; set; } = NoNormalMap;

    /// <summary>
    ///     The frame's ripple-source index after the video toggle: the resolved animated/NNAM
    ///     index while ripples are on, else the flat normal. Shared by the generic per-frame path
    ///     and the FNV per-material path so both obey the toggle identically.
    /// </summary>
    private uint ApplyRippleToggle(uint noiseIndex) =>
        RipplesEnabled || FlatNormalBindlessIndex == NoNormalMap ? noiseIndex : FlatNormalBindlessIndex;

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
    ///     The stable list is owned by <see cref="ReferenceRenderer12" /> and updates as meshes stream
    ///     in or their owning references change visibility; this renderer reads it each frame and draws
    ///     the visible surfaces with the same shader as cell water. Cheap reference assignment — call
    ///     once per frame before Render.
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
        WaterSurfaceParams surface,
        in FnvNoiseTileSet tiles)
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
            BlendIndex = tiles.BlendBindlessIndex,
            NoiseScale = surface.NoiseScale,
            TexScaleAmplitude = new Vector4(
                FnvWaterNoiseAnimation.TextureScale(surface.Layer1.UvScale),
                FnvWaterNoiseAnimation.TextureScale(surface.Layer2.UvScale),
                FnvWaterNoiseAnimation.TextureScale(surface.Layer3.UvScale),
                0f),
            Scroll0 = new Vector4(FnvWaterNoiseAnimation.Scroll(surface.Layer1, elapsedSeconds), 0f, 0f),
            Scroll1 = new Vector4(FnvWaterNoiseAnimation.Scroll(surface.Layer2, elapsedSeconds), 0f, 0f),
            Scroll2 = new Vector4(FnvWaterNoiseAnimation.Scroll(surface.Layer3, elapsedSeconds), 0f, 0f),
            Amplitude = new Vector4(
                surface.Layer1.AmpScale,
                surface.Layer2.AmpScale,
                surface.Layer3.AmpScale,
                0f),
        };

        // The allocator THROWS when a frame slot's transient region is exhausted; a saturated frame
        // must degrade to the authored normal map, not tear down the renderer.
        GpuDescriptorHeapAllocator12.DescriptorAllocation uavs;
        try
        {
            uavs = _cbvSrvUavHeap.Allocate(2);
        }
        catch (InvalidOperationException ex)
        {
            Log.Warn("WaterRenderer12: FNV noise prepass skipped — descriptor heap exhausted: {0}",
                ex.Message);
            return false;
        }

        var blendUavCpu = uavs.Cpu;
        var normalUavCpu = uavs.Cpu;
        normalUavCpu.Ptr += uavs.DescriptorSize;
        _gpu.Device.CreateUnorderedAccessView(tiles.BlendTexture, null, null, blendUavCpu);
        _gpu.Device.CreateUnorderedAccessView(tiles.NormalTexture, null, null, normalUavCpu);

        var blendUavGpu = uavs.Gpu;
        var normalUavGpu = uavs.Gpu;
        normalUavGpu.Ptr += uavs.DescriptorSize;

        cmd.SetComputeRootSignature(_sharedRootSignature);
        cmd.SetComputeRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, constants.GpuAddress);
        cmd.SetComputeRootDescriptorTable(
            GpuRootSignature12.Slots.BindlessSrvTable,
            _cbvSrvUavHeap.BindlessHeapStartGpu);

        cmd.ResourceBarrierTransition(
            tiles.BlendTexture,
            ResourceStates.NonPixelShaderResource,
            ResourceStates.UnorderedAccess);
        cmd.SetPipelineState(_fnvNoiseScrollBlendPso);
        cmd.SetComputeRootDescriptorTable(GpuRootSignature12.Slots.WaterNoiseUavTable, blendUavGpu);
        cmd.Dispatch(FnvNoiseDimension / 8, FnvNoiseDimension / 8, 1);
        cmd.ResourceBarrierTransition(
            tiles.BlendTexture,
            ResourceStates.UnorderedAccess,
            ResourceStates.NonPixelShaderResource);

        cmd.ResourceBarrierTransition(
            tiles.NormalTexture,
            ResourceStates.PixelShaderResource,
            ResourceStates.UnorderedAccess);
        cmd.SetPipelineState(_fnvNoiseNormalPso);
        cmd.SetComputeRootDescriptorTable(GpuRootSignature12.Slots.WaterNoiseUavTable, normalUavGpu);
        cmd.Dispatch(FnvNoiseDimension / 8, FnvNoiseDimension / 8, 1);

        if (!FnvNoiseMipsEnabled)
        {
            cmd.ResourceBarrierTransition(
                tiles.NormalTexture,
                ResourceStates.UnorderedAccess,
                ResourceStates.PixelShaderResource);
            return true;
        }

        // Fill the mip chain: each step reads the just-written level (transitioned to
        // NonPixelShaderResource per SUBRESOURCE — the rest of the texture stays a UAV) and
        // writes the next level through its own mip UAV. A ring shortfall stops the chain early;
        // the SRV still exposes the whole chain, so a stale upper level persists for that frame —
        // acceptable next to dropping the pass.
        cmd.SetPipelineState(_fnvNoiseDownsamplePso);
        var builtLevels = 1;
        for (var level = 1; level < FnvNoiseMipCount; level++)
        {
            if (!_ringBuffer.TryAllocate(
                    frameIndex,
                    (uint)Marshal.SizeOf<FnvNoiseUniforms>(),
                    out var mipConstants,
                    GpuRingBuffer12.CbAlignment))
            {
                break;
            }

            var destDimension = (int)FnvNoiseDimension >> level;
            *(FnvNoiseUniforms*)mipConstants.CpuPtr = new FnvNoiseUniforms
            {
                SourceIndex = tiles.NormalMipSrvIndices[level - 1],
                // Padding0 doubles as the destination dimension for mainDownsample.
                Padding0 = destDimension,
            };

            // Acquire BEFORE the source level's barrier. Both early-outs in this loop must leave
            // every subresource in the state the restore below assumes (levels < builtLevels-1 in
            // NonPixelShaderResource, the rest in UnorderedAccess); breaking after the transition
            // would strand level-1 in NonPixelShaderResource while the trailing restore declares it
            // a UAV.
            GpuDescriptorHeapAllocator12.DescriptorAllocation mipUav;
            try
            {
                mipUav = _cbvSrvUavHeap.Allocate(1);
            }
            catch (InvalidOperationException)
            {
                // Same contract as the ring shortfall above: stop the chain, keep the levels built
                // so far. A stale upper level persists for the frame — acceptable next to dropping
                // the pass.
                break;
            }

            cmd.ResourceBarrierTransition(
                tiles.NormalTexture,
                ResourceStates.UnorderedAccess,
                ResourceStates.NonPixelShaderResource,
                (uint)(level - 1));

            _gpu.Device.CreateUnorderedAccessView(
                tiles.NormalTexture,
                null,
                new UnorderedAccessViewDescription
                {
                    Format = Format.R8G8B8A8_UNorm,
                    ViewDimension = Vortice.Direct3D12.UnorderedAccessViewDimension.Texture2D,
                    Texture2D = new Texture2DUnorderedAccessView { MipSlice = (uint)level },
                },
                mipUav.Cpu);

            cmd.SetComputeRootConstantBufferView(
                GpuRootSignature12.Slots.PerFrameCbv, mipConstants.GpuAddress);
            cmd.SetComputeRootDescriptorTable(GpuRootSignature12.Slots.WaterNoiseUavTable, mipUav.Gpu);
            var groups = (uint)Math.Max(destDimension / 8, 1);
            cmd.Dispatch(groups, groups, 1);
            builtLevels = level + 1;
        }

        // Return every subresource to PixelShaderResource: levels 0..builtLevels-2 were left as
        // read sources, builtLevels-1..N-1 as UAV targets (written or untouched).
        for (var level = 0; level < builtLevels - 1; level++)
        {
            cmd.ResourceBarrierTransition(
                tiles.NormalTexture,
                ResourceStates.NonPixelShaderResource,
                ResourceStates.PixelShaderResource,
                (uint)level);
        }
        for (var level = builtLevels - 1; level < FnvNoiseMipCount; level++)
        {
            cmd.ResourceBarrierTransition(
                tiles.NormalTexture,
                ResourceStates.UnorderedAccess,
                ResourceStates.PixelShaderResource,
                (uint)level);
        }

        return true;
    }

    /// <summary>IWorldRenderer entry — absolute path (zero render origin).</summary>
    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder) =>
        RenderCore(viewProj, cylinder, default, isPerspectiveProjection: false, animationTimeSeconds: null);

    /// <summary>Draws the visible cell-water grid + authored NIF water geometry; returns the surface count.</summary>
    /// <param name="renderOrigin">
    ///     The world-space point <paramref name="viewProj" /> treats as its origin (the same value the
    ///     scene binds as the camera-relative render origin). The VS subtracts it from each water vertex
    ///     before projection so far-from-origin water keeps float32 depth precision (the absolute path
    ///     shimmered the water edge as the camera rotated). Absolute callers (top-down, export) use the
    ///     two-arg overload — a no-op. vWorldPos stays absolute for the PS's noise-UV/view-vector anchoring.
    /// </param>
    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder, Vector3 renderOrigin) =>
        RenderCore(viewProj, cylinder, renderOrigin, isPerspectiveProjection: true, animationTimeSeconds: null);

    /// <summary>Live-view entry that repeats the host's projection eligibility at draw time.</summary>
    internal int Render(
        Matrix4x4 viewProj,
        VisibilityCylinder cylinder,
        Vector3 renderOrigin,
        bool isPerspectiveProjection) =>
        RenderCore(viewProj, cylinder, renderOrigin, isPerspectiveProjection, animationTimeSeconds: null);

    /// <summary>
    ///     Unified-transparency entry: runs everything the live render does EXCEPT the FNV batch
    ///     draws, which are queued farthest-first for interleaved draining
    ///     (<see cref="DrainTransparencyDownTo" /> / <see cref="DrainAllTransparency" /> /
    ///     <see cref="FinishTransparencyStream" />). Only call when
    ///     <see cref="CanStreamTransparency" /> returned true this frame; any other water path
    ///     draws inline here exactly as the live render would.
    /// </summary>
    internal int PrepareTransparencyStream(
        Matrix4x4 viewProj,
        VisibilityCylinder cylinder,
        Vector3 renderOrigin,
        bool isPerspectiveProjection,
        float? animationTimeSeconds = null)
    {
        // The capture path pins one authored clock (see RenderAtTime); the live path passes null
        // and keeps the renderer's own elapsed time.
        if (animationTimeSeconds is { } pinned &&
            (pinned < 0f || !float.IsFinite(pinned)))
        {
            throw new ArgumentOutOfRangeException(
                nameof(animationTimeSeconds), pinned, "Animation time must be finite and >= 0.");
        }

        return RenderCore(viewProj, cylinder, renderOrigin, isPerspectiveProjection,
            animationTimeSeconds, deferFnvBatches: true);
    }

    /// <summary>
    ///     Deterministic entry point (scene capture + the static top-down/export overlays). Uses one
    ///     authored clock for legacy frame selection, noise scrolling, and shader time instead of
    ///     renderer construction time. <paramref name="isPerspectiveProjection" /> defaults to true for
    ///     the capture path; the ortho overlay/export callers pass false so the FNV WATER001 contract
    ///     evaluations (this flag's only consumers) see the true projection mode if a preflight is
    ///     ever armed on those paths.
    /// </summary>
    internal int RenderAtTime(
        Matrix4x4 viewProj,
        VisibilityCylinder cylinder,
        Vector3 renderOrigin,
        float animationTimeSeconds,
        bool isPerspectiveProjection = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(animationTimeSeconds);
        if (!float.IsFinite(animationTimeSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(animationTimeSeconds), animationTimeSeconds, "Animation time must be finite.");
        }

        return RenderCore(
            viewProj,
            cylinder,
            renderOrigin,
            isPerspectiveProjection,
            animationTimeSeconds);
    }

    private int RenderCore(
        Matrix4x4 viewProj,
        VisibilityCylinder cylinder,
        Vector3 renderOrigin,
        bool isPerspectiveProjection,
        float? animationTimeSeconds,
        bool deferFnvBatches = false)
    {
        LastStats.Reset();
        // NaN = "this frame queued no stream water". Reset unconditionally (legacy inline frames
        // included) so the above-all-water classification can never read a previous frame's plane —
        // the same stale-state discipline as the partition telemetry counters.
        StreamMaxQueuedSurfaceHeight = float.NaN;
        // The opaque snapshot is valid as a shader resource only for this host-bracketed pass.
        // Consume it up front so every early return fails closed and no later frame can sample a
        // resource that the host has transitioned back to CopyDest/ResolveDest.
        var fnvWater001ArmedPreflight = _fnvWater001PendingPreflight;
        var fnvWater001Snapshot = _fnvWater001Snapshot;
        _fnvWater001PendingPreflight =
            FnvWater001Preflight.Fallback(FnvWater001FallbackReason.SnapshotUnavailable);
        _fnvWater001Snapshot = default;
        LastFnvWater001Decision = fnvWater001ArmedPreflight.Candidate
            ? FnvWater001Preflight.Fallback(FnvWater001FallbackReason.NoVisibleCellWater)
            : fnvWater001ArmedPreflight;
        var elapsedSeconds = animationTimeSeconds ??
                             (float)Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds;
        LastStats.WaterAnimationSeconds = elapsedSeconds;
        LastStats.WaterAnimationFps = _waterProfile.SurfaceFrameFps;
        LastStats.WaterMapPaths = _telemetryMapPaths;
        LastStats.WaterMapRoles = _telemetryMapRoles;
        LastStats.WaterMapResolved = _telemetryMapResolved;
        string? telemetryUnavailableReason = null;
        if (_telemetryMapPaths.Length == 0)
        {
            telemetryUnavailableReason = _appearance is null
                ? "no WATR appearance resolved; the shader uses profile colors and a procedural normal fallback"
                : "the active WATR appearance carries no authored texture path; a procedural normal fallback is used";
        }
        LastStats.WaterTelemetryUnavailableReason = telemetryUnavailableReason;
        LastStats.WaterPipeline = ModernWaterPipeline.TelemetryName(
            _game,
            explicitlyEnabled: false,
            resourcesReady: false,
            prepassesRecorded: false);
        if (_waterCells.Count == 0 && _nifWaterPlanes.Count == 0) return 0;

        var started = StartTiming();
        var cmd = _recorder.CommandList;
        var frameIndex = _recorder.FrameIndex;
        // Per-material amortization is valid only within one frame's command list: ring
        // allocations belong to this frame slot and each noise tile is re-recorded per frame.
        // Reset here, which covers the stream and the legacy inline path alike.
        _frameNoiseSlots.Clear();
        Array.Clear(_frameNoiseSlotReady);
        _frameNoiseSlotsUsed = 0;
        _frameNoiseSlotOverflows = 0;
        _frameMaterialCb.Clear();

        var segmentStarted = StartTiming();
        var cellVisible = GatherVisibleWater(cylinder);
        var nifVisible = GatherVisibleNifPlanes(cylinder);
        var visibleSurfaces = cellVisible + nifVisible;
        var fnvWater001Cells = InspectFnvWater001VisibleCells(_visibleWaterScratch);
        if (fnvWater001ArmedPreflight.Candidate)
        {
            LastFnvWater001Decision = EvaluateFnvWater001(
                _visibleWaterScratch,
                fnvWater001Cells,
                cylinder.Position.Z,
                isPerspectiveProjection,
                requireSnapshot: true,
                snapshot: fnvWater001Snapshot);
        }
        var useFnvWater001 = LastFnvWater001Decision.Candidate;
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
        _fnvWaterCellDrawBatches.Clear();
        if (_game is BethesdaGame.FalloutNewVegas or BethesdaGame.Fallout3 && cellVisible > 0)
        {
            // Keep each XCWT material contiguous so one CB/normal prepass can drive exactly the
            // generated CELL packets that authored it, ordered farthest-first WITHIN the material.
            // The retail engine draws water as depth-sorted alpha passes — decompile-proven:
            // BSBatchRenderer::SortAlphaPasses orders on dot(worldPos, viewDir) descending and
            // RenderAlphaGeometry always takes the farther pass next — so wherever two planes
            // overlap at different heights (the routine `mixed-visible-plane-heights` case) the
            // correct composite is back-to-front from the CAMERA, not any fixed grid order. A
            // perspective viewProj's clip-space w of the centroid IS that view-axis distance; on
            // the ortho paths w is constant and the grid-key tiebreak (unique per cell ⇒ total
            // order) carries the whole comparison, keeping those captures deterministic. Depth is
            // PRECOMPUTED per cell so the unstable introsort compares plain fields.
            var wColumn = new Vector4(viewProj.M14, viewProj.M24, viewProj.M34, viewProj.M44);
            _fnvWaterSortScratch.Clear();
            for (var i = 0; i < cellVisible; i++)
            {
                var water = _visibleWaterScratch[i];
                var half = water.FootprintSize * 0.5f;
                var depth = ((water.OriginXY.X + half) * wColumn.X)
                            + ((water.OriginXY.Y + half) * wColumn.Y)
                            + (water.Height * wColumn.Z)
                            + wColumn.W;
                // Non-finite → pin to the FAR end (drawn first), mirroring the reference
                // renderer's blended-sort guard. A NaN here would otherwise fail every
                // stream-drain comparison and silently push ALL queued water to post-blended
                // order for the frame.
                if (!float.IsFinite(depth))
                {
                    depth = float.PositiveInfinity;
                }

                _fnvWaterSortScratch.Add((water, EffectiveFnvWaterFormId(water), depth));
            }

            _fnvWaterSortScratch.Sort(static (left, right) =>
            {
                var byMaterial = left.FormId.CompareTo(right.FormId);
                if (byMaterial != 0) return byMaterial;
                var byDepth = right.Depth.CompareTo(left.Depth); // farther first
                if (byDepth != 0) return byDepth;
                var byX = left.Cell.Key.gx.CompareTo(right.Cell.Key.gx);
                return byX != 0 ? byX : left.Cell.Key.gy.CompareTo(right.Cell.Key.gy);
            });

            var batchStart = 0;
            var batchFormId = _fnvWaterSortScratch[0].FormId;
            for (var i = 0; i < cellVisible; i++)
            {
                var (water, formId, _) = _fnvWaterSortScratch[i];
                _visibleWaterScratch[i] = water;
                if (formId != batchFormId)
                {
                    _fnvWaterCellDrawBatches.Add(new FnvWaterCellDrawBatch(
                        batchFormId,
                        batchStart,
                        i - batchStart,
                        ResolveFnvWaterMaterial(batchFormId),
                        _fnvWaterSortScratch[batchStart].Depth));
                    batchStart = i;
                    batchFormId = formId;
                }

                _instanceScratch[i] = CreateQuadPacket(
                    water.OriginXY,
                    water.Height,
                    new Vector2(water.FootprintSize));
            }
            _fnvWaterCellDrawBatches.Add(new FnvWaterCellDrawBatch(
                batchFormId,
                batchStart,
                cellVisible - batchStart,
                ResolveFnvWaterMaterial(batchFormId),
                _fnvWaterSortScratch[batchStart].Depth));

            // Engine parity at the BATCH level too: material batches DRAW farthest-first (each
            // batch's leading instance is its farthest after the intra-material sort). Only the
            // draw order moves — the buffer layout above is untouched.
            _fnvWaterCellDrawBatches.Sort(static (left, right) =>
            {
                var byDepth = right.FarthestDepth.CompareTo(left.FarthestDepth);
                return byDepth != 0 ? byDepth : left.WaterFormId.CompareTo(right.WaterFormId);
            });
        }
        else
        {
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
        }

        // Placed NIF water follows the cell-water packets in the same buffer. Each surface renders
        // with ITS OWN water identity (stamped at placement — see
        // ReferenceRenderer12.AccumulateNifWaterPlanes), so for FNV the visible surfaces are sorted
        // into contiguous per-WATR runs ordered farthest-first (the engine's proven alpha-pass
        // order — see the cell sort above), then grouped like the cell batches. Surface counts are
        // small, so the comparer derives depth inline; the bounds tiebreak keeps the order total.
        var nifWColumn = new Vector4(viewProj.M14, viewProj.M24, viewProj.M34, viewProj.M44);
        float NifViewDepth(NifWaterGeometry geometry)
        {
            var centroid = (geometry.BoundsMin + geometry.BoundsMax) * 0.5f;
            var depth = (centroid.X * nifWColumn.X)
                        + (centroid.Y * nifWColumn.Y)
                        + (centroid.Z * nifWColumn.Z)
                        + nifWColumn.W;
            // Parsed-NIF bounds are untrusted input: pin non-finite to the FAR end (drawn first)
            // so one bad plane cannot stall the stream drain (see the cell-depth guard above).
            return float.IsFinite(depth) ? depth : float.PositiveInfinity;
        }

        if (_game is BethesdaGame.FalloutNewVegas or BethesdaGame.Fallout3 && nifVisible > 1)
        {
            _visibleNifScratch.Sort((left, right) =>
            {
                var byMaterial = left.WaterFormId.CompareTo(right.WaterFormId);
                if (byMaterial != 0) return byMaterial;
                var byDepth = NifViewDepth(right).CompareTo(NifViewDepth(left)); // farther first
                if (byDepth != 0) return byDepth;
                var byX = left.BoundsMin.X.CompareTo(right.BoundsMin.X);
                if (byX != 0) return byX;
                var byY = left.BoundsMin.Y.CompareTo(right.BoundsMin.Y);
                return byY != 0 ? byY : left.BoundsMin.Z.CompareTo(right.BoundsMin.Z);
            });
        }

        _fnvNifDrawBatches.Clear();
        var packetCursor = cellVisible;
        var nifBatchFormId = 0u;
        var nifBatchStartPacket = 0;
        var nifBatchDepth = 0f;
        for (var j = 0; j < nifVisible; j++)
        {
            var geometry = _visibleNifScratch[j];
            var relativePacket = packetCursor - cellVisible;
            if (j == 0)
            {
                nifBatchFormId = geometry.WaterFormId;
                nifBatchDepth = NifViewDepth(geometry);
            }
            else if (geometry.WaterFormId != nifBatchFormId)
            {
                _fnvNifDrawBatches.Add(new FnvNifWaterDrawBatch(
                    nifBatchFormId, nifBatchStartPacket, relativePacket - nifBatchStartPacket,
                    nifBatchDepth));
                nifBatchFormId = geometry.WaterFormId;
                nifBatchStartPacket = relativePacket;
                nifBatchDepth = NifViewDepth(geometry);
            }

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

        if (nifVisible > 0)
        {
            _fnvNifDrawBatches.Add(new FnvNifWaterDrawBatch(
                nifBatchFormId,
                nifBatchStartPacket,
                packetCursor - cellVisible - nifBatchStartPacket,
                nifBatchDepth));
            // Farthest group draws first, mirroring the cell-batch draw order above.
            _fnvNifDrawBatches.Sort(static (left, right) =>
            {
                var byDepth = right.FarthestDepth.CompareTo(left.FarthestDepth);
                return byDepth != 0 ? byDepth : left.WaterFormId.CompareTo(right.WaterFormId);
            });
        }
        LastStats.InstanceBuildMilliseconds = ElapsedMilliseconds(segmentStarted);

        segmentStarted = StartTiming();
        if (_game is BethesdaGame.FalloutNewVegas or BethesdaGame.Fallout3 && _fnvWaterCellDrawBatches.Count > 0)
        {
            if (deferFnvBatches)
            {
                // Unified transparency stream: queue ONE entry PER CELL (and per placed-NIF
                // geometry), farthest-first. The engine sorts alpha passes per SHAPE
                // (BSBatchRenderer::SortAlphaPasses) and each generated cell quad is its own
                // pass in retail. The earlier per-material key (= the batch's FARTHEST cell)
                // drained a whole lake-spanning surface before any blended draw nearer than its
                // far shore — so wholly-submerged decals issued AFTER the surface above them and
                // composited on top (water writes no depth; ordering is the only lever). With
                // per-cell keys the cell above a submerged decal is NEARER than the decal and
                // drains after it — the correct composite. The drain coalesces consecutive
                // same-material contiguous entries back into one draw (DrawNextStreamRun), so
                // draw/CB/prepass counts stay at batch scale in the common case.
                UploadInstances(instanceCount);
                LastStats.GpuUploadMilliseconds = ElapsedMilliseconds(segmentStarted);
                _streamPending.Clear();
                _streamNext = 0;
                _streamFailed = false;
                _streamUsedNoisePrepass = false;
                _streamRuns = 0;
                _streamNoisePrepasses = 0;
                _streamDrawnCells = 0;
                _streamDrewNifPackets = false;
                _streamNifVisible = nifVisible;
                _streamArgs = new FnvBatchDrawArgs(
                    cmd, frameIndex, viewProj, cylinder.Position, renderOrigin, elapsedSeconds,
                    fnvWater001Snapshot.BindlessIndex, fnvWater001Snapshot.Width,
                    fnvWater001Snapshot.Height, _depthBindlessIndex != NoNormalMap);

                // Track the highest surface actually queued while the entries are built: the
                // reference renderer's wholly-above-all-water classification orders blended draws
                // against THIS frame's drawn water, nothing wider.
                var maxQueuedSurfaceZ = float.NaN;
                foreach (var batch in _fnvWaterCellDrawBatches)
                {
                    for (var offset = 0; offset < batch.InstanceCount; offset++)
                    {
                        var instance = batch.StartInstance + offset;
                        var cellHeight = _visibleWaterScratch[instance].Height;
                        if (float.IsFinite(cellHeight) &&
                            !(cellHeight <= maxQueuedSurfaceZ))
                        {
                            maxQueuedSurfaceZ = cellHeight;
                        }

                        _streamPending.Add(new PendingStreamBatch(
                            _fnvWaterSortScratch[instance].Depth, batch.WaterFormId,
                            batch.Material, instance, 1, useFnvWater001,
                            IsNifPacketBatch: false));
                    }
                }

                var streamPacketCursor = cellVisible;
                for (var j = 0; j < nifVisible; j++)
                {
                    var geometry = _visibleNifScratch[j];
                    var planeTop = geometry.BoundsMax.Z;
                    if (float.IsFinite(planeTop) && !(planeTop <= maxQueuedSurfaceZ))
                    {
                        maxQueuedSurfaceZ = planeTop;
                    }

                    _streamPending.Add(new PendingStreamBatch(
                        NifViewDepth(geometry), geometry.WaterFormId,
                        ResolveFnvWaterMaterial(geometry.WaterFormId),
                        streamPacketCursor, geometry.TrianglePacketCount,
                        Water001: false, IsNifPacketBatch: true));
                    streamPacketCursor += geometry.TrianglePacketCount;
                }

                StreamMaxQueuedSurfaceHeight = maxQueuedSurfaceZ;

                // One global farthest-first order across cells + NIF geometries. Kind/material/
                // instance tiebreaks keep it TOTAL (stable across frames — the engine's stable
                // merge-sort semantics); within one material, instance order is preserved, which
                // is what lets DrawNextStreamRun re-form contiguous runs.
                _streamPending.Sort(static (left, right) =>
                {
                    var byDepth = right.Depth.CompareTo(left.Depth);
                    if (byDepth != 0) return byDepth;
                    var byKind = left.IsNifPacketBatch.CompareTo(right.IsNifPacketBatch);
                    if (byKind != 0) return byKind;
                    var byMaterial = left.WaterFormId.CompareTo(right.WaterFormId);
                    return byMaterial != 0
                        ? byMaterial
                        : left.StartInstance.CompareTo(right.StartInstance);
                });

                // Technique telemetry depends only on the decision + batch counts — write it now so
                // HUD/trace consumers see it regardless of how far the drain gets.
                WriteFnvBatchTechniqueTelemetry(useFnvWater001, nifPacketCount);
                LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
                return visibleSurfaces;
            }

            return RenderFnvWaterMaterialBatches(
                viewProj,
                cylinder,
                renderOrigin,
                elapsedSeconds,
                cmd,
                frameIndex,
                cellVisible,
                nifVisible,
                nifPacketCount,
                instanceCount,
                useFnvWater001,
                fnvWater001Snapshot,
                started,
                segmentStarted);
        }

        // Per-frame CB (b0) — viewProj + DNAM colors + (camera pos, elapsed time) for the
        // procedural ripple/Fresnel/specular shading.
        if (!_ringBuffer.TryAllocate(
                frameIndex,
                WaterFrameUniformsByteSize,
                out var perFrameAlloc,
                GpuRingBuffer12.CbAlignment))
        {
            // Frame ring slot exhausted by the earlier scene passes (dense whole-map views). Skip
            // the water pass this frame — the next frame retries — instead of throwing, which
            // killed the whole render loop (the water pass runs LAST, so it hits the wall first).
            LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
            return 0;
        }
        var surface =
 _appearance?.Surface ?? BethesdaMultitool.Core.Formats.Esm.Models.Records.World.WaterSurfaceParams.Default;
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
        // Video-settings "Water ripples": off flattens the ripple source to the flat normal.
        // Morrowind is exempt — its legacy frames are the DIFFUSE surface, not a ripple field.
        if (_waterProfile.ShaderVariant != WaterShaderVariant.MorrowindWater)
        {
            noiseIndex = ApplyRippleToggle(noiseIndex);
        }
        var usedFnvNoisePrepass = false;
        if (_useFnvNoisePrepass && noiseIndex != NoNormalMap &&
            // The generic path draws every surface from ONE camera-level appearance, so it owns a
            // single slot under a key no WATR FormID can take.
            TryAcquireFnvNoiseSlot(
                GenericWaterNoiseKey, cmd, frameIndex, elapsedSeconds, noiseIndex, surface,
                out var genericSlot))
        {
            usedFnvNoisePrepass = true;
            noiseIndex = _fnvNoiseTiles[genericSlot].NormalBindlessIndex;
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
        if (_waterProfile.ShaderVariant == WaterShaderVariant.MorrowindWater ||
            // The flat stand-in takes the profile alpha only when the WATR authored no ANAM of its
            // own: WaterSurfaceParams.Opacity defaults to a fully opaque 1.0 for a silent/unparsed
            // record, and an opaque plane would hide the scene under it. An authored opacity (< 1)
            // is real ESM data and wins.
            (_waterProfile.ShaderVariant == WaterShaderVariant.FlatTinted && waterOpacity >= 1f))
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
                Surface0 =
 new Vector4(ResolveSurfaceUvScale(surface), surface.FresnelAmount, surface.ReflectivityAmount, surface.Shininess),
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
                    DepthLutIndex =
 modernResources?.BindlessIndices[ModernWaterResources12.DepthLutOutput] ?? NoNormalMap,
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
                FnvWater001SnapshotIndex = useFnvWater001
                    ? fnvWater001Snapshot.BindlessIndex
                    : NoNormalMap,
                FnvWater001SnapshotWidth = useFnvWater001 ? fnvWater001Snapshot.Width : 0,
                FnvWater001SnapshotHeight = useFnvWater001 ? fnvWater001Snapshot.Height : 0,
                FnvWater001PlaneHeight = useFnvWater001 ? LastFnvWater001Decision.PlaneHeight : 0f,
                FnvWater001Surface = new Vector4(
                    surface.UnderwaterFogNear,
                    surface.UnderwaterFogFar,
                    surface.AboveWaterFogAmount,
                    surface.RefractionDistortionAmount),
                WaterReflectionIndex = _reflectionBindlessIndex,
                WaterReflectionSceneWidth = _reflectionSceneWidth,
                WaterReflectionSceneHeight = _reflectionSceneHeight,
                WaterReflectionDistortion = ReflectionDistortionScale,
                ReflectionViewProj = _reflectionViewProj,
                ReflectionSceneFlag = _reflectionIsScene ? 1u : 0u,
            };
        }

        // Copy CPU instance scratch into the persistent-mapped UPLOAD buffer.
        UploadInstances(instanceCount);
        LastStats.GpuUploadMilliseconds = ElapsedMilliseconds(segmentStarted);

        segmentStarted = StartTiming();
        // Build this frame's instance SRV directly in the frame's shader-visible heap slot,
        // windowed onto the frame-slotted region of the instance buffer.
        var srvAlloc = _cbvSrvUavHeap.Allocate(1);
        var instanceSrvDesc = new ShaderResourceViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Buffer,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Buffer = new BufferShaderResourceView
            {
                FirstElement = (ulong)frameIndex * (ulong)_instanceCapacity,
                NumElements = (uint)_instanceCapacity,
                StructureByteStride = (uint)Marshal.SizeOf<WaterInstance>(),
                Flags = BufferShaderResourceViewFlags.None,
            },
        };
        _gpu.Device.CreateShaderResourceView(_instanceBuffer, instanceSrvDesc, srvAlloc.Cpu);

        // With a scene-depth SRV the host has bound its READ-ONLY DSV + transitioned depth to
        // DEPTH_READ | PIXEL_SHADER_RESOURCE, so use the depth-sample PSO (hardware GreaterEqual
        // occlusion + in-shader depth fade). Otherwise the writable DSV is still bound → _pso.
        var depthSample = _depthBindlessIndex != NoNormalMap;
        if (useFnvWater001)
        {
            var depthRoute = _depthSampleCount > 1
                ? $"msaa{_depthSampleCount}x"
                : "1x";
            LastStats.WaterTechnique =
                $"FnvWater001Reconstructed-opaque-snapshot-main-scene-depth-approx-{depthRoute}";
            if (nifPacketCount > 0)
            {
                LastStats.WaterTechnique +=
                    $"+FnvWater003RtFree-scene-depth-{depthRoute}-placed-nif";
            }
            // Retail WATER001 samples a selectively populated refraction target. This bounded route
            // uses the opaque main-scene snapshot and main depth, so expose that approximation even
            // when every eligibility gate succeeds.
            LastStats.WaterTelemetryUnavailableReason =
                "selective-content-mask-approximated-by-main-depth";
        }
        else
        {
            LastStats.WaterTechnique = DescribeTechnique(
                _game,
                _waterProfile.ShaderVariant,
                useModernPipeline,
                modernTechnique,
                depthSample,
                _depthSampleCount);
            if (_game == BethesdaGame.FalloutNewVegas &&
                _waterProfile.ShaderVariant == WaterShaderVariant.FnvWater000)
            {
                LastStats.WaterTelemetryUnavailableReason = LastFnvWater001Decision.ReasonCode;
            }
        }
        if (useFnvWater001)
        {
            cmd.SetPipelineState(_psoFnvWater001DepthSample);
        }
        else if (useModernPipeline)
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
                // No depthSample twin: the flat plane never samples the depth SRV, and its occlusion
                // is the same hardware GreaterEqual test in both host depth states.
                WaterShaderVariant.FlatTinted => _psoFlat,
                _ => depthSample ? _psoDepthSample : _pso,
            });
        }
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, perFrameAlloc.GpuAddress);
        cmd.SetGraphicsRootDescriptorTable(GpuRootSignature12.Slots.SrvTable, srvAlloc.Gpu);
        // Bind the shared bindless alias tables (space1 t0.. + the space3 MSAA depth alias) at the
        // heap head so the PS can sample the resolved NNAM normal map via
        // NonUniformResourceIndex(uNoiseParams.x) and the multisampled scene depth.
        GpuRootSignature12.SetGraphicsBindlessTables(cmd, _cbvSrvUavHeap.BindlessHeapStartGpu);

        // WATER001's reconstructed horizontal plane is valid only for generated cell quads. Draw
        // those packets first with the refraction permutation, then switch back to the established
        // WATER003 depth-sampled PSO for every placed-NIF packet (sloped/multi-height authored water).
        // When WATER001 is ineligible, retain the original one-call WATER003/variant path exactly.
        if (useFnvWater001)
        {
            cmd.DrawInstanced(6, (uint)cellVisible, 0, 0);
            if (nifPacketCount > 0)
            {
                cmd.SetPipelineState(_psoDepthSample);
                cmd.DrawInstanced(6, (uint)nifPacketCount, 0, (uint)cellVisible);
            }
        }
        else
        {
            // Six vertices per packet: a cell quad or one/two authored NIF triangles.
            cmd.DrawInstanced(6, (uint)instanceCount, 0, 0);
        }
        LastStats.DrawCallMilliseconds = ElapsedMilliseconds(segmentStarted);
        LastStats.WaterDraws = visibleSurfaces;
        LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
        return visibleSurfaces;
    }

    /// <summary>One FNV stream entry, farthest-first: a single generated cell quad or one placed-NIF
    /// geometry's packet run (the engine's per-shape alpha-pass granularity).</summary>
    private readonly record struct PendingStreamBatch(
        float Depth,
        uint WaterFormId,
        ResolvedFnvWaterMaterial Material,
        int StartInstance,
        int InstanceCount,
        bool Water001,
        bool IsNifPacketBatch);

    // Unified-transparency drain state (see PrepareTransparencyStream). Valid for one frame.
    // Entries are PER CELL / per placed-NIF geometry (engine granularity: one alpha-pass key per
    // shape); WaterFormId identifies the material for run coalescing and the total-order tiebreak.
    private readonly List<PendingStreamBatch> _streamPending = new(16);

    /// <summary>
    ///     The highest water surface QUEUED for this frame's transparency stream (visible cell
    ///     heights + placed-NIF plane tops), or NaN when the frame queued none. Valid between
    ///     <see cref="PrepareTransparencyStream" /> and <see cref="FinishTransparencyStream" />;
    ///     reset every <c>RenderCore</c> so a non-streaming frame can never serve a stale plane.
    ///     The reference renderer's wholly-above-all-water classification consumes it — bounded to
    ///     the DRAWN water on purpose: water that never draws cannot occlude anything, and the
    ///     unbounded world gather is what killed the original single-plane submerged split.
    /// </summary>
    public float StreamMaxQueuedSurfaceHeight { get; private set; } = float.NaN;
    private int _streamNext;
    private bool _streamFailed;
    private bool _streamUsedNoisePrepass;
    private int _streamDrawnCells;
    private bool _streamDrewNifPackets;
    private int _streamNifVisible;
    private FnvBatchDrawArgs _streamArgs;
    private uint _streamTailReservationBytes;
    // Cost + loss counters for the frame's stream (published in FinishTransparencyStream). Runs
    // are DRAWS, entries are queued shapes; a gap between them is the coalescing working, and a
    // non-zero dropped count means the drain died mid-frame and nearer water never rendered.
    private int _streamRuns;
    private int _streamNoisePrepasses;

    // ── Per-material amortization (per frame) ────────────────────────────────────────────────
    // The transparency stream sorts water per SHAPE, so one lake gets sliced into as many runs as
    // there are blended reference draws interleaved with it (~1,225 at the default distance, not
    // the ~6 visible WATRs). Re-running the full per-material setup per run cost 2,816 ring bytes
    // and 11 transient descriptors EACH — 3.3 MB/frame against a 64 KiB protected reservation, and
    // on exhaustion the drain latched and dropped every remaining nearer pool ("some pools of
    // water are entirely missing"). Both halves of that setup are pure functions of the material,
    // so they are computed once per material per frame:
    //   • the noise prepass — the shared 256² normal tile only needs re-recording when a DIFFERENT
    //     material's normals must occupy it, so track which material is resident;
    //   • the frame uniforms CB — every field is a function of (material, water001, frame); the
    //     only per-run values are the instance-window SRV and the draw's instance count.
    /// <summary>One material's noise tiles + their persistent bindless views (see
    /// <see cref="FnvNoiseMaterialSlots" />).</summary>
    private readonly record struct FnvNoiseTileSet(
        ID3D12Resource BlendTexture,
        ID3D12Resource NormalTexture,
        uint BlendBindlessIndex,
        uint NormalBindlessIndex,
        uint[] NormalMipSrvIndices);

    // Per-frame material→slot assignment and the prepass result for each. Both are valid only for
    // the frame that filled them (the ring allocations behind a prepass belong to that frame slot),
    // so RenderCore clears them; a material assigned a slot keeps it for the whole frame, which is
    // what makes its resolved noise index a stable fact rather than an ordering invariant.
    private readonly Dictionary<uint, int> _frameNoiseSlots = new(FnvNoiseMaterialSlots);
    private readonly bool[] _frameNoiseSlotReady = new bool[FnvNoiseMaterialSlots];
    private int _frameNoiseSlotsUsed;
    private readonly Dictionary<(uint FormId, bool Water001), ulong> _frameMaterialCb = new();

    /// <summary>
    ///     Assigns this frame's noise tile slot for a WATR material and records its prepass, both
    ///     exactly once. Returns false when the pool is exhausted OR the prepass could not be
    ///     recorded; either way the caller falls back to the material's authored normal map for the
    ///     whole frame.
    ///     <para>
    ///         The decision is taken ONCE and remembered — a retry on a later run of the same
    ///         material would re-record into a tile no draw is going to read, because the run that
    ///         first failed has already cached a uniforms CB pointing at the authored map. One
    ///         attempt per material per frame keeps the CB cache and the tile state in agreement.
    ///     </para>
    ///     <para>
    ///         Measured visible-material counts are 4-6 in FNV's densest water views, so the pool is
    ///         sized well clear of the fallback — and <see cref="LastStats" /> reports the overflow
    ///         so "more materials than slots" can never become a silent quality cliff.
    ///     </para>
    /// </summary>
    private bool TryAcquireFnvNoiseSlot(
        uint waterFormId,
        ID3D12GraphicsCommandList cmd,
        int frameIndex,
        float elapsedSeconds,
        uint sourceIndex,
        WaterSurfaceParams surface,
        out int slot)
    {
        if (_frameNoiseSlots.TryGetValue(waterFormId, out slot))
        {
            return slot >= 0 && _frameNoiseSlotReady[slot];
        }

        if (_frameNoiseSlotsUsed >= FnvNoiseMaterialSlots)
        {
            _frameNoiseSlotOverflows++;
            _frameNoiseSlots[waterFormId] = -1;
            slot = -1;
            return false;
        }

        slot = _frameNoiseSlotsUsed++;
        _frameNoiseSlots[waterFormId] = slot;
        _frameNoiseSlotReady[slot] = RecordFnvNoisePrepass(
            cmd, frameIndex, elapsedSeconds, sourceIndex, surface, _fnvNoiseTiles[slot]);
        if (!_frameNoiseSlotReady[slot]) return false;

        // Counts both paths; only the stream publishes it (reset at prepare, read at finish), so
        // legacy increments are discarded harmlessly.
        _streamNoisePrepasses++;
        return true;
    }

    private int _frameNoiseSlotOverflows;

    /// <summary>Slot key for the generic (non-per-WATR) path. 0 is a legitimate unresolved WATR
    /// identity, so the sentinel has to sit outside the FormID space.</summary>
    private const uint GenericWaterNoiseKey = uint.MaxValue;

    /// <summary>
    ///     Hands the host's protected water tail reservation to the stream so it is released at
    ///     the LAST possible moment: after the reference renderer's blended capacity plan has
    ///     allocated (the plan must NOT see the water budget as free ring space — spending it on
    ///     far blended draws is the dense-frame ring-starvation water flicker fixed on 07-24) and
    ///     immediately before the first water batch allocates. Released lazily by the first
    ///     drained batch, and unconditionally by <see cref="FinishTransparencyStream" /> /
    ///     <see cref="AbandonTransparencyStream" /> so a zero-drain or failed frame cannot hold it
    ///     into the shadow pass (the ring also self-heals at ResetFrame).
    /// </summary>
    public void DeferStreamTailReservationRelease(uint reservedBytes) =>
        _streamTailReservationBytes = reservedBytes;

    private void ReleaseStreamTailReservation()
    {
        if (_streamTailReservationBytes == 0)
        {
            return;
        }

        _ringBuffer.ReleaseTailReservation(_streamTailReservationBytes);
        _streamTailReservationBytes = 0;
    }

    /// <summary>
    ///     Host failure path: forget this frame's queued batches (so a later frame cannot drain
    ///     stale draws against a dead command list) and release the deferred tail reservation.
    /// </summary>
    public void AbandonTransparencyStream()
    {
        _streamPending.Clear();
        _streamNext = 0;
        _streamFailed = false;
        StreamMaxQueuedSurfaceHeight = float.NaN;
        ReleaseStreamTailReservation();
    }

    /// <summary>True when this frame's water can enter the unified transparency stream: the
    /// classic-Fallout per-WATR batch path with at least one visible generated cell. FO3 parity
    /// 2026-08-10: FO3 joins FNV — its BSBatchRenderer::SortAlphaPasses (0x00BD0440) sorts the
    /// identical descending dot(worldPos, viewDir) key, so the per-cell farthest-first queue and
    /// both water partitions (wholly-below and wholly-above-all) apply verbatim. Everything else
    /// (other games, NIF-only scenes) keeps the legacy inline water pass.</summary>
    public bool CanStreamTransparency(VisibilityCylinder cylinder) =>
        _game is BethesdaGame.FalloutNewVegas or BethesdaGame.Fallout3 && GatherVisibleWater(cylinder) > 0;

    /// <summary>True while queued stream batches remain and the ring has not failed.</summary>
    public bool HasPendingTransparency => !_streamFailed && _streamNext < _streamPending.Count;

    /// <summary>The farthest undrained stream batch's view-axis depth (clip-space w — the SAME
    /// metric the reference renderer's blended sort uses, from the SAME viewProj), or
    /// NegativeInfinity when drained/failed.</summary>
    public float PeekTransparencyDepth() =>
        HasPendingTransparency ? _streamPending[_streamNext].Depth : float.NegativeInfinity;

    /// <summary>Draws every queued batch at least as far as <paramref name="depth" /> — the merge
    /// step the reference renderer's blended loop calls before each of its own draws. Returns true
    /// when at least one batch drew (the caller must rebind its shared frame state).</summary>
    public bool DrainTransparencyDownTo(float depth)
    {
        var drewAny = false;
        while (HasPendingTransparency && _streamPending[_streamNext].Depth >= depth)
        {
            if (!DrawNextStreamRun(depth)) break;
            drewAny = true;
        }

        return drewAny;
    }

    /// <summary>Drains every remaining queued batch (the nearer-than-everything tail).</summary>
    public void DrainAllTransparency()
    {
        while (HasPendingTransparency)
        {
            if (!DrawNextStreamRun(float.NegativeInfinity)) break;
        }
    }

    /// <summary>
    ///     Draws the maximal RUN starting at the queue head: consecutive entries that still meet
    ///     the drain threshold, share the material/kind (one CB + prepass + PSO serves them), and
    ///     are contiguous in the instance buffer (one SRV window covers them). Entries are
    ///     per-cell for engine-correct sort granularity, but within a material the queue keeps
    ///     instance order, so whole batches re-form here whenever no other material or nearer
    ///     blended draw interleaves — draw counts stay at batch scale in the common case.
    /// </summary>
    private bool DrawNextStreamRun(float depth)
    {
        // First allocation of the stream: the blended capacity plan has already run (it executes
        // before any drain), so the protected water budget can open for this run's CBs now.
        ReleaseStreamTailReservation();
        var first = _streamPending[_streamNext];
        var count = first.InstanceCount;
        var end = _streamNext + 1;
        while (end < _streamPending.Count)
        {
            var next = _streamPending[end];
            if (next.Depth < depth ||
                next.WaterFormId != first.WaterFormId ||
                next.IsNifPacketBatch != first.IsNifPacketBatch ||
                next.Water001 != first.Water001 ||
                next.StartInstance != first.StartInstance + count)
            {
                break;
            }

            count += next.InstanceCount;
            end++;
        }

        // Interleaved reference draws clobber topology and the bindless tables; each run's PSO,
        // CB and SRV are self-bound in DrawFnvWaterBatch. Rebinding these two per run is cheap
        // and simpler than owner tracking.
        _streamArgs.Cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        GpuRootSignature12.SetGraphicsBindlessTables(
            _streamArgs.Cmd, _cbvSrvUavHeap.BindlessHeapStartGpu);
        if (!DrawFnvWaterBatch(
                _streamArgs, first.Material, first.StartInstance, count,
                first.Water001, ref _streamUsedNoisePrepass))
        {
            // Ring exhaustion: the remaining (nearer) entries drop for this frame, mirroring the
            // legacy loop's break-on-failure.
            _streamFailed = true;
            return false;
        }

        if (first.IsNifPacketBatch) _streamDrewNifPackets = true;
        else _streamDrawnCells += count;
        _streamRuns++;
        _streamNext = end;
        return true;
    }

    /// <summary>Writes the frame's water stats after the stream is drained. Pair with
    /// <see cref="PrepareTransparencyStream" />.</summary>
    public int FinishTransparencyStream()
    {
        // A frame that never drained (or failed before its first batch) must not hold the
        // deferred water reservation into the shadow pass.
        ReleaseStreamTailReservation();
        LastStats.WaterNoisePrepassUsed = _streamUsedNoisePrepass;
        // Same accounting as the legacy loop: drawn generated cells, plus the visible NIF surface
        // count once any placed-NIF batch drew.
        var waterDraws = _streamDrawnCells + (_streamDrewNifPackets ? _streamNifVisible : 0);
        LastStats.WaterDraws = waterDraws;
        LastStats.WaterStreamEntries = _streamPending.Count;
        LastStats.WaterStreamRuns = _streamRuns;
        LastStats.WaterStreamNoisePrepasses = _streamNoisePrepasses;
        LastStats.WaterNoiseSlotOverflows = _frameNoiseSlotOverflows;
        // Anything still queued when the stream ends never drew. Only a mid-drain failure can
        // leave a remainder (both drains run to exhaustion otherwise), so this is the direct
        // measure of the "some pools are entirely missing" symptom.
        LastStats.WaterStreamDroppedEntries = Math.Max(0, _streamPending.Count - _streamNext);
        _streamPending.Clear();
        _streamNext = 0;
        return waterDraws;
    }

    /// <summary>Immutable per-frame context every FNV material-batch draw shares. One copy serves
    /// both the legacy inline loop and the unified-transparency drain.</summary>
    private readonly record struct FnvBatchDrawArgs(
        ID3D12GraphicsCommandList Cmd,
        int FrameIndex,
        Matrix4x4 ViewProj,
        Vector3 CylinderPosition,
        Vector3 RenderOrigin,
        float ElapsedSeconds,
        uint SnapshotBindlessIndex,
        uint SnapshotWidth,
        uint SnapshotHeight,
        bool DepthSample);

    /// <summary>
    ///     Draws one FNV per-WATR batch: optional noise prepass immediately before the draw (the
    ///     shared 256² normal target relies on command-list order), a fresh per-frame CB, PSO by
    ///     technique, and an SRV window into this frame's slot of the instance buffer. Fully
    ///     self-binding, so batches can interleave with reference draws in the unified transparency
    ///     stream. Returns false on ring exhaustion (the caller stops draining).
    /// </summary>
    private unsafe bool DrawFnvWaterBatch(
        in FnvBatchDrawArgs args,
        in ResolvedFnvWaterMaterial material,
        int startInstance,
        int drawInstanceCount,
        bool water001,
        ref bool usedAnyNoisePrepass)
    {
        if (drawInstanceCount <= 0) return true;
        if (_instanceBuffer is null) return true;

        var cmd = args.Cmd;
        var appearance = material.Appearance;
        var surface = appearance?.Surface ?? WaterSurfaceParams.Default;
        // Same video-settings "Water ripples" gate as the generic per-frame path — the FNV
        // per-material path must obey the toggle identically (a flat normal through the scroll
        // prepass composes to a flat normal, so the substitution is order-independent here).
        var noiseIndex = ApplyRippleToggle(material.NoiseIndex);
        var usedNoisePrepass = false;
        if (_useFnvNoisePrepass && noiseIndex != NoNormalMap &&
            TryAcquireFnvNoiseSlot(
                material.WaterFormId, cmd, args.FrameIndex, args.ElapsedSeconds, noiseIndex, surface,
                out var slot))
        {
            // This material OWNS its tile for the frame: the prepass ran exactly once, at slot
            // assignment, so the resolved index is a property of the material rather than of
            // whatever happened to be recorded last. That is what lets the uniforms CB below be
            // cached per material without the cached copy and a later run ever disagreeing.
            usedNoisePrepass = true;
            noiseIndex = _fnvNoiseTiles[slot].NormalBindlessIndex;
            usedAnyNoisePrepass = true;
        }

        // The uniforms CB is a pure function of (material, water001, frame) — nothing in it
        // derives from the instance window — so one allocation serves every run of that material.
        if (_frameMaterialCb.TryGetValue((material.WaterFormId, water001), out var cachedCb))
        {
            return DrawFnvWaterInstances(cmd, cachedCb, water001, args, startInstance, drawInstanceCount);
        }

        if (!_ringBuffer.TryAllocate(
                args.FrameIndex,
                WaterFrameUniformsByteSize,
                out var perFrame,
                GpuRingBuffer12.CbAlignment))
        {
            return false;
        }

        *(WaterFrameUniforms*)perFrame.CpuPtr = new WaterFrameUniforms
        {
            ViewProj = args.ViewProj,
            Shallow = ColorToVector4(appearance?.Shallow, _waterProfile.DefaultShallow),
            Deep = ColorToVector4(appearance?.Deep, _waterProfile.DefaultDeep),
            Reflection = ColorToVector4(appearance?.Reflection, _waterProfile.DefaultReflection),
            CamPosTime = new Vector4(args.CylinderPosition, args.ElapsedSeconds),
            NoiseIndex = noiseIndex,
            NoiseTiling = _waterProfile.NoiseTilingWorldUnits,
            NoiseScale = surface.NoiseScale,
            WaterOpacity = surface.Opacity,
            Surface0 = new Vector4(
                ResolveSurfaceUvScale(surface),
                surface.FresnelAmount,
                surface.ReflectivityAmount,
                surface.Shininess),
            Surface1 = new Vector4(
                surface.SunPower,
                surface.DepthFalloffStart,
                surface.DepthFalloffEnd,
                appearance?.IsLava == true ? 1f : 0f),
            Layer1 = LayerToVector4(surface.Layer1),
            Layer2 = LayerToVector4(surface.Layer2),
            Layer3 = LayerToVector4(surface.Layer3),
            DepthIndex = _depthBindlessIndex,
            DepthNear = _depthNear,
            DepthFar = _depthFar,
            DepthTieBias = _waterProfile.DepthTieBiasWorldUnits,
            RenderOrigin = new Vector4(args.RenderOrigin, _depthSampleCount),
            NormalIndex1 = material.NormalIndex1,
            NormalIndex2 = material.NormalIndex2,
            NormalIndex3 = material.NormalIndex3,
            NormalIndex4 = usedNoisePrepass ? 1u : 0u,
            // The FNV programs read the above-water fog planes (DNAM@32/@36) from
            // LegacySurface1.xy for the WATER003 alpha law — same fill as the generic path;
            // leaving this zeroed collapsed the fog ramp to the fogFar guard.
            LegacySurface1 = new Vector4(
                surface.FogNear, surface.FogFar, surface.TextureBlend, surface.WindVelocity),
            FnvWater001SnapshotIndex = water001 ? args.SnapshotBindlessIndex : NoNormalMap,
            FnvWater001SnapshotWidth = water001 ? args.SnapshotWidth : 0,
            FnvWater001SnapshotHeight = water001 ? args.SnapshotHeight : 0,
            FnvWater001PlaneHeight = water001 ? LastFnvWater001Decision.PlaneHeight : 0f,
            FnvWater001Surface = new Vector4(
                surface.UnderwaterFogNear,
                surface.UnderwaterFogFar,
                surface.AboveWaterFogAmount,
                surface.RefractionDistortionAmount),
            WaterReflectionIndex = _reflectionBindlessIndex,
            WaterReflectionSceneWidth = _reflectionSceneWidth,
            WaterReflectionSceneHeight = _reflectionSceneHeight,
            WaterReflectionDistortion = ReflectionDistortionScale,
            ReflectionViewProj = _reflectionViewProj,
            ReflectionSceneFlag = _reflectionIsScene ? 1u : 0u,
        };

        _frameMaterialCb[(material.WaterFormId, water001)] = perFrame.GpuAddress;
        return DrawFnvWaterInstances(
            cmd, perFrame.GpuAddress, water001, args, startInstance, drawInstanceCount);
    }

    /// <summary>
    ///     Issues one run against an already-resolved per-material frame CB: PSO by technique, the
    ///     shared CB, and a fresh SRV window over this run's slice of the instance buffer. Split out
    ///     of <see cref="DrawFnvWaterBatch" /> so the many runs the transparency stream slices a
    ///     single material into reuse that CB instead of re-allocating it (the ring spend that used
    ///     to exhaust mid-frame and drop every remaining nearer pool).
    /// </summary>
    private bool DrawFnvWaterInstances(
        ID3D12GraphicsCommandList cmd,
        ulong perFrameCbAddress,
        bool water001,
        in FnvBatchDrawArgs args,
        int startInstance,
        int drawInstanceCount)
    {
        var pso = _pso;
        if (water001)
        {
            pso = _psoFnvWater001DepthSample;
        }
        else if (args.DepthSample)
        {
            pso = _psoDepthSample;
        }
        cmd.SetPipelineState(pso);
        cmd.SetGraphicsRootConstantBufferView(
            GpuRootSignature12.Slots.PerFrameCbv,
            perFrameCbAddress);
        // D3D12's SV_InstanceID does NOT include StartInstanceLocation, so a non-zero start
        // never reached the VS's uInstances[instanceId] fetch: every batch after the first
        // re-drew batch 0's slice. Sorted by water FormID, the worldspace-default material
        // (lowest FormID — the -2300 river/sea cells) always sorted first, so every per-XCWT
        // batch — exactly the explicit-XCLW lakes (Lake Mead 2600, upstream basins) — silently
        // vanished. Window the batch's slice through a per-draw SRV instead and always draw
        // instances [0, count).
        GpuDescriptorHeapAllocator12.DescriptorAllocation batchSrv;
        try
        {
            batchSrv = _cbvSrvUavHeap.Allocate(1);
        }
        catch (InvalidOperationException ex)
        {
            // The transient heap THROWS on frame-slot exhaustion. Degrade to dropping this run
            // (the caller stops draining) instead of taking the whole frame down, matching the
            // modern-water path's guard.
            Log.Warn("WaterRenderer12: transient descriptor exhaustion during the water drain: {0}",
                ex.Message);
            return false;
        }

        var batchSrvDesc = new ShaderResourceViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Buffer,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Buffer = new BufferShaderResourceView
            {
                // Window inside this frame's slot of the frame-slotted instance buffer.
                FirstElement =
                    ((ulong)_recorder.FrameIndex * (ulong)_instanceCapacity) + (ulong)startInstance,
                NumElements = (uint)drawInstanceCount,
                StructureByteStride = (uint)Marshal.SizeOf<WaterInstance>(),
                Flags = BufferShaderResourceViewFlags.None,
            },
        };
        _gpu.Device.CreateShaderResourceView(_instanceBuffer, batchSrvDesc, batchSrv.Cpu);
        cmd.SetGraphicsRootDescriptorTable(GpuRootSignature12.Slots.SrvTable, batchSrv.Gpu);
        cmd.DrawInstanced(6, (uint)drawInstanceCount, 0, 0);
        return true;
    }

    /// <summary>
    ///     FNV generated water is materialized in contiguous XCWT batches.  Each batch records its
    ///     own recovered noise prepass immediately before its draw because the 256x256 normal target
    ///     is intentionally shared; command-list ordering guarantees that a later WATR cannot
    ///     overwrite an earlier batch before that earlier draw consumes it.  Placed NIF water stays
    ///     on WATER003 but resolves each surface's OWN water identity (stamped at placement from
    ///     its cell's XCWT) through the same catalog, in contiguous per-WATR runs.
    /// </summary>
    private unsafe int RenderFnvWaterMaterialBatches(
        Matrix4x4 viewProj,
        VisibilityCylinder cylinder,
        Vector3 renderOrigin,
        float elapsedSeconds,
        ID3D12GraphicsCommandList cmd,
        int frameIndex,
        int cellVisible,
        int nifVisible,
        int nifPacketCount,
        int instanceCount,
        bool useFnvWater001,
        in FnvWater001SnapshotDescriptor snapshot,
        long frameStarted,
        long uploadStarted)
    {
        UploadInstances(instanceCount);
        LastStats.GpuUploadMilliseconds = ElapsedMilliseconds(uploadStarted);

        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        GpuRootSignature12.SetGraphicsBindlessTables(cmd, _cbvSrvUavHeap.BindlessHeapStartGpu);

        var drawStarted = StartTiming();
        var depthSample = _depthBindlessIndex != NoNormalMap;
        var usedAnyNoisePrepass = false;
        var drawnCells = 0;
        var drewNifPackets = false;
        // C# does not allow an `in` parameter to be captured by a local function. Copy the small
        // immutable descriptor once; every material batch consumes the same frame snapshot.
        var args = new FnvBatchDrawArgs(
            cmd, frameIndex, viewProj, cylinder.Position, renderOrigin, elapsedSeconds,
            snapshot.BindlessIndex, snapshot.Width, snapshot.Height, depthSample);

        bool DrawBatch(
            in ResolvedFnvWaterMaterial material,
            int startInstance,
            int drawInstanceCount,
            bool water001) =>
            DrawFnvWaterBatch(args, material, startInstance, drawInstanceCount, water001,
                ref usedAnyNoisePrepass);

        foreach (var batch in _fnvWaterCellDrawBatches)
        {
            if (!DrawBatch(
                    batch.Material,
                    batch.StartInstance,
                    batch.InstanceCount,
                    useFnvWater001))
            {
                break;
            }
            drawnCells += batch.InstanceCount;
        }

        if (nifPacketCount > 0)
        {
            // Placed water renders with the water type of where it IS — each per-WATR run resolves
            // through the same catalog as cell water. The old single-batch draw used the CAMERA
            // cell's compatibility appearance, whose selection is sticky flight history: crossing
            // an XCWT boundary mid-flight repainted every placed surface wholesale.
            foreach (var nifBatch in _fnvNifDrawBatches)
            {
                if (!DrawBatch(
                        ResolveFnvWaterMaterial(nifBatch.WaterFormId),
                        cellVisible + nifBatch.StartPacket,
                        nifBatch.PacketCount,
                        water001: false))
                {
                    break;
                }
                drewNifPackets = true;
            }
        }

        LastStats.WaterNoisePrepassUsed = usedAnyNoisePrepass;
        WriteFnvBatchTechniqueTelemetry(useFnvWater001, nifPacketCount);

        LastStats.DrawCallMilliseconds = ElapsedMilliseconds(drawStarted);
        var waterDraws = drawnCells + (drewNifPackets ? nifVisible : 0);
        LastStats.WaterDraws = waterDraws;
        LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(frameStarted);
        return waterDraws;
    }

    /// <summary>Writes the FNV batch path's technique/reason telemetry. Depends only on the frame's
    /// WATER001 decision and batch counts, so the unified-transparency defer path can emit it at
    /// queue time and the legacy inline path after its draws — one source of truth for the strings
    /// the HUD/trace consumers (and their pinned tests) read.</summary>
    private void WriteFnvBatchTechniqueTelemetry(bool useFnvWater001, int nifPacketCount)
    {
        var depthRoute = _depthSampleCount > 1
            ? $"msaa{_depthSampleCount}x"
            : "1x";
        if (useFnvWater001)
        {
            LastStats.WaterTechnique =
                $"FnvWater001Reconstructed-opaque-snapshot-main-scene-depth-approx-{depthRoute}";
            if (_fnvWaterCellDrawBatches.Count > 1)
            {
                LastStats.WaterTechnique += $"+multi-watr-{_fnvWaterCellDrawBatches.Count}";
            }
            if (nifPacketCount > 0)
            {
                LastStats.WaterTechnique +=
                    $"+FnvWater003RtFree-scene-depth-{depthRoute}-placed-nif";
            }
            LastStats.WaterTelemetryUnavailableReason =
                "selective-content-mask-approximated-by-main-depth";
        }
        else
        {
            LastStats.WaterTechnique = DescribeTechnique(
                _game,
                _waterProfile.ShaderVariant,
                false,
                ModernWaterTechnique.Baseline,
                _depthBindlessIndex != NoNormalMap,
                _depthSampleCount);
            if (_fnvWaterCellDrawBatches.Count > 1)
            {
                LastStats.WaterTechnique += $"+multi-watr-{_fnvWaterCellDrawBatches.Count}";
            }
            LastStats.WaterTelemetryUnavailableReason = LastFnvWater001Decision.ReasonCode;
        }
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
        if (!modernPipeline && shaderVariant == WaterShaderVariant.FlatTinted)
        {
            // The flat plane reads no scene depth by design, so it must not claim a scene-depth
            // route just because the host happened to bind one. Its occlusion is the hardware
            // GreaterEqual test alone, in either host depth state.
            depthName = "hardware-depth-only";
        }
        else if (sceneDepth)
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
        _psoFnvWater001DepthSample.Dispose();
        _psoOblivion.Dispose();
        _psoOblivionDepthSample.Dispose();
        _psoFo4.Dispose();
        _psoFo4DepthSample.Dispose();
        _psoMorrowind.Dispose();
        _psoMorrowindDepthSample.Dispose();
        _psoFlat.Dispose();
        _fnvNoiseScrollBlendPso.Dispose();
        _fnvNoiseNormalPso.Dispose();
        _fnvNoiseDownsamplePso.Dispose();
        foreach (var tiles in _fnvNoiseTiles)
        {
            _deletionQueue.EnqueueDispose(tiles.BlendTexture);
            _deletionQueue.EnqueueDispose(tiles.NormalTexture);
            _deletionQueue.EnqueueDispose(
                new PersistentSlotReturn(_cbvSrvUavHeap, tiles.BlendBindlessIndex));
            _deletionQueue.EnqueueDispose(
                new PersistentSlotReturn(_cbvSrvUavHeap, tiles.NormalBindlessIndex));
            foreach (var mipSrvIndex in tiles.NormalMipSrvIndices)
            {
                _deletionQueue.EnqueueDispose(new PersistentSlotReturn(_cbvSrvUavHeap, mipSrvIndex));
            }
        }
        _modernWater?.DisposeInto(_deletionQueue, _cbvSrvUavHeap);
        _modernWater = null;
    }

    private readonly record struct FnvWater001VisibleCellContract(
        int Count,
        float PlaneHeight,
        bool MixedPlaneHeights,
        bool HasEffectiveWaterType,
        bool MixedWaterTypes,
        uint? EffectiveWaterFormId);

    private uint EffectiveFnvWaterFormId(global::BethesdaMultitool.Core.WorldData.WorldWaterCell water) =>
        water.Cell.WaterFormId is > 0
            ? water.Cell.WaterFormId.Value
            : _fnvWater001WorldspaceDefaultWaterFormId ?? 0u;

    private ResolvedFnvWaterMaterial ResolveFnvWaterMaterial(uint formId)
    {
        if (TryResolveFnvWaterMaterial(formId, out var material))
        {
            return material;
        }

        // Water renders with the type of where it IS: an unresolved/zero identity inherits the
        // WORLDSPACE default (WRLD NAM2), never the camera's cell — that selection is sticky
        // flight history. The camera-cell compatibility appearance survives only as the last
        // resort (interiors — where the camera cell IS the water's cell — and worldspaces with no
        // NAM2). Either fallback stays out of WATER001 (the aggregate preflight rejects the
        // missing exact binding).
        if (_fnvWater001WorldspaceDefaultWaterFormId is { } worldspaceDefault &&
            TryResolveFnvWaterMaterial(worldspaceDefault, out material))
        {
            return material;
        }

        // Engine terminal default (decompile-PROVEN): TESDataHandler::GenerateDefaultObjects pins
        // the global fallback water to WATR FormID 0x18 ("DefaultWater") — TESObjectCELL/
        // TESWorldSpace::GetWaterType both end at that hardcoded global; no GMST/INI is involved.
        if (TryResolveFnvWaterMaterial(EngineDefaultWaterFormId, out material))
        {
            return material;
        }

        return new ResolvedFnvWaterMaterial(
            _appearance,
            _noiseBindlessIndex,
            _normalBindlessIndices[0],
            _normalBindlessIndices[1],
            _normalBindlessIndices[2],
            formId);
    }

    private bool TryResolveFnvWaterMaterial(uint formId, out ResolvedFnvWaterMaterial material)
    {
        if (formId > 0 && _fnvWaterMaterials.TryGetValue(formId, out var binding))
        {
            var first = NoNormalMap;
            if (binding.NormalMapBindlessIndices is not null)
            {
                for (var i = 0; i < binding.NormalMapBindlessIndices.Count; i++)
                {
                    if (binding.NormalMapBindlessIndices[i] is { } resolved)
                    {
                        first = resolved;
                        break;
                    }
                }
            }

            uint ResolveIndex(int index) =>
                binding.NormalMapBindlessIndices is not null &&
                index < binding.NormalMapBindlessIndices.Count
                    ? binding.NormalMapBindlessIndices[index] ?? first
                    : first;

            material = new ResolvedFnvWaterMaterial(
                binding.Appearance,
                first,
                ResolveIndex(0),
                ResolveIndex(1),
                ResolveIndex(2),
                formId);
            return true;
        }

        // Synthetic/profiler fixtures historically supplied only SetAppearance + the selected
        // identity. Preserve that exact one-material contract without allowing an unrelated
        // camera material to masquerade as a missing CELL binding.
        if (formId > 0 && formId == _fnvWater001SelectedWaterFormId)
        {
            material = new ResolvedFnvWaterMaterial(
                _appearance,
                _noiseBindlessIndex,
                _normalBindlessIndices[0],
                _normalBindlessIndices[1],
                _normalBindlessIndices[2],
                formId);
            return true;
        }

        material = default;
        return false;
    }

    private FnvWater001VisibleCellContract InspectFnvWater001VisibleCells(
        List<global::BethesdaMultitool.Core.WorldData.WorldWaterCell> cells)
    {
        if (cells.Count == 0)
        {
            return new FnvWater001VisibleCellContract(0, 0f, false, false, false, null);
        }

        var planeHeight = cells[0].Height;
        var mixedPlaneHeights = false;
        var hasEffectiveWaterType = true;
        var mixedWaterTypes = false;
        uint? commonWaterFormId = null;
        var hasCommonWaterFormId = false;
        for (var i = 0; i < cells.Count; i++)
        {
            var water = cells[i];
            // One global plane equation is appended to the frame CB. Exact equality is intentional:
            // accepting an epsilon-separated surface would make one packet reconstruct against a
            // different plane than the geometry the pixel came from.
#pragma warning disable S1244 // exact plane-height identity by design (see comment above)
            if (water.Height != planeHeight)
#pragma warning restore S1244
            {
                mixedPlaneHeights = true;
            }

            var effectiveWaterFormId = EffectiveFnvWaterFormId(water);
            if (effectiveWaterFormId == 0)
            {
                hasEffectiveWaterType = false;
                continue;
            }
            if (!hasCommonWaterFormId)
            {
                commonWaterFormId = effectiveWaterFormId;
                hasCommonWaterFormId = true;
            }
            else if (commonWaterFormId != effectiveWaterFormId)
            {
                mixedWaterTypes = true;
            }
        }

        return new FnvWater001VisibleCellContract(
            cells.Count,
            planeHeight,
            mixedPlaneHeights,
            hasEffectiveWaterType && hasCommonWaterFormId,
            mixedWaterTypes,
            commonWaterFormId);
    }

    private FnvWater001Preflight EvaluateFnvWater001(
        List<global::BethesdaMultitool.Core.WorldData.WorldWaterCell> visibleCells,
        in FnvWater001VisibleCellContract cells,
        float cameraHeight,
        bool isPerspectiveProjection,
        bool requireSnapshot,
        in FnvWater001SnapshotDescriptor snapshot)
    {
        // WATER001 is a per-material shader draw.  Mixed visible WATRs are therefore valid once
        // generated CELL packets are split into per-WATR batches; every distinct binding must pass
        // the exact single-material contract independently, while the one shared plane remains a
        // global requirement.
        _fnvVisibleWaterTypeScratch.Clear();
        for (var i = 0; i < visibleCells.Count; i++)
        {
            var formId = EffectiveFnvWaterFormId(visibleCells[i]);
            if (formId > 0) _fnvVisibleWaterTypeScratch.Add(formId);
        }

        if (_fnvVisibleWaterTypeScratch.Count == 0)
        {
            var missingSurface = _appearance?.Surface ?? WaterSurfaceParams.Default;
            return FnvWater001Contract.Evaluate(new FnvWater001EligibilityInput(
                _game, _waterProfile.ShaderVariant, _appearance?.IsLava == true,
                isPerspectiveProjection, _depthBindlessIndex != NoNormalMap,
                requireSnapshot, snapshot.IsValid, snapshot.Width, snapshot.Height,
                cells.Count, _appearance is not null,
                missingSurface.HasAuthoredClassicRefractionInputs,
                _fnvWater001HasWaterTypeContext, false, false, cells.MixedPlaneHeights,
                cells.PlaneHeight, cameraHeight,
                missingSurface.DepthFalloffStart, missingSurface.DepthFalloffEnd,
                missingSurface.UnderwaterFogNear, missingSurface.UnderwaterFogFar,
                missingSurface.AboveWaterFogAmount, missingSurface.RefractionDistortionAmount,
                null));
        }

        foreach (var formId in _fnvVisibleWaterTypeScratch)
        {
            var hasMaterial = TryResolveFnvWaterMaterial(formId, out var material);
            var appearance = hasMaterial ? material.Appearance : null;
            var surface = appearance?.Surface ?? WaterSurfaceParams.Default;
            var result = FnvWater001Contract.Evaluate(new FnvWater001EligibilityInput(
                _game,
                _waterProfile.ShaderVariant,
                appearance?.IsLava == true,
                isPerspectiveProjection,
                _depthBindlessIndex != NoNormalMap,
                requireSnapshot,
                snapshot.IsValid,
                snapshot.Width,
                snapshot.Height,
                cells.Count,
                appearance is not null,
                surface.HasAuthoredClassicRefractionInputs,
                _fnvWater001HasWaterTypeContext,
                cells.HasEffectiveWaterType && hasMaterial,
                // This invocation describes one draw batch. The renderer's aggregate loop is the
                // proof that mixed identities do not share constants.
                HasMixedWaterTypes: false,
                HasMixedPlaneHeights: cells.MixedPlaneHeights,
                PlaneHeight: cells.PlaneHeight,
                CameraHeight: cameraHeight,
                DepthFalloffStart: surface.DepthFalloffStart,
                DepthFalloffEnd: surface.DepthFalloffEnd,
                UnderwaterFogNear: surface.UnderwaterFogNear,
                UnderwaterFogFar: surface.UnderwaterFogFar,
                AboveWaterFogAmount: surface.AboveWaterFogAmount,
                RefractionDistortionAmount: surface.RefractionDistortionAmount,
                EffectiveWaterFormId: formId));
            if (!result.Candidate) return result;
        }

        return new FnvWater001Preflight(
            true,
            FnvWater001FallbackReason.None,
            cells.PlaneHeight,
            _fnvVisibleWaterTypeScratch.Count == 1 ? _fnvVisibleWaterTypeScratch.First() : null);
    }

    private void ClearFnvWater001TransientState(bool clearWaterTypeContext)
    {
        _fnvWater001PendingPreflight =
            FnvWater001Preflight.Fallback(FnvWater001FallbackReason.SnapshotUnavailable);
        _fnvWater001Snapshot = default;
        if (!clearWaterTypeContext) return;
        _fnvWater001SelectedWaterFormId = null;
        _fnvWater001WorldspaceDefaultWaterFormId = null;
        _fnvWater001HasWaterTypeContext = false;
    }

    private int GatherVisibleWater(VisibilityCylinder cylinder)
    {
        _visibleWaterScratch.Clear();
        // One-cell exit margin matching the terrain gather's draw-only hysteresis so water and
        // terrain leave the screen in lockstep at the pushed-out boundary (a strict same-radius
        // test popped whole visible boundary cells off in one frame under camera motion). Water
        // instances are fully resident, so the margin costs nothing to draw.
        var gatherRadius = cylinder.Radius + WorldGridConstants.CellSize;
        if (_spatialIndex is not null)
        {
            _spatialIndex.QueryWaterCellsInRadius(
                cylinder.Position.X,
                -cylinder.Position.Y,
                gatherRadius,
                _visibleWaterScratch);
            return _visibleWaterScratch.Count;
        }

        var marginCylinder = new VisibilityCylinder(cylinder.Position, gatherRadius);
        foreach (var water in _waterCells)
        {
            var key = water.Key;
            if (marginCylinder.ContainsCell(key.gx, key.gy))
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
        // A WNAM-inherited default (TES4 child worldspaces) only waters Has-Water-flagged cells.
        if (DefaultWaterRequiresCellHasWater && !cell.HasWater)
            return null;
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
        var stride = Marshal.SizeOf<WaterInstance>();
        var byteCount = (uint)(instanceCount * stride);
        // Write this frame's slot only — the previous frame's slot may still be feeding in-flight
        // draws on the GPU.
        var slotOffset = (nint)_recorder.FrameIndex * _instanceCapacity * stride;
        fixed (WaterInstance* src = _instanceScratch)
        {
            System.Runtime.CompilerServices.Unsafe.CopyBlockUnaligned(
                destination: (void*)(_instanceMapped + slotOffset),
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
        // One slot per frame in flight — see the _instanceBuffer field doc.
        var byteWidth = (ulong)capacity * stride * GpuCommandRecorder12.FramesInFlight;

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
    }

    private long StartTiming() => DetailedProfilingEnabled ? Stopwatch.GetTimestamp() : 0;

    private static double ElapsedMilliseconds(long started) =>
        started == 0 ? 0 : Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    // D3DCOMPILE_ENABLE_UNBOUNDED_DESCRIPTOR_TABLES — required by fxc/D3DCompile for any shader
    // that declares an unbounded resource array (e.g. `Texture2D gWaterTextures[] : register(t0, space1)`).
    /// <summary>
    ///     Forwards to the one shared compiler. The private copy this replaces detected the
    ///     unbounded-descriptor need by scanning for <c>"[] : register"</c> — a DIFFERENT rule from
    ///     the sibling renderers' <c>"textures[]"</c>, because water's array is
    ///     <c>gWaterTextures[]</c>. That divergence is exactly why the flag is now unconditional in
    ///     <see cref="GpuShaderCompiler12" /> instead of inferred from source text.
    /// </summary>
    private static byte[] CompileEmbeddedShader(
        string name, string entryPoint, string profile, params ShaderMacro[] defines) =>
        GpuShaderCompiler12.Compile(name, entryPoint, profile, defines);

    /// <summary>
    ///     TES4 surface-animation tile. The machine-bound near-field permutation WATER007 samples
    ///     the NormalMap at <c>t7.zw = (worldXY + QPosAdjust) · (3/4096)</c> — a 4096/3 ≈ 1365.33
    ///     world-unit tile hard-wired in the water VS (oblivion_water_shaders asm, regenerated
    ///     2026-08-08; the 2026-08-08 adversarial review explicitly inverted the earlier
    ///     "do not use 1365.33" note for this variant). The ini's <c>fSurfaceTileSize = 2048</c>
    ///     feeds the mesh UV (t6), which WATER007 gives to the DisplacementMap — a sim input the
    ///     viewer reproduces for no game. Oblivion authors no per-WATR NormalsUVScale (the parsed
    ///     value is FNV's default 1000); other variants keep the authored/parsed scale.
    /// </summary>
    private float ResolveSurfaceUvScale(WaterSurfaceParams surface) =>
        _waterProfile.ShaderVariant == WaterShaderVariant.OblivionWater000
            ? 4096f / 3f
            : surface.NormalsUvScale;

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
        if (uniformSize != (int)WaterFrameUniformsByteSize)
        {
            throw new InvalidOperationException(
                $"Water constant-buffer layout drifted: CPU={uniformSize} bytes, " +
                $"HLSL={WaterFrameUniformsByteSize} bytes.");
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
                    "water_fo4.frag.hlsl",
                    "main",
                    "ps_5_1",
                    new ShaderMacro("FO4_WATER_ARCHITECTURAL", "1"));
                // The depth-sample template carries a hardware GreaterEqual test against the
                // host's read-only DSV, so its pixel shader must be the WATER_HARDWARE_OCCLUSION
                // compile (occlusion clip dropped) like every other depth-sample PSO.
                var modernPixelDepthSampleBytecode = CompileEmbeddedShader(
                    "water_fo4.frag.hlsl",
                    "main",
                    "ps_5_1",
                    new ShaderMacro("FO4_WATER_ARCHITECTURAL", "1"),
                    new ShaderMacro("WATER_HARDWARE_OCCLUSION", "1"));
                var pixelDescription = depthTemplate;
                pixelDescription.PixelShader = modernPixelBytecode;
                pixel = gpu.Device.CreateGraphicsPipelineState(pixelDescription);
                var pixelDepthDescription = depthSampleTemplate;
                pixelDepthDescription.PixelShader = modernPixelDepthSampleBytecode;
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

        internal void DisposeInto(
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
        // Bounded FNV WATER001 tail. The uint/float quartet is one raw register matching
        // uFnvWater001Snapshot: (snapshot SRV, width, height, asuint(horizontal plane height)).
        public uint FnvWater001SnapshotIndex;
        public uint FnvWater001SnapshotWidth;
        public uint FnvWater001SnapshotHeight;
        public float FnvWater001PlaneHeight;
        // UnderwaterFogNear/Far, AboveWaterFogAmount, RefractionDistortionAmount.
        public Vector4 FnvWater001Surface;
        // Planar sky-reflection target: (SRV index, scene viewport width, height,
        // asuint(UV distortion scale)). See uWaterReflection / SampleSkyReflection.
        public uint WaterReflectionIndex;
        public uint WaterReflectionSceneWidth;
        public uint WaterReflectionSceneHeight;
        public float WaterReflectionDistortion;
        // Appended: the mirror pass's view-projection (origin-relative) + the scene-content flag
        // (x = 1 ⇒ the RT holds mirrored SCENE content and Oblivion's projective WATER007 arm
        // applies; 0 ⇒ sky-only screen-UV semantics). See uReflectionViewProj / uReflectionParams.
        public Matrix4x4 ReflectionViewProj;
        public uint ReflectionSceneFlag;
        public uint ReflectionPad0;
        public uint ReflectionPad1;
        public uint ReflectionPad2;
    }
}

#endif
