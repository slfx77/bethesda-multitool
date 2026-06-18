#if WINDOWS_GUI
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D12 = Vortice.Direct3D12;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>
///     v3 atmosphere skybox. Draws a single fullscreen triangle FIRST (before terrain), depth test/write
///     OFF, filling the cleared color target with a horizon→top gradient (colors from the shared b3
///     atmosphere CB, so the sky tracks the time-of-day slider + weather) plus the real in-game CLOUD and
///     STAR textures projected onto the sky and composited over the gradient. The sun/moon are drawn
///     separately as textured billboards (<see cref="SkyBillboardRenderer12" />) after this pass. Its own
///     b0 CB carries the inverse view-projection (VS world-ray reconstruction) + cloud/star tint/scroll/
///     index params; clouds/stars are sampled from the shared bindless table. No vertex buffer (the VS
///     expands the triangle from <c>SV_VertexID</c>).
///     <para>
///         Drawing first with depth disabled (DSV still bound for the geometry passes that follow)
///         sidesteps the reversed-Z sky-at-infinity precision problem: the sky never writes depth, and
///         terrain/references overwrite it via the normal depth test.
///     </para>
/// </summary>
internal sealed class SkyboxRenderer12 : IDisposable
{
    // Gentle cloud drift + projection scales (tunable). Scroll is time-based so clouds animate
    // continuously; scale tiles the texture across the sky hemisphere.
    private const float CloudScrollSpeedX = 0.0040f;
    private const float CloudScrollSpeedY = 0.0015f;
    private const float CloudScale = 0.45f;
    private const float StarScale = 0.30f;

    private readonly GpuCommandRecorder12 _recorder;
    private readonly GpuRingBuffer12 _ringBuffer;
    private readonly GpuDescriptorHeapAllocator12 _cbvSrvUavHeap;
    private readonly ID3D12PipelineState _pso;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private bool _disposed;

    public SkyboxRenderer12(
        GpuDevice12 gpu,
        GpuCommandRecorder12 recorder,
        GpuRingBuffer12 ringBuffer,
        GpuRootSignature12 rootSignature,
        GpuDescriptorHeapAllocator12 cbvSrvUavHeap)
    {
        _recorder = recorder;
        _ringBuffer = ringBuffer;
        _cbvSrvUavHeap = cbvSrvUavHeap;

        var vsBytecode = CompileEmbeddedShader("skybox.vert.hlsl", "main", "vs_5_1");
        var psBytecode = CompileEmbeddedShader("skybox.frag.hlsl", "main", "ps_5_1");

        // Depth OFF — the sky is the background; depth-written geometry overwrites it afterward. The DSV
        // stays bound (terrain follows in the same pass), so the PSO's DSV format must still match it.
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
            CullMode = D12.CullMode.None,
            FrontCounterClockwise = true,
            DepthClipEnable = false, // fullscreen triangle at the far plane — never clip it
            // Antialias edges on the multisampled scene RT (no-op when the scene isn't MSAA).
            MultisampleEnable = gpu.SceneSampleCount > 1,
        };

        var blend = new D12.BlendDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false,
        };
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
            InputLayout = new InputLayoutDescription(Array.Empty<InputElementDescription>()),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { Format.B8G8R8A8_UNorm },
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription((uint)gpu.SceneSampleCount, 0),
            SampleMask = uint.MaxValue,
        };
        _pso = gpu.Device.CreateGraphicsPipelineState(psoDesc);
    }

    /// <summary>
    ///     Draws the sky for <paramref name="viewProj" /> — the gradient (b3 colors) plus the cloud + star
    ///     textures composited over it. Cloud scroll is derived from this renderer's own clock. Pass
    ///     <see cref="SkyBillboardRenderer12.NoTexture" /> for a cloud/star index to skip that layer.
    ///     No-op if the matrix can't be inverted. Must be called AFTER the per-frame atmosphere bind and
    ///     BEFORE the terrain pass, with the descriptor heap already bound.
    /// </summary>
    public void Render(
        Matrix4x4 viewProj,
        Vector3 cloudTint, float cloudOpacity, uint cloudTex,
        Vector3 starTint, float starFade, uint starTex)
    {
        if (_disposed) return;
        if (!Matrix4x4.Invert(viewProj, out var invViewProj)) return;

        var elapsed = (float)Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds;
        var constants = new SkyboxConstants
        {
            InvViewProj = invViewProj,
            CloudTintOpacity = new Vector4(cloudTint, cloudOpacity),
            StarTintFade = new Vector4(starTint, starFade),
            CloudScroll = new Vector4(elapsed * CloudScrollSpeedX, elapsed * CloudScrollSpeedY, CloudScale, StarScale),
            CloudTex = cloudTex,
            StarTex = starTex,
        };

        var frameIndex = _recorder.FrameIndex;
        var cmd = _recorder.CommandList;

        var cbAlloc = _ringBuffer.Allocate(frameIndex, SkyboxConstants.ByteSize, GpuRingBuffer12.CbAlignment);
        unsafe { *(SkyboxConstants*)cbAlloc.CpuPtr = constants; }

        cmd.SetPipelineState(_pso);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, cbAlloc.GpuAddress);
        cmd.SetGraphicsRootDescriptorTable(GpuRootSignature12.Slots.BindlessSrvTable, _cbvSrvUavHeap.BindlessHeapStartGpu);
        cmd.DrawInstanced(3, 1, 0, 0); // fullscreen triangle, expanded from SV_VertexID
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pso.Dispose();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SkyboxConstants
    {
        public Matrix4x4 InvViewProj;    // 64
        public Vector4 CloudTintOpacity; // rgb cloud tint, a opacity
        public Vector4 StarTintFade;     // rgb star tint, a fade
        public Vector4 CloudScroll;      // xy scroll offset, z cloud scale, w star scale
        public uint CloudTex;            // bindless cloud texture index (uint4.x in HLSL)
        public uint StarTex;             // uint4.y
        public uint Pad0;
        public uint Pad1;

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
        // runtime compile, or PSO creation fails with X3596. Detect by the array shape (like WaterRenderer12).
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
