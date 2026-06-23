#if WINDOWS_GUI
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D12 = Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>
///     Renders the REAL climate sky-dome NIF geometry — the atmosphere/stars/clouds layers the engine
///     itself draws (extracted from the game's own <c>Sky\*.nif</c>) — instead of a procedural dome. Each
///     layer is drawn camera-centered, depth-OFF, on the dome's OWN authored UVs, with a blend mode keyed
///     to its <see cref="SkyObjectType" /> (gradient/opaque for SKY, additive for STARS, alpha for
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
        public int Mode;                 // 1 stars, 2 clouds (gradient comes from the fallback dome)
        public uint TexIndex;            // bindless diffuse index
        public Vector2 ScrollVelocity;   // UV/sec drift (clouds: per-layer from QNAM/RNAM; stars: zero)
        public WeatherColor? CloudColor; // PNAM per-layer cloud color (RGB tint + A opacity); null = fallback
    }

    private readonly GpuCommandRecorder12 _recorder;
    private readonly GpuRingBuffer12 _ringBuffer;
    private readonly GpuDescriptorHeapAllocator12 _cbvSrvUavHeap;
    private readonly ID3D12PipelineState _psoGradient;
    private readonly ID3D12PipelineState _psoStars;
    private readonly ID3D12PipelineState _psoClouds;
    private readonly List<GpuLayer> _layers = new();
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    // Procedural gradient dome rendered EVERY frame as the sky base — so games whose sky NIFs we can't
    // parse (Oblivion's pre-SkyShaderProperty NIFs, an unresolved climate MODL, …) still get the b3 sky
    // gradient instead of a black void. The real stars/clouds geometry layers draw on top of it.
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
    ///     Replaces the sky geometry with a new climate's STARS + CLOUDS layers (the gradient is the
    ///     fallback dome). Each input carries its mesh + UVs + indices, its <see cref="SkyObjectType" />,
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
            if (mode == 0)
            {
                continue; // gradient layers are covered by the fallback dome — don't double-draw
            }

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
                    : 0xFFFFFFFFu;
            }

            _layers.Add(new GpuLayer
            {
                Vertices = verts,
                Indices = layer.Indices,
                Mode = mode,
                TexIndex = layer.TextureIndex,
                ScrollVelocity = mode == 2 ? layer.ScrollSpeed : Vector2.Zero,
                CloudColor = mode == 2 ? layer.CloudColor : null,
            });
        }
    }

    public void Clear() => _layers.Clear();

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
        Vector3 cloudTint, float cloudOpacity, Vector3 starTint, float starFade,
        float gameHour, AtmosphereState.ClimateTiming? cloudTiming)
    {
        if (_disposed) return;

        var frameIndex = _recorder.FrameIndex;
        var cmd = _recorder.CommandList;
        var elapsed = (float)Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds;

        cmd.SetGraphicsRootDescriptorTable(GpuRootSignature12.Slots.BindlessSrvTable, _cbvSrvUavHeap.BindlessHeapStartGpu);

        // Gradient base FIRST — always (every game gets a sky). Opaque background fill.
        DrawMesh(cmd, frameIndex, viewProj, camPos, _fallbackVerts, _fallbackIndices,
            mode: 0, scale: TargetRadius, Vector3.One, 1f, Vector2.Zero, NoTexture);

        // Real stars + clouds geometry on top.
        foreach (var layer in _layers)
        {
            if ((layer.Mode == 1 && starFade <= 0.001f) || (layer.Mode == 2 && cloudOpacity <= 0.001f) ||
                layer.TexIndex == NoTexture)
            {
                continue; // suppressed this frame (interior) or no resolved texture
            }

            Vector3 tint;
            float param;
            if (layer.Mode == 1)
            {
                tint = starTint;
                param = starFade;
            }
            else if (layer.CloudColor is { } cc)
            {
                // Per-layer cloud color: the weather's PNAM color (RGB tint + A opacity) blended by the
                // game hour — the engine's per-draw cloud uniform (SkyShader::SetupGeometryConstants). The
                // loop only runs when clouds are enabled (cloudOpacity gate above), so the PNAM alpha is the
                // opacity. vertex alpha (the horizon fade) and texture alpha still multiply in the shader.
                var rgba = AtmosphereState.SampleCloudColor(cc, gameHour, cloudTiming);
                tint = new Vector3(rgba.X, rgba.Y, rgba.Z);
                param = rgba.W;
            }
            else
            {
                tint = cloudTint;       // fallback for weathers with no PNAM cloud color
                param = cloudOpacity;
            }

            // Per-layer UV drift: stars hold still; each cloud layer scrolls at its own QNAM/RNAM speed.
            var scroll = elapsed * layer.ScrollVelocity;
            DrawMesh(cmd, frameIndex, viewProj, camPos, layer.Vertices, layer.Indices,
                layer.Mode, TargetRadius, tint, param, scroll, layer.TexIndex);
        }
    }

    // Uploads one mesh through the ring buffer (sky geometry is small + static-per-climate, so per-frame
    // copies beat managing static buffers) and draws it camera-centered with the mode's blend PSO.
    private void DrawMesh(
        ID3D12GraphicsCommandList cmd, int frameIndex, Matrix4x4 viewProj, Vector3 camPos,
        SkyVertex[] verts, ushort[] indices, int mode, float scale, Vector3 tint, float param,
        Vector2 scroll, uint texIndex)
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
            ScrollMode = new Vector4(scroll, mode, 0f),
            TexIndex = texIndex,
        };
        var cbAlloc = _ringBuffer.Allocate(frameIndex, SkyGeoConstants.ByteSize, GpuRingBuffer12.CbAlignment);
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
            RenderTargetFormats = new[] { Format.B8G8R8A8_UNorm },
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

        public const uint ByteSize = 64 + (4 * 16); // 128
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

        // Bindless shaders (`Texture2D x[] : register`) need the unbounded-descriptor-tables flag at
        // runtime compile, or PSO creation fails with X3596.
        var shaderFlags = source.Contains("[] : register", StringComparison.Ordinal)
            ? (ShaderFlags)0x00100000
            : ShaderFlags.None;

        var result = Compiler.Compile(
            source, Array.Empty<ShaderMacro>(), include: null!, entryPoint, sourceName: name, profile,
            shaderFlags, EffectFlags.None, out Blob? bytecode, out Blob? errors);

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
    public byte[]? VertexColors { get; init; }       // RGBA * vertexCount; alpha = baked horizon fade (null -> opaque)
    public required ushort[] Indices { get; init; }
    public SkyObjectType Type { get; init; }
    public uint TextureIndex { get; init; }
    public Vector2 ScrollSpeed { get; init; }        // clouds: per-layer UV/sec drift (QNAM/RNAM); else zero
    public WeatherColor? CloudColor { get; init; }   // clouds: weather PNAM per-layer color (RGB tint + A opacity)
}
#endif
