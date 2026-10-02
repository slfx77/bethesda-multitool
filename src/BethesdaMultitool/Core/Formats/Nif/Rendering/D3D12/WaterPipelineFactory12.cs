using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Slfx77.Multitool.WinUI.Direct3D12.Shaders;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D12 = Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>Creates the game-specific water pipeline families without recording commands or allocating textures.</summary>
/// <remarks>Callers retain initialized families before calling. Each family owns partial and complete allocations;
/// returned handles are borrowed until caller-proven GPU retirement.</remarks>
internal static class WaterPipelineFactory12
{
    /// <summary>Compiles and creates all eighteen base water graphics and noise compute permutations.</summary>
    /// <param name="gpu">Borrowed device supplying the scene sample count.</param>
    /// <param name="pipelineResources">Initialized world-root family with eighteen unused slots.</param>
    /// <returns>Borrowed handles and the descriptions used for optional modern-water permutations.</returns>
    /// <remarks>Failures leave successful allocations owned by the caller-retained family.</remarks>
    internal static WaterPipelineSet12 CreateBasePipelines(
        GpuDevice12 gpu, ShaderPipelineResources pipelineResources)
    {
        var noiseScrollBlendBytecode = CompileEmbeddedShader(
            "water_noise.comp.hlsl", "mainScrollBlend", "cs_5_1");
        var noiseNormalBytecode = CompileEmbeddedShader(
            "water_noise.comp.hlsl", "mainNormal", "cs_5_1");
        var noiseDownsampleBytecode = CompileEmbeddedShader(
            "water_noise.comp.hlsl", "mainDownsample", "cs_5_1");
        var noiseScrollBlend = pipelineResources.CreateCompute(0,
            new ComputePipelineStateDescription
            {
                RootSignature = pipelineResources.RootSignature,
                ComputeShader = noiseScrollBlendBytecode,
            });
        var noiseNormal = pipelineResources.CreateCompute(1,
            new ComputePipelineStateDescription
            {
                RootSignature = pipelineResources.RootSignature,
                ComputeShader = noiseNormalBytecode,
            });
        var noiseDownsample = pipelineResources.CreateCompute(2,
            new ComputePipelineStateDescription
            {
                RootSignature = pipelineResources.RootSignature,
                ComputeShader = noiseDownsampleBytecode,
            });

        var vsBytecode = CompileEmbeddedShader("water.vert.hlsl", "main", "vs_5_1");
        // Each game selects its shader file; Fallout 76 adds the float-optics/dual-source macro
        // on the Fallout 4 file. All direct variants are eager because loading data can change
        // the active game without rebuilding pipelines.
        var psBytecode = CompileEmbeddedShader("water_fnv.frag.hlsl", "main", "ps_5_1");

        var rasterizer = new D12.RasterizerDescription
        {
            FillMode = D12.FillMode.Solid,
            CullMode = D12.CullMode.None, // flat plane, both faces
            FrontCounterClockwise = true,
            DepthClipEnable = true,
            // Antialias edges on the multisampled scene RT (no-op when scene isn't MSAA).
            MultisampleEnable = gpu.SceneSampleCount > 1,
        };

        // Read depth so terrain occludes submerged water; don't write depth so layer
        // order (terrain → references → water → wireframe) stays sane.
        var depth = new D12.DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = D12.DepthWriteMask.Zero,
            // reversed-Z (near→1, far→0; depth clear = 0). GreaterEqual (not Greater) so water WINS a
            // coplanar tie with the terrain beneath it (3D-2 z-fighting): at a shoreline where the water
            // plane and the land mesh resolve to the same depth, Greater would reject the water fragment
            // and the land would flicker through; GreaterEqual draws the water. Water writes no depth, so
            // letting it pass ties has no knock-on effect on later passes. (This hardware-test PSO is the
            // no-scene-depth-SRV fallback; the depth-sample PSO does the same tie-break in the shader.)
            DepthFunc = ComparisonFunction.GreaterEqual,
            StencilEnable = false,
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
            // Preserve destination alpha for offscreen capture while blending translucent color.
            // The live swapchain ignores scene alpha; capture readback still consumes it.
            SourceBlendAlpha = D12.Blend.One,
            DestinationBlendAlpha = D12.Blend.One,
            BlendOperationAlpha = D12.BlendOperation.Max,
            RenderTargetWriteMask = D12.ColorWriteEnable.All,
        };

        // Skyrim's recovered BSWaterShader output is opaque: transmission comes from the
        // RefractionSampler RGB value, not destination blending, and oC0.a is always one. Keep a
        // separate blend state so the snapshot shader overwrites the scene target exactly once;
        // forcing alpha=1 through the SrcAlpha/InvSrcAlpha PSO would discard the sampled scene lane.
        var skyrimOpaqueSnapshotBlend = new D12.BlendDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false,
        };
        skyrimOpaqueSnapshotBlend.RenderTarget[0] = new D12.RenderTargetBlendDescription
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

        // FO76 per-channel transmission uses the pixel shader's SV_Target1.rgb as the destination
        // factor: final.rgb = Target0.rgb + scene.rgb * Target1.rgb. D3D12 dual-source blending is
        // defined for a single bound render target, which is exactly this pass's SceneColor layout.
        // Alpha remains the established Max(source,destination) contract for capture readback.
        var fallout76OpticsBlend = new D12.BlendDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false,
        };
        fallout76OpticsBlend.RenderTarget[0] = new D12.RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = D12.Blend.One,
            DestinationBlend = D12.Blend.Source1Color,
            BlendOperation = D12.BlendOperation.Add,
            SourceBlendAlpha = D12.Blend.One,
            DestinationBlendAlpha = D12.Blend.One,
            BlendOperationAlpha = D12.BlendOperation.Max,
            RenderTargetWriteMask = D12.ColorWriteEnable.All,
        };

        var psOblivionBytecode = CompileEmbeddedShader(
            "water_oblivion.frag.hlsl", "main", "ps_5_1");
        var psFo4Bytecode = CompileEmbeddedShader(
            "water_fo4.frag.hlsl", "main", "ps_5_1");
        var psFo76OpticsBytecode = CompileEmbeddedShader(
            "water_fo4.frag.hlsl", "main", "ps_5_1",
            new ShaderMacro("FO76_WATER_OPTICS", "1"));
        var psMorrowindBytecode = CompileEmbeddedShader(
            "water_morrowind.frag.hlsl", "main", "ps_5_1");
        var psStarfieldBytecode = CompileEmbeddedShader(
            "water_starfield.frag.hlsl", "main", "ps_5_1");
        // Games without a recovered water shader use the flat fallback.
        var psFlatBytecode = CompileEmbeddedShader(
            "water_flat.frag.hlsl", "main", "ps_5_1");
        var psFnvWater001Bytecode = CompileEmbeddedShader(
            "water_fnv001.frag.hlsl", "main", "ps_5_1",
            new ShaderMacro("WATER_HARDWARE_OCCLUSION", "1"));
        var psSkyrimOpaqueSnapshotBytecode = CompileEmbeddedShader(
            "water_fnv.frag.hlsl", "main", "ps_5_1",
            new ShaderMacro("SKYRIM_OPAQUE_REFRACTION", "1"),
            new ShaderMacro("WATER_HARDWARE_OCCLUSION", "1"));

        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = pipelineResources.RootSignature,
            VertexShader = vsBytecode,
            PixelShader = psBytecode,
            BlendState = blend,
            RasterizerState = rasterizer,
            DepthStencilState = depth,
            InputLayout = new InputLayoutDescription(Array.Empty<InputElementDescription>()),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { Gpu.D3D12.GpuSceneFormats.SceneColor },
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription((uint)gpu.SceneSampleCount, 0),
            SampleMask = uint.MaxValue,
        };
        // Vortice descriptions are reference types. Retained templates and blend exceptions
        // need independent descriptions so a later shader or blend assignment cannot leak into them.
        var depthTemplate = CopyDescription(psoDesc);
        var water = pipelineResources.CreateGraphics(3, psoDesc);
        psoDesc.PixelShader = psOblivionBytecode;
        var oblivion = pipelineResources.CreateGraphics(4, psoDesc);
        psoDesc.PixelShader = psFo4Bytecode;
        var fo4 = pipelineResources.CreateGraphics(5, psoDesc);
        var fallout76OpticsPsoDesc = CopyDescription(psoDesc);
        fallout76OpticsPsoDesc.PixelShader = psFo76OpticsBytecode;
        fallout76OpticsPsoDesc.BlendState = fallout76OpticsBlend;
        var fo76Optics = pipelineResources.CreateGraphics(6, fallout76OpticsPsoDesc);
        psoDesc.PixelShader = psMorrowindBytecode;
        var morrowind = pipelineResources.CreateGraphics(7, psoDesc);
        psoDesc.PixelShader = psStarfieldBytecode;
        var starfield = pipelineResources.CreateGraphics(8, psoDesc);
        psoDesc.PixelShader = psFlatBytecode;
        var flat = pipelineResources.CreateGraphics(9, psoDesc);
        psoDesc.PixelShader = psBytecode;

        // Depth-sample variant: the scene depth buffer is bound BOTH as an SRV (depth-fade /
        // column math) and as a READ-ONLY DSV, so the same GreaterEqual hardware test as _pso
        // still rejects water behind opaque geometry — per sample, which antialiases water edges
        // at MSAA'd mesh silhouettes. The WATER_HARDWARE_OCCLUSION shader variants drop their
        // pixel-rate occlusion clip (binary keep/kill there left bright fringes around meshes in
        // front of water). Hosts must bind the read-only DSV while depth sits in
        // DepthRead | PixelShaderResource; depth state and formats intentionally match _pso.
        var psDepthSampleBytecode = CompileEmbeddedShader(
            "water_fnv.frag.hlsl", "main", "ps_5_1",
            new ShaderMacro("WATER_HARDWARE_OCCLUSION", "1"));
        var psOblivionDepthSampleBytecode = CompileEmbeddedShader(
            "water_oblivion.frag.hlsl", "main", "ps_5_1",
            new ShaderMacro("WATER_HARDWARE_OCCLUSION", "1"));
        var psFo4DepthSampleBytecode = CompileEmbeddedShader(
            "water_fo4.frag.hlsl", "main", "ps_5_1",
            new ShaderMacro("WATER_HARDWARE_OCCLUSION", "1"));
        var psFo76OpticsDepthSampleBytecode = CompileEmbeddedShader(
            "water_fo4.frag.hlsl", "main", "ps_5_1",
            new ShaderMacro("FO76_WATER_OPTICS", "1"),
            new ShaderMacro("WATER_HARDWARE_OCCLUSION", "1"));
        var psMorrowindDepthSampleBytecode = CompileEmbeddedShader(
            "water_morrowind.frag.hlsl", "main", "ps_5_1",
            new ShaderMacro("WATER_HARDWARE_OCCLUSION", "1"));
        var psStarfieldDepthSampleBytecode = CompileEmbeddedShader(
            "water_starfield.frag.hlsl", "main", "ps_5_1",
            new ShaderMacro("WATER_HARDWARE_OCCLUSION", "1"));
        psoDesc.PixelShader = psDepthSampleBytecode;
        var depthSampleTemplate = CopyDescription(psoDesc);
        var depthSample = pipelineResources.CreateGraphics(10, psoDesc);
        // WATER001 always consumes both scene depth and a separate single-sample opaque-scene
        // snapshot; like every depth-sample PSO it keeps the hardware GreaterEqual test through
        // the host's read-only DSV (its manual occlusion clip is compiled out above).
        psoDesc.PixelShader = psFnvWater001Bytecode;
        var fnvWater001DepthSample = pipelineResources.CreateGraphics(11, psoDesc);
        var skyrimOpaqueSnapshotPsoDesc = CopyDescription(psoDesc);
        skyrimOpaqueSnapshotPsoDesc.PixelShader = psSkyrimOpaqueSnapshotBytecode;
        skyrimOpaqueSnapshotPsoDesc.BlendState = skyrimOpaqueSnapshotBlend;
        var skyrimOpaqueSnapshotDepthSample = pipelineResources.CreateGraphics(12, skyrimOpaqueSnapshotPsoDesc);
        psoDesc.PixelShader = psOblivionDepthSampleBytecode;
        var oblivionDepthSample = pipelineResources.CreateGraphics(13, psoDesc);
        psoDesc.PixelShader = psFo4DepthSampleBytecode;
        var fo4DepthSample = pipelineResources.CreateGraphics(14, psoDesc);
        fallout76OpticsPsoDesc = CopyDescription(psoDesc);
        fallout76OpticsPsoDesc.PixelShader = psFo76OpticsDepthSampleBytecode;
        fallout76OpticsPsoDesc.BlendState = fallout76OpticsBlend;
        var fo76OpticsDepthSample = pipelineResources.CreateGraphics(15, fallout76OpticsPsoDesc);
        psoDesc.PixelShader = psMorrowindDepthSampleBytecode;
        var morrowindDepthSample = pipelineResources.CreateGraphics(16, psoDesc);
        psoDesc.PixelShader = psStarfieldDepthSampleBytecode;
        var starfieldDepthSample = pipelineResources.CreateGraphics(17, psoDesc);

        return new WaterPipelineSet12
        {
            NoiseScrollBlend = noiseScrollBlend,
            NoiseNormal = noiseNormal,
            NoiseDownsample = noiseDownsample,
            Water = water,
            Oblivion = oblivion,
            Fo4 = fo4,
            Fo76Optics = fo76Optics,
            Morrowind = morrowind,
            Starfield = starfield,
            Flat = flat,
            DepthSample = depthSample,
            FnvWater001DepthSample = fnvWater001DepthSample,
            SkyrimOpaqueSnapshotDepthSample = skyrimOpaqueSnapshotDepthSample,
            OblivionDepthSample = oblivionDepthSample,
            Fo4DepthSample = fo4DepthSample,
            Fo76OpticsDepthSample = fo76OpticsDepthSample,
            MorrowindDepthSample = morrowindDepthSample,
            StarfieldDepthSample = starfieldDepthSample,
            DepthTemplate = depthTemplate,
            DepthSampleTemplate = depthSampleTemplate,
        };
    }

    /// <summary>Creates two modern-water graphics permutations and four ordered compute permutations.</summary>
    /// <param name="pipelineResources">Initialized world-root family with six unused slots.</param>
    /// <param name="depthTemplate">Independent base description for ordinary alpha-blended, depth-tested water.</param>
    /// <param name="depthSampleTemplate">Independent base description for ordinary alpha-blended water sampling read-only depth.</param>
    /// <returns>Borrowed graphics and compute handles after all six native creations succeed.</returns>
    /// <remarks>Failures leave successful allocations owned by the caller-retained family.</remarks>
    internal static ModernWaterPipelineSet12 CreateModernPipelines(
        ShaderPipelineResources pipelineResources,
        GraphicsPipelineStateDescription depthTemplate,
        GraphicsPipelineStateDescription depthSampleTemplate)
    {
        var modernPixelBytecode = CompileEmbeddedShader(
            "water_fo4.frag.hlsl",
            "main",
            "ps_5_1",
            new ShaderMacro("FO4_WATER_ARCHITECTURAL", "1"));
        // The depth-sample template carries a hardware GreaterEqual test against the
        // host's read-only DSV, so its pixel shader must be the WATER_HARDWARE_OCCLUSION
        // compile (occlusion clip dropped) like every other depth-sample PSO.
        var modernPixelDepthSampleBytecode = CompileEmbeddedShader(
            "water_fo4.frag.hlsl",
            "main",
            "ps_5_1",
            new ShaderMacro("FO4_WATER_ARCHITECTURAL", "1"),
            new ShaderMacro("WATER_HARDWARE_OCCLUSION", "1"));
        var pixelDescription = CopyDescription(depthTemplate);
        pixelDescription.PixelShader = modernPixelBytecode;
        var pixel = pipelineResources.CreateGraphics(0, pixelDescription);
        var pixelDepthDescription = CopyDescription(depthSampleTemplate);
        pixelDepthDescription.PixelShader = modernPixelDepthSampleBytecode;
        var pixelDepthSample = pipelineResources.CreateGraphics(1, pixelDepthDescription);

        string[] entryPoints =
        [
            "mainBodyCoverage",
            "mainNormal",
            "mainGloss",
            "mainDepthLut",
        ];
        var compute = new ID3D12PipelineState[entryPoints.Length];
        for (var index = 0; index < entryPoints.Length; index++)
        {
            var bytecode = CompileEmbeddedShader(
                "water_modern.comp.hlsl", entryPoints[index], "cs_5_1");
            compute[index] = pipelineResources.CreateCompute(index + 2,
                new ComputePipelineStateDescription
                {
                    RootSignature = pipelineResources.RootSignature,
                    ComputeShader = bytecode,
                });
        }

        return new ModernWaterPipelineSet12
        {
            Pixel = pixel,
            PixelDepthSample = pixelDepthSample,
            ComputePipelines = compute,
        };
    }

    /// <summary>Copies the pinned Vortice description and mutable layout arrays for an independent permutation.</summary>
    /// <param name="source">Complete description whose pipeline state must remain unchanged.</param>
    /// <returns>An independent description with the same shader, raster, depth, sampling and binding values.</returns>
    /// <remarks>Copies all twenty fields in Vortice.Direct3D12 3.8.2. Shader memories and the root are borrowed,
    /// and a cached-PSO blob remains borrowed; cloning the description creates no native-resource ownership.</remarks>
    private static GraphicsPipelineStateDescription CopyDescription(GraphicsPipelineStateDescription source) => new()
    {
        RootSignature = source.RootSignature,
        VertexShader = source.VertexShader,
        PixelShader = source.PixelShader,
        DomainShader = source.DomainShader,
        HullShader = source.HullShader,
        GeometryShader = source.GeometryShader,
        StreamOutput = source.StreamOutput is { } streamOutput
            ? new StreamOutputDescription
            {
                Elements = streamOutput.Elements?.ToArray(),
                Strides = streamOutput.Strides?.ToArray(),
                RasterizedStream = streamOutput.RasterizedStream,
            }
            : null,
        BlendState = source.BlendState,
        SampleMask = source.SampleMask,
        RasterizerState = source.RasterizerState,
        DepthStencilState = source.DepthStencilState,
        InputLayout = source.InputLayout is { } inputLayout
            ? new InputLayoutDescription { Elements = inputLayout.Elements?.ToArray() }
            : null,
        IndexBufferStripCutValue = source.IndexBufferStripCutValue,
        PrimitiveTopologyType = source.PrimitiveTopologyType,
        RenderTargetFormats = source.RenderTargetFormats.ToArray(),
        DepthStencilFormat = source.DepthStencilFormat,
        SampleDescription = source.SampleDescription,
        NodeMask = source.NodeMask,
        CachedPSO = source.CachedPSO,
        Flags = source.Flags,
    };

    /// <summary>Obtains a cached shader permutation through the application cache and Shared compiler.</summary>
    /// <param name="name">Embedded shader file name.</param>
    /// <param name="entryPoint">HLSL entry point.</param>
    /// <param name="profile">Compiler target profile.</param>
    /// <param name="defines">Definitions selecting the shader permutation.</param>
    /// <returns>Cached DXBC passed to native pipeline creation without a payload copy.</returns>
    private static ReadOnlyMemory<byte> CompileEmbeddedShader(
        string name, string entryPoint, string profile, params ShaderMacro[] defines) =>
        GpuShaderCompiler12.Compile(name, entryPoint, profile, defines);
}
