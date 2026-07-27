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
///     Draws the 3D export's captured world bounds (+ a view-direction gizmo) as a screen-space
///     wireframe overlay while the right-panel Export tab's "Show framing" toggle is on, so the user
///     sees the region and projection angle the export will capture before committing to a render.
///     <para>
///         Reuses the collision overlay's <c>collision_line</c> shaders + the main root signature: each
///         edge is two <c>float3</c> entries bound as a <c>StructuredBuffer&lt;float3&gt;</c> root SRV
///         (t8/space0) that the VS expands into a feathered screen-space quad from SV_VertexID — no
///         input-assembler stream. Unlike the persistent collision cage, the framing is a dozen edges
///         that change with projection/scale, so both the line buffer AND the constants ride the
///         transient ring buffer. Depth is disabled (an overlay); drawn into the LDR back buffer after
///         tonemapping in the live frame (a scene-target PSO exists for parity but the export render
///         itself never draws framing — it IS the framing).
///     </para>
/// </summary>
internal sealed class ExportFramingOverlay : IDisposable
{
    private const uint UniformsByteSize = 96; // float4x4 (64) + float4 color (16) + float4 params (16)

    // Slightly bolder than the collision cage (0.6/0.65) so the frame reads clearly over busy terrain.
    private const float CoreHalfWidthPx = 0.9f;
    private const float FeatherPx = 0.75f;

    private readonly GpuCommandRecorder12 _recorder;
    private readonly GpuRingBuffer12 _ringBuffer;
    private readonly ID3D12PipelineState _scenePso;
    private readonly ID3D12PipelineState _ldrPso;
    private bool _disposed;

    public ExportFramingOverlay(
        GpuDevice12 gpu,
        GpuCommandRecorder12 recorder,
        GpuRingBuffer12 ringBuffer,
        GpuRootSignature12 rootSignature)
    {
        _recorder = recorder;
        _ringBuffer = ringBuffer;

        var vsBytecode = CompileEmbeddedShader("collision_line.vert.hlsl", "main", "vs_5_1");
        var psBytecode = CompileEmbeddedShader("collision_line.frag.hlsl", "main", "ps_5_1");

        // Depth DISABLED: a preview overlay must stay visible through terrain/meshes (like the
        // collision cage / selection highlight). It never writes depth, so later layers are unaffected.
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
            MultisampleEnable = false, // analytic pixel-shader feather — deterministic across GPUs.
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
            // No IA stream: the VS reads the line buffer through the t8 root SRV and expands quads.
            InputLayout = new InputLayoutDescription(),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { GpuSceneFormats.LdrOutput },
            DepthStencilFormat = Format.Unknown,
            SampleDescription = new SampleDescription(1, 0),
            SampleMask = uint.MaxValue,
        };
        _ldrPso = gpu.Device.CreateGraphicsPipelineState(psoDesc);

        // Scene-target parity PSO (HDR/MSAA) — kept so the overlay can draw into a scene target if ever
        // needed; the live frame draws it into the LDR back buffer after tonemapping.
        rasterizer.MultisampleEnable = gpu.SceneSampleCount > 1;
        psoDesc.RasterizerState = rasterizer;
        psoDesc.RenderTargetFormats = new[] { GpuSceneFormats.SceneColor };
        psoDesc.DepthStencilFormat = Format.D32_Float;
        psoDesc.SampleDescription = new SampleDescription((uint)gpu.SceneSampleCount, 0);
        _scenePso = gpu.Device.CreateGraphicsPipelineState(psoDesc);
    }

    /// <summary>
    ///     Draws <paramref name="lineVertices" /> (an even list of edge endpoint PAIRS, world space) as
    ///     feathered lines. No-op for &lt; 1 edge or a degenerate viewport. The line buffer + constants
    ///     are transient (ring buffer), so it is safe to call with a different edge set each frame.
    /// </summary>
    public void Render(
        Matrix4x4 viewProj, ReadOnlySpan<Vector3> lineVertices, Vector4 color,
        float viewportWidth, float viewportHeight, bool ldrTarget)
    {
        if (_disposed) return;
        var segments = lineVertices.Length / 2;
        if (segments < 1 || viewportWidth < 1f || viewportHeight < 1f) return;

        var frameIndex = _recorder.FrameIndex;
        var cmd = _recorder.CommandList;

        var vertexBytes = (uint)(segments * 2 * Marshal.SizeOf<Vector3>());
        if (!_ringBuffer.TryAllocate(frameIndex, vertexBytes, out var vbAlloc, 16)) return;
        unsafe
        {
            var dst = (Vector3*)vbAlloc.CpuPtr;
            for (var i = 0; i < segments * 2; i++) dst[i] = lineVertices[i];
        }

        if (!_ringBuffer.TryAllocate(frameIndex, UniformsByteSize, out var cbAlloc, GpuRingBuffer12.CbAlignment))
        {
            return;
        }
        var uniforms = new FramingUniforms
        {
            ViewProj = viewProj,
            LineColor = color,
            LineParams = new Vector4(viewportWidth, viewportHeight, CoreHalfWidthPx, FeatherPx),
        };
        unsafe { *(FramingUniforms*)cbAlloc.CpuPtr = uniforms; }

        cmd.SetPipelineState(ldrTarget ? _ldrPso : _scenePso);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cmd.SetGraphicsRootShaderResourceView(
            (uint)GpuRootSignature12.Slots.ReferenceInstanceSrv, vbAlloc.GpuAddress);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, cbAlloc.GpuAddress);
        cmd.DrawInstanced((uint)(segments * 6), 1, 0, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scenePso.Dispose();
        _ldrPso.Dispose();
    }

    /// <summary>
    ///     Forwards to the one shared compiler — see <see cref="GpuShaderCompiler12" />.
    ///     This was one of a dozen copy-pasted private compilers that had drifted apart on
    ///     shader flags and manifest lookup; the flag decision is now made once, unconditionally.
    /// </summary>
    private static byte[] CompileEmbeddedShader(string name, string entryPoint, string profile) =>
        GpuShaderCompiler12.Compile(name, entryPoint, profile);

    [StructLayout(LayoutKind.Sequential)]
    private struct FramingUniforms
    {
        public Matrix4x4 ViewProj;
        public Vector4 LineColor;
        public Vector4 LineParams; // x,y = viewport px; z = core half-width px; w = feather px
    }
}
#endif
