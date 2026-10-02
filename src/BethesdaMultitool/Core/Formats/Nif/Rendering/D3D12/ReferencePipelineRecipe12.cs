using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D12 = Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>Builds the production reference-material pipeline state without native allocation or command recording.</summary>
internal static class ReferencePipelineRecipe12
{
    /// <summary>Describes the common direct or instanced reference pipeline using the supplied shader ABI.</summary>
    /// <param name="rootSignature">Borrowed world root matching the vertex and pixel shader bindings.</param>
    /// <param name="sceneSampleCount">Scene render-target and depth sample count.</param>
    /// <param name="vsBytecode">Borrowed direct or instanced vertex shader bytecode.</param>
    /// <param name="psBytecode">Borrowed game/material pixel shader bytecode.</param>
    /// <param name="state">Exact culling, blending, depth, decal, coverage and mirror-winding choices.</param>
    /// <returns>A new description retaining the established mesh input ABI and scene target formats.</returns>
    internal static GraphicsPipelineStateDescription CreateGraphicsDescription(
        ID3D12RootSignature rootSignature,
        int sceneSampleCount,
        ReadOnlyMemory<byte> vsBytecode,
        ReadOnlyMemory<byte> psBytecode,
        ReferencePipelineRenderState12 state)
    {
        var rasterizer = new D12.RasterizerDescription
        {
            FillMode = D12.FillMode.Solid,
            CullMode = state.DoubleSided ? D12.CullMode.None : D12.CullMode.Back,
            // A mirrored (negative-determinant) viewProj flips triangle orientation in screen
            // space; the water-reflection replay uses winding-flipped twins of the back-culled
            // opaque PSOs so single-sided geometry keeps its front faces in the mirror.
            FrontCounterClockwise = !state.MirrorWinding,
            DepthClipEnable = true,
            // Antialias triangle edges on the multisampled scene RT (no-op when scene isn't MSAA).
            MultisampleEnable = sceneSampleCount > 1,
        };

        if (state.Decal)
        {
            // Decal overlays are authored coplanar with their backing surface; bias them TOWARD the
            // camera so they win the depth tie. Reversed-Z (GreaterEqual, near→1) means "closer" is a
            // LARGER depth value, so the bias is positive. Small relative to the navmesh overlay's
            // 2000/2.0 — a decal must only clear its own backing wall, not float over nearby props.
            rasterizer.DepthBias = 64;
            rasterizer.DepthBiasClamp = 0f;
            rasterizer.SlopeScaledDepthBias = 1f;
        }

        var depth = new D12.DepthStencilDescription
        {
            // NoLighting "zbuffer test" Shader Flags bit 31 CLEAR ⇒ test OFF (engine-honored for
            // the NoLighting family only; every other route passes the default true).
            DepthEnable = state.DepthTestEnabled,
            DepthWriteMask = state.DepthWriteEnabled ? D12.DepthWriteMask.All : D12.DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.GreaterEqual, // reversed-Z (near→1, far→0); depth clear = 0
            StencilEnable = false,
        };

        var blend = new D12.BlendDescription
        {
            AlphaToCoverageEnable = state.AlphaToCoverage,
            IndependentBlendEnable = false,
        };
        blend.RenderTarget[0] = state.BlendAttachment ?? new D12.RenderTargetBlendDescription
        {
            BlendEnable = false,
            SourceBlend = D12.Blend.One,
            DestinationBlend = D12.Blend.Zero,
            BlendOperation = D12.BlendOperation.Add,
            SourceBlendAlpha = D12.Blend.One,
            DestinationBlendAlpha = D12.Blend.Zero,
            BlendOperationAlpha = D12.BlendOperation.Add,
            RenderTargetWriteMask = D12.ColorWriteEnable.All,
        };

        return new GraphicsPipelineStateDescription
        {
            RootSignature = rootSignature,
            VertexShader = vsBytecode,
            PixelShader = psBytecode,
            BlendState = blend,
            RasterizerState = rasterizer,
            DepthStencilState = depth,
            InputLayout = new InputLayoutDescription(GpuMeshBufferFactory12.InputElements),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { Gpu.D3D12.GpuSceneFormats.SceneColor },
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription((uint)sceneSampleCount, 0),
            SampleMask = uint.MaxValue,
        };
    }

    /// <summary>Describes authored color blending while preserving maximum source/destination alpha.</summary>
    /// <param name="srcBlendMode">Unmodified NiAlphaProperty source factor byte.</param>
    /// <param name="dstBlendMode">Unmodified NiAlphaProperty destination factor byte.</param>
    /// <returns>The production blend attachment, including the established unknown-factor fallback.</returns>
    internal static D12.RenderTargetBlendDescription CreateBlendAttachment(byte srcBlendMode, byte dstBlendMode)
    {
        return new D12.RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = NifD3D12BlendMapper.ResolveBlendFactor(srcBlendMode),
            DestinationBlend = NifD3D12BlendMapper.ResolveBlendFactor(dstBlendMode),
            BlendOperation = D12.BlendOperation.Add,
            SourceBlendAlpha = D12.Blend.One,
            DestinationBlendAlpha = D12.Blend.One,
            BlendOperationAlpha = D12.BlendOperation.Max,
            RenderTargetWriteMask = D12.ColorWriteEnable.All
        };
    }
}
