using System.Globalization;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using BethesdaMultitool.Tests.Helpers;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Terrain;

/// <summary>
///     Creates real terrain pipeline states on WARP. This is the only check that validates the
///     terrain vertex ABI end to end, because PSO creation is where D3D12 cross-checks the three
///     things that have to agree and that nothing else can compare:
///     <list type="bullet">
///         <item>the input layout against the compiled vertex shader's input signature,</item>
///         <item>each element's DXGI format against what the input assembler can actually fetch,</item>
///         <item>the shader's register bindings against the shared root signature.</item>
///     </list>
///     <para>
///         Source pins cannot reach any of that, and the headless reflection test can only compare
///         semantic names. After the terrain vertex went from 72 bytes to 12 across three format
///         changes — octahedral SNORM16 normals, a UNORM8 colour, UNORM16 blend weights, and a
///         position rebuilt from <c>SV_VertexID</c> plus root constants at <c>b4</c> — this is what
///         says the result is something a driver will accept.
///     </para>
///     <para>
///         Since phase 3d the layout is a FAMILY: a cell declares only the layer-weight quads its
///         slot count reaches, so every width is exercised here. A width that only some worldspaces
///         produce would otherwise be validated by whichever game happened to be loaded.
///     </para>
///     <para>
///         WARP rather than the installed adapter, deliberately: it is the strictest available
///         validator and the one configuration every machine has.
///     </para>
/// </summary>
[Trait("Category", TestCategories.Gpu)]
public sealed class TerrainPipelineStateGpuTests
{
    /// <summary>Widths a colour pipeline is built for — 1 quad (4 slots) through 4 (all 16).</summary>
    public static TheoryData<int> ColorBlendQuadCounts =>
        [.. Enumerable.Range(1, TerrainVertexLayout.MaxBlendQuads)];

    private static ShaderMacro[] Macros(int blendQuadCount) =>
        [new ShaderMacro("TERRAIN_BLEND_QUADS", blendQuadCount.ToString(CultureInfo.InvariantCulture))];

    [Theory]
    [MemberData(nameof(ColorBlendQuadCounts))]
    public void The_terrain_input_layout_shader_and_root_signature_form_a_valid_pipeline_state(int blendQuadCount)
    {
        GpuTestGuard.SkipUnlessEnabled();

        using var device = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(device);
        using var rootSignature = GpuRootSignature12.Create(device);

        var macros = Macros(blendQuadCount);
        var vertexShader = GpuShaderCompiler12.Compile("terrain_textured.vert.hlsl", "main", "vs_5_1", macros);
        var pixelShader = GpuShaderCompiler12.Compile("terrain_textured.frag.hlsl", "main", "ps_5_1", macros);

        var description = new GraphicsPipelineStateDescription
        {
            RootSignature = rootSignature.RootSignature,
            VertexShader = vertexShader,
            PixelShader = pixelShader,
            InputLayout = new InputLayoutDescription(TerrainVertexLayout.ElementsFor(blendQuadCount)),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.Default,
            DepthStencilFormat = Format.D32_Float,
            RenderTargetFormats = [Format.R8G8B8A8_UNorm],
            SampleDescription = SampleDescription.Default
        };

        using var pipelineState = device.Device.CreateGraphicsPipelineState(description);

        Assert.NotNull(pipelineState);
    }

    [Fact]
    public void The_depth_only_variant_is_valid_without_a_pixel_shader_or_any_layer_weights()
    {
        // The shadow and depth-only passes bind no pixel shader and no per-cell constant buffer,
        // which is exactly why the cell grid travels as root constants. If the vertex shader had
        // picked up a binding those passes do not supply, this is where it would surface.
        //
        // They also compile at zero quads and bind no blend-weight vertex buffer at all: the layer
        // weights were fetched and discarded on every cascade before 3d. A quad-count-0 shader that
        // still declared a weight input would fail here rather than silently reading slot 1's
        // leftover binding from the previous pass.
        GpuTestGuard.SkipUnlessEnabled();

        using var device = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(device);
        using var rootSignature = GpuRootSignature12.Create(device);

        var elements = TerrainVertexLayout.ElementsFor(0);
        Assert.All(elements, e => Assert.Equal(TerrainVertexLayout.GeometrySlot, (int)e.Slot));

        var description = new GraphicsPipelineStateDescription
        {
            RootSignature = rootSignature.RootSignature,
            VertexShader = GpuShaderCompiler12.Compile(
                "terrain_textured.vert.hlsl", "main", "vs_5_1", Macros(0)),
            InputLayout = new InputLayoutDescription(elements),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.Default,
            DepthStencilFormat = Format.D32_Float,
            RenderTargetFormats = [],
            SampleDescription = SampleDescription.Default
        };

        using var pipelineState = device.Device.CreateGraphicsPipelineState(description);

        Assert.NotNull(pipelineState);
    }

    [Fact]
    public void A_narrow_layout_under_the_widest_shader_is_rejected_rather_than_reading_rubbish()
    {
        // The mismatch that makes the family dangerous, asserted as an actual driver refusal: the
        // widest vertex shader declares four weight attributes, and a one-quad layout supplies one.
        // D3D12 rejects exactly this direction — which is what lets EnsureBlendPipelines pair a cell
        // with a permutation and be sure a wrong pairing cannot render as subtly wrong terrain.
        GpuTestGuard.SkipUnlessEnabled();

        using var device = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(device);
        using var rootSignature = GpuRootSignature12.Create(device);

        var widest = Macros(TerrainVertexLayout.MaxBlendQuads);
        var description = new GraphicsPipelineStateDescription
        {
            RootSignature = rootSignature.RootSignature,
            VertexShader = GpuShaderCompiler12.Compile("terrain_textured.vert.hlsl", "main", "vs_5_1", widest),
            PixelShader = GpuShaderCompiler12.Compile("terrain_textured.frag.hlsl", "main", "ps_5_1", widest),
            InputLayout = new InputLayoutDescription(TerrainVertexLayout.ElementsFor(1)),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RasterizerState = RasterizerDescription.CullNone,
            BlendState = BlendDescription.Opaque,
            DepthStencilState = DepthStencilDescription.Default,
            DepthStencilFormat = Format.D32_Float,
            RenderTargetFormats = [Format.R8G8B8A8_UNorm],
            SampleDescription = SampleDescription.Default
        };

        Assert.ThrowsAny<Exception>(() => device.Device.CreateGraphicsPipelineState(description));
    }
}
