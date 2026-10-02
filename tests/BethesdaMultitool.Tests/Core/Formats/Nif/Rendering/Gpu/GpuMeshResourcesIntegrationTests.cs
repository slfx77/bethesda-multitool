using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Vortice.Direct3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>Checks actual arena reuse and cache references across the production mesh lifetime.</summary>
[Trait("Category", GpuTestGuard.Category)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class GpuMeshResourcesIntegrationTests
{
    /// <summary>The acquired range and duplicate texture references remain live until proven retirement.</summary>
    /// <param name="commit">Whether ownership passed to a completed mesh or is rolling back materialization.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArenaRangeAndTextureReferencesRetireTogether(bool commit)
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
        using var arena = new GpuGeometryArena12(gpu, blockSize: 65_536);
        var vertices = new byte[64];
        var indices = new byte[6];
        var resources = GpuMeshResources12.Begin(queue, arena.Free, cache.Release);
        var range = resources.AcquireGeometry(() => arena.Upload(vertices, indices));
        var texture = resources.AcquireTexture(() => cache.GetOrUpload("textures/lifetime.dds"));
        Assert.Same(texture, resources.AcquireTexture(() => cache.GetOrUpload("textures/lifetime.dds")));
        if (commit)
        {
            resources.Commit();
            queue.Tick();
            queue.Tick();
            Assert.False(resources.IsReleased);
            Assert.Equal(2, texture.RefCount);
            queue.EnqueueDispose(resources);
        }
        queue.Tick(); // No command list used these UPLOAD bytes; no GPU wait is needed in this case.
        Assert.Equal(2, texture.RefCount);
        Assert.Equal(0UL, arena.ReclamationGeneration);
        var other = arena.Upload(vertices, indices);
        Assert.NotEqual(range.VertexBufferLocation, other.VertexBufferLocation);
        queue.Tick();
        Assert.True(resources.IsReleased);
        Assert.Equal(0, texture.RefCount);
        Assert.Equal(1UL, arena.ReclamationGeneration);
        var reused = arena.Upload(vertices, indices);
        Assert.Equal(range.VertexBufferLocation, reused.VertexBufferLocation);
        Assert.Equal(2U, heap.PersistentCount); // Fallback + deferred texture descriptor.
        queue.Tick();
        queue.Tick();
        Assert.Equal(1U, heap.PersistentCount);
        arena.Free(other);
        arena.Free(reused);
    }
}
