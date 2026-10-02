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

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

/// <summary>Retains the real base and modern water families with bounded raster and compute resources.</summary>
internal sealed class WaterPipelineGpuFixture : IDisposable
{
    internal const int Size = 32;
    internal const int ComputeSize = 8;
    private static readonly ConcurrentBag<WaterPipelineGpuFixture> Unretired = new();
    private readonly RetiredResourceDisposal _resources = new();
    private readonly GpuDevice12 _gpu = null!;
    private readonly GpuCommandRecorder12 _recorder = null!;
    private readonly GpuRootSignature12 _root = null!;
    private readonly GpuOffscreenSceneTarget12 _target = null!;
    private readonly ID3D12Resource _constants = null!;
    private readonly ID3D12Resource _secondConstants = null!;
    private readonly ID3D12Resource _atmosphere = null!;
    private readonly GpuDescriptorHeapAllocator12 _descriptors = null!;
    private readonly ID3D12DescriptorHeap _depthViews = null!;
    private readonly ID3D12Resource _computeOutput = null!;
    private readonly ID3D12Resource _computeReadback = null!;
    private readonly PlacedSubresourceFootPrint _computeFootprint;
    private readonly int _readbackBytes;
    private readonly uint _descriptorIncrement;
    private bool _retired;

    /// <summary>Creates all production family members and one constant input texture on the explicit WARP device.</summary>
    internal WaterPipelineGpuFixture()
    {
        try
        {
            _gpu = Own(GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly)
                ?? throw new InvalidOperationException("WARP did not create the water test device."), 5);
            _recorder = Own(new GpuCommandRecorder12(_gpu), 0);
            _root = Own(GpuRootSignature12.Create(_gpu), 4);
            BaseResources = Own(_root.CreatePipelineResources(18), 3);
            Base = WaterPipelineFactory12.CreateBasePipelines(_gpu, BaseResources);
            OriginalDepthPixelShader = Base.DepthTemplate.PixelShader;
            OriginalDepthSamplePixelShader = Base.DepthSampleTemplate.PixelShader;
            ModernResources = Own(_root.CreatePipelineResources(6), 3);
            Modern = WaterPipelineFactory12.CreateModernPipelines(ModernResources, Base.DepthTemplate, Base.DepthSampleTemplate);
            _target = Own(new GpuOffscreenSceneTarget12(_gpu, Size, Size));
            _target.TonemapSettings = GpuTonemapSettings.GammaAcesDefaults with { Mode = GpuTonemapMode.LegacyClamp };
            _constants = Upload<byte>(new byte[1024]);
            _secondConstants = Upload<byte>(new byte[1024]);
            var atmosphere = new Vector4[64];
            atmosphere[0] = new Vector4(0, 0, 1, 0);
            atmosphere[1] = new Vector4(0, 0, 0, 1); // Enabled, zero sun/ambient: no unbounded lighting oracle.
            _atmosphere = Upload<Vector4>(atmosphere);
            var instances = Upload<Vector4>(CreateInstances());
            _descriptors = Own(new GpuDescriptorHeapAllocator12(_gpu,
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                20, GpuCommandRecorder12.FramesInFlight, 16));
            for (var slot = 0; slot < 16; slot++)
            {
                var allocation = _descriptors.AllocatePersistent();
                if (allocation.BindlessIndex != (uint)slot)
                    throw new InvalidOperationException("The production persistent prefix changed its shader indices.");
            }
            _descriptorIncrement = _gpu.Device.GetDescriptorHandleIncrementSize(
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
            for (var slot = 0; slot < 16; slot++)
            {
                _gpu.Device.CreateShaderResourceView(null, new ShaderResourceViewDescription
                {
                    Format = Format.R32G32B32A32_Float,
                    ViewDimension = ShaderResourceViewDimension.Texture2D,
                    Shader4ComponentMapping = ShaderComponentMapping.Default,
                    Texture2D = new Texture2DShaderResourceView { MipLevels = 1 }
                }, Cpu(slot));
            }
            for (var instance = 0; instance < 2; instance++)
            {
                _gpu.Device.CreateShaderResourceView(instances, new ShaderResourceViewDescription
                {
                    Format = Format.Unknown,
                    ViewDimension = ShaderResourceViewDimension.Buffer,
                    Shader4ComponentMapping = ShaderComponentMapping.Default,
                    Buffer = new BufferShaderResourceView
                    {
                        FirstElement = (ulong)instance,
                        NumElements = 1,
                        StructureByteStride = 6 * 16
                    }
                }, Cpu(instance));
            }
            var source = Own(_gpu.Device.CreateCommittedResource<ID3D12Resource>(HeapProperties.DefaultHeapProperties,
                HeapFlags.None, ResourceDescription.Texture2D(Format.R32G32B32A32_Float, 1, 1, 1, 1),
                ResourceStates.CopyDest));
            var sourceFootprints = new PlacedSubresourceFootPrint[1];
            _gpu.Device.GetCopyableFootprints(source.Description, 0, 1, 0, sourceFootprints, new uint[1],
                new ulong[1], out var sourceBytes);
            var sourceUpload = Upload<Vector4>([new Vector4(0.25f, 0.75f, 0.5f, 1)], checked((int)sourceBytes));
            _gpu.Device.CreateShaderResourceView(source, new ShaderResourceViewDescription
            {
                Format = Format.R32G32B32A32_Float,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1 }
            }, Cpu(8));
            _computeOutput = Own(_gpu.Device.CreateCommittedResource<ID3D12Resource>(HeapProperties.DefaultHeapProperties,
                HeapFlags.None, ResourceDescription.Texture2D(Format.R32G32B32A32_Float, ComputeSize, ComputeSize,
                    1, 1, 1, 0, ResourceFlags.AllowUnorderedAccess), ResourceStates.UnorderedAccess));
            var outputFootprints = new PlacedSubresourceFootPrint[1];
            _gpu.Device.GetCopyableFootprints(_computeOutput.Description, 0, 1, 0, outputFootprints,
                new uint[1], new ulong[1], out var outputBytes);
            _computeFootprint = outputFootprints[0];
            _readbackBytes = checked((int)outputBytes);
            _computeReadback = Own(_gpu.Device.CreateCommittedResource<ID3D12Resource>(HeapProperties.ReadbackHeapProperties,
                HeapFlags.None, ResourceDescription.Buffer(outputBytes), ResourceStates.CopyDest));
            _depthViews = Own(_gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(
                new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, 1)));
            _gpu.Device.CreateDepthStencilView(_target.DepthResource, new DepthStencilViewDescription
            {
                Format = Format.D32_Float,
                ViewDimension = _target.SampleCount > 1
                    ? DepthStencilViewDimension.Texture2DMultisampled
                    : DepthStencilViewDimension.Texture2D
            }, _depthViews.GetCPUDescriptorHandleForHeapStart());
            VerifyDevice("water descriptors");
            _recorder.BeginFrame();
            _recorder.CommandList.CopyTextureRegion(new TextureCopyLocation(source), 0, 0, 0,
                new TextureCopyLocation(sourceUpload, sourceFootprints[0]));
            _recorder.CommandList.ResourceBarrierTransition(source, ResourceStates.CopyDest,
                ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource);
            CompleteFrame();
        }
        catch (Exception failure)
        {
            DisposeAfterFailure(failure);
            throw;
        }
    }

    /// <summary>Gets the retained eighteen-slot base family.</summary>
    internal ShaderPipelineResources BaseResources { get; } = null!;

    /// <summary>Gets the replaceable modern family sharing the exact base root.</summary>
    internal ShaderPipelineResources ModernResources { get; private set; } = null!;

    /// <summary>Gets all borrowed production base handles and the independent templates supplied to modern families.</summary>
    internal WaterPipelineSet12 Base { get; } = null!;

    /// <summary>Gets the read-only depth shader memory captured before the first modern family is created.</summary>
    internal ReadOnlyMemory<byte> OriginalDepthPixelShader { get; }

    /// <summary>Gets the read-only depth-sample shader memory captured before modern creation can mutate a description.</summary>
    internal ReadOnlyMemory<byte> OriginalDepthSamplePixelShader { get; }

    /// <summary>Gets the borrowed modern raster and compute handles.</summary>
    internal ModernWaterPipelineSet12 Modern { get; private set; } = null!;

    /// <summary>Captures ordinary flat water, optionally layering nearer then farther water without depth writes.</summary>
    /// <param name="depth">Initial opaque reversed-Z depth.</param>
    /// <param name="layered">Whether to draw red at .75 followed by green at .5; otherwise one .5-depth tint.</param>
    /// <returns>Actual fenced BGRA pixels.</returns>
    internal byte[] CaptureFlat(float depth, bool layered = false)
    {
        WriteRasterConstants(_constants, layered ? new Vector3(0.5f, 0, 0) : new Vector3(0.5f, 0.25f, 0));
        if (layered)
        {
            WriteRasterConstants(_secondConstants, new Vector3(0, 0.5f, 0));
        }
        BeginRaster(depth);
        Draw(Base.Flat, _constants, layered ? 0u : 1u);
        if (layered)
        {
            Draw(Base.Flat, _secondConstants, 1);
        }
        return FinishRaster();
    }

    /// <summary>Captures the FO76 dual-source path with a black source and distinct per-channel transmission.</summary>
    /// <returns>Pixels whose RGB comes solely from the production destination transmission factors.</returns>
    internal byte[] CaptureTransmission()
    {
        WriteRasterConstants(_constants, Vector3.Zero);
        BeginRaster(0.5f);
        Draw(Base.Fo76Optics, _constants, 1);
        return FinishRaster();
    }

    /// <summary>Captures the explicit Skyrim opaque fallback with a read-only DSV and no scene-depth input.</summary>
    /// <returns>The black opaque source replacing the fractional-alpha background.</returns>
    internal byte[] CaptureOpaqueFallback()
    {
        WriteRasterConstants(_constants, Vector3.Zero);
        BeginRaster(0.5f);
        var commands = _recorder.CommandList;
        _target.BindColorOnly(commands);
        commands.ResourceBarrierTransition(_target.DepthResource, ResourceStates.DepthWrite,
            ResourceStates.DepthRead | ResourceStates.PixelShaderResource);
        _target.BindColorReadOnlyDepth(commands);
        Draw(Base.SkyrimOpaqueSnapshotDepthSample, _constants, 1);
        _target.BindColorOnly(commands);
        commands.ResourceBarrierTransition(_target.DepthResource, ResourceStates.DepthRead | ResourceStates.PixelShaderResource,
            ResourceStates.DepthWrite);
        _target.Rebind(commands);
        return FinishRaster();
    }

    /// <summary>Runs the production FNV normal or modern body-coverage shader over one bounded 8-by-8 group.</summary>
    /// <param name="modern">True for modern body coverage; false for the normal reconstruction from a constant source.</param>
    /// <returns>Every float4 output after native submission and readback completion.</returns>
    internal Vector4[] Dispatch(bool modern)
    {
        var mapped = _constants.Map<Vector4>(0, 64);
        try
        {
            mapped.Clear();
            if (modern)
            {
                mapped[1] = new Vector4(0.125f, 0.25f, 0.5f, 0.75f);
            }
            else
            {
                mapped[0] = new Vector4(0, 0, 1, 0);
            }
        }
        finally { _constants.Unmap(0); }
        _recorder.BeginFrame();
        // The recorder proves slot retirement before the production allocator resets that frame's partition.
        _descriptors.BeginFrame(_recorder.FrameIndex);
        var outputView = _descriptors.Allocate(1);
        _gpu.Device.CreateUnorderedAccessView(_computeOutput, null, new UnorderedAccessViewDescription
        {
            Format = Format.R32G32B32A32_Float,
            ViewDimension = UnorderedAccessViewDimension.Texture2D
        }, outputView.Cpu);
        var commands = _recorder.CommandList;
        commands.SetComputeRootSignature(_root.RootSignature);
        commands.SetDescriptorHeaps(1, new[] { _descriptors.Heap });
        commands.SetComputeRootDescriptorTable(GpuRootSignature12.Slots.BindlessSrvTable, Gpu(8));
        commands.SetComputeRootDescriptorTable(GpuRootSignature12.Slots.WaterNoiseUavTable, outputView.Gpu);
        commands.SetComputeRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, _constants.GPUVirtualAddress);
        commands.SetPipelineState(modern ? Modern.ComputePipelines[0] : Base.NoiseNormal);
        commands.Dispatch(1, 1, 1);
        commands.ResourceBarrierTransition(_computeOutput, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
        commands.CopyTextureRegion(new TextureCopyLocation(_computeReadback, _computeFootprint), 0, 0, 0,
            new TextureCopyLocation(_computeOutput));
        commands.ResourceBarrierTransition(_computeOutput, ResourceStates.CopySource, ResourceStates.UnorderedAccess);
        CompleteFrame();
        var pixels = new Vector4[ComputeSize * ComputeSize];
        var bytes = _computeReadback.Map<byte>(0, _readbackBytes);
        try
        {
            for (var y = 0; y < ComputeSize; y++)
            {
                for (var x = 0; x < ComputeSize; x++)
                {
                    var offset = checked((int)_computeFootprint.Offset + y * (int)_computeFootprint.Footprint.RowPitch + x * 16);
                    pixels[y * ComputeSize + x] = MemoryMarshal.Read<Vector4>(bytes[offset..]);
                }
            }
        }
        finally { _computeReadback.Unmap(0, new Vortice.Direct3D12.Range(0, 0)); }
        return pixels;
    }

    /// <summary>Releases only the idle modern family and recreates it against the still-live base root.</summary>
    internal void RecreateModernFamily()
    {
        _recorder.WaitForGpuIdle();
        ModernResources.Dispose();
        ModernResources = Own(_root.CreatePipelineResources(6), 3);
        Modern = WaterPipelineFactory12.CreateModernPipelines(ModernResources, Base.DepthTemplate, Base.DepthSampleTemplate);
    }

    /// <summary>Binds the actual structured-instance table and initializes a fractional-alpha scene background.</summary>
    /// <param name="depth">Depth value representing opaque geometry already in the target.</param>
    private void BeginRaster(float depth)
    {
        _recorder.BeginFrame();
        _descriptors.BeginFrame(_recorder.FrameIndex);
        var commands = _recorder.CommandList;
        _target.Bind(commands, new Color4(0.125f, 0.25f, 0.5f, 0.25f));
        commands.ClearDepthStencilView(_depthViews.GetCPUDescriptorHandleForHeapStart(), ClearFlags.Depth, depth, 0);
        commands.SetGraphicsRootSignature(_root.RootSignature);
        commands.SetDescriptorHeaps(1, new[] { _descriptors.Heap });
        commands.SetGraphicsRootDescriptorTable(GpuRootSignature12.Slots.SrvTable, Gpu(0));
        GpuRootSignature12.SetGraphicsBindlessTables(commands, Gpu(8));
        commands.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.AtmosphereCbv, _atmosphere.GPUVirtualAddress);
        commands.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
    }

    /// <summary>Draws one real water packet through the production structured-buffer vertex shader.</summary>
    /// <param name="pipeline">Family-owned production pipeline.</param>
    /// <param name="constants">Immutable constants retained for this draw.</param>
    /// <param name="instance">Zero for the nearer packet; one for the farther packet.</param>
    private void Draw(ID3D12PipelineState pipeline, ID3D12Resource constants, uint instance)
    {
        var commands = _recorder.CommandList;
        commands.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, constants.GPUVirtualAddress);
        // SV_InstanceID stays zero for this one-instance draw; select the production packet through its SRV window.
        commands.SetGraphicsRootDescriptorTable(GpuRootSignature12.Slots.SrvTable, Gpu(checked((int)instance)));
        commands.SetPipelineState(pipeline);
        commands.DrawInstanced(6, 1, 0, 0);
    }

    /// <summary>Uses the established HDR resolve/tonemap path and returns only proven-complete display pixels.</summary>
    /// <returns>Tightly packed BGRA output.</returns>
    private byte[] FinishRaster()
    {
        _target.RecordReadback(_recorder);
        CompleteFrame();
        return _target.ReadbackToBytes();
    }

    /// <summary>Initializes the unchanged water register layout with neutral lighting and explicit missing optional sources.</summary>
    /// <param name="resource">Retained constant buffer to update while the queue is idle.</param>
    /// <param name="shallow">Flat surface tint; zero also removes the opaque fallback's body contribution.</param>
    private static void WriteRasterConstants(ID3D12Resource resource, Vector3 shallow)
    {
        var values = resource.Map<Vector4>(0, 64);
        try
        {
            values.Clear();
            values[0] = new Vector4(1, 0, 0, 0);
            values[1] = new Vector4(0, 1, 0, 0);
            values[2] = new Vector4(0, 0, 1, 0);
            values[3] = new Vector4(0, 0, 0, 1);
            values[4] = new Vector4(shallow, 1);
            values[7] = new Vector4(0, 0, 2, 0); // Nondegenerate surface-to-eye direction.
            values[8] = new Vector4(0, 1, 1, 0.5f); // Texture zero; alpha bits in NoiseParams.w.
            values[9] = new Vector4(1, 0, 0, 1);
            values[11] = new Vector4(1, 0, 0, 0);
            values[12] = values[13] = values[11];
            values[14] = new Vector4(BitConverter.UInt32BitsToSingle(uint.MaxValue), 0.1f, 100f, 0);
            values[19] = new Vector4(BitConverter.UInt32BitsToSingle(uint.MaxValue)); // Three absent authored normal sources.
            values[25] = new Vector4(0, 0.5f, 1, 0); // FO76 RGB opacity: distinguish every transmission channel.
            values[28] = new Vector4(BitConverter.UInt32BitsToSingle(uint.MaxValue), 0, 0, 0); // No reflection snapshot.
        }
        finally { resource.Unmap(0); }
    }

    /// <summary>Encodes two complete six-vertex water packets with identical screen coverage and distinct reversed depths.</summary>
    /// <returns>The production 96-byte structured element layout repeated twice.</returns>
    private static Vector4[] CreateInstances()
    {
        Vector2[] corners = [new(-0.5f, -0.5f), new(0.5f, -0.5f), new(-0.5f, 0.5f),
            new(-0.5f, 0.5f), new(0.5f, -0.5f), new(0.5f, 0.5f)];
        var result = new Vector4[12];
        for (var index = 0; index < result.Length; index++)
        {
            var corner = corners[index % 6];
            result[index] = new Vector4(corner.X, corner.Y, index < 6 ? 0.75f : 0.5f, 1);
        }
        return result;
    }

    /// <summary>Gets the checked-in fixture's persistent CPU descriptor address without transferring heap ownership.</summary>
    /// <param name="slot">Descriptor offset within the sixteen-slot persistent prefix.</param>
    /// <returns>The borrowed CPU descriptor handle.</returns>
    private CpuDescriptorHandle Cpu(int slot) => new(_descriptors.Heap.GetCPUDescriptorHandleForHeapStart(), slot, _descriptorIncrement);

    /// <summary>Gets a retained shader-visible descriptor address for the current recording.</summary>
    /// <param name="slot">Descriptor offset within the fixture heap.</param>
    /// <returns>The borrowed GPU descriptor handle.</returns>
    private GpuDescriptorHandle Gpu(int slot) => new(_descriptors.BindlessHeapStartGpu, slot, _descriptorIncrement);

    /// <summary>Owns an upload allocation before mapping or populating its exact payload.</summary>
    /// <typeparam name="T">Blittable payload element.</typeparam>
    /// <param name="data">Payload to copy.</param>
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

    /// <summary>Submits and retires native commands before reading or reusing any fixture memory.</summary>
    private void CompleteFrame()
    {
        _recorder.EndFrame();
        _recorder.WaitForGpuIdle();
        VerifyDevice("completed water frame");
    }

    /// <summary>Attributes device removal to setup or recording rather than a later native allocation.</summary>
    /// <param name="operation">The operation that just completed.</param>
    private void VerifyDevice(string operation)
    {
        var reason = _gpu.Device.DeviceRemovedReason;
        if (reason.Success)
        {
            return;
        }
        _gpu.PumpDebugMessages();
        _gpu.LogDeviceRemovedDiagnostics(operation);
        throw new InvalidOperationException($"Water fixture device removed after {operation}: {reason}.");
    }

    /// <summary>Retains a new native owner before subsequent initialization can fail.</summary>
    /// <typeparam name="T">Disposable owner type.</typeparam>
    /// <param name="resource">Newly created resource.</param>
    /// <param name="stage">Release stage after GPU retirement.</param>
    /// <returns>The registered owner.</returns>
    private T Own<T>(T resource, int stage = 1) where T : IDisposable
    {
        _resources.Add(resource, "water pipeline fixture", stage);
        return resource;
    }

    /// <summary>Preserves an earlier failure if retirement or disposal also fails.</summary>
    /// <param name="failure">The original construction or assertion failure.</param>
    internal void DisposeAfterFailure(Exception failure)
    {
        try { Dispose(); }
        catch (Exception cleanupFailure)
        {
            throw new AggregateException("Water fixture failed and cleanup could not complete.", failure, cleanupFailure);
        }
    }

    /// <summary>Proves retirement before releasing families, their shared root and the device.</summary>
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
                if (_gpu is not null && !_gpu.TryForceDeviceRemoval("water-pipeline-fixture"))
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
