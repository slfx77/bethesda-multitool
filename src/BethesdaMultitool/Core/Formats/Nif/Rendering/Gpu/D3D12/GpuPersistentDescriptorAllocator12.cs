using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     CPU-only descriptor heap for descriptors that can be written once and copied into
///     the frame's shader-visible heap when bound.
/// </summary>
internal sealed class GpuPersistentDescriptorAllocator12 : IDisposable
{
    private readonly uint _descriptorSize;
    private readonly ID3D12DescriptorHeap _heap;
    private bool _disposed;

    public GpuPersistentDescriptorAllocator12(GpuDevice12 gpu, DescriptorHeapType type, uint capacity)
    {
        if (capacity == 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be > 0.");
        if (type != DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView)
            throw new ArgumentException("Persistent descriptor allocator currently supports CBV/SRV/UAV heaps only.",
                nameof(type));

        _heap = gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
        {
            Type = type,
            DescriptorCount = capacity,
            Flags = DescriptorHeapFlags.None
        });
        _descriptorSize = gpu.Device.GetDescriptorHandleIncrementSize(type);
        Capacity = capacity;
    }

    public uint Count { get; private set; }

    public uint Capacity { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _heap.Dispose();
    }

    public CpuDescriptorHandle Allocate()
    {
        if (Count >= Capacity)
            throw new InvalidOperationException(
                $"GpuPersistentDescriptorAllocator12 exhausted ({Count}/{Capacity}). Increase capacity.");

        return new CpuDescriptorHandle(_heap.GetCPUDescriptorHandleForHeapStart(), (int)Count++, _descriptorSize);
    }
}
