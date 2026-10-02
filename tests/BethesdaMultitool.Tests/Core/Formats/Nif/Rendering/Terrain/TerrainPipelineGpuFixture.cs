using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using Slfx77.Multitool.Core.Lifetime;
using Slfx77.Multitool.WinUI.Direct3D12.Shaders;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using ShaderResourceViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Terrain;

/// <summary>Retains a small production terrain pipeline family and its real raster/readback dependencies.</summary>
internal sealed class TerrainPipelineGpuFixture : IDisposable
{
    internal const int Size = 32;
    private static readonly ConcurrentBag<TerrainPipelineGpuFixture> Unretired = new();
    private readonly RetiredResourceDisposal _resources = new();
    private readonly GpuDevice12 _gpu = null!;
    private readonly GpuCommandRecorder12 _recorder = null!;
    private readonly GpuRootSignature12 _root = null!;
    private readonly GpuOffscreenSceneTarget12 _target = null!;
    private readonly ID3D12Resource _frame = null!;
    private readonly ID3D12Resource _cell = null!;
    private readonly ID3D12Resource _mode = null!;
    private readonly ID3D12Resource _atmosphere = null!;
    private readonly ID3D12Resource _geometry = null!;
    private readonly ID3D12Resource _nearGeometry = null!;
    private readonly ID3D12Resource _indices = null!;
    private readonly ID3D12Resource _reversedIndices = null!;
    private readonly ID3D12Resource _weights = null!;
    private readonly ID3D12DescriptorHeap _nullTextures = null!;
    private readonly ID3D12DescriptorHeap _depthViews = null!;
    private readonly ID3D12Resource _shadow = null!;
    private readonly CpuDescriptorHandle _sceneDsv;
    private readonly CpuDescriptorHandle _shadowDsv;
    private bool _retired;

    /// <summary>Creates the established world root, existing recorder/target, and bounded synthetic streams on WARP.</summary>
    internal TerrainPipelineGpuFixture()
    {
        try
        {
            _gpu = Own(GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly)
                ?? throw new InvalidOperationException("WARP did not create the terrain test device."), 5);
            _recorder = Own(new GpuCommandRecorder12(_gpu), 0);
            _root = Own(GpuRootSignature12.Create(_gpu), 4);
            Pipelines = Own(_root.CreatePipelineResources(10), 3);
            _target = Own(new GpuOffscreenSceneTarget12(_gpu, Size, Size), 1);
            _target.TonemapSettings = GpuTonemapSettings.GammaAcesDefaults with { Mode = GpuTonemapMode.LegacyClamp };
            _frame = Upload<Matrix4x4>([Matrix4x4.Identity], 256);
            _cell = Upload<byte>(new byte[256]);
            _mode = Upload<Vector4>([new Vector4(0, 1, 1, 0)], 256);
            var atmosphere = new Vector4[AtmosphereConstantBufferLayout.Float4Count];
            atmosphere[AtmosphereConstantBufferLayout.ClipPlaneFloat4Slot] = new Vector4(0, 0, 0, 1);
            _atmosphere = Upload<Vector4>(atmosphere, 1024);
            _geometry = Upload<TerrainVertex>(Vertices(0.5f));
            _nearGeometry = Upload<TerrainVertex>(Vertices(0.75f));
            // Match the production grid's +Z-facing triangles and retain their reversed-winding twin.
            _indices = Upload<ushort>([0, 1, 2, 2, 1, 3]);
            _reversedIndices = Upload<ushort>([0, 2, 1, 2, 3, 1]);
            _weights = Upload<ushort>(new ushort[4 * 4 * TerrainVertexLayout.MaxBlendQuads]);
            _nullTextures = Own(_gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(
                new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                    8, DescriptorHeapFlags.ShaderVisible)));
            var increment = _gpu.Device.GetDescriptorHandleIncrementSize(
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            for (var index = 0; index < 8; index++)
            {
                _gpu.Device.CreateShaderResourceView(null, new ShaderResourceViewDescription
                {
                    Format = Format.R8G8B8A8_UNorm,
                    ViewDimension = ShaderResourceViewDimension.Texture2D,
                    Shader4ComponentMapping = ShaderComponentMapping.Default,
                    Texture2D = new Texture2DShaderResourceView { MipLevels = 1 }
                }, new CpuDescriptorHandle(_nullTextures.GetCPUDescriptorHandleForHeapStart(), index, increment));
            }
            VerifyDevice("null texture descriptors");
            _shadow = Own(_gpu.Device.CreateCommittedResource<ID3D12Resource>(HeapProperties.DefaultHeapProperties,
                HeapFlags.None, ResourceDescription.Texture2D(Format.D32_Float, Size, Size, 1, 1, 1, 0,
                    ResourceFlags.AllowDepthStencil), ResourceStates.DepthWrite,
                new ClearValue(Format.D32_Float, new DepthStencilValue(0f))));
            _depthViews = Own(_gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(
                new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 2)));
            _sceneDsv = _depthViews.GetCPUDescriptorHandleForHeapStart();
            _shadowDsv = new CpuDescriptorHandle(_sceneDsv, 1,
                _gpu.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.DepthStencilView));
            _gpu.Device.CreateDepthStencilView(_target.DepthResource, new DepthStencilViewDescription
            {
                Format = Format.D32_Float,
                ViewDimension = _target.SampleCount > 1
                    ? DepthStencilViewDimension.Texture2DMultisampled
                    : DepthStencilViewDimension.Texture2D
            }, _sceneDsv);
            _gpu.Device.CreateDepthStencilView(_shadow, null, _shadowDsv);
            VerifyDevice("typed depth descriptors");
        }
        catch (Exception constructionFailure)
        {
            DisposeAfterFailure(constructionFailure);
            throw;
        }
    }

    /// <summary>Gets the borrowed family whose slots are filled only by the production terrain factory.</summary>
    internal ShaderPipelineResources Pipelines { get; private set; } = null!;

    /// <summary>Builds or retrieves one production color/mirror pair with its actual width-specific vertex layout.</summary>
    /// <param name="width">Terrain blend-quad width from one through four.</param>
    /// <returns>The two borrowed production handles.</returns>
    internal (ID3D12PipelineState Pso, ID3D12PipelineState MirrorPso) ColorPipelines(int width) =>
        TerrainPipelineFactory12.BuildColorPipelineStates(_gpu, Pipelines, width,
            TerrainPipelineFactory12.CompileVertexShader(width), TerrainPipelineFactory12.CompilePixelShader(width),
            TerrainVertexLayout.ElementsFor(width));

    /// <summary>Creates the zero-weight depth or single-sample shadow pipeline through its production factory.</summary>
    /// <param name="shadow">True for the shadow family slot; false for scene-depth prepass.</param>
    /// <returns>The family-owned native pipeline.</returns>
    internal ID3D12PipelineState DepthPipeline(bool shadow) => shadow
        ? TerrainPipelineFactory12.BuildShadowPipelineState(Pipelines,
            TerrainPipelineFactory12.CompileVertexShader(0), TerrainVertexLayout.ElementsFor(0))
        : TerrainPipelineFactory12.BuildDepthOnlyPipelineState(_gpu, Pipelines,
            TerrainPipelineFactory12.CompileVertexShader(0), TerrainVertexLayout.ElementsFor(0));

    /// <summary>Releases a proven-idle family and creates another family against the same live world root.</summary>
    internal void RecreateFamily()
    {
        _recorder.WaitForGpuIdle();
        Pipelines.Dispose();
        Pipelines = Own(_root.CreatePipelineResources(10), 3);
    }

    /// <summary>Captures one real color draw, optionally preceded by a nearer depth-only occluder.</summary>
    /// <param name="pipeline">Borrowed production color or mirror pipeline.</param>
    /// <param name="width">Weight-stream stride paired with this pipeline.</param>
    /// <param name="mirrored">Whether projection reverses the horizontal winding.</param>
    /// <param name="depthPrepass">Optional production zero-weight pipeline writing nearer depth.</param>
    /// <returns>Fenced 32-by-32 BGRA pixels from the existing offscreen target.</returns>
    internal byte[] Capture(ID3D12PipelineState pipeline, int width, bool mirrored,
        ID3D12PipelineState? depthPrepass = null)
    {
        WriteFrame(mirrored);
        _recorder.BeginFrame();
        var commands = _recorder.CommandList;
        _target.Bind(commands, new Color4(0, 0, 0, 1));
        BindInputs(width, false);
        if (depthPrepass is not null)
        {
            commands.OMSetRenderTargets(Array.Empty<CpuDescriptorHandle>(), _sceneDsv);
            commands.SetPipelineState(depthPrepass);
            BindGeometry(_nearGeometry);
            commands.DrawIndexedInstanced(6, 1, 0, 0, 0);
            _target.Rebind(commands);
            BindGeometry(_geometry);
        }
        commands.SetPipelineState(pipeline);
        commands.DrawIndexedInstanced(6, 1, 0, 0, 0);
        _target.RecordReadback(_recorder);
        CompleteFrame();
        return _target.ReadbackToBytes();
    }

    /// <summary>Draws reverse-wound geometry into the single-sample shadow target and reads actual D32 texels.</summary>
    /// <param name="pipeline">Production cull-none shadow pipeline.</param>
    /// <returns>The center depth and untouched upper-left depth after fenced completion.</returns>
    internal (float Center, float Outside) CaptureShadow(ID3D12PipelineState pipeline)
    {
        WriteFrame(false);
        var footprints = new PlacedSubresourceFootPrint[1];
        _gpu.Device.GetCopyableFootprints(_shadow.Description, 0, 1, 0, footprints,
            new uint[1], new ulong[1], out var bytes);
        var readback = Own(_gpu.Device.CreateCommittedResource<ID3D12Resource>(HeapProperties.ReadbackHeapProperties,
            HeapFlags.None, ResourceDescription.Buffer(bytes), ResourceStates.CopyDest));
        _recorder.BeginFrame();
        var commands = _recorder.CommandList;
        commands.OMSetRenderTargets(Array.Empty<CpuDescriptorHandle>(), _shadowDsv);
        commands.ClearDepthStencilView(_shadowDsv, ClearFlags.Depth, 0f, 0);
        commands.RSSetViewport(new Viewport(0, 0, Size, Size));
        commands.RSSetScissorRect(Size, Size);
        BindInputs(0, true);
        BindGeometry(_nearGeometry);
        commands.SetPipelineState(pipeline);
        commands.DrawIndexedInstanced(6, 1, 0, 0, 0);
        commands.OMSetRenderTargets(Array.Empty<CpuDescriptorHandle>());
        commands.ResourceBarrierTransition(_shadow, ResourceStates.DepthWrite, ResourceStates.CopySource);
        commands.CopyTextureRegion(new TextureCopyLocation(readback, footprints[0]), 0, 0, 0,
            new TextureCopyLocation(_shadow));
        commands.ResourceBarrierTransition(_shadow, ResourceStates.CopySource, ResourceStates.DepthWrite);
        CompleteFrame();
        var mapped = readback.Map<byte>(0, checked((int)bytes));
        try
        {
            var center = checked((int)footprints[0].Offset + Size / 2 * (int)footprints[0].Footprint.RowPitch + Size / 2 * 4);
            return (MemoryMarshal.Read<float>(mapped[center..]),
                MemoryMarshal.Read<float>(mapped[checked((int)footprints[0].Offset)..]));
        }
        finally { readback.Unmap(0, new Vortice.Direct3D12.Range(0, 0)); }
    }

    /// <summary>Binds valid descriptors and constant buffers while disabling texture, fog and dynamic-light branches.</summary>
    /// <param name="width">Number of weight quads in the currently selected input layout.</param>
    /// <param name="reverseIndices">Whether to supply the opposite triangle winding.</param>
    private void BindInputs(int width, bool reverseIndices)
    {
        var commands = _recorder.CommandList;
        commands.SetGraphicsRootSignature(_root.RootSignature);
        commands.SetDescriptorHeaps(1, new[] { _nullTextures });
        commands.SetGraphicsRootDescriptorTable(GpuRootSignature12.Slots.SrvTable,
            _nullTextures.GetGPUDescriptorHandleForHeapStart());
        GpuRootSignature12.SetGraphicsBindlessTables(commands, _nullTextures.GetGPUDescriptorHandleForHeapStart());
        commands.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, _frame.GPUVirtualAddress);
        commands.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerDrawCbv, _cell.GPUVirtualAddress);
        commands.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerModeCbv, _mode.GPUVirtualAddress);
        commands.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.AtmosphereCbv, _atmosphere.GPUVirtualAddress);
        commands.SetGraphicsRootShaderResourceView(GpuRootSignature12.Slots.PointLightsSrv, _cell.GPUVirtualAddress);
        commands.SetGraphicsRootShaderResourceView(GpuRootSignature12.Slots.PointLightTilesSrv, _cell.GPUVirtualAddress);
        uint[] grid = [BitConverter.SingleToUInt32Bits(-0.5f), BitConverter.SingleToUInt32Bits(-0.5f),
            BitConverter.SingleToUInt32Bits(1f), 2];
        commands.SetGraphicsRoot32BitConstants(GpuRootSignature12.Slots.TerrainCellGridConstants, grid, 0);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        BindGeometry(_geometry);
        if (width > 0)
        {
            var stride = TerrainVertexLayout.BlendWeightStrideFor(width);
            commands.IASetVertexBuffers(1, new VertexBufferView(_weights.GPUVirtualAddress, stride * 4, stride));
        }
        commands.IASetIndexBuffer(new IndexBufferView((reverseIndices ? _reversedIndices : _indices).GPUVirtualAddress,
            6 * sizeof(ushort), Format.R16_UInt));
    }

    /// <summary>Selects one immutable four-vertex height/normal/color stream.</summary>
    /// <param name="geometry">The normal or nearer quad's retained upload resource.</param>
    private void BindGeometry(ID3D12Resource geometry) => _recorder.CommandList.IASetVertexBuffers(0,
        new VertexBufferView(geometry.GPUVirtualAddress, 4 * TerrainVertex.SizeInBytes, TerrainVertex.SizeInBytes));

    /// <summary>Writes the next projection only after the preceding frame has retired.</summary>
    /// <param name="mirrored">Whether to reverse the horizontal projection axis.</param>
    private void WriteFrame(bool mirrored)
    {
        var mapped = _frame.Map<Matrix4x4>(0, 1);
        try { mapped[0] = mirrored ? Matrix4x4.CreateScale(-1, 1, 1) : Matrix4x4.Identity; }
        finally { _frame.Unmap(0); }
    }

    /// <summary>Submits the current production recorder and proves completion before any readback or buffer reuse.</summary>
    private void CompleteFrame()
    {
        _recorder.EndFrame();
        _recorder.WaitForGpuIdle();
        VerifyDevice("completed terrain draw");
    }

    /// <summary>Attributes a device removal to the last descriptor or draw operation instead of a later allocation.</summary>
    /// <param name="operation">The bounded fixture operation that has just completed.</param>
    private void VerifyDevice(string operation)
    {
        var reason = _gpu.Device.DeviceRemovedReason;
        if (reason.Success) return;
        _gpu.PumpDebugMessages();
        _gpu.LogDeviceRemovedDiagnostics(operation);
        throw new InvalidOperationException($"Terrain fixture device removed after {operation}: {reason}.");
    }

    /// <summary>Preserves the original construction or assertion failure if proven-retirement cleanup also fails.</summary>
    /// <param name="failure">The failure already being propagated by the caller.</param>
    internal void DisposeAfterFailure(Exception failure)
    {
        try { Dispose(); }
        catch (Exception cleanupFailure)
        {
            throw new AggregateException("Terrain fixture failed and cleanup could not complete.", failure, cleanupFailure);
        }
    }

    /// <summary>Allocates and retains an upload resource before mapping and populating its bounded payload.</summary>
    /// <typeparam name="T">Blittable vertex, index or constant element.</typeparam>
    /// <param name="data">Exact input bytes to preserve.</param>
    /// <param name="minimumBytes">Optional padded allocation size for constant buffers.</param>
    /// <returns>A fixture-owned resource whose contents remain immutable during GPU work.</returns>
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

    /// <summary>Builds a uniform green terrain cell with the production packed normal and color representation.</summary>
    /// <param name="height">World/clip depth shared by all four grid vertices.</param>
    /// <returns>The small slot-zero vertex stream.</returns>
    private static TerrainVertex[] Vertices(float height) => Enumerable.Repeat(
        new TerrainVertex(height, Vector3.UnitZ, TerrainVertex.PackColor(0, 255, 0)), 4).ToArray();

    /// <summary>Registers each newly constructed dependency before subsequent fixture work can fail.</summary>
    /// <typeparam name="T">Owned disposable resource type.</typeparam>
    /// <param name="value">Newly created resource.</param>
    /// <param name="stage">Dependency-order release stage after native retirement.</param>
    /// <returns>The same resource for synchronous fixture initialization.</returns>
    private T Own<T>(T value, int stage = 1) where T : IDisposable
    {
        _resources.Add(value, "terrain pipeline fixture", stage);
        return value;
    }

    /// <summary>Discards unfinished recording and proves queue/device retirement before releasing the dependent graph.</summary>
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
                if (_gpu is not null && !_gpu.TryForceDeviceRemoval("terrain-pipeline-fixture"))
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
