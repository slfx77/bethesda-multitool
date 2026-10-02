#if WINDOWS_GUI
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Slfx77.Multitool.Core.Lifetime;
using Slfx77.Multitool.WinUI.Direct3D12.Shaders;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>Retains optional water pipeline and texture ownership through initialization and retirement.</summary>
/// <remarks>The renderer retains this object before initialization; failed releases remain reachable for retry.
/// The caller must retire GPU use before disposal. Texture and descriptor releases use the existing deletion queue.</remarks>
internal sealed class ModernWaterResources12 : IDisposable
{
    internal const int BodyOutput = 0;
    internal const int NormalOutput = 1;
    internal const int GlossOutput = 2;
    internal const int DepthLutOutput = 3;

    private readonly GpuDevice12 _gpu;
    private readonly GpuRootSignature12 _rootSignature;
    private readonly GpuDescriptorHeapAllocator12 _heap;
    private readonly GpuDeletionQueue12 _deletionQueue;
    private readonly RetiredResourceDisposal _retiredResources = new();
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly bool[] _allocatedSlots = new bool[4];
    private ShaderPipelineResources? _pipelineResources;
    private bool _disposed;

    /// <summary>Creates the managed owner without allocating native resources.</summary>
    /// <param name="gpu">Borrowed native device.</param>
    /// <param name="rootSignature">World root used by the independent six-slot pipeline family.</param>
    /// <param name="heap">Borrowed bindless heap providing four output SRVs.</param>
    /// <param name="deletionQueue">Borrowed queue retiring submitted texture and descriptor uses.</param>
    internal ModernWaterResources12(
        GpuDevice12 gpu,
        GpuRootSignature12 rootSignature,
        GpuDescriptorHeapAllocator12 heap,
        GpuDeletionQueue12 deletionQueue)
    {
        _gpu = gpu;
        _rootSignature = rootSignature;
        _heap = heap;
        _deletionQueue = deletionQueue;
        // Register the bounded ownership graph before native allocation. Initialization can fail
        // at any instruction without depending on a later callback/list allocation for cleanup.
        for (var index = 0; index < Outputs.Length; index++)
        {
            var outputIndex = index;
            _retiredResources.Add(() => RetireOutput(outputIndex), "modern water output");
        }
        for (var index = 0; index < BindlessIndices.Length; index++)
        {
            var slotIndex = index;
            _retiredResources.Add(() => RetireSlot(slotIndex), "modern water descriptor");
        }
        _retiredResources.Add(ReleasePipelineResources, "modern water pipeline family", stage: 1);
    }

    /// <summary>Gets the borrowed ordinary depth-test graphics permutation after initialization.</summary>
    internal ID3D12PipelineState Pixel { get; private set; } = null!;

    /// <summary>Gets the borrowed read-only depth-sample graphics permutation after initialization.</summary>
    internal ID3D12PipelineState PixelDepthSample { get; private set; } = null!;

    /// <summary>Gets borrowed body/coverage, normal, gloss and depth-LUT compute pipelines in output order.</summary>
    internal ID3D12PipelineState[] ComputePipelines { get; private set; } = [];

    /// <summary>Gets the four allocated outputs, valid only after initialization completes.</summary>
    internal ID3D12Resource[] Outputs { get; } = new ID3D12Resource[4];

    /// <summary>Gets the corresponding output SRV indices, valid only after initialization completes.</summary>
    internal uint[] BindlessIndices { get; } = new uint[4];

    /// <summary>Creates the retained pipeline family and unchanged output textures and descriptors once.</summary>
    /// <param name="depthTemplate">Base description for ordinary depth-tested water.</param>
    /// <param name="depthSampleTemplate">Base description for read-only depth-sampled water.</param>
    /// <remarks>The caller retains this owner before entry and disposes it if initialization fails.</remarks>
    /// <exception cref="InvalidOperationException">Initialization was already attempted or the thread changed.</exception>
    /// <exception cref="ObjectDisposedException">The owner was disposed.</exception>
    internal void Initialize(
        GraphicsPipelineStateDescription depthTemplate,
        GraphicsPipelineStateDescription depthSampleTemplate)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pipelineResources is not null)
        {
            throw new InvalidOperationException("Modern water initialization was already attempted.");
        }
        _pipelineResources = _rootSignature.CreatePipelineResources(6);
        var pipelines = WaterPipelineFactory12.CreateModernPipelines(
            _pipelineResources, depthTemplate, depthSampleTemplate);
        Pixel = pipelines.Pixel;
        PixelDepthSample = pipelines.PixelDepthSample;
        ComputePipelines = pipelines.ComputePipelines;

        for (var i = 0; i < Outputs.Length; i++)
        {
            var width = i == DepthLutOutput
                ? ModernWaterPipeline.DepthLutWidth
                : ModernWaterPipeline.SurfaceDimension;
            var height = i == DepthLutOutput
                ? ModernWaterPipeline.DepthLutHeight
                : ModernWaterPipeline.SurfaceDimension;
            var output = _gpu.Device.CreateCommittedResource<ID3D12Resource>(
                new HeapProperties(HeapType.Default),
                HeapFlags.None,
                ResourceDescription.Texture2D(
                    Format.R16G16B16A16_Float,
                    width,
                    height,
                    1,
                    1,
                    1,
                    0,
                    ResourceFlags.AllowUnorderedAccess),
                ModernWaterPipeline.DynamicReadState);
            Outputs[i] = output;
            output.Name = i switch
            {
                BodyOutput => "Modern Water Body+Coverage",
                NormalOutput => "Modern Water Composite Normal",
                GlossOutput => "Modern Water Gloss+Flow",
                _ => "Modern Water Depth LUT",
            };

            var allocation = _heap.AllocatePersistent();
            BindlessIndices[i] = allocation.BindlessIndex;
            _allocatedSlots[i] = true;
            _gpu.Device.CreateShaderResourceView(
                output,
                new ShaderResourceViewDescription
                {
                    Format = Format.R16G16B16A16_Float,
                    ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                    Shader4ComponentMapping = ShaderComponentMapping.Default,
                    Texture2D = new Texture2DShaderResourceView
                    {
                        MostDetailedMip = 0,
                        MipLevels = 1,
                    },
                },
                allocation.Cpu);
        }
    }

    /// <summary>Transfers outputs for deferred release and retries pending native family disposal.</summary>
    /// <remarks>GPU retirement is established by the caller. Successful transfers are removed once;
    /// a failed transfer retains that output and prevents pipeline-family release until retried.</remarks>
    /// <exception cref="InvalidOperationException">Called from another managed thread.</exception>
    /// <exception cref="AggregateException">A release or deferred transfer remains pending.</exception>
    public void Dispose()
    {
        VerifyAccess();
        _disposed = true;
        _retiredResources.Dispose();
    }

    /// <summary>Transfers one allocated output once, clearing ownership only after the queue accepts it.</summary>
    /// <param name="index">Output index whose retained allocation may be absent after partial initialization.</param>
    private void RetireOutput(int index)
    {
        if (Outputs[index] is not { } output) { return; }
        _deletionQueue.EnqueueDispose(output);
        Outputs[index] = null!;
    }

    /// <summary>Transfers one allocated descriptor return once, retaining its index if transfer fails.</summary>
    /// <param name="index">Output index whose corresponding persistent slot may be unallocated.</param>
    private void RetireSlot(int index)
    {
        if (!_allocatedSlots[index]) { return; }
        _deletionQueue.EnqueueDispose(new WaterPersistentSlotReturn12(_heap, BindlessIndices[index]));
        _allocatedSlots[index] = false;
    }

    /// <summary>Retries family disposal until all native handles and its shared-root dependency are released.</summary>
    private void ReleasePipelineResources()
    {
        _pipelineResources?.Dispose();
        _pipelineResources = null;
    }

    /// <summary>Rejects cross-thread ownership changes before stopped or native state can mutate.</summary>
    /// <exception cref="InvalidOperationException">Called from a thread other than the creating thread.</exception>
    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("Modern water resources must be accessed on their creating thread.");
        }
    }
}
#endif
