#if WINDOWS_GUI
using System.Reflection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D12 = Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Compiles the embedded terrain HLSL shaders and builds the two graphics pipeline states the
///     <see cref="TerrainRenderer12" /> uses: the textured color PSO and a depth-only variant (same
///     vertex path + depth state, no pixel shader, no render targets) for the top-down overlay's
///     depth pre-pass. Pure setup — holds no state and issues no GPU commands.
/// </summary>
internal static class TerrainPipelineFactory12
{
    /// <summary>
    ///     Forwards to the one shared compiler — see <see cref="GpuShaderCompiler12" />. This was one
    ///     of a dozen copy-pasted private compilers that had drifted apart on shader flags and
    ///     manifest lookup; the flag decision is now made once, unconditionally.
    /// </summary>
    public static byte[] CompileEmbeddedShader(string name, string entryPoint, string profile) =>
        GpuShaderCompiler12.Compile(name, entryPoint, profile);

    /// <summary>
    ///     Build the color + depth-only terrain pipeline states from the compiled shader bytecode and
    ///     the terrain input layout. Both share the same reversed-Z depth state, back-face cull, and
    ///     MSAA settings; the depth-only variant drops the pixel shader and all render targets.
    /// </summary>
    public static (ID3D12PipelineState Pso, ID3D12PipelineState DepthOnlyPso) BuildPipelineStates(
        GpuDevice12 gpu,
        GpuRootSignature12 rootSignature,
        byte[] vsBytecode,
        byte[] psBytecode,
        InputElementDescription[] inputElements)
    {
        var rasterizer = new D12.RasterizerDescription
        {
            FillMode = D12.FillMode.Solid,
            CullMode = D12.CullMode.Back,
            FrontCounterClockwise = true,
            DepthClipEnable = true,
            // Required for triangle edges to be antialiased on a multisampled RT: with this FALSE,
            // primitives render aliased even into an MSAA target. No-op when the scene isn't MSAA.
            MultisampleEnable = gpu.SceneSampleCount > 1,
        };

        var depth = new D12.DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = D12.DepthWriteMask.All,
            DepthFunc = ComparisonFunction.GreaterEqual, // reversed-Z (near→1, far→0); depth clear = 0
            StencilEnable = false,
        };

        var blend = new D12.BlendDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false,
        };
        blend.RenderTarget[0] = new D12.RenderTargetBlendDescription
        {
            BlendEnable = false,
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
            RenderTargetFormats = new[] { Gpu.D3D12.GpuSceneFormats.SceneColor },
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription((uint)gpu.SceneSampleCount, 0),
            SampleMask = uint.MaxValue,
        };
        var pso = gpu.Device.CreateGraphicsPipelineState(psoDesc);

        // Depth-only PSO: same vertex path + depth state, but no pixel shader and no render
        // targets, so it writes only the depth buffer. Used by the top-down overlay pre-pass.
        var depthOnlyPsoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = rootSignature.RootSignature,
            VertexShader = vsBytecode,
            BlendState = blend,
            RasterizerState = rasterizer,
            DepthStencilState = depth,
            InputLayout = new InputLayoutDescription(inputElements),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = Array.Empty<Format>(),
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription((uint)gpu.SceneSampleCount, 0),
            SampleMask = uint.MaxValue,
        };
        var depthOnlyPso = gpu.Device.CreateGraphicsPipelineState(depthOnlyPsoDesc);

        return (pso, depthOnlyPso);
    }

    /// <summary>
    ///     Sun-shadow depth PSO: like the depth-only variant but always SINGLE-sample (the shadow
    ///     map is never MSAA regardless of the scene), cull NONE (a grazing sun sees the back faces
    ///     of slopes — culling them leaks light through hills), and a NEGATIVE depth bias (reversed-Z
    ///     stores larger values nearer the light, so the acne fix pushes stored depth SMALLER).
    ///     Mirrors <c>ReferencePipelineFactory12.CreateShadowPipelineState</c>.
    /// </summary>
    public static ID3D12PipelineState BuildShadowPipelineState(
        GpuDevice12 gpu,
        GpuRootSignature12 rootSignature,
        byte[] vsBytecode,
        InputElementDescription[] inputElements)
    {
        var rasterizer = new D12.RasterizerDescription
        {
            FillMode = D12.FillMode.Solid,
            CullMode = D12.CullMode.None,
            FrontCounterClockwise = true,
            DepthClipEnable = true,
            DepthBias = -1000,
            DepthBiasClamp = 0f,
            SlopeScaledDepthBias = -2f,
        };

        var depth = new D12.DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = D12.DepthWriteMask.All,
            DepthFunc = ComparisonFunction.GreaterEqual, // reversed-Z, same as the scene PSOs
            StencilEnable = false,
        };

        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = rootSignature.RootSignature,
            VertexShader = vsBytecode,
            BlendState = D12.BlendDescription.Opaque,
            RasterizerState = rasterizer,
            DepthStencilState = depth,
            InputLayout = new InputLayoutDescription(inputElements),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = Array.Empty<Format>(),
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
            SampleMask = uint.MaxValue,
        };
        return gpu.Device.CreateGraphicsPipelineState(psoDesc);
    }
}
#endif
