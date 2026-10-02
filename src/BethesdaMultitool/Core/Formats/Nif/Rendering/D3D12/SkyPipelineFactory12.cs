using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Slfx77.Multitool.WinUI.Direct3D12.Shaders;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D12 = Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>Creates the authored sky geometry and celestial billboard pipelines without recording GPU commands.</summary>
/// <remarks>
/// Each caller retains an initialized family sharing the world root before calling. Returned handles are borrowed;
/// the family owns every successful allocation, including partial construction, until caller-proven GPU retirement.
/// </remarks>
internal static class SkyPipelineFactory12
{
    /// <summary>Compiles and creates the opaque atmosphere, additive stars and alpha-composited cloud pipelines.</summary>
    /// <param name="gpu">Borrowed device supplying the scene sample count.</param>
    /// <param name="pipelineResources">Initialized family with unused slots zero through two and the world root.</param>
    /// <returns>The borrowed gradient, stars and clouds handles after all three creations succeed.</returns>
    /// <remarks>On failure, the retained family still owns earlier successful allocations for disposal.</remarks>
    public static (ID3D12PipelineState Gradient, ID3D12PipelineState Stars, ID3D12PipelineState Clouds)
        CreateGeometryPipelines(GpuDevice12 gpu, ShaderPipelineResources pipelineResources)
    {
        var vs = GpuShaderCompiler12.Compile("sky_geo.vert.hlsl", "main", "vs_5_1");
        var ps = GpuShaderCompiler12.Compile("sky_geo.frag.hlsl", "main", "ps_5_1");
        var gradient = CreateGeometryPipeline(gpu, pipelineResources, 0, vs, ps, blendEnabled: false, additive: false);
        var stars = CreateGeometryPipeline(gpu, pipelineResources, 1, vs, ps, blendEnabled: true, additive: true);
        var clouds = CreateGeometryPipeline(gpu, pipelineResources, 2, vs, ps, blendEnabled: true, additive: false);
        return (gradient, stars, clouds);
    }

    /// <summary>Compiles and creates the additive glare and alpha-composited sun or moon billboard pipelines.</summary>
    /// <param name="gpu">Borrowed device supplying the scene sample count.</param>
    /// <param name="pipelineResources">Initialized family with unused slots zero and one and the world root.</param>
    /// <returns>The borrowed additive and alpha handles after both creations succeed.</returns>
    /// <remarks>Billboards retain their own alpha-channel blending and disabled depth clipping.</remarks>
    public static (ID3D12PipelineState Additive, ID3D12PipelineState Alpha)
        CreateBillboardPipelines(GpuDevice12 gpu, ShaderPipelineResources pipelineResources)
    {
        var vs = GpuShaderCompiler12.Compile("sky_billboard.vert.hlsl", "main", "vs_5_1");
        var ps = GpuShaderCompiler12.Compile("sky_billboard.frag.hlsl", "main", "ps_5_1");

        // Depth OFF (DSV stays bound — terrain follows in the same pass and overwrites these).
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
            DepthClipEnable = false,
            MultisampleEnable = gpu.SceneSampleCount > 1,
        };

        var additive = CreateBillboardPipeline(gpu, pipelineResources, vs, ps, depth, rasterizer, additive: true);
        var alpha = CreateBillboardPipeline(gpu, pipelineResources, vs, ps, depth, rasterizer, additive: false);
        return (additive, alpha);
    }

    /// <summary>Creates one sky geometry pipeline with the layer's original blend policy and vertex layout.</summary>
    /// <param name="gpu">Device and scene sample configuration.</param>
    /// <param name="pipelineResources">Retained pipeline family supplying the world root.</param>
    /// <param name="slot">Unused family slot for this layer's pipeline.</param>
    /// <param name="vs">Read-only vertex bytecode borrowed during creation.</param>
    /// <param name="ps">Read-only pixel bytecode borrowed during creation.</param>
    /// <param name="blendEnabled">False for the opaque atmosphere layer.</param>
    /// <param name="additive">True for stars, whose color and alpha accumulate over the destination.</param>
    /// <returns>The borrowed native sky geometry pipeline.</returns>
    private static ID3D12PipelineState CreateGeometryPipeline(
        GpuDevice12 gpu, ShaderPipelineResources pipelineResources, int slot,
        ReadOnlyMemory<byte> vs, ReadOnlyMemory<byte> ps, bool blendEnabled, bool additive)
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
        if (blendEnabled)
        {
            blend.RenderTarget[0] = new D12.RenderTargetBlendDescription
            {
                BlendEnable = true,
                SourceBlend = D12.Blend.SourceAlpha,
                DestinationBlend = additive ? D12.Blend.One : D12.Blend.InverseSourceAlpha,
                BlendOperation = D12.BlendOperation.Add,
                SourceBlendAlpha = D12.Blend.One,
                DestinationBlendAlpha = additive ? D12.Blend.One : D12.Blend.InverseSourceAlpha,
                BlendOperationAlpha = D12.BlendOperation.Add,
                RenderTargetWriteMask = D12.ColorWriteEnable.All,
            };
        }
        else
        {
            blend.RenderTarget[0] = new D12.RenderTargetBlendDescription
            {
                BlendEnable = false, // opaque background fill
                RenderTargetWriteMask = D12.ColorWriteEnable.All,
            };
        }

        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = pipelineResources.RootSignature,
            VertexShader = vs,
            PixelShader = ps,
            BlendState = blend,
            RasterizerState = rasterizer,
            DepthStencilState = depth,
            InputLayout = new InputLayoutDescription(inputElements),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { GpuSceneFormats.SceneColor },
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription((uint)gpu.SceneSampleCount, 0),
            SampleMask = uint.MaxValue,
        };
        return pipelineResources.CreateGraphics(slot, psoDesc);
    }

    /// <summary>Creates a billboard pipeline while preserving its distinct color and alpha blending.</summary>
    /// <param name="gpu">Device and scene sample configuration.</param>
    /// <param name="pipelineResources">Retained pipeline family supplying the world root.</param>
    /// <param name="vs">Read-only vertex bytecode borrowed during creation.</param>
    /// <param name="ps">Read-only pixel bytecode borrowed during creation.</param>
    /// <param name="depth">Depth and stencil policy.</param>
    /// <param name="rasterizer">Billboard rasterization policy.</param>
    /// <param name="additive">Whether to add shaded color instead of alpha-compositing it; destination alpha remains inverse source alpha.</param>
    /// <returns>The borrowed native billboard pipeline.</returns>
    private static ID3D12PipelineState CreateBillboardPipeline(
        GpuDevice12 gpu, ShaderPipelineResources pipelineResources,
        ReadOnlyMemory<byte> vs, ReadOnlyMemory<byte> ps,
        D12.DepthStencilDescription depth, D12.RasterizerDescription rasterizer, bool additive)
    {
        var blend = new D12.BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
        blend.RenderTarget[0] = new D12.RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = D12.Blend.SourceAlpha,
            DestinationBlend = additive ? D12.Blend.One : D12.Blend.InverseSourceAlpha,
            BlendOperation = D12.BlendOperation.Add,
            SourceBlendAlpha = D12.Blend.One,
            DestinationBlendAlpha = D12.Blend.InverseSourceAlpha,
            BlendOperationAlpha = D12.BlendOperation.Add,
            RenderTargetWriteMask = D12.ColorWriteEnable.All,
        };

        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = pipelineResources.RootSignature,
            VertexShader = vs,
            PixelShader = ps,
            BlendState = blend,
            RasterizerState = rasterizer,
            DepthStencilState = depth,
            InputLayout = new InputLayoutDescription(Array.Empty<InputElementDescription>()),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { GpuSceneFormats.SceneColor },
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription((uint)gpu.SceneSampleCount, 0),
            SampleMask = uint.MaxValue,
        };
        return pipelineResources.CreateGraphics(additive ? 0 : 1, psoDesc);
    }
}
