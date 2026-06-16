#if WINDOWS_GUI
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
///     v3 Phase 5 — atmosphere skybox. Draws a single fullscreen triangle FIRST (before terrain),
///     depth test/write OFF, filling the cleared color target with a horizon→top gradient + a
///     procedural sun disc. The gradient/sun colors come from the shared b3 atmosphere CB (bound once
///     per frame by the host), so the sky tracks the time-of-day slider + selected weather. Its own b0
///     CB carries the inverse view-projection the VS uses to reconstruct per-pixel world rays. No
///     textures, no vertex buffer (the VS expands the triangle from <c>SV_VertexID</c>).
///     <para>
///         Drawing first with depth disabled (DSV still bound for the geometry passes that follow)
///         sidesteps the reversed-Z sky-at-infinity precision problem: the sky never writes depth, and
///         terrain/references overwrite it via the normal depth test. Mirrors
///         <see cref="SelectionHighlightRenderer12" /> for the minimal-CB / depth-disabled PSO shape.
///     </para>
/// </summary>
internal sealed class SkyboxRenderer12 : IDisposable
{
    private const uint UniformsByteSize = 64; // float4x4 inverse view-projection

    private readonly GpuCommandRecorder12 _recorder;
    private readonly GpuRingBuffer12 _ringBuffer;
    private readonly ID3D12PipelineState _pso;
    private bool _disposed;

    public SkyboxRenderer12(
        GpuDevice12 gpu,
        GpuCommandRecorder12 recorder,
        GpuRingBuffer12 ringBuffer,
        GpuRootSignature12 rootSignature)
    {
        _recorder = recorder;
        _ringBuffer = ringBuffer;

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
    ///     Draws the sky for <paramref name="viewProj" />, reading the atmosphere colors from the b3 CB
    ///     the host already bound this frame. No-op if the matrix can't be inverted (degenerate camera).
    ///     Must be called AFTER the host's per-frame atmosphere bind and BEFORE the terrain pass.
    /// </summary>
    public void Render(Matrix4x4 viewProj)
    {
        if (_disposed) return;
        if (!Matrix4x4.Invert(viewProj, out var invViewProj)) return;

        var frameIndex = _recorder.FrameIndex;
        var cmd = _recorder.CommandList;

        var cbAlloc = _ringBuffer.Allocate(frameIndex, UniformsByteSize, GpuRingBuffer12.CbAlignment);
        unsafe { *(Matrix4x4*)cbAlloc.CpuPtr = invViewProj; }

        cmd.SetPipelineState(_pso);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, cbAlloc.GpuAddress);
        cmd.DrawInstanced(3, 1, 0, 0); // fullscreen triangle, expanded from SV_VertexID
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pso.Dispose();
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
}
#endif
