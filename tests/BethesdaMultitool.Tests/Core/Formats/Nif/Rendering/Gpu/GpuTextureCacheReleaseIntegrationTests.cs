using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Vortice.Direct3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>Exercises actual cache acquisitions and failed final-reference transfers against WARP resources.</summary>
[Trait("Category", GpuTestGuard.Category)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class GpuTextureCacheReleaseIntegrationTests
{
    /// <summary>Aliases consume separate acquisitions, failed eviction retains its slot, and retries cannot evict a replacement.</summary>
    [Fact]
    public void FailedLastReleaseRetainsIdentityAndDoesNotConsumeAReplacementAcquisition()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        using var heap = new GpuDescriptorHeapAllocator12(
            gpu, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 16, 2, 8);
        using var queue = new GpuDeletionQueue12(2);
        using var recorder = new GpuCommandRecorder12(gpu);
        using var resolver = new NifGpuTextureResolver(_ => null);
        using var cache = new GpuTextureCache12(gpu, recorder, heap, resolver, queue);
        var entry = cache.GetOrUpload("textures/test.dds");
        Assert.Same(entry, cache.GetOrUpload("textures\\test.dds"));
        var fallback = cache.WhitePixel;
        Assert.Same(fallback.Texture, entry.Texture);
        cache.Release(fallback);
        cache.Release(null);
        Assert.Equal(2, entry.RefCount);
        cache.Release(entry);
        Assert.Equal(1, entry.RefCount);
        Assert.Equal(0, queue.GetStats().QueueDepth);

        GpuTextureRetirementTests.SetFrameOrdinal(queue, ulong.MaxValue);
        Assert.Throws<OverflowException>(() => cache.Release(entry));
        Assert.Equal(0, entry.RefCount);
        Assert.Equal(0, cache.GetStats().EntryCount);
        Assert.Equal(1, cache.GetStats().Evictions);
        Assert.Equal(2U, heap.PersistentCount);
        var replacement = cache.GetOrUpload("textures/test.dds");
        Assert.NotSame(entry, replacement);
        GpuTextureRetirementTests.SetFrameOrdinal(queue, 0);
        cache.Release(entry);
        cache.Release(entry);
        Assert.Equal(1, replacement.RefCount);
        Assert.Equal(1, cache.GetStats().EntryCount);
        Assert.Equal(1, cache.GetStats().Evictions);
        Assert.Equal(1, queue.GetStats().QueueDepth);
        cache.Release(replacement);
        Assert.Equal(2, cache.GetStats().Evictions);
        queue.Tick();
        Assert.Equal(3U, heap.PersistentCount);
        queue.Tick();
        Assert.Equal(1U, heap.PersistentCount);
        Assert.NotEqual(IntPtr.Zero, fallback.Texture.NativePointer);
        Assert.Equal(fallback.ByteSize, cache.GetStats().EstimatedBytes);
    }

    /// <summary>Cache shutdown still owns an evicted entry when its original caller never retries a rejected transfer.</summary>
    [Fact]
    public void CacheShutdownDrainsRejectedEntryTransfers()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        using var heap = new GpuDescriptorHeapAllocator12(
            gpu, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 16, 2, 8);
        using var queue = new GpuDeletionQueue12(2);
        using var recorder = new GpuCommandRecorder12(gpu);
        using var resolver = new NifGpuTextureResolver(_ => null);
        using var cache = new GpuTextureCache12(gpu, recorder, heap, resolver, queue);
        var entry = cache.GetOrUpload("textures/test.dds");
        GpuTextureRetirementTests.SetFrameOrdinal(queue, ulong.MaxValue);
        Assert.Throws<OverflowException>(() => cache.Release(entry));
        Assert.False(entry.Retirement!.IsTransferred);
        GpuTextureRetirementTests.SetFrameOrdinal(queue, 0);
        cache.Dispose();
        Assert.True(entry.Retirement.IsTransferred);
        queue.Dispose(); // No draw was submitted; cache disposal proved the copy queue drained.
        Assert.Equal(0U, heap.PersistentCount);
        cache.Release(entry); // A late retry must not resubmit to the stopped queue.
        Assert.Equal(0U, heap.PersistentCount);
    }
}
