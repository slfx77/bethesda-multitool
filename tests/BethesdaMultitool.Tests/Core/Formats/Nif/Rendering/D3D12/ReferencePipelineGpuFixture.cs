using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Abstractions;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Games;
using Slfx77.Multitool.Core.Lifetime;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

/// <summary>Exercises production reference recipes and route owners using bounded WARP raster resources.</summary>
/// <remarks>The constant-color shader is a fixed-function oracle, not a game-shader fidelity claim.</remarks>
internal sealed class ReferencePipelineGpuFixture : IDisposable
{
    internal const int Size = 32;
    private const string OracleSource = """
        cbuffer Oracle : register(b0) { float4 tint; };
        struct VertexInput
        {
            float3 position : TEXCOORD0;
            float3 normal : TEXCOORD1;
            float2 uv : TEXCOORD2;
            float4 color : TEXCOORD3;
            float3 tangent : TEXCOORD4;
            float3 bitangent : TEXCOORD5;
        };
        float4 VertexMain(VertexInput input) : SV_Position { return float4(input.position, 1); }
        float4 PixelMain() : SV_Target { return tint; }
        float4 AlternatePixelMain() : SV_Target { return tint.zyxw; }
        """;

    private static readonly ConcurrentBag<ReferencePipelineGpuFixture> Unretired = new();
    private readonly RetiredResourceDisposal _resources = new();
    private readonly GpuDevice12 _gpu;
    private readonly GpuCommandRecorder12 _recorder;
    private readonly GpuRootSignature12 _root;
    private readonly GpuOffscreenSceneTarget12 _target;
    private readonly ID3D12DescriptorHeap _depthViews;
    private readonly ID3D12Resource _vertices;
    private readonly ID3D12Resource[] _colors = new ID3D12Resource[3];
    private readonly ReadOnlyMemory<byte> _vertexShader;
    private readonly ReadOnlyMemory<byte> _pixelShader;
    private bool _retired;

    /// <summary>Creates the real world root, separate route caches and one explicitly sampled scene target.</summary>
    /// <param name="sampleCount">One or four scene samples; ordinary dynamic-recipe tests retain four.</param>
    internal ReferencePipelineGpuFixture(int sampleCount = 4)
    {
        try
        {
            _gpu = Own(CreateSampledDevice(sampleCount), 5);
            _recorder = Own(new GpuCommandRecorder12(_gpu), 0);
            _root = Own(GpuRootSignature12.Create(_gpu), 4);
            _vertexShader = GpuShaderCompiler12.CompileSource(
                OracleSource, "reference-pipeline-oracle.hlsl", "VertexMain", "vs_5_1");
            _pixelShader = GpuShaderCompiler12.CompileSource(
                OracleSource, "reference-pipeline-oracle.hlsl", "PixelMain", "ps_5_1");
            var alternatePixelShader = GpuShaderCompiler12.CompileSource(
                OracleSource, "reference-pipeline-oracle.hlsl", "AlternatePixelMain", "ps_5_1");
            Primary = Own(new ReferenceShaderRoute12(_root, _vertexShader, _pixelShader), 3);
            Alternate = Own(new ReferenceShaderRoute12(_root, _vertexShader, alternatePixelShader), 3);
            Production = Own(new ReferenceShaderRoute12(_root), 3);
            _target = Own(new GpuOffscreenSceneTarget12(_gpu, Size, Size));
            _target.TonemapSettings = GpuTonemapSettings.GammaAcesDefaults with { Mode = GpuTonemapMode.LegacyClamp };
            _vertices = Upload<GpuMeshUploader.GpuVertex>(CreateVertices());
            _colors[0] = Upload<Vector4>([new Vector4(0.75f, 0.5f, 0.25f, 0.5f)], 256);
            _colors[1] = Upload<Vector4>([new Vector4(1, 0, 0, 0.5f)], 256);
            _colors[2] = Upload<Vector4>([new Vector4(0, 1, 0, 0.25f)], 256);
            _depthViews = Own(_gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(
                new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1)));
            _gpu.Device.CreateDepthStencilView(_target.DepthResource, new DepthStencilViewDescription
            {
                Format = Format.D32_Float,
                ViewDimension = _target.SampleCount > 1
                    ? DepthStencilViewDimension.Texture2DMultisampled
                    : DepthStencilViewDimension.Texture2D
            }, _depthViews.GetCPUDescriptorHandleForHeapStart());
            VerifyDevice("reference fixture initialization");
        }
        catch (Exception failure)
        {
            DisposeAfterFailure(failure);
            throw;
        }
    }

    /// <summary>Gets the replaceable synthetic route with ordinary color output.</summary>
    internal ReferenceShaderRoute12 Primary { get; private set; }

    /// <summary>Gets the independent synthetic route whose pixel shader swaps red and blue.</summary>
    internal ReferenceShaderRoute12 Alternate { get; }

    /// <summary>Gets a profile-controlled route for the actual embedded reference shader pair.</summary>
    internal ReferenceShaderRoute12 Production { get; }

    /// <summary>Gets the shared world signature borrowed by every independently owned route.</summary>
    internal ID3D12RootSignature Root => _root.RootSignature;

    /// <summary>Gets the actual scene sample count admitted by WARP.</summary>
    internal int SampleCount => _target.SampleCount;

    /// <summary>Gets callback executions so cache hits can be distinguished from native re-creation.</summary>
    internal int PipelineCreations { get; private set; }

    /// <summary>Retains an actual full reference factory under a bounded, restored game-specialization override.</summary>
    /// <param name="game">Game whose ordinary optional families are requested.</param>
    /// <param name="shaderOverride">Exact modern-specialization override, or null for the normal game default.</param>
    /// <returns>The fixture-retained factory over the same live world root.</returns>
    internal ReferencePipelineFactory12 CreateFactory(BethesdaGame game, string? shaderOverride = null)
    {
        var previous = Environment.GetEnvironmentVariable(EnvironmentVariables.Viewer.ReferenceModernStandardShader);
        try
        {
            Environment.SetEnvironmentVariable(EnvironmentVariables.Viewer.ReferenceModernStandardShader, shaderOverride);
            return Own(new ReferencePipelineFactory12(_gpu, _root, game), 3);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvironmentVariables.Viewer.ReferenceModernStandardShader, previous);
        }
    }

    /// <summary>Establishes an ordinary fence before a caller changes a factory profile or releases its pipelines.</summary>
    internal void RetireGpu() => _recorder.WaitForGpuIdle();

    /// <summary>Fences and releases one retained factory while leaving sibling routes and the world root usable.</summary>
    /// <param name="factory">Fixture-retained factory whose submitted uses are complete.</param>
    internal void DisposeFactory(ReferencePipelineFactory12 factory)
    {
        RetireGpu();
        factory.Dispose();
    }

    /// <summary>Runs the actual instanced shadow vertex shader into a single-sample depth-only target.</summary>
    /// <param name="factory">The full production factory supplying its opaque shadow pipeline.</param>
    /// <param name="reversedWinding">Reverses the mesh winding to verify the shadow pass remains double-sided.</param>
    /// <param name="clearDepth">Existing reversed-Z occluder depth.</param>
    /// <returns>The actual D32 depth values after copy and fence completion.</returns>
    internal float[] CaptureOpaqueShadow(ReferencePipelineFactory12 factory, bool reversedWinding = false,
        float clearDepth = 0)
    {
        var depth = Own(_gpu.Device.CreateCommittedResource<ID3D12Resource>(HeapProperties.DefaultHeapProperties,
            HeapFlags.None, ResourceDescription.Texture2D(Format.R32_Typeless, Size, Size, 1, 1, 1, 0,
                ResourceFlags.AllowDepthStencil), ResourceStates.DepthWrite,
            new ClearValue(Format.D32_Float, 0, 0)));
        var views = Own(_gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(
            new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1)));
        var dsv = views.GetCPUDescriptorHandleForHeapStart();
        _gpu.Device.CreateDepthStencilView(depth, new DepthStencilViewDescription
        {
            Format = Format.D32_Float, ViewDimension = DepthStencilViewDimension.Texture2D
        }, dsv);
        var footprints = new PlacedSubresourceFootPrint[1];
        _gpu.Device.GetCopyableFootprints(depth.Description, 0, 1, 0, footprints, new uint[1], new ulong[1], out var byteCount);
        var readback = Own(_gpu.Device.CreateCommittedResource<ID3D12Resource>(HeapProperties.ReadbackHeapProperties,
            HeapFlags.None, ResourceDescription.Buffer(byteCount), ResourceStates.CopyDest));
        // The shadow b0 layout is 352 bytes: view-projection at 0, wind matrices at 64,
        // and the unused card basis at 320. Padding is zeroed; b1 leaves WindMatrixValid
        // and the leaf/material flags clear, so those optional deformation inputs stay inert.
        var frame = Upload<Matrix4x4>([Matrix4x4.Identity], 512);
        // t8 contains one 64-byte world matrix. The zeroed b1 tail keeps InstanceBase at 0;
        // its first float4 is AlphaState, and the complete InstanceDraw layout fits 256 bytes.
        var instance = Upload<Matrix4x4>([Matrix4x4.Identity]);
        var draw = Upload<Vector4>([new Vector4(0, -1, 1, 0)], 512);
        var atmosphere = Upload<byte>(new byte[1024]);

        _recorder.BeginFrame();
        var commands = _recorder.CommandList;
        commands.OMSetRenderTargets(Array.Empty<CpuDescriptorHandle>(), dsv);
        commands.ClearDepthStencilView(dsv, ClearFlags.Depth, clearDepth, 0);
        commands.RSSetViewport(new Viewport(0, 0, Size, Size, 0, 1));
        commands.RSSetScissorRect(Size, Size);
        commands.SetGraphicsRootSignature(_root.RootSignature);
        commands.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, frame.GPUVirtualAddress);
        commands.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerDrawCbv, draw.GPUVirtualAddress);
        commands.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.AtmosphereCbv, atmosphere.GPUVirtualAddress);
        commands.SetGraphicsRootShaderResourceView(GpuRootSignature12.Slots.ReferenceInstanceSrv, instance.GPUVirtualAddress);
        commands.SetPipelineState(factory.ShadowOpaquePso);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.IASetVertexBuffers(0, GpuMeshBufferFactory12.VertexBufferViewOf(
            _vertices, checked((uint)GpuMeshUploader.GpuVertexSize)));
        commands.DrawInstanced(6, 1, reversedWinding ? 6u : 0u, 0);
        commands.ResourceBarrierTransition(depth, ResourceStates.DepthWrite, ResourceStates.CopySource);
        commands.CopyTextureRegion(new TextureCopyLocation(readback, footprints[0]), 0, 0, 0,
            new TextureCopyLocation(depth));
        commands.ResourceBarrierTransition(depth, ResourceStates.CopySource, ResourceStates.DepthWrite);
        _recorder.EndFrame();
        RetireGpu();
        VerifyDevice("completed reference shadow frame");
        var result = new float[Size * Size];
        var mapped = readback.Map<byte>(0, checked((int)byteCount));
        try
        {
            for (var y = 0; y < Size; y++)
            {
                for (var x = 0; x < Size; x++)
                {
                    var offset = checked((int)footprints[0].Offset + y * (int)footprints[0].Footprint.RowPitch + x * sizeof(float));
                    result[y * Size + x] = MemoryMarshal.Read<float>(mapped[offset..]);
                }
            }
        }
        finally { readback.Unmap(0, new Vortice.Direct3D12.Range(0, 0)); }
        return result;
    }

    /// <summary>Creates or retrieves an authored blend key through the production route and recipe.</summary>
    /// <param name="route">Independent shader route whose cache owns the result.</param>
    /// <param name="key">Unmodified authored blend bytes and raster flags.</param>
    /// <param name="depthWrite">Selects the route's separate depth-writing cache.</param>
    /// <returns>The route-owned native pipeline.</returns>
    internal ID3D12PipelineState GetPipeline(
        ReferenceShaderRoute12 route, ReferenceBlendPipelineKey key, bool depthWrite = false)
    {
        return route.GetOrCreate(key, depthWrite, (Fixture: this, Key: key, DepthWrite: depthWrite),
            static (request, vertex, pixel, owner) =>
            {
                var state = new ReferencePipelineRenderState12(
                    request.Key.DoubleSided,
                    ReferencePipelineRecipe12.CreateBlendAttachment(request.Key.SrcBlendMode, request.Key.DstBlendMode),
                    request.DepthWrite, request.Key.Decal, DepthTestEnabled: !request.Key.DepthTestOff);
                var description = ReferencePipelineRecipe12.CreateGraphicsDescription(
                    owner.RootSignature, request.Fixture.SampleCount, vertex, pixel, state);
                var pipeline = owner.CreateGraphics(0, description);
                request.Fixture.PipelineCreations++;
                return pipeline;
            });
    }

    /// <summary>Draws one constant tint through the caller's production recipe and returns fenced display bytes.</summary>
    /// <param name="pipeline">Retained native pipeline to bind.</param>
    /// <param name="geometry">Zero for ordinary winding, one for its reverse, two for nearer depth, three for a decal just behind the clear depth.</param>
    /// <param name="clearDepth">Opaque reversed-Z depth already present in the scene.</param>
    /// <param name="clearAlpha">Destination alpha used to distinguish maximum-alpha accumulation.</param>
    /// <returns>Tightly packed BGRA output after queue completion.</returns>
    internal byte[] Capture(ID3D12PipelineState pipeline, int geometry = 0, float clearDepth = 0.5f,
        float clearAlpha = 0.25f)
    {
        BeginRaster(clearDepth, clearAlpha);
        Draw(pipeline, geometry, 0);
        return FinishRaster();
    }

    /// <summary>Draws nearer red followed by farther green to expose whether blending writes reversed depth.</summary>
    /// <param name="pipeline">The retained One/Zero color-blend pipeline under test.</param>
    /// <returns>Actual pixels: green without depth writes, red when the first layer writes depth.</returns>
    internal byte[] CaptureLayers(ID3D12PipelineState pipeline)
    {
        BeginRaster(0.25f, 0.25f);
        Draw(pipeline, 2, 1);
        Draw(pipeline, 0, 2);
        return FinishRaster();
    }

    /// <summary>Retires submitted work, releases only the ordinary route and recreates it against the retained root.</summary>
    internal void RecreatePrimary()
    {
        _recorder.WaitForGpuIdle();
        Primary.Dispose();
        Primary = Own(new ReferenceShaderRoute12(_root, _vertexShader, _pixelShader), 3);
    }

    /// <summary>Changes the real embedded shader profile only after every submitted use has retired.</summary>
    /// <param name="enabled">Whether to install the actual reference pair or select the disabled fallback.</param>
    internal void SetProductionProfile(bool enabled)
    {
        _recorder.WaitForGpuIdle();
        Production.Set(enabled
            ? new GameShaderPair(true, "reference.vert.hlsl", "reference.frag.hlsl")
            : default, "ReferencePipelineGpuFixture");
    }

    /// <summary>Requests the specified WARP sample count independently of the caller's interactive viewport override.</summary>
    /// <param name="sampleCount">One or four supported scene samples.</param>
    /// <returns>The retained software device, or throws if native creation fails.</returns>
    private static GpuDevice12 CreateSampledDevice(int sampleCount)
    {
        if (sampleCount is not (1 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        }
        return GpuDevice12.Create(
            adapterPolicy: GpuAdapterPolicy.WarpOnly,
            requestedSceneSampleCount: sampleCount)
            ?? throw new InvalidOperationException("WARP did not create the reference pipeline test device.");
    }

    /// <summary>Encodes four quads in the production sixty-byte input layout without changing shader descriptions.</summary>
    /// <returns>Ordinary, reverse-wound, nearer and barely occluded six-vertex quads.</returns>
    private static GpuMeshUploader.GpuVertex[] CreateVertices()
    {
        Vector2[] corners = [new(-0.5f, -0.5f), new(0.5f, -0.5f), new(-0.5f, 0.5f),
            new(-0.5f, 0.5f), new(0.5f, -0.5f), new(0.5f, 0.5f)];
        var result = new GpuMeshUploader.GpuVertex[24];
        for (var quad = 0; quad < 4; quad++)
        {
            for (var vertex = 0; vertex < 6; vertex++)
            {
                var cornerIndex = quad == 1 ? vertex / 3 * 3 + 2 - vertex % 3 : vertex;
                var corner = corners[cornerIndex];
                var depth = quad switch
                {
                    2 => 0.75f,
                    3 => 0.5f - 8 * (0.5f - MathF.BitDecrement(0.5f)),
                    _ => 0.5f
                };
                result[quad * 6 + vertex] = new GpuMeshUploader.GpuVertex
                {
                    Position = new Vector3(corner, depth), Normal = Vector3.UnitZ,
                    VertexColorRgba = uint.MaxValue, Tangent = Vector3.UnitX, Bitangent = Vector3.UnitY
                };
            }
        }
        return result;
    }

    /// <summary>Begins one frame against the production scene target and world root.</summary>
    /// <param name="depth">Opaque reversed depth used by the independent oracle.</param>
    /// <param name="alpha">Destination alpha used by the independent oracle.</param>
    private void BeginRaster(float depth, float alpha)
    {
        _recorder.BeginFrame();
        var commands = _recorder.CommandList;
        _target.Bind(commands, new Color4(0.125f, 0.25f, 0.5f, alpha));
        commands.ClearDepthStencilView(_depthViews.GetCPUDescriptorHandleForHeapStart(), ClearFlags.Depth, depth, 0);
        commands.SetGraphicsRootSignature(_root.RootSignature);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        commands.IASetVertexBuffers(0, GpuMeshBufferFactory12.VertexBufferViewOf(
            _vertices, checked((uint)GpuMeshUploader.GpuVertexSize)));
    }

    /// <summary>Binds one immutable tint and six-vertex geometry packet.</summary>
    /// <param name="pipeline">Route-owned pipeline.</param>
    /// <param name="geometry">Quad index in the immutable production-layout buffer.</param>
    /// <param name="color">Tint index in the retained upload resources.</param>
    private void Draw(ID3D12PipelineState pipeline, int geometry, int color)
    {
        var commands = _recorder.CommandList;
        commands.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, _colors[color].GPUVirtualAddress);
        commands.SetPipelineState(pipeline);
        commands.DrawInstanced(6, 1, checked((uint)(geometry * 6)), 0);
    }

    /// <summary>Resolves, reads back and fences the actual scene output before any resource can be reused.</summary>
    /// <returns>The retired BGRA display readback.</returns>
    private byte[] FinishRaster()
    {
        _target.RecordReadback(_recorder);
        _recorder.EndFrame();
        _recorder.WaitForGpuIdle();
        VerifyDevice("completed reference pipeline frame");
        return _target.ReadbackToBytes();
    }

    /// <summary>Retains upload memory before mapping and initializes any constant-buffer padding.</summary>
    /// <typeparam name="T">Unmanaged payload element.</typeparam>
    /// <param name="data">Payload copied into the owned allocation.</param>
    /// <param name="minimumBytes">Optional allocation padding.</param>
    /// <returns>The retained upload resource.</returns>
    private ID3D12Resource Upload<T>(ReadOnlySpan<T> data, int minimumBytes = 0) where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(data);
        var size = Math.Max(bytes.Length, minimumBytes);
        var resource = Own(_gpu.Device.CreateCommittedResource<ID3D12Resource>(HeapProperties.UploadHeapProperties,
            HeapFlags.None, ResourceDescription.Buffer(checked((ulong)size)), ResourceStates.GenericRead));
        var mapped = resource.Map<byte>(0, size);
        try { mapped.Clear(); bytes.CopyTo(mapped); }
        finally { resource.Unmap(0); }
        return resource;
    }

    /// <summary>Fails at the responsible operation when the native device has been removed.</summary>
    /// <param name="operation">Completed setup or recording operation.</param>
    private void VerifyDevice(string operation)
    {
        var reason = _gpu.Device.DeviceRemovedReason;
        if (reason.Success)
        {
            return;
        }
        _gpu.PumpDebugMessages();
        _gpu.LogDeviceRemovedDiagnostics(operation);
        throw new InvalidOperationException($"Reference fixture device removed after {operation}: {reason}.");
    }

    /// <summary>Retains each owner before subsequent initialization can fail.</summary>
    /// <typeparam name="T">Disposable owner type.</typeparam>
    /// <param name="resource">New owner to retain.</param>
    /// <param name="stage">Ordered release stage after GPU retirement.</param>
    /// <returns>The registered resource.</returns>
    private T Own<T>(T resource, int stage = 1) where T : IDisposable
    {
        _resources.Add(resource, "reference pipeline fixture", stage);
        return resource;
    }

    /// <summary>Preserves the original failure if native retirement or cleanup fails as well.</summary>
    /// <param name="failure">The original initialization or assertion failure.</param>
    internal void DisposeAfterFailure(Exception failure)
    {
        try { Dispose(); }
        catch (Exception cleanupFailure)
        {
            throw new AggregateException("Reference fixture failed and cleanup could not complete.", failure, cleanupFailure);
        }
    }

    /// <summary>Releases resources only after a completed queue fence or confirmed terminal device retirement.</summary>
    public void Dispose()
    {
        if (!_retired)
        {
            try
            {
                _recorder?.AbortFrame();
                _recorder?.WaitForGpuIdle();
            }
            catch
            {
                bool removed;
                try { removed = _gpu is null || _gpu.TryForceDeviceRemoval("reference-pipeline-fixture"); }
                catch
                {
                    Unretired.Add(this);
                    throw;
                }
                if (!removed)
                {
                    Unretired.Add(this);
                    throw;
                }
            }
            _retired = true;
        }
        _resources.Dispose();
    }
}
