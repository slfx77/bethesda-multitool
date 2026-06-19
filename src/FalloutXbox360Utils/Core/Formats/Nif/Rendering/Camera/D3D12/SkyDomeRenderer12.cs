#if WINDOWS_GUI
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D12 = Vortice.Direct3D12;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>
///     Procedural sky-DOME renderer — the exterior sky. Draws a celestial-sphere mesh
///     (<see cref="SkyDomeMesh" />) centered on the camera, depth-OFF first, compositing the horizon→top
///     gradient (b3 atmosphere colors) plus the in-game star texture sampled on the dome's LAT-LONG UVs
///     and the cloud texture on a flat overhead plane (gnomonic projection in skydome.frag, so clouds
///     scroll as a flat ceiling that compresses toward the horizon rather than wrapping the dome).
///     <para>
///         The dome mesh is uploaded through the per-frame ring buffer (it is small — a few thousand
///         vertices — so the per-frame copy is cheaper than managing a static default-heap buffer + its
///         GPU-residency lifetime). Drawn with culling off + depth-clip on, so the back hemisphere clips
///         at the near plane and only the visible sky renders.
///     </para>
/// </summary>
internal sealed class SkyDomeRenderer12 : IDisposable
{
    private const float CloudScrollSpeedX = 0.0040f;
    private const float CloudScrollSpeedY = 0.0015f;
    // CloudTiling = planar scale for the flat overhead cloud plane (gnomonic projection in
    // skydome.frag); larger = smaller, more-repeated clouds overhead. (Was the azimuth wrap count when
    // clouds were lat-long; re-tune to taste.) StarTiling = azimuth wrap count for the lat-long stars.
    private const float CloudTiling = 1.0f;
    private const float StarTiling = 4.0f;

    [StructLayout(LayoutKind.Sequential)]
    private struct DomeVertex
    {
        public Vector3 Dir;
        public Vector2 Uv;
    }

    private const uint VertexStride = 20; // float3 dir + float2 uv

    private readonly GpuCommandRecorder12 _recorder;
    private readonly GpuRingBuffer12 _ringBuffer;
    private readonly GpuDescriptorHeapAllocator12 _cbvSrvUavHeap;
    private readonly ID3D12PipelineState _pso;
    private readonly DomeVertex[] _vertices;
    private readonly ushort[] _indices;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private bool _disposed;

    public SkyDomeRenderer12(
        GpuDevice12 gpu,
        GpuCommandRecorder12 recorder,
        GpuRingBuffer12 ringBuffer,
        GpuRootSignature12 rootSignature,
        GpuDescriptorHeapAllocator12 cbvSrvUavHeap)
    {
        _recorder = recorder;
        _ringBuffer = ringBuffer;
        _cbvSrvUavHeap = cbvSrvUavHeap;

        var mesh = SkyDomeMesh.Generate();
        _vertices = new DomeVertex[mesh.Vertices.Length];
        for (var i = 0; i < mesh.Vertices.Length; i++)
        {
            _vertices[i] = new DomeVertex { Dir = mesh.Vertices[i].Direction, Uv = mesh.Vertices[i].Uv };
        }
        _indices = mesh.Indices;

        var vsBytecode = CompileEmbeddedShader("skydome.vert.hlsl", "main", "vs_5_1");
        var psBytecode = CompileEmbeddedShader("skydome.frag.hlsl", "main", "ps_5_1");

        var inputElements = new[]
        {
            new InputElementDescription("TEXCOORD", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 1, Format.R32G32_Float, 12, 0),
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
            CullMode = D12.CullMode.None, // view the inside of the sphere; winding is irrelevant
            FrontCounterClockwise = true,
            DepthClipEnable = true, // clip the back hemisphere at the near plane
            MultisampleEnable = gpu.SceneSampleCount > 1,
        };

        var blend = new D12.BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
        blend.RenderTarget[0] = new D12.RenderTargetBlendDescription
        {
            BlendEnable = false, // opaque background fill
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
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { Format.B8G8R8A8_UNorm },
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription((uint)gpu.SceneSampleCount, 0),
            SampleMask = uint.MaxValue,
        };
        _pso = gpu.Device.CreateGraphicsPipelineState(psoDesc);
    }

    /// <summary>
    ///     Draws the dome for <paramref name="viewProj" /> centered on <paramref name="camPos" /> at
    ///     <paramref name="radius" />, compositing the gradient + star/cloud textures over the cleared
    ///     target. Must run AFTER the per-frame atmosphere bind and BEFORE terrain, with the heap bound.
    /// </summary>
    public void Render(
        Matrix4x4 viewProj, Vector3 camPos, float radius,
        Vector3 cloudTint, float cloudOpacity, uint cloudTex,
        Vector3 starTint, float starFade, uint starTex)
    {
        if (_disposed) return;

        var frameIndex = _recorder.FrameIndex;
        var cmd = _recorder.CommandList;

        var vbByteCount = (uint)_vertices.Length * VertexStride;
        if (!_ringBuffer.TryAllocate(frameIndex, vbByteCount, out var vbAlloc, alignment: 4)) return;
        var ibByteCount = (uint)_indices.Length * sizeof(ushort);
        if (!_ringBuffer.TryAllocate(frameIndex, ibByteCount, out var ibAlloc, alignment: 4)) return;
        unsafe
        {
            fixed (DomeVertex* src = _vertices)
            {
                System.Runtime.CompilerServices.Unsafe.CopyBlockUnaligned(
                    (void*)vbAlloc.CpuPtr, src, vbByteCount);
            }
            fixed (ushort* src = _indices)
            {
                System.Runtime.CompilerServices.Unsafe.CopyBlockUnaligned(
                    (void*)ibAlloc.CpuPtr, src, ibByteCount);
            }
        }

        var elapsed = (float)Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds;
        var constants = new SkyDomeConstants
        {
            ViewProj = viewProj,
            CamPosRadius = new Vector4(camPos, radius),
            CloudTintOpacity = new Vector4(cloudTint, cloudOpacity),
            StarTintFade = new Vector4(starTint, starFade),
            CloudScroll = new Vector4(elapsed * CloudScrollSpeedX, elapsed * CloudScrollSpeedY, CloudTiling, StarTiling),
            CloudTex = cloudTex,
            StarTex = starTex,
        };
        var cbAlloc = _ringBuffer.Allocate(frameIndex, SkyDomeConstants.ByteSize, GpuRingBuffer12.CbAlignment);
        unsafe { *(SkyDomeConstants*)cbAlloc.CpuPtr = constants; }

        cmd.SetPipelineState(_pso);
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
        cmd.SetGraphicsRootDescriptorTable(GpuRootSignature12.Slots.BindlessSrvTable, _cbvSrvUavHeap.BindlessHeapStartGpu);
        cmd.DrawIndexedInstanced((uint)_indices.Length, 1, 0, 0, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pso.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SkyDomeConstants
    {
        public Matrix4x4 ViewProj;       // 64
        public Vector4 CamPosRadius;     // xyz cam pos, w radius
        public Vector4 CloudTintOpacity; // rgb cloud tint, a opacity
        public Vector4 StarTintFade;     // rgb star tint, a fade
        public Vector4 CloudScroll;      // xy scroll, z cloud planar scale, w star tiling
        public uint CloudTex;            // uint4.x
        public uint StarTex;             // uint4.y
        public uint Pad0;
        public uint Pad1;

        public const uint ByteSize = 64 + (5 * 16); // 144
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
#endif
