using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>Checks actual terrain range retirement after native copies complete.</summary>
[Collection(SequentialIntegrationGroup.Name)]
public sealed class GpuTerrainArenaLifetimeTests
{
    /// <summary>A stopped retirement queue rejects the upload before ranges, backing, or staging are acquired.</summary>
    [Fact]
    [Trait("Category", GpuTestGuard.Category)]
    public void StoppedQueueRejectsUploadWithoutAllocatingTerrainResources()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        using var recorder = new GpuCommandRecorder12(gpu);
        using var arena = new GpuTerrainArena12(gpu, blockSize: 65_536);
        using var queue = new GpuDeletionQueue12(2);
        queue.Dispose();
        try
        {
            recorder.BeginFrame();
            Assert.Throws<InvalidOperationException>(() => arena.Upload(
                recorder.CommandList, queue, new byte[17], new byte[3]));
            Assert.Equal(0L, arena.AllocatedBytes);
            Assert.Equal(0, arena.BlockCount);
            Assert.Equal(0L, arena.GetStats().EstimatedBytes);
            Assert.Equal(0L, arena.StagingRing.CapacityBytes);
            Assert.Equal(0L, arena.StagingRing.LiveBytes);
            Assert.Equal(0, queue.GetStats().QueueDepth);
        }
        finally { recorder.Dispose(); }
    }

    /// <summary>Failed callback acquisition stays queued, while successful capture transfers the exact range before native work.</summary>
    [Fact]
    [Trait("Category", GpuTestGuard.Category)]
    public void CallbackFailureRetainsUnpublishedRangeUntilCopyQueueRetirement()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        using var recorder = new GpuCommandRecorder12(gpu);
        using var arena = new GpuTerrainArena12(gpu, blockSize: 65_536);
        using var queue = new GpuDeletionQueue12(2);
        try
        {
            recorder.BeginFrame();
            TerrainAllocation12 rejected = default;
            var expected = new InvalidOperationException("Reject provisional ownership.");
            var failure = Assert.Throws<InvalidOperationException>(() => arena.Upload(
                recorder.CommandList, queue, new byte[17], new byte[3], "rejected", provisional =>
                {
                    rejected = provisional;
                    throw expected;
                }));
            Assert.Same(expected, failure);
            Assert.Equal(0UL, rejected.VertexGpuAddress);
            Assert.Equal(0UL, rejected.BlendGpuAddress);
            Assert.Equal(48L, rejected.Allocation.AlignedSize);
            Assert.Equal(48L, arena.AllocatedBytes);
            Assert.Equal(0, arena.BlockCount);
            Assert.Equal(0L, arena.StagingRing.CapacityBytes);
            Assert.Equal(1, queue.GetStats().QueueDepth);

            TerrainAllocation12 retained = default;
            var completed = arena.Upload(recorder.CommandList, queue, new byte[17], new byte[3],
                "retained", provisional => retained = provisional);
            Assert.Equal(completed.Allocation, retained.Allocation);
            Assert.Equal(0UL, retained.VertexGpuAddress);
            Assert.Equal(0UL, retained.BlendGpuAddress);
            Assert.NotEqual(0UL, completed.VertexGpuAddress);
            Assert.Equal(completed.VertexGpuAddress + 32UL, completed.BlendGpuAddress);
            Assert.Equal(17U, retained.VertexBytes);
            Assert.Equal(3U, retained.BlendBytes);
            Assert.Equal("retained", retained.DebugTag);
            Assert.NotEqual(rejected.Allocation.Offset, retained.Allocation.Offset);
            Assert.Equal(96L, arena.AllocatedBytes);
            Assert.Equal(48L, arena.StagingRing.LiveBytes);
            Assert.Equal(2, queue.GetStats().QueueDepth);

            recorder.EndFrame();
            recorder.WaitForGpuIdle();
            queue.Dispose();
            Assert.Equal(48L, arena.AllocatedBytes);
            Assert.Equal(0L, arena.StagingRing.LiveBytes);
            Assert.Equal(0, queue.GetStats().QueueDepth);
            // The provisional token already names the complete owned range; no final GPU view is
            // needed to release it after a later cell-construction failure.
            arena.Free(retained);
            Assert.Equal(0L, arena.AllocatedBytes);
        }
        finally
        {
            recorder.Dispose();
            queue.Dispose();
        }
    }

    /// <summary>Staging release handles survive thread rejection and return each FIFO reservation exactly once.</summary>
    [Fact]
    [Trait("Category", GpuTestGuard.Category)]
    public void StagingReleaseRetriesOnOwnerThreadWithoutConsumingTheNextReservation()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        using var ring = new GpuTerrainStagingRing12(gpu);
        Assert.True(ring.TryReserve(17, out var first));
        Assert.True(ring.TryReserve(3, out var second));
        Assert.Same(first.Resource, second.Resource);
        Assert.Equal(0UL, first.Offset);
        Assert.Equal(32UL, second.Offset);
        Assert.Equal(35L, ring.LiveBytes);
        Assert.Equal(2L, ring.ServedCount);
        Assert.Equal(GpuTerrainStagingRing12.MinCapacityBytes, ring.CapacityBytes);

        Exception? failure = null;
        var worker = new Thread(() => failure = Record.Exception(first.Release.Dispose)) { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "The rejected staging release did not return.");
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Equal(35L, ring.LiveBytes);
        first.Release.Dispose();
        Assert.Equal(18L, ring.LiveBytes);
        first.Release.Dispose();
        Assert.Equal(18L, ring.LiveBytes);
        second.Release.Dispose();
        Assert.Equal(0L, ring.LiveBytes);
        Assert.Equal(0, ring.GetStats().EntryCount);
        ring.Dispose();
        second.Release.Dispose();
        Assert.False(ring.TryReserve(16, out _));
    }

    /// <summary>Owner-thread shutdown may precede retired tickets; their late disposal cannot double-release native backing.</summary>
    [Fact]
    [Trait("Category", GpuTestGuard.Category)]
    public void GpuIdleShutdownAllowsLateUploadAndRangeRetirement()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        using var recorder = new GpuCommandRecorder12(gpu);
        using var arena = new GpuTerrainArena12(gpu, blockSize: 65_536);
        using var queue = new GpuDeletionQueue12(2);
        try
        {
            recorder.BeginFrame();
            var allocation = arena.Upload(recorder.CommandList, queue, new byte[17], new byte[3]);
            using var free = arena.DeferredFreeHandle(allocation);
            recorder.EndFrame();
            recorder.WaitForGpuIdle();

            Exception? failure = null;
            var worker = new Thread(() => failure = Record.Exception(arena.Dispose)) { IsBackground = true };
            worker.Start();
            Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "The rejected terrain shutdown did not return.");
            Assert.IsType<InvalidOperationException>(failure);
            Assert.True(arena.GetStats().EstimatedBytes > 0);
            Assert.Equal(48L, arena.StagingRing.LiveBytes);

            arena.Dispose();
            Assert.Equal(0L, arena.GetStats().EstimatedBytes);
            Assert.Equal(0, arena.BlockCount);
            Assert.Equal(0L, arena.StagingRing.CapacityBytes);
            queue.Dispose();
            free.Dispose();
            free.Dispose();
            arena.Dispose();
            Assert.Equal(0, queue.GetStats().QueueDepth);
        }
        finally
        {
            recorder.Dispose();
            queue.Dispose();
        }
    }

    /// <summary>A rejected foreign-thread release retains the range for owner-thread retry and cannot double-return it.</summary>
    [Fact]
    [Trait("Category", GpuTestGuard.Category)]
    public void DeferredRangeReleaseRetriesAfterWrongThreadAndTransfersOnceToStoppedQueue()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        using var recorder = new GpuCommandRecorder12(gpu);
        using var arena = new GpuTerrainArena12(gpu, blockSize: 65_536);
        using var queue = new GpuDeletionQueue12(2);
        try
        {
            recorder.BeginFrame();
            var allocation = arena.Upload(recorder.CommandList, queue, new byte[17], new byte[3]);
            recorder.EndFrame();
            recorder.WaitForGpuIdle();
            queue.Dispose(); // Retire staging; subsequent admission releases synchronously.
            var chargedBytes = allocation.Allocation.AlignedSize;
            Assert.Equal(chargedBytes, arena.AllocatedBytes);
            using var handle = arena.DeferredFreeHandle(allocation);

            Exception? failure = null;
            var worker = new Thread(() => failure = Record.Exception(handle.Dispose)) { IsBackground = true };
            worker.Start();
            Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "The rejected terrain release did not return.");
            Assert.IsType<InvalidOperationException>(failure);
            Assert.Equal(chargedBytes, arena.AllocatedBytes);
            queue.EnqueueDispose(handle);
            Assert.Equal(0L, arena.AllocatedBytes);
            queue.EnqueueDispose(handle);
            handle.Dispose();
            Assert.Equal(0L, arena.AllocatedBytes);
            Assert.Equal(0, queue.GetStats().QueueDepth);
        }
        finally
        {
            // Native resources remain owned until submitted or abandoned recording has retired.
            recorder.Dispose();
            queue.Dispose();
        }
    }
}
