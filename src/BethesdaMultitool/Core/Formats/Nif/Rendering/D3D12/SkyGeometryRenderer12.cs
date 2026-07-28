#if WINDOWS_GUI
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D12 = Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Renders the REAL climate sky-dome NIF geometry — the atmosphere/stars/clouds layers the engine
///     itself draws (extracted from the game's own <c>Sky\*.nif</c>) — instead of a procedural dome. Each
///     layer is drawn camera-centered, depth-OFF, on the dome's OWN authored UVs, with a blend mode keyed
///     to its <see cref="SkyObjectType" /> (authored blend-weight/opaque for SKY, additive for STARS, alpha for
///     CLOUDS). Using authored geometry + UVs removes the procedural dome's tiling stretch, horizon seam,
///     and "too far" feel, and the per-layer texture is data-driven (the weather's cloud textures, the
///     sky NIF's stars), not a hardcoded heuristic.
///     <para>
///         Layer geometry is uploaded through the per-frame ring buffer (the sky is small — a few dome
///         shapes — so per-frame copies beat managing static buffers and their residency). Drawn with
///         culling off + depth-clip on, like the old <c>SkyDomeRenderer12</c> it replaces.
///     </para>
/// </summary>
internal sealed class SkyGeometryRenderer12 : IDisposable
{
    private const float TargetRadius = 12000f;     // normalize the NIF dome to this radius (> near, < far)
    private const uint NoTexture = 0xFFFFFFFFu;

    [StructLayout(LayoutKind.Sequential)]
    private struct SkyVertex
    {
        public Vector3 Pos;
        public Vector2 Uv;
        public uint Color; // RGBA8 — the NIF's per-vertex color; ALPHA is the artist-baked horizon fade
    }

    private const uint VertexStride = 24; // float3 pos + float2 uv + rgba8 color

    /// <summary>One uploaded sky layer: interleaved geometry + how to draw it.</summary>
    private sealed class GpuLayer
    {
        public required SkyVertex[] Vertices;
        public required ushort[] Indices;
        public int Mode;                 // 0 atmosphere, 1 stars, 2 clouds
        public bool HasAuthoredBlendWeights; // SKY vertex RGB selects horizon/lower/upper BlendColor rows
        public uint TexIndex;            // bindless diffuse index
        public Vector2 ScrollVelocity;   // UV/sec drift (clouds: per-layer from QNAM/RNAM; stars: zero)
        public WeatherColor? CloudColor; // PNAM per-layer cloud color (RGB tint + A opacity); null = fallback
        public WeatherColor? OutgoingCloudColor; // outgoing PNAM sampled before one weather blend
        public WeatherCloudAlpha? CloudAlpha; // JNAM per-layer opacity (modern weathers); null = no JNAM
        public WeatherCloudAlpha? OutgoingCloudAlpha; // outgoing JNAM sampled before one weather blend
        public float? CloudCurrentWeatherWeight; // null for atomic weather; otherwise current=t, outgoing=1-t
        public float CloudWeatherWeight; // texture contribution; 1 when equal textures coalesce to one draw
        public int CloudSourceIndex;
        public bool IsOutgoingCloudPass;
    }

    private sealed class CloudScrollState
    {
        public Vector2 Offset;
        public long UpdatedFrame;
    }

    private readonly GpuCommandRecorder12 _recorder;
    private readonly GpuRingBuffer12 _ringBuffer;
    private readonly GpuDescriptorHeapAllocator12 _cbvSrvUavHeap;
    private readonly ID3D12PipelineState _psoGradient;
    private readonly ID3D12PipelineState _psoStars;
    private readonly ID3D12PipelineState _psoClouds;
    private readonly List<GpuLayer> _layers = new();
    private readonly Dictionary<int, CloudScrollState> _cloudScrollStates = new();
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private long _lastScrollTimestamp = Stopwatch.GetTimestamp();
    private long _scrollFrame;
    // Procedural gradient dome used only when no authored Atmosphere.nif layer was recovered. Games whose
    // atmosphere NIFs cannot be parsed still get a sky instead of a black void.
    private readonly SkyVertex[] _fallbackVerts;
    private readonly ushort[] _fallbackIndices;
    private bool _disposed;

    public SkyGeometryRenderer12(
        GpuDevice12 gpu,
        GpuCommandRecorder12 recorder,
        GpuRingBuffer12 ringBuffer,
        GpuRootSignature12 rootSignature,
        GpuDescriptorHeapAllocator12 cbvSrvUavHeap)
    {
        _recorder = recorder;
        _ringBuffer = ringBuffer;
        _cbvSrvUavHeap = cbvSrvUavHeap;

        var vs = CompileEmbeddedShader("sky_geo.vert.hlsl", "main", "vs_5_1");
        var ps = CompileEmbeddedShader("sky_geo.frag.hlsl", "main", "ps_5_1");

        _psoGradient = CreatePso(gpu, rootSignature, vs, ps, SkyBlend.Opaque);
        _psoStars = CreatePso(gpu, rootSignature, vs, ps, SkyBlend.Additive);
        _psoClouds = CreatePso(gpu, rootSignature, vs, ps, SkyBlend.Alpha);

        (_fallbackVerts, _fallbackIndices) = GenerateGradientDome();
    }

    // A low-res UV-sphere of unit directions (Z up); the gradient PS reads dir.z for the horizon→top blend
    // so it only needs directions, not a real dome. Rendered camera-centered at a fixed radius.
    private static (SkyVertex[] Verts, ushort[] Indices) GenerateGradientDome()
    {
        const int rings = 16;    // latitude, -90..+90
        const int segments = 24; // longitude, 0..360
        var verts = new SkyVertex[(rings + 1) * (segments + 1)];
        var vi = 0;
        for (var r = 0; r <= rings; r++)
        {
            var lat = (MathF.PI * r / rings) - (MathF.PI / 2f); // -pi/2 .. +pi/2
            var cosLat = MathF.Cos(lat);
            var sinLat = MathF.Sin(lat);
            for (var s = 0; s <= segments; s++)
            {
                var lon = MathF.Tau * s / segments;
                verts[vi++] = new SkyVertex
                {
                    Pos = new Vector3(cosLat * MathF.Cos(lon), cosLat * MathF.Sin(lon), sinLat),
                    Uv = Vector2.Zero,
                    Color = 0xFFFFFFFFu, // gradient PS (mode 0) ignores vertex color; white keeps the format consistent
                };
            }
        }

        var indices = new List<ushort>(rings * segments * 6);
        for (var r = 0; r < rings; r++)
        {
            for (var s = 0; s < segments; s++)
            {
                var a = (ushort)((r * (segments + 1)) + s);
                var b = (ushort)(a + segments + 1);
                indices.Add(a); indices.Add(b); indices.Add((ushort)(a + 1));
                indices.Add((ushort)(a + 1)); indices.Add(b); indices.Add((ushort)(b + 1));
            }
        }

        return (verts, indices.ToArray());
    }

    public bool HasLayers => _layers.Count > 0;

    /// <summary>
    ///     Replaces the sky geometry with a new climate's ATMOSPHERE + STARS + CLOUDS layers. Each input
    ///     carries its mesh + UVs + indices, its <see cref="SkyObjectType" />,
    ///     and its resolved bindless texture index. The vertex shader places every layer's vertices on one
    ///     camera-centered sphere by DIRECTION (so the layers' very different authored sizes/centers don't
    ///     matter — they all overlay into one sky), so no per-layer scale is needed here.
    /// </summary>
    public void SetLayers(IReadOnlyList<SkyGeometryLayer> layers)
    {
        _layers.Clear();

        foreach (var layer in layers)
        {
            var mode = ModeFor(layer.Type);

            var positions = layer.Positions;
            var uvs = layer.Uvs;
            var colors = layer.VertexColors; // RGBA bytes; alpha = the baked horizon fade (null -> opaque)
            var vertexCount = positions.Length / 3;
            var verts = new SkyVertex[vertexCount];
            for (var v = 0; v < vertexCount; v++)
            {
                verts[v].Pos = new Vector3(positions[v * 3], positions[(v * 3) + 1], positions[(v * 3) + 2]);
                verts[v].Uv = uvs != null && (v * 2) + 1 < uvs.Length
                    ? new Vector2(uvs[v * 2], uvs[(v * 2) + 1])
                    : Vector2.Zero;
                // Pack RGBA bytes so R8G8B8A8_UNORM reads .r=R .g=G .b=B .a=A. The mesh's vertex alpha IS
                // the engine's cloud-dome fade (cloudcloudy ~2 at the rim/horizon -> 255 overhead).
                verts[v].Color = colors != null && (v * 4) + 3 < colors.Length
                    ? (uint)(colors[v * 4] | (colors[(v * 4) + 1] << 8) | (colors[(v * 4) + 2] << 16) | (colors[(v * 4) + 3] << 24))
                    : 0xFF0000FFu; // BlendColor0 weight=1, alpha=1; G/B are weights, not an RGB tint
            }

            _layers.Add(new GpuLayer
            {
                Vertices = verts,
                Indices = layer.Indices,
                Mode = mode,
                HasAuthoredBlendWeights = mode == 0 && colors is not null,
                TexIndex = layer.TextureIndex,
                ScrollVelocity = mode == 2 ? layer.ScrollSpeed : Vector2.Zero,
                CloudColor = mode == 2 ? layer.CloudColor : null,
                OutgoingCloudColor = mode == 2 ? layer.OutgoingCloudColor : null,
                CloudAlpha = mode == 2 ? layer.CloudAlpha : null,
                OutgoingCloudAlpha = mode == 2 ? layer.OutgoingCloudAlpha : null,
                CloudCurrentWeatherWeight = mode == 2 ? layer.CloudCurrentWeatherWeight : null,
                CloudWeatherWeight = mode == 2 ? Math.Clamp(layer.CloudWeatherWeight, 0f, 1f) : 1f,
                CloudSourceIndex = mode == 2 ? layer.CloudSourceIndex : -1,
                IsOutgoingCloudPass = mode == 2 && layer.IsOutgoingCloudPass,
            });
        }
    }

    /// <summary>
    ///     Updates the continuously changing weather percentage without rebuilding the retained NIF
    ///     geometry. Both texture candidates for one source layer receive the same recovered velocity;
    ///     equal-texture transitions were already coalesced when the topology was built.
    /// </summary>
    public void UpdateCloudWeatherTransition(
        WeatherRecord? currentWeather,
        WeatherRecord? outgoingWeather,
        float currentWeatherWeight,
        BethesdaGame game)
    {
        foreach (var layer in _layers)
        {
            if (layer.Mode != 2 || layer.CloudSourceIndex < 0)
            {
                continue;
            }

            var transition = WeatherCloudTransitionResolver.Resolve(
                currentWeather, outgoingWeather, layer.CloudSourceIndex, currentWeatherWeight, game);
            layer.ScrollVelocity = transition.ScrollVelocity;
            layer.CloudCurrentWeatherWeight = outgoingWeather is null
                ? null
                : transition.CurrentWeatherWeight;
            layer.CloudWeatherWeight = layer.IsOutgoingCloudPass
                ? transition.OutgoingTextureWeight
                : transition.CurrentTextureWeight;
        }
    }

    public void Clear() => _layers.Clear();

    /// <summary>Authored source indices with resolved NIF geometry and textures in the retained topology.</summary>
    public int[] GetCookedCloudSourceIndices() => _layers
        .Where(static layer => layer.Mode == 2 && layer.CloudSourceIndex >= 0)
        .Select(static layer => layer.CloudSourceIndex)
        .Distinct()
        .Order()
        .ToArray();

    /// <summary>Number of retained texture candidates for one source; active-only excludes zero-weight endpoints.</summary>
    public int GetCookedCloudDrawCandidateCount(int sourceIndex, bool activeOnly = false) => _layers.Count(layer =>
        layer.Mode == 2 &&
        layer.CloudSourceIndex == sourceIndex &&
        (!activeOnly || layer.CloudWeatherWeight > 0.001f));

    /// <summary>
    ///     Draws every sky layer for the frame, centered on <paramref name="camPos" />. Must run AFTER the
    ///     per-frame atmosphere (b3) bind — the gradient layer reads the sky colors from it — and BEFORE
    ///     terrain (depth-off background that terrain overwrites), with the bindless heap bound. Stars use
    ///     <paramref name="starTint" />/<paramref name="starFade" />; clouds use
    ///     <paramref name="cloudTint" />/<paramref name="cloudOpacity" /> (pass 0 opacity/fade to suppress
    ///     them, e.g. in interiors).
    /// </summary>
    public void Render(
        Matrix4x4 viewProj, Vector3 camPos,
        Vector3 skyUpper, Vector3 skyLower, Vector3 skyHorizon, Vector3 fallbackHorizon,
        Vector3 cloudTint, float cloudOpacity, Vector3 starTint, float starFade,
        float gameHour, AtmosphereState.ClimateTiming? cloudTiming, BethesdaGame game,
        float? animationTimeSeconds = null)
    {
        if (_disposed) return;

        var frameIndex = _recorder.FrameIndex;
        var cmd = _recorder.CommandList;
        var elapsed = animationTimeSeconds is { } pinned && float.IsFinite(pinned) && pinned >= 0f
            ? pinned
            : (float)Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds;
        var pinnedAnimation = animationTimeSeconds is { } captureTime &&
                              float.IsFinite(captureTime) && captureTime >= 0f;
        var scrollDeltaSeconds = 0f;
        var scrollFrame = 0L;
        if (!pinnedAnimation)
        {
            var now = Stopwatch.GetTimestamp();
            scrollDeltaSeconds = (float)Stopwatch.GetElapsedTime(_lastScrollTimestamp, now).TotalSeconds;
            _lastScrollTimestamp = now;
            scrollFrame = ++_scrollFrame;

            // Clouds::Update advances the retained offset independently of draw visibility. Do this before
            // opacity, endpoint-weight, texture, or JNAM suppression so a hidden layer cannot freeze and
            // then jump behind the rest of the sky when it becomes visible again.
            foreach (var cloudLayer in _layers)
            {
                if (cloudLayer.Mode != 2 || cloudLayer.CloudSourceIndex < 0)
                {
                    continue;
                }

                if (!_cloudScrollStates.TryGetValue(cloudLayer.CloudSourceIndex, out var state))
                {
                    state = new CloudScrollState();
                    _cloudScrollStates.Add(cloudLayer.CloudSourceIndex, state);
                }

                if (state.UpdatedFrame == scrollFrame)
                {
                    continue; // current/outgoing candidates for one source share exactly one integration
                }

                state.Offset = WeatherCloudTransitionResolver.AdvanceOffset(
                    state.Offset, cloudLayer.ScrollVelocity, scrollDeltaSeconds);
                state.UpdatedFrame = scrollFrame;
            }
        }

        cmd.SetGraphicsRootDescriptorTable(GpuRootSignature12.Slots.BindlessSrvTable, _cbvSrvUavHeap.BindlessHeapStartGpu);

        // The procedural dome is strictly a missing-asset fallback. An authored Atmosphere.nif owns the
        // background whenever one was decoded, including its non-linear vertex blend bands.
        if (!_layers.Any(static layer => layer.Mode == 0))
        {
            DrawMesh(cmd, frameIndex, viewProj, camPos, _fallbackVerts, _fallbackIndices,
                mode: 0, scale: TargetRadius, fallbackHorizon, 1f, Vector2.Zero, NoTexture,
                skyUpper, skyLower, skyHorizon, authoredAtmosphere: false);
        }

        // Real atmosphere + stars + clouds geometry.
        foreach (var layer in _layers)
        {
            if ((layer.Mode == 1 && starFade <= 0.001f)
                || (layer.Mode == 2 && (cloudOpacity <= 0.001f || layer.CloudWeatherWeight <= 0.001f)) ||
                (layer.Mode != 0 && layer.TexIndex == NoTexture))
            {
                continue; // suppressed this frame (interior) or no resolved texture
            }

            // Per-layer cloud OPACITY from the weather's JNAM "Cloud Alphas" (modern weathers) — the engine's
            // real per-layer cloud alpha. A weather hides a cloud-dome shape by authoring 0 and thins others
            // with fractions, so a CLEAR weather (most layers 0/low) shows sky while a CLOUDY one (all 1.0)
            // overcasts. Applied as a MULTIPLIER on the host opacity (which keeps the global translucency +
            // day/night fade), so FO3/FNV — no JNAM → factor 1.0 — render exactly as before.
            var currentCloudAlpha = layer.CloudAlpha is { } ca
                ? AtmosphereState.SampleCloudAlpha(ca, gameHour, cloudTiming, game)
                : 1f;
            var outgoingCloudAlpha = layer.OutgoingCloudAlpha is { } outgoingAlpha
                ? AtmosphereState.SampleCloudAlpha(outgoingAlpha, gameHour, cloudTiming, game)
                : 1f;
            var cloudAlphaFactor = layer.CloudCurrentWeatherWeight is { } alphaWeight
                ? WeatherCloudTransitionResolver.BlendSample(
                    currentCloudAlpha, outgoingCloudAlpha, alphaWeight)
                : currentCloudAlpha;
            if (layer.Mode == 2 && cloudAlphaFactor <= 0.001f)
            {
                continue; // this cloud layer is authored fully transparent for this weather/time — skip it
            }

            Vector3 tint;
            float param;
            if (layer.Mode == 0)
            {
                tint = fallbackHorizon;
                param = 1f;
            }
            else if (layer.Mode == 1)
            {
                tint = starTint;
                param = starFade;
            }
            else
            {
                Vector3 SampleTint(WeatherColor? color)
                {
                    if (color is null) return cloudTint;
                    // PNAM alpha is retained RGBX metadata; JNAM/host opacity owns visibility.
                    var rgb = AtmosphereState.SampleCloudColor(color, gameHour, cloudTiming, game);
                    var pnam = new Vector3(rgb.X, rgb.Y, rgb.Z);
                    // Bounded lighting fallback for the MODERN generations only: Skyrim+ author
                    // black placeholder PNAM rows that must not blacken the sheet. TES4/FO3/FNV
                    // author every band meaningfully — Oblivion's genuinely dark night rows must
                    // render dark (clamping them to white made night clouds glow).
                    var blackIsPlaceholder = game is BethesdaGame.Skyrim
                        or BethesdaGame.Fallout4 or BethesdaGame.Fallout76;
                    return blackIsPlaceholder && pnam.LengthSquared() < 0.0025f ? cloudTint : pnam;
                }

                var currentTint = SampleTint(layer.CloudColor);
                tint = layer.CloudCurrentWeatherWeight is { } colorWeight
                    ? WeatherCloudTransitionResolver.BlendSample(
                        currentTint, SampleTint(layer.OutgoingCloudColor), colorWeight)
                    : currentTint;
                param = cloudOpacity * cloudAlphaFactor * layer.CloudWeatherWeight;
            }

            // Integrate one retained offset per authored source layer. Current/outgoing texture candidates
            // therefore cannot diverge while the runtime weather weight changes. Pinned captures derive the
            // equivalent wrapped offset directly from their deterministic animation clock.
            var scroll = Vector2.Zero;
            if (layer.Mode == 2)
            {
                if (pinnedAnimation || layer.CloudSourceIndex < 0)
                {
                    scroll = WeatherCloudTransitionResolver.OffsetAtTime(
                        layer.ScrollVelocity, elapsed);
                }
                else
                {
                    scroll = _cloudScrollStates.TryGetValue(layer.CloudSourceIndex, out var state)
                        ? state.Offset
                        : Vector2.Zero;
                }
            }
            DrawMesh(cmd, frameIndex, viewProj, camPos, layer.Vertices, layer.Indices,
                layer.Mode, TargetRadius, tint, param, scroll, layer.TexIndex,
                skyUpper, skyLower, skyHorizon, layer.HasAuthoredBlendWeights);
        }
    }

    // Uploads one mesh through the ring buffer (sky geometry is small + static-per-climate, so per-frame
    // copies beat managing static buffers) and draws it camera-centered with the mode's blend PSO.
    private void DrawMesh(
        ID3D12GraphicsCommandList cmd, int frameIndex, Matrix4x4 viewProj, Vector3 camPos,
        SkyVertex[] verts, ushort[] indices, int mode, float scale, Vector3 tint, float param,
        Vector2 scroll, uint texIndex, Vector3 skyUpper, Vector3 skyLower, Vector3 skyHorizon,
        bool authoredAtmosphere)
    {
        var vbByteCount = (uint)verts.Length * VertexStride;
        if (!_ringBuffer.TryAllocate(frameIndex, vbByteCount, out var vbAlloc, alignment: 4)) return;
        var ibByteCount = (uint)indices.Length * sizeof(ushort);
        if (!_ringBuffer.TryAllocate(frameIndex, ibByteCount, out var ibAlloc, alignment: 4)) return;
        unsafe
        {
            fixed (SkyVertex* src = verts)
            {
                System.Runtime.CompilerServices.Unsafe.CopyBlockUnaligned((void*)vbAlloc.CpuPtr, src, vbByteCount);
            }

            fixed (ushort* src = indices)
            {
                System.Runtime.CompilerServices.Unsafe.CopyBlockUnaligned((void*)ibAlloc.CpuPtr, src, ibByteCount);
            }
        }

        var constants = new SkyGeoConstants
        {
            ViewProj = viewProj,
            CamPosScale = new Vector4(camPos, scale),
            TintParam = new Vector4(tint, param),
            ScrollMode = new Vector4(scroll, mode, authoredAtmosphere ? 1f : 0f),
            TexIndex = texIndex,
            SkyUpper = new Vector4(skyUpper, 1f),
            SkyLower = new Vector4(skyLower, 1f),
            SkyHorizon = new Vector4(skyHorizon, 1f),
        };
        // Soft-fail on ring exhaustion — drop this sky mesh for the frame rather than throwing.
        if (!_ringBuffer.TryAllocate(frameIndex, SkyGeoConstants.ByteSize, out var cbAlloc, GpuRingBuffer12.CbAlignment))
        {
            return;
        }
        unsafe { *(SkyGeoConstants*)cbAlloc.CpuPtr = constants; }

        cmd.SetPipelineState(mode switch { 1 => _psoStars, 2 => _psoClouds, _ => _psoGradient });
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cmd.IASetVertexBuffers(0, new VertexBufferView
        {
            BufferLocation = vbAlloc.GpuAddress,
            SizeInBytes = vbByteCount,
            StrideInBytes = VertexStride,
        });
        cmd.IASetIndexBuffer(new IndexBufferView
        {
            BufferLocation = ibAlloc.GpuAddress,
            SizeInBytes = ibByteCount,
            Format = Format.R16_UInt,
        });
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, cbAlloc.GpuAddress);
        cmd.DrawIndexedInstanced((uint)indices.Length, 1, 0, 0, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _psoGradient.Dispose();
        _psoStars.Dispose();
        _psoClouds.Dispose();
        _layers.Clear();
    }

    private static int ModeFor(SkyObjectType type) => type switch
    {
        SkyObjectType.Stars => 1,
        SkyObjectType.Clouds => 2,
        _ => 0, // Sky / SkyTexture / sun-glare / mask -> gradient background
    };

    private enum SkyBlend { Opaque, Additive, Alpha }

    private static ID3D12PipelineState CreatePso(
        GpuDevice12 gpu, GpuRootSignature12 rootSignature, byte[] vs, byte[] ps, SkyBlend mode)
    {
        var inputElements = new[]
        {
            new InputElementDescription("TEXCOORD", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 1, Format.R32G32_Float, 12, 0),
            new InputElementDescription("COLOR", 0, Format.R8G8B8A8_UNorm, 20, 0),
        };

        // Depth OFF — the sky is the background; depth-written geometry overwrites it afterward (the DSV
        // stays bound for the geometry passes that follow, so the format must still match).
        var depth = new D12.DepthStencilDescription
        {
            DepthEnable = false,
            DepthWriteMask = D12.DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.Always,
            StencilEnable = false,
        };

        var rasterizer = new D12.RasterizerDescription
        {
            FillMode = D12.FillMode.Solid,
            CullMode = D12.CullMode.None, // view the inside of the dome; winding is irrelevant
            FrontCounterClockwise = true,
            DepthClipEnable = true, // clip the back hemisphere at the near plane
            MultisampleEnable = gpu.SceneSampleCount > 1,
        };

        var blend = new D12.BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
        blend.RenderTarget[0] = mode switch
        {
            SkyBlend.Additive => new D12.RenderTargetBlendDescription
            {
                BlendEnable = true,
                SourceBlend = D12.Blend.SourceAlpha,
                DestinationBlend = D12.Blend.One,
                BlendOperation = D12.BlendOperation.Add,
                SourceBlendAlpha = D12.Blend.One,
                DestinationBlendAlpha = D12.Blend.One,
                BlendOperationAlpha = D12.BlendOperation.Add,
                RenderTargetWriteMask = D12.ColorWriteEnable.All,
            },
            SkyBlend.Alpha => new D12.RenderTargetBlendDescription
            {
                BlendEnable = true,
                SourceBlend = D12.Blend.SourceAlpha,
                DestinationBlend = D12.Blend.InverseSourceAlpha,
                BlendOperation = D12.BlendOperation.Add,
                SourceBlendAlpha = D12.Blend.One,
                DestinationBlendAlpha = D12.Blend.InverseSourceAlpha,
                BlendOperationAlpha = D12.BlendOperation.Add,
                RenderTargetWriteMask = D12.ColorWriteEnable.All,
            },
            _ => new D12.RenderTargetBlendDescription
            {
                BlendEnable = false, // opaque background fill
                RenderTargetWriteMask = D12.ColorWriteEnable.All,
            },
        };

        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = rootSignature.RootSignature,
            VertexShader = vs,
            PixelShader = ps,
            BlendState = blend,
            RasterizerState = rasterizer,
            DepthStencilState = depth,
            InputLayout = new InputLayoutDescription(inputElements),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { Gpu.D3D12.GpuSceneFormats.SceneColor },
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription((uint)gpu.SceneSampleCount, 0),
            SampleMask = uint.MaxValue,
        };
        return gpu.Device.CreateGraphicsPipelineState(psoDesc);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SkyGeoConstants
    {
        public Matrix4x4 ViewProj;   // 64
        public Vector4 CamPosScale;  // xyz cam, w scale
        public Vector4 TintParam;    // rgb tint, a fade/opacity
        public Vector4 ScrollMode;   // xy scroll, z mode, w unused
        public uint TexIndex;        // uint4.x
        public uint Pad0;
        public uint Pad1;
        public uint Pad2;
        public Vector4 SkyUpper;     // recovered SKY BlendColor[2]
        public Vector4 SkyLower;     // recovered SKY BlendColor[1]
        public Vector4 SkyHorizon;   // recovered SKY BlendColor[0]

        public const uint ByteSize = 64 + (7 * 16); // 176
    }

    /// <summary>
    ///     Forwards to the one shared compiler — see <see cref="GpuShaderCompiler12" />.
    ///     This was one of a dozen copy-pasted private compilers that had drifted apart on
    ///     shader flags and manifest lookup; the flag decision is now made once, unconditionally.
    /// </summary>
    private static byte[] CompileEmbeddedShader(string name, string entryPoint, string profile) =>
        GpuShaderCompiler12.Compile(name, entryPoint, profile);
}

/// <summary>
///     One cooked sky-dome layer handed to <see cref="SkyGeometryRenderer12.SetLayers" />: a sky NIF
///     submesh's geometry + UVs + indices, its <see cref="SkyObjectType" />, and the resolved bindless
///     texture index to draw it with (the weather's cloud texture for clouds, the NIF's stars texture for
///     stars; ignored for the gradient atmosphere layer).
/// </summary>
internal sealed class SkyGeometryLayer
{
    public required float[] Positions { get; init; } // xyz * vertexCount
    public float[]? Uvs { get; init; }               // uv * vertexCount (null -> 0,0)
    public byte[]? VertexColors { get; set; }        // RGBA * vertexCount; alpha = baked horizon fade (null -> opaque; settable so Morrowind's synthesized fog fade can attach one)
    public required ushort[] Indices { get; init; }
    public SkyObjectType Type { get; init; }
    public uint TextureIndex { get; init; }
    public Vector2 ScrollSpeed { get; set; }         // clouds: one blended per-layer UV/sec drift; else zero
    public WeatherColor? CloudColor { get; set; }    // clouds: current weather PNAM per-layer color
    public WeatherColor? OutgoingCloudColor { get; set; } // clouds: outgoing PNAM before weather blend
    public WeatherCloudAlpha? CloudAlpha { get; set; } // clouds: current weather JNAM per-layer opacity
    public WeatherCloudAlpha? OutgoingCloudAlpha { get; set; } // outgoing JNAM before weather blend
    public float? CloudCurrentWeatherWeight { get; set; } // null=atomic; otherwise current=t/outgoing=1-t
    public int CloudSourceIndex { get; init; } = -1;
    public bool IsOutgoingCloudPass { get; init; }
    public float CloudWeatherWeight { get; set; } = 1f; // texture contribution; one for coalesced textures
}
#endif
