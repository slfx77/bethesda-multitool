using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Vortice.Direct3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>Exercises physical block admission and retirement on the production arena with a real WARP device.</summary>
[Trait("Category", GpuTestGuard.Category)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class GpuGeometryBackingBudgetTests
{
    /// <summary>Multiple small ranges share one aligned native allocation, which remains charged until the last range retires.</summary>
    [Fact]
    public void PackedRangesShareOnePhysicalChargeAndReuseAfterRelease()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        var physicalBytes = checked((long)gpu.Device.GetResourceAllocationInfo(0, ResourceDescription.Buffer(128)).SizeInBytes);
        using var arena = new GpuGeometryArena12(gpu, blockSize: 128, maximumBackingBytes: physicalBytes);
        var first = arena.Upload(new byte[16], []);
        var second = arena.Upload(new byte[16], []);
        Assert.Equal(first.Allocation.BlockIndex, second.Allocation.BlockIndex);
        Assert.Equal(physicalBytes, arena.BackingBytes);
        Assert.Equal(physicalBytes, arena.GetStats().EstimatedBytes);
        Assert.True(arena.BackingBytes > first.Allocation.AlignedSize + second.Allocation.AlignedSize);
        arena.Free(first);
        Assert.Equal(0L, arena.ReleaseEmptyBlocks());
        Assert.Equal(physicalBytes, arena.BackingBytes);
        arena.Free(second);
        Assert.Equal(physicalBytes, arena.ReleaseEmptyBlocks());
        Assert.Equal(0L, arena.BackingBytes);
        var replacement = arena.Upload(new byte[16], []);
        Assert.Equal(first.Allocation.BlockIndex, replacement.Allocation.BlockIndex);
        Assert.Equal(physicalBytes, arena.BackingBytes);
        arena.Free(replacement);
        arena.Dispose();
        Assert.Equal(0L, arena.BackingBytes);
    }

    /// <summary>A budget smaller than the device allocation refuses even a tiny payload before any native backing is retained.</summary>
    [Fact]
    public void DeviceAlignmentIsAdmittedBeforeNativeCreation()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        var physicalBytes = checked((long)gpu.Device.GetResourceAllocationInfo(0, ResourceDescription.Buffer(128)).SizeInBytes);
        using var arena = new GpuGeometryArena12(gpu, blockSize: 128, maximumBackingBytes: physicalBytes - 1);
        var denied = Assert.Throws<GpuGeometryBudgetExceededException>(() => arena.Upload(new byte[16], []));
        Assert.Equal(physicalBytes, denied.RequestedBytes);
        Assert.Equal(physicalBytes - 1, denied.MaximumBytes);
        Assert.Equal(0L, arena.BackingBytes);
        Assert.Equal(0L, arena.ReleaseEmptyBlocks());
        Assert.Equal(0, arena.GetStats().EntryCount);
    }

    /// <summary>Free holes do not become physical headroom, and a denied allocation leaves existing geometry readable.</summary>
    [Fact]
    public void FragmentationCannotExceedThePhysicalCeiling()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        const int blockBytes = 65_536;
        var physicalBytes = checked((long)gpu.Device.GetResourceAllocationInfo(0, ResourceDescription.Buffer(blockBytes)).SizeInBytes);
        using var arena = new GpuGeometryArena12(gpu, blockSize: blockBytes, maximumBackingBytes: physicalBytes);
        var first = arena.Upload(new byte[32_768], []);
        var survivingData = Enumerable.Repeat((byte)0xA7, 32_768).ToArray();
        var survivor = arena.Upload(survivingData, []);
        Assert.True(arena.TryHashRange(survivor, survivor.VertexBufferLocation, survivor.VertexBytes, out var before));
        arena.Free(first);
        Assert.Throws<GpuGeometryBudgetExceededException>(() => arena.Upload(new byte[49_152], []));
        Assert.Equal(physicalBytes, arena.BackingBytes);
        Assert.True(arena.TryHashRange(survivor, survivor.VertexBufferLocation, survivor.VertexBytes, out var after));
        Assert.Equal(before, after);
        Assert.Equal(0L, arena.ReleaseEmptyBlocks());
        arena.Free(survivor);
        Assert.Equal(physicalBytes, arena.ReleaseEmptyBlocks());
        var replacement = arena.Upload(new byte[49_152], []);
        Assert.Equal(physicalBytes, arena.BackingBytes);
        arena.Free(replacement);
    }

    /// <summary>Rejected provisional ranges remain owned by an accepting caller until its retirement callback runs.</summary>
    [Fact]
    public void CallerRetainsTheRejectedRangeWithoutOwningAnyBacking()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        using var arena = new GpuGeometryArena12(gpu, blockSize: 128, maximumBackingBytes: 1);
        GeometryAllocation12? retained = null;
        Assert.Throws<GpuGeometryBudgetExceededException>(() => arena.Upload(new byte[16], [],
            retainAllocation: allocation => retained = allocation));
        Assert.NotNull(retained);
        Assert.Equal(0UL, arena.ReclamationGeneration);
        Assert.Equal(0L, arena.BackingBytes);
        arena.Free(retained.Value);
        Assert.Equal(1UL, arena.ReclamationGeneration);
    }

    /// <summary>DEFAULT backing stays charged after a range is returned while its recorded copy still owns the block.</summary>
    [Fact]
    public void RecordedCopyHoldsPhysicalBackingUntilActualRetirement()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        using var recorder = new GpuCommandRecorder12(gpu);
        const int blockBytes = 65_536;
        var physicalBytes = checked((long)gpu.Device.GetResourceAllocationInfo(0, ResourceDescription.Buffer(blockBytes)).SizeInBytes);
        using var arena = new GpuGeometryArena12(gpu, blockSize: blockBytes,
            backingMode: GpuGeometryArenaBackingMode.DefaultHeap, maximumBackingBytes: physicalBytes);
        using var queue = new GpuDeletionQueue12(2);
        try
        {
            recorder.BeginFrame();
            var allocation = arena.Upload(recorder.CommandList, queue, new byte[64], []);
            arena.Free(allocation);
            Assert.Equal(1, arena.PendingCopyCount);
            Assert.Equal(0L, arena.ReleaseEmptyBlocks());
            Assert.Equal(physicalBytes, arena.BackingBytes);
            recorder.EndFrame();
            recorder.WaitForGpuIdle();
            queue.Dispose();
            Assert.Equal(0, arena.PendingCopyCount);
            Assert.Equal(physicalBytes, arena.ReleaseEmptyBlocks());
            Assert.Equal(0L, arena.BackingBytes);
        }
        finally
        {
            recorder.Dispose();
            queue.Dispose();
        }
    }

    /// <summary>Cross-thread admission is rejected before the allocator or physical ledger can change.</summary>
    [Fact]
    public void WrongThreadCannotReserveOrReturnRanges()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        using var arena = new GpuGeometryArena12(gpu, blockSize: 128);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { arena.Upload(new byte[16], []); }
            catch (Exception error) { failure = error; }
        });
        thread.Start();
        thread.Join();
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(0, arena.BlockCount);
        Assert.Equal(0L, arena.BackingBytes);
        var allocation = arena.Upload(new byte[16], []);
        long observedBytes = -1;
        failure = null;
        var diagnostics = new Thread(() =>
        {
            try { observedBytes = arena.GetStats().EstimatedBytes; }
            catch (Exception error) { failure = error; }
        });
        diagnostics.Start();
        diagnostics.Join();
        Assert.Null(failure);
        Assert.Equal(arena.BackingBytes, observedBytes);
        arena.Free(allocation);
    }
}
