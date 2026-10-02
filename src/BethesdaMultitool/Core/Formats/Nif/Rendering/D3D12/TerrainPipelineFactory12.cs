using System.Globalization;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using Slfx77.Multitool.WinUI.Direct3D12.Shaders;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D12 = Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Compiles the embedded terrain HLSL shaders and builds the graphics pipeline states the
///     <c>TerrainRenderer12</c> uses: the textured color PSO and its mirror-winding twin, a
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

    /// <summary>Gets an embedded shader permutation through the application cache and Shared compiler.</summary>
    /// <param name="name">Embedded shader file name.</param>
    /// <param name="entryPoint">HLSL entry point.</param>
    /// <param name="profile">Native compiler target profile.</param>
    /// <returns>Read-only cached DXBC passed directly to native pipeline creation without a payload copy.</returns>
    public static ReadOnlyMemory<byte> CompileEmbeddedShader(string name, string entryPoint, string profile) =>
        GpuShaderCompiler12.Compile(name, entryPoint, profile);

    /// <summary>Terrain vertex shader reading <paramref name="blendQuadCount" /> layer-weight quads.</summary>
    /// <returns>Read-only cached vertex bytecode for the requested layer-weight layout.</returns>
    public static ReadOnlyMemory<byte> CompileVertexShader(int blendQuadCount) =>
        GpuShaderCompiler12.Compile(VertexShaderFile, "main", "vs_5_1", BlendQuadMacros(blendQuadCount));

    /// <summary>Terrain pixel shader for the same count. Never compiled at 0 — see the class remarks.</summary>
    /// <returns>Read-only cached pixel bytecode for the requested layer-weight layout.</returns>
    public static ReadOnlyMemory<byte> CompilePixelShader(int blendQuadCount) =>
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
    /// <param name="gpu">Borrowed device supplying the scene sample count.</param>
    /// <param name="pipelineResources">Retained family sharing the world root and owning the returned handles.</param>
    /// <param name="blendQuadCount">Layer-weight width, from one through the maximum supported terrain width.</param>
    /// <param name="vsBytecode">Compiled vertex shader matching the requested layer-weight width.</param>
    /// <param name="psBytecode">Compiled pixel shader matching the requested layer-weight width.</param>
    /// <param name="inputElements">Vertex layout matching both shader permutations.</param>
    /// <returns>Borrowed color and mirror handles, published together only after both are available.</returns>
    /// <remarks>A failed mirror allocation leaves the first handle owned for a retry using the same permutation.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The layer-weight width is outside the supported range.</exception>
    public static (ID3D12PipelineState Pso, ID3D12PipelineState MirrorPso)
        BuildColorPipelineStates(
        GpuDevice12 gpu,
        ShaderPipelineResources pipelineResources,
        int blendQuadCount,
        ReadOnlyMemory<byte> vsBytecode,
        ReadOnlyMemory<byte> psBytecode,
        InputElementDescription[] inputElements)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blendQuadCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(blendQuadCount, TerrainVertexLayout.MaxBlendQuads);
        var colorSlot = 2 + 2 * (blendQuadCount - 1);
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
            RootSignature = pipelineResources.RootSignature,
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
        if (!pipelineResources.TryGetPipeline(colorSlot, out var pso))
        {
            pso = pipelineResources.CreateGraphics(colorSlot, psoDesc);
        }

        // Mirror-winding twin of the color PSO for the water-reflection pass: a mirrored
        // (negative-determinant) viewProj flips screen-space winding, so the mirror pass draws
        // terrain with the front-face orientation reversed instead of culling everything visible.
        var mirrorRasterizer = rasterizer;
        mirrorRasterizer.FrontCounterClockwise = false;
        var mirrorPsoDesc = psoDesc;
        mirrorPsoDesc.RasterizerState = mirrorRasterizer;
        if (!pipelineResources.TryGetPipeline(colorSlot + 1, out var mirrorPso))
        {
            mirrorPso = pipelineResources.CreateGraphics(colorSlot + 1, mirrorPsoDesc);
        }

        return (pso!, mirrorPso!);
    }

    /// <summary>
    ///     Depth-only PSO: the same vertex path and depth state as the color PSO, but no pixel
    ///     shader and no render targets, so it writes only the depth buffer. Used by the top-down
    ///     overlay pre-pass. Built from the quad-count-0 vertex shader and layout, so this pass
    ///     fetches no layer weights at all — it discarded every one of them before.
    /// </summary>
    /// <param name="gpu">Borrowed device supplying the scene sample count.</param>
    /// <param name="pipelineResources">Retained family sharing the world root and owning reserved slot zero.</param>
    /// <param name="vsBytecode">Compiled zero-quad vertex shader.</param>
    /// <param name="inputElements">Zero-quad terrain vertex layout.</param>
    /// <returns>The borrowed depth-only handle retained by the family.</returns>
    public static ID3D12PipelineState BuildDepthOnlyPipelineState(
        GpuDevice12 gpu,
        ShaderPipelineResources pipelineResources,
        ReadOnlyMemory<byte> vsBytecode,
        InputElementDescription[] inputElements)
    {
        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = pipelineResources.RootSignature,
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
        return pipelineResources.CreateGraphics(0, psoDesc);
    }

    /// <summary>
    ///     Sun-shadow depth PSO: like the depth-only variant but always SINGLE-sample (the shadow
    ///     map is never MSAA regardless of the scene), cull NONE (a grazing sun sees the back faces
    ///     of slopes — culling them leaks light through hills), and a NEGATIVE depth bias (reversed-Z
    ///     stores larger values nearer the light, so the acne fix pushes stored depth SMALLER).
    ///     Mirrors <c>ReferencePipelineFactory12.CreateShadowPipelineState</c>.
    /// </summary>
    /// <param name="pipelineResources">Retained family sharing the world root and owning reserved slot one.</param>
    /// <param name="vsBytecode">Compiled zero-quad vertex shader.</param>
    /// <param name="inputElements">Zero-quad terrain vertex layout.</param>
    /// <returns>The borrowed single-sample shadow handle retained by the family.</returns>
    public static ID3D12PipelineState BuildShadowPipelineState(
        ShaderPipelineResources pipelineResources,
        ReadOnlyMemory<byte> vsBytecode,
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
            RootSignature = pipelineResources.RootSignature,
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
        return pipelineResources.CreateGraphics(1, psoDesc);
    }
}
