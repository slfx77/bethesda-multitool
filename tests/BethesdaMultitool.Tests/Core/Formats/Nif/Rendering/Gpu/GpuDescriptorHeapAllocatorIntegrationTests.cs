using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Vortice.Direct3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>Checks the production allocator's exact descriptor ABI and retained application allocation policy on WARP.</summary>
[Trait("Category", GpuTestGuard.Category)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class GpuDescriptorHeapAllocatorIntegrationTests
{
    private const DescriptorHeapType ResourceType = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;

    /// <summary>Partitions use exact absolute native offsets, including zero-sized end markers without cursor mutation.</summary>
    /// <param name="persistentCapacity">Whether frame zero begins at the heap base or after a persistent prefix.</param>
    [Theory]
    [InlineData(0U)]
    [InlineData(4U)]
    public void FramePartitionsPreserveIndicesAndBoundEveryReservation(uint persistentCapacity)
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        using var heap = new GpuDescriptorHeapAllocator12(gpu, ResourceType, persistentCapacity + 12, 3, persistentCapacity);
        var cpuBase = (ulong)heap.Heap.GetCPUDescriptorHandleForHeapStart().Ptr;
        var gpuBase = heap.Heap.GetGPUDescriptorHandleForHeapStart().Ptr;
        var increment = gpu.Device.GetDescriptorHandleIncrementSize(ResourceType);
        Assert.Equal(gpuBase, heap.BindlessHeapStartGpu.Ptr);
        Assert.Equal(4U, heap.PerFrameCapacity);
        for (var frame = 0; frame < 3; frame++)
        {
            heap.BeginFrame(frame);
            var absoluteStart = persistentCapacity + (uint)frame * 4;
            var marker = heap.Allocate(0);
            Assert.Equal(cpuBase + absoluteStart * increment, (ulong)marker.Cpu.Ptr);
            Assert.Equal(gpuBase + absoluteStart * increment, marker.Gpu.Ptr);
            Assert.Equal(0U, heap.CurrentFrameUsed);
            var full = heap.Allocate(4);
            Assert.Equal(marker.Cpu, full.Cpu);
            Assert.Equal(marker.Gpu, full.Gpu);
            Assert.Equal(increment, full.DescriptorSize);
            Assert.Equal(4U, heap.CurrentFrameUsed);
            Assert.Equal(4U, heap.CurrentFramePeak);
            Assert.Throws<InvalidOperationException>(() => heap.Allocate(1));
            Assert.Throws<InvalidOperationException>(() => heap.Allocate(uint.MaxValue));
            var end = heap.Allocate(0);
            Assert.Equal(cpuBase + (absoluteStart + 4) * increment, (ulong)end.Cpu.Ptr);
            Assert.Equal(gpuBase + (absoluteStart + 4) * increment, end.Gpu.Ptr);
            Assert.Equal(4U, heap.CurrentFrameUsed);
            Assert.Equal(4U, heap.CurrentFramePeak);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => heap.BeginFrame(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => heap.BeginFrame(3));
        Assert.Equal(4U, heap.CurrentFrameUsed);
        heap.BeginFrame(0); // No commands were submitted, so all frame partitions remain retired.
        Assert.Equal(0U, heap.CurrentFrameUsed);
        Assert.Equal(0U, heap.CurrentFramePeak);
        Assert.Throws<InvalidOperationException>(() => heap.Allocate(uint.MaxValue));
        Assert.Equal(0U, heap.CurrentFrameUsed);
        Assert.Equal(cpuBase + persistentCapacity * increment, (ulong)heap.Allocate(1).Cpu.Ptr);
        if (persistentCapacity == 0)
            Assert.Throws<InvalidOperationException>(() => heap.AllocatePersistent());
    }

    /// <summary>Persistent indices retain LIFO reuse and high-water statistics while invalid returns cannot invent ownership.</summary>
    [Fact]
    public void PersistentSlotsReuseLifoAndRejectDuplicateOrUnallocatedReturns()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        using var heap = new GpuDescriptorHeapAllocator12(gpu, ResourceType, 13, 2, 5);
        var first = heap.AllocatePersistent();
        var second = heap.AllocatePersistent();
        var third = heap.AllocatePersistent();
        Assert.Equal(0U, first.BindlessIndex);
        Assert.Equal(1U, second.BindlessIndex);
        Assert.Equal(2U, third.BindlessIndex);
        Assert.Throws<ArgumentOutOfRangeException>(() => heap.FreePersistent(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => heap.FreePersistent(5));
        Assert.Equal(3U, heap.PersistentCount);
        Assert.Equal(3U, heap.PersistentPeak);
        heap.FreePersistent(first.BindlessIndex);
        heap.FreePersistent(third.BindlessIndex);
        Assert.Throws<InvalidOperationException>(() => heap.FreePersistent(third.BindlessIndex));
        Assert.Equal(1U, heap.PersistentCount);
        Assert.Equal(third, heap.AllocatePersistent());
        Assert.Equal(first, heap.AllocatePersistent());
        Assert.Equal(3U, heap.AllocatePersistent().BindlessIndex);
        Assert.Equal(4U, heap.AllocatePersistent().BindlessIndex);
        Assert.Throws<InvalidOperationException>(() => heap.AllocatePersistent());
        Assert.Equal(5U, heap.PersistentCount);
        Assert.Equal(5U, heap.PersistentPeak);
        heap.BeginFrame(1);
        _ = heap.Allocate(4);
        heap.BeginFrame(0);
        Assert.Equal(5U, heap.PersistentCount);
        heap.FreePersistent(second.BindlessIndex);
        Assert.Equal(second, heap.AllocatePersistent());
        Assert.Equal(5U, heap.PersistentPeak);
    }

    /// <summary>Foreign-thread rejection occurs before native borrowing, ownership changes, cursor changes or disposal.</summary>
    [Fact]
    public void ForeignThreadCannotMutateOrStopTheCreatingThreadOwner()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        using var heap = new GpuDescriptorHeapAllocator12(gpu, ResourceType, 10, 2, 2);
        var persistent = heap.AllocatePersistent();
        var native = heap.Heap;
        var first = heap.Allocate(1);
        AssertForeignThreadRejected(
            () => { _ = heap.Heap; },
            () => { _ = heap.BindlessHeapStartGpu; },
            () => heap.Allocate(1),
            () => heap.AllocatePersistent(),
            () => heap.FreePersistent(persistent.BindlessIndex),
            () => heap.BeginFrame(1),
            heap.Dispose);
        Assert.Same(native, heap.Heap);
        Assert.Equal(1U, heap.PersistentCount);
        Assert.Equal(1U, heap.CurrentFrameUsed);
        var second = heap.Allocate(1);
        Assert.Equal((ulong)first.Cpu.Ptr + first.DescriptorSize, (ulong)second.Cpu.Ptr);
        heap.FreePersistent(persistent.BindlessIndex);
        Assert.Equal(persistent, heap.AllocatePersistent());
    }

    /// <summary>Disposal invalidates borrowed handles once and leaves the caller's native device usable.</summary>
    [Fact]
    public void StoppedOwnerRejectsHandleAccessAndAllocationButAllowsDisposalRetry()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        using var heap = new GpuDescriptorHeapAllocator12(gpu, ResourceType, 10, 2, 2);
        var persistent = heap.AllocatePersistent();
        var native = heap.Heap;
        heap.Dispose();
        heap.Dispose();
        Assert.Equal(IntPtr.Zero, native.NativePointer);
        Assert.Throws<ObjectDisposedException>(() => heap.Heap);
        Assert.Throws<ObjectDisposedException>(() => heap.BindlessHeapStartGpu);
        Assert.Throws<ObjectDisposedException>(() => heap.Allocate(0));
        Assert.Throws<ObjectDisposedException>(() => heap.AllocatePersistent());
        Assert.Throws<ObjectDisposedException>(() => heap.FreePersistent(persistent.BindlessIndex));
        Assert.Throws<ObjectDisposedException>(() => heap.BeginFrame(0));
        AssertForeignThreadRejected(heap.Dispose);
        using var fence = gpu.Device.CreateFence<ID3D12Fence>();
        Assert.NotEqual(IntPtr.Zero, fence.NativePointer);
    }

    /// <summary>Sampler-domain ranges use their own native increment and keep the same persistent/frame layout.</summary>
    [Fact]
    public void SamplerPartitionsUseTheSamplerDescriptorIncrement()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        using var heap = new GpuDescriptorHeapAllocator12(gpu, DescriptorHeapType.Sampler, 10, 2, 2);
        var increment = gpu.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Sampler);
        var start = (ulong)heap.Heap.GetCPUDescriptorHandleForHeapStart().Ptr;
        var first = heap.AllocatePersistent();
        var second = heap.AllocatePersistent();
        Assert.Equal(start, (ulong)first.Cpu.Ptr);
        Assert.Equal(start + increment, (ulong)second.Cpu.Ptr);
        heap.BeginFrame(1);
        var table = heap.Allocate(4);
        Assert.Equal(start + 6UL * increment, (ulong)table.Cpu.Ptr);
        Assert.Equal(heap.BindlessHeapStartGpu.Ptr + 6UL * increment, table.Gpu.Ptr);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GpuDescriptorHeapAllocator12(
            gpu, DescriptorHeapType.Sampler, 2050, 2));
        using var fence = gpu.Device.CreateFence<ID3D12Fence>();
        Assert.NotEqual(IntPtr.Zero, fence.NativePointer);
    }

    /// <summary>Oversized resource budgets fail before creating a native heap and leave the borrowed device usable.</summary>
    [Fact]
    public void ResourceCapacityAdmissionRejectsOversizedAndOverflowingRequests()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GpuDescriptorHeapAllocator12(gpu, ResourceType, 1_000_001, 1));
        Assert.Throws<OverflowException>(() => new GpuDescriptorHeapAllocator12(gpu, ResourceType, uint.MaxValue, 1));
        using var heap = new GpuDescriptorHeapAllocator12(gpu, ResourceType, 2, 2);
        Assert.NotEqual(IntPtr.Zero, heap.Heap.NativePointer);
        using var fence = gpu.Device.CreateFence<ID3D12Fence>();
        Assert.NotEqual(IntPtr.Zero, fence.NativePointer);
    }

    /// <summary>Executes bounded guarded operations on another thread and reports every rejection to the owning test.</summary>
    /// <param name="operations">Calls required to fail before mutating or borrowing owner state.</param>
    private static void AssertForeignThreadRejected(params Action[] operations)
    {
        var failures = new Exception?[operations.Length];
        var worker = new Thread(() =>
        {
            for (var index = 0; index < operations.Length; index++)
                failures[index] = Record.Exception(operations[index]);
        }) { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "Foreign-thread descriptor checks did not finish.");
        foreach (var failure in failures) Assert.IsType<InvalidOperationException>(failure);
    }
}
