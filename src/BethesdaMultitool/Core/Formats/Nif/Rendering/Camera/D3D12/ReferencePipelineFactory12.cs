#if WINDOWS_GUI
using System.Reflection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D12 = Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>
///     Owns the GPU pipeline-state objects (PSOs) used by <see cref="ReferenceRenderer12" />:
///     compiles the reference shaders once, builds the two opaque (back-face / double-sided)
///     instanced PSOs up front, and lazily creates + caches blended PSOs keyed by blend mode.
///     This is a pure GPU-resource factory — it records no command-list work, so the renderer's
///     draw ordering is unaffected by the extraction.
/// </summary>
internal sealed class ReferencePipelineFactory12 : IDisposable
{
    private const ShaderFlags EnableUnboundedDescriptorTables = (ShaderFlags)0x00100000;

    private readonly GpuDevice12 _gpu;
    private readonly GpuRootSignature12 _rootSignature;
    private readonly byte[] _blendedVsBytecode;
    private readonly byte[] _psBytecode;
    private readonly Dictionary<BlendPipelineKey, ID3D12PipelineState> _blendPsos = new();
    private bool _disposed;

    public ReferencePipelineFactory12(GpuDevice12 gpu, GpuRootSignature12 rootSignature)
    {
        _gpu = gpu;
        _rootSignature = rootSignature;

        _blendedVsBytecode = CompileEmbeddedShader("reference.vert.hlsl", "main", "vs_5_1");
        var instancedVsBytecode = CompileEmbeddedShader("reference_instanced.vert.hlsl", "main", "vs_5_1");
        _psBytecode = CompileEmbeddedShader("reference.frag.hlsl", "main", "ps_5_1");
        OpaqueBackPso = CreatePipelineState(instancedVsBytecode, _psBytecode, doubleSided: false, blendAttachment: null,
            depthWriteEnabled: true);
        OpaqueDoublePso = CreatePipelineState(instancedVsBytecode, _psBytecode, doubleSided: true, blendAttachment: null,
            depthWriteEnabled: true);
    }

    /// <summary>Instanced opaque PSO with back-face culling (single-sided submeshes).</summary>
    public ID3D12PipelineState OpaqueBackPso { get; }

    /// <summary>Instanced opaque PSO with no culling (double-sided submeshes).</summary>
    public ID3D12PipelineState OpaqueDoublePso { get; }

    public ID3D12PipelineState GetBlendPipeline(byte srcBlendMode, byte dstBlendMode, bool doubleSided)
    {
        var key = new BlendPipelineKey(srcBlendMode, dstBlendMode, doubleSided);
        if (_blendPsos.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var rtBlend = new D12.RenderTargetBlendDescription
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

        var pso = CreatePipelineState(_blendedVsBytecode, _psBytecode, doubleSided, rtBlend, depthWriteEnabled: false);
        _blendPsos[key] = pso;
        return pso;
    }

    private ID3D12PipelineState CreatePipelineState(
        byte[] vsBytecode,
        byte[] psBytecode,
        bool doubleSided,
        D12.RenderTargetBlendDescription? blendAttachment,
        bool depthWriteEnabled)
    {
        var rasterizer = new D12.RasterizerDescription
        {
            FillMode = D12.FillMode.Solid,
            CullMode = doubleSided ? D12.CullMode.None : D12.CullMode.Back,
            FrontCounterClockwise = true,
            DepthClipEnable = true,
            // Antialias triangle edges on the multisampled scene RT (no-op when scene isn't MSAA).
            MultisampleEnable = _gpu.SceneSampleCount > 1,
        };

        var depth = new D12.DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = depthWriteEnabled ? D12.DepthWriteMask.All : D12.DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.GreaterEqual, // reversed-Z (near→1, far→0); depth clear = 0
            StencilEnable = false,
        };

        var blend = new D12.BlendDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false,
        };
        blend.RenderTarget[0] = blendAttachment ?? new D12.RenderTargetBlendDescription
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

        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = _rootSignature.RootSignature,
            VertexShader = vsBytecode,
            PixelShader = psBytecode,
            BlendState = blend,
            RasterizerState = rasterizer,
            DepthStencilState = depth,
            InputLayout = new InputLayoutDescription(GpuMeshBufferFactory12.InputElements),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { Format.B8G8R8A8_UNorm },
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription((uint)_gpu.SceneSampleCount, 0),
            SampleMask = uint.MaxValue,
        };
        return _gpu.Device.CreateGraphicsPipelineState(psoDesc);
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

        var shaderFlags = source.Contains("textures[]", StringComparison.Ordinal)
            ? EnableUnboundedDescriptorTables
            : ShaderFlags.None;

        var result = Compiler.Compile(
            source,
            Array.Empty<ShaderMacro>(),
            include: null!,
            entryPoint,
            sourceName: name,
            profile,
            shaderFlags,
            EffectFlags.None,
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var pso in _blendPsos.Values)
        {
            pso.Dispose();
        }
        _blendPsos.Clear();
        OpaqueDoublePso.Dispose();
        OpaqueBackPso.Dispose();
    }

    private readonly record struct BlendPipelineKey(byte SrcBlendMode, byte DstBlendMode, bool DoubleSided);
}
#endif
