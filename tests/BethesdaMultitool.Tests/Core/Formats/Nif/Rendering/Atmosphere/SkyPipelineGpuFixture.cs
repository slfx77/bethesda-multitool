using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Slfx77.Multitool.Core.Lifetime;
using Slfx77.Multitool.WinUI.Direct3D12.Shaders;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using ShaderResourceViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Atmosphere;

/// <summary>Owns a bounded native sky fixture using the production world root, shaders, recorder and target.</summary>
internal sealed class SkyPipelineGpuFixture : IDisposable
{
    internal const int Size = 32;
    private static readonly ConcurrentBag<SkyPipelineGpuFixture> Unretired = new();
    private readonly RetiredResourceDisposal _resources = new();
    private readonly GpuDevice12 _gpu = null!;
    private readonly GpuCommandRecorder12 _recorder = null!;
    private readonly GpuRootSignature12 _root = null!;
    private readonly GpuOffscreenSceneTarget12 _target = null!;
    private readonly ID3D12Resource _constants = null!;
    private readonly ID3D12Resource _vertices = null!;
    private readonly ID3D12Resource _indices = null!;
    private readonly ID3D12DescriptorHeap _textures = null!;
    private readonly ID3D12DescriptorHeap _depthViews = null!;
    private bool _retired;

    /// <summary>Creates two independent production sky pipeline families and uploads one opaque white texel.</summary>
    internal SkyPipelineGpuFixture()
    {
        try
        {
            _gpu = Own(GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly)
                ?? throw new InvalidOperationException("WARP did not create the sky test device."), 5);
            _recorder = Own(new GpuCommandRecorder12(_gpu), 0);
            _root = Own(GpuRootSignature12.Create(_gpu), 4);
            GeometryResources = Own(_root.CreatePipelineResources(3), 3);
            BillboardResources = Own(_root.CreatePipelineResources(2), 3);
            Geometry = SkyPipelineFactory12.CreateGeometryPipelines(_gpu, GeometryResources);
            Billboards = SkyPipelineFactory12.CreateBillboardPipelines(_gpu, BillboardResources);
            _target = Own(new GpuOffscreenSceneTarget12(_gpu, Size, Size));
            _target.TonemapSettings = GpuTonemapSettings.GammaAcesDefaults with { Mode = GpuTonemapMode.LegacyClamp };
            _constants = Upload<byte>(new byte[256]);
            _vertices = Upload<byte>(CreateVertices());
            _indices = Upload<ushort>([0, 1, 2, 2, 1, 3]);
            _textures = Own(_gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription(
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 8,
                DescriptorHeapFlags.ShaderVisible)));
            var texture = Own(_gpu.Device.CreateCommittedResource<ID3D12Resource>(HeapProperties.DefaultHeapProperties,
                HeapFlags.None, ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, 1, 1, 1, 1),
                ResourceStates.CopyDest));
            var footprints = new PlacedSubresourceFootPrint[1];
            _gpu.Device.GetCopyableFootprints(texture.Description, 0, 1, 0, footprints, new uint[1],
                new ulong[1], out var textureBytes);
            var upload = Upload<byte>([255, 255, 255, 255], checked((int)textureBytes));
            var increment = _gpu.Device.GetDescriptorHandleIncrementSize(
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            for (var index = 0; index < 8; index++)
            {
                _gpu.Device.CreateShaderResourceView(texture, new ShaderResourceViewDescription
                {
                    Format = Format.R8G8B8A8_UNorm,
                    ViewDimension = ShaderResourceViewDimension.Texture2D,
                    Shader4ComponentMapping = ShaderComponentMapping.Default,
                    Texture2D = new Texture2DShaderResourceView { MipLevels = 1 }
                }, new CpuDescriptorHandle(_textures.GetCPUDescriptorHandleForHeapStart(), index, increment));
            }
            _depthViews = Own(_gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(
                new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1)));
            _gpu.Device.CreateDepthStencilView(_target.DepthResource, new DepthStencilViewDescription
            {
                Format = Format.D32_Float,
                ViewDimension = _target.SampleCount > 1
                    ? DepthStencilViewDimension.Texture2DMultisampled
                    : DepthStencilViewDimension.Texture2D
            }, _depthViews.GetCPUDescriptorHandleForHeapStart());
            VerifyDevice("sky descriptors");
            _recorder.BeginFrame();
            _recorder.CommandList.CopyTextureRegion(new TextureCopyLocation(texture), 0, 0, 0,
                new TextureCopyLocation(upload, footprints[0]));
            _recorder.CommandList.ResourceBarrierTransition(texture, ResourceStates.CopyDest,
                ResourceStates.PixelShaderResource);
            CompleteFrame();
        }
        catch (Exception failure)
        {
            DisposeAfterFailure(failure);
            throw;
        }
    }

    /// <summary>Gets the replaceable geometry owner; its exact world root is shared with the billboard owner.</summary>
    internal ShaderPipelineResources GeometryResources { get; private set; } = null!;

    /// <summary>Gets the independently retained billboard family.</summary>
    internal ShaderPipelineResources BillboardResources { get; } = null!;

    /// <summary>Gets borrowed production gradient, star and cloud pipeline handles.</summary>
    internal (ID3D12PipelineState Gradient, ID3D12PipelineState Stars, ID3D12PipelineState Clouds) Geometry { get; private set; }

    /// <summary>Gets borrowed production billboard handles, including their distinct destination-alpha rule.</summary>
    internal (ID3D12PipelineState Additive, ID3D12PipelineState Alpha) Billboards { get; }

    /// <summary>Captures real indexed sky geometry with a selected production shader mode.</summary>
    /// <param name="pipeline">The gradient, star or cloud production pipeline.</param>
    /// <param name="mode">Shader mode zero, one or two matching that pipeline.</param>
    /// <param name="beyondFarPlane">Whether projection puts every vertex beyond the far clipping plane.</param>
    /// <returns>Fenced, tightly packed BGRA pixels.</returns>
    internal byte[] CaptureGeometry(ID3D12PipelineState pipeline, int mode, bool beyondFarPlane = false) =>
        Capture(pipeline, false, mode, beyondFarPlane);

    /// <summary>Captures a production four-vertex billboard strip entirely beyond the far clipping plane.</summary>
    /// <param name="pipeline">The additive or alpha production billboard pipeline.</param>
    /// <returns>Fenced pixels that exercise the billboard's disabled depth clipping.</returns>
    internal byte[] CaptureBillboard(ID3D12PipelineState pipeline) => Capture(pipeline, true, 0, true);

    /// <summary>Releases only the idle geometry family and constructs another family against the retained world root.</summary>
    internal void RecreateGeometryFamily()
    {
        _recorder.WaitForGpuIdle();
        GeometryResources.Dispose();
        GeometryResources = Own(_root.CreatePipelineResources(3), 3);
        Geometry = SkyPipelineFactory12.CreateGeometryPipelines(_gpu, GeometryResources);
    }

    /// <summary>Records one actual production draw over a fractional-alpha background with nearer depth already present.</summary>
    /// <param name="pipeline">Borrowed family-owned pipeline.</param>
    /// <param name="billboard">Whether to use the empty-layout billboard strip instead of indexed sky geometry.</param>
    /// <param name="mode">Geometry shader mode.</param>
    /// <param name="beyondFarPlane">Whether to multiply clip-space depth by four.</param>
    /// <returns>The existing offscreen target's actual native readback.</returns>
    private byte[] Capture(ID3D12PipelineState pipeline, bool billboard, int mode, bool beyondFarPlane)
    {
        WriteConstants(billboard, mode, beyondFarPlane);
        _recorder.BeginFrame();
        var commands = _recorder.CommandList;
        _target.Bind(commands, new Color4(0.125f, 0.25f, 0.5f, 0.25f));
        commands.ClearDepthStencilView(_depthViews.GetCPUDescriptorHandleForHeapStart(), ClearFlags.Depth, 1f, 0);
        commands.SetGraphicsRootSignature(_root.RootSignature);
        commands.SetDescriptorHeaps(1, new[] { _textures });
        commands.SetGraphicsRootDescriptorTable(GpuRootSignature12.Slots.SrvTable,
            _textures.GetGPUDescriptorHandleForHeapStart());
        GpuRootSignature12.SetGraphicsBindlessTables(commands, _textures.GetGPUDescriptorHandleForHeapStart());
        commands.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, _constants.GPUVirtualAddress);
        commands.SetPipelineState(pipeline);
        if (billboard)
        {
            commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
            commands.DrawInstanced(4, 1, 0, 0);
        }
        else
        {
            commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            commands.IASetVertexBuffers(0, new VertexBufferView(_vertices.GPUVirtualAddress, 4 * 24, 24));
            commands.IASetIndexBuffer(new IndexBufferView(_indices.GPUVirtualAddress, 6 * sizeof(ushort), Format.R16_UInt));
            commands.DrawIndexedInstanced(6, 1, 0, 0, 0);
        }
        _target.RecordReadback(_recorder);
        CompleteFrame();
        return _target.ReadbackToBytes();
    }

    /// <summary>Writes the production b0 layouts between retired frames, using exact binary-fraction blend inputs.</summary>
    /// <param name="billboard">Whether to populate the billboard layout instead of SkyGeo.</param>
    /// <param name="mode">Geometry mode selecting atmosphere, stars or clouds.</param>
    /// <param name="beyondFarPlane">Whether projection clips ordinary geometry while leaving billboard behavior observable.</param>
    private void WriteConstants(bool billboard, int mode, bool beyondFarPlane)
    {
        var mapped = _constants.Map<Vector4>(0, 16);
        try
        {
            mapped.Clear();
            mapped[0] = new Vector4(1, 0, 0, 0);
            mapped[1] = new Vector4(0, 1, 0, 0);
            mapped[2] = new Vector4(0, 0, beyondFarPlane ? 4 : 1, 0);
            mapped[3] = new Vector4(0, 0, 0, 1);
            if (billboard)
            {
                mapped[4] = new Vector4(0, 0, 1, 0.5f);
                mapped[5] = new Vector4(1, 0, 0, 0.5f);
                mapped[6] = new Vector4(0, 1, 0, 1);
                mapped[7] = new Vector4(0, 0, 0, 1);
                mapped[8] = new Vector4(0.5f, 0.25f, 0, 0.5f);
            }
            else
            {
                mapped[4] = new Vector4(0, 0, 0, 1);
                mapped[5] = new Vector4(0.5f, 0.25f, 0, 0.5f);
                mapped[6] = new Vector4(0, 0, mode, 1);
                mapped[7] = new Vector4(0, 0, 0, 1);
                mapped[8] = mapped[9] = mapped[10] = new Vector4(0.25f, 0.5f, 0.75f, 1);
            }
        }
        finally { _constants.Unmap(0); }
    }

    /// <summary>Encodes the production 24-byte sky vertex layout with authored red-channel blend weights.</summary>
    /// <returns>Four corner vertices with positions, UVs and packed RGBA selectors.</returns>
    private static byte[] CreateVertices()
    {
        var bytes = new byte[4 * 24];
        for (var index = 0; index < 4; index++)
        {
            var position = new Vector3((index & 1) == 0 ? -1 : 1, (index & 2) == 0 ? -1 : 1, 1);
            var uv = new Vector2((index & 1) == 0 ? 0 : 1, (index & 2) == 0 ? 0 : 1);
            uint color = 0xFF0000FF;
            MemoryMarshal.Write(bytes.AsSpan(index * 24), in position);
            MemoryMarshal.Write(bytes.AsSpan(index * 24 + 12), in uv);
            MemoryMarshal.Write(bytes.AsSpan(index * 24 + 20), in color);
        }
        return bytes;
    }

    /// <summary>Owns an upload resource before mapping, preserving partial-construction cleanup.</summary>
    /// <typeparam name="T">Blittable element type.</typeparam>
    /// <param name="data">Exact payload to copy.</param>
    /// <param name="minimumBytes">Optional footprint padding.</param>
    /// <returns>The retained upload allocation.</returns>
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

    /// <summary>Submits and waits for native completion before readback, mapping or family replacement.</summary>
    private void CompleteFrame()
    {
        _recorder.EndFrame();
        _recorder.WaitForGpuIdle();
        VerifyDevice("completed sky frame");
    }

    /// <summary>Reports native device removal at the failing setup or execution stage.</summary>
    /// <param name="operation">The fixture stage being checked.</param>
    private void VerifyDevice(string operation)
    {
        var reason = _gpu.Device.DeviceRemovedReason;
        if (reason.Success) return;
        _gpu.PumpDebugMessages();
        _gpu.LogDeviceRemovedDiagnostics(operation);
        throw new InvalidOperationException($"Sky fixture device removed after {operation}: {reason}.");
    }

    /// <summary>Registers owned resources in dependency order before subsequent native work can fail.</summary>
    /// <typeparam name="T">Disposable resource type.</typeparam>
    /// <param name="resource">Newly created owned resource.</param>
    /// <param name="stage">Release stage after proven GPU retirement.</param>
    /// <returns>The same registered resource.</returns>
    private T Own<T>(T resource, int stage = 1) where T : IDisposable
    {
        _resources.Add(resource, "sky pipeline fixture", stage);
        return resource;
    }

    /// <summary>Preserves the original failure when cleanup also fails.</summary>
    /// <param name="failure">The construction or assertion failure already being propagated.</param>
    internal void DisposeAfterFailure(Exception failure)
    {
        try { Dispose(); }
        catch (Exception cleanupFailure)
        {
            throw new AggregateException("Sky fixture failed and cleanup could not complete.", failure, cleanupFailure);
        }
    }

    /// <summary>Retires the whole queue before releasing families, the root, and finally the native device.</summary>
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
                if (_gpu is not null && !_gpu.TryForceDeviceRemoval("sky-pipeline-fixture"))
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
