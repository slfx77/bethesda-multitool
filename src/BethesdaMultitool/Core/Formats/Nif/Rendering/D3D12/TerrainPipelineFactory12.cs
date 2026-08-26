#if WINDOWS_GUI
using System.Globalization;
using System.Reflection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D12 = Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Compiles the embedded terrain HLSL shaders and builds the graphics pipeline states the
///     <see cref="TerrainRenderer12" /> uses: the textured color PSO and its mirror-winding twin, a
///     depth-only variant (same vertex path + depth state, no pixel shader, no render targets) for
///     the top-down overlay's depth pre-pass, and the sun-shadow depth PSO. Pure setup — holds no
///     state and issues no GPU commands.
///     <para>
///         Everything is keyed by <b>blend-quad count</b> since phase 3d: the shader permutation and
///         the input layout have to be chosen together, because D3D12 rejects a PSO whose vertex
///         shader declares an input the layout omits. The two depth passes compile at quad count 0
///         and so need no per-cell variants at all.
///     </para>
/// </summary>
internal static class TerrainPipelineFactory12
{
    private const string VertexShaderFile = "terrain_textured.vert.hlsl";
    private const string PixelShaderFile = "terrain_textured.frag.hlsl";

    /// <summary>The macro both terrain shaders switch their layer-weight count on.</summary>
    public const string BlendQuadMacro = "TERRAIN_BLEND_QUADS";

    /// <summary>
    ///     Forwards to the one shared compiler — see <see cref="GpuShaderCompiler12" />. This was one
    ///     of a dozen copy-pasted private compilers that had drifted apart on shader flags and
    ///     manifest lookup; the flag decision is now made once, unconditionally.
    /// </summary>
    public static byte[] CompileEmbeddedShader(string name, string entryPoint, string profile) =>
        GpuShaderCompiler12.Compile(name, entryPoint, profile);

    /// <summary>Terrain vertex shader reading <paramref name="blendQuadCount" /> layer-weight quads.</summary>
    public static byte[] CompileVertexShader(int blendQuadCount) =>
        GpuShaderCompiler12.Compile(VertexShaderFile, "main", "vs_5_1", BlendQuadMacros(blendQuadCount));

    /// <summary>Terrain pixel shader for the same count. Never compiled at 0 — see the class remarks.</summary>
    public static byte[] CompilePixelShader(int blendQuadCount) =>
        GpuShaderCompiler12.Compile(PixelShaderFile, "main", "ps_5_1", BlendQuadMacros(blendQuadCount));

    /// <summary>
    ///     Warms <see cref="GpuShaderCompiler12" />'s process-lifetime bytecode cache for a
    ///     permutation, so the render thread pays only <c>CreateGraphicsPipelineState</c> when the
    ///     first cell of that width uploads.
    ///     <para>
    ///         Called from the background cell-build tasks, which is where a permutation is first
    ///         KNOWN to be needed and the one place the FXC compile costs nothing visible. Safe off
    ///         the render thread: the compiler holds no GPU state, its cache is a
    ///         <c>ConcurrentDictionary</c>, and each compile now owns its include handler (a shared
    ///         one was the 2026-08-24 <c>X1505</c> race).
    ///     </para>
    /// </summary>
    public static void PrecompileBlendPermutation(int blendQuadCount)
    {
        CompileVertexShader(blendQuadCount);
        CompilePixelShader(blendQuadCount);
    }

    private static ShaderMacro[] BlendQuadMacros(int blendQuadCount) =>
        [new ShaderMacro(BlendQuadMacro, blendQuadCount.ToString(CultureInfo.InvariantCulture))];

    /// <summary>
    ///     Build the color pipeline state and its mirror-winding twin from the compiled shader
    ///     bytecode and the matching terrain input layout. Both share the reversed-Z depth state,
    ///     back-face cull, and MSAA settings.
    /// </summary>
    public static (ID3D12PipelineState Pso, ID3D12PipelineState MirrorPso)
        BuildColorPipelineStates(
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

        // Mirror-winding twin of the color PSO for the water-reflection pass: a mirrored
        // (negative-determinant) viewProj flips screen-space winding, so the mirror pass draws
        // terrain with the front-face orientation reversed instead of culling everything visible.
        var mirrorRasterizer = rasterizer;
        mirrorRasterizer.FrontCounterClockwise = false;
        var mirrorPsoDesc = psoDesc;
        mirrorPsoDesc.RasterizerState = mirrorRasterizer;
        var mirrorPso = gpu.Device.CreateGraphicsPipelineState(mirrorPsoDesc);

        return (pso, mirrorPso);
    }

    /// <summary>
    ///     Depth-only PSO: the same vertex path and depth state as the color PSO, but no pixel
    ///     shader and no render targets, so it writes only the depth buffer. Used by the top-down
    ///     overlay pre-pass. Built from the quad-count-0 vertex shader and layout, so this pass
    ///     fetches no layer weights at all — it discarded every one of them before.
    /// </summary>
    public static ID3D12PipelineState BuildDepthOnlyPipelineState(
        GpuDevice12 gpu,
        GpuRootSignature12 rootSignature,
        byte[] vsBytecode,
        InputElementDescription[] inputElements)
    {
        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = rootSignature.RootSignature,
            VertexShader = vsBytecode,
            BlendState = D12.BlendDescription.Opaque,
            RasterizerState = new D12.RasterizerDescription
            {
                FillMode = D12.FillMode.Solid,
                CullMode = D12.CullMode.Back,
                FrontCounterClockwise = true,
                DepthClipEnable = true,
                MultisampleEnable = gpu.SceneSampleCount > 1,
            },
            DepthStencilState = new D12.DepthStencilDescription
            {
                DepthEnable = true,
                DepthWriteMask = D12.DepthWriteMask.All,
                DepthFunc = ComparisonFunction.GreaterEqual, // reversed-Z, same as the color PSO
                StencilEnable = false,
            },
            InputLayout = new InputLayoutDescription(inputElements),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = Array.Empty<Format>(),
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription((uint)gpu.SceneSampleCount, 0),
            SampleMask = uint.MaxValue,
        };
        return gpu.Device.CreateGraphicsPipelineState(psoDesc);
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
