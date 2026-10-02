using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Vortice.Direct3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

[Trait("Category", GpuTestGuard.Category)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class WorldActorTextureLifetimeTests
{
    [Fact]
    public void GeneratedActorTextureUsesRefcountedUploadAndReleasesBeforeDispatch()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(gpu);
        using var heap = new GpuDescriptorHeapAllocator12(
            gpu, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 16, 2, 8);
        using var queue = new GpuDeletionQueue12(2);
        using var recorder = new GpuCommandRecorder12(gpu);
        var archiveLookups = 0;
        using var resolver = new NifGpuTextureResolver(_ => { archiveLookups++; return null; });
        using var cache = new GpuTextureCache12(gpu, recorder, heap, resolver, queue);
        var pixels = new DecodedTexture
        {
            MipLevels = [new DecodedTextureMipLevel { Width = 1, Height = 1, Pixels = [1, 2, 3, 255] }]
        };
        var entry = cache.GetOrUpload("textures/actor-face.dds", generatedTexture: pixels);
        Assert.Same(entry, cache.GetOrUpload(@"textures\actor-face.dds", generatedTexture: pixels));
        Assert.Equal(0, archiveLookups);
        Assert.Equal(0, cache.PendingResolveCount);
        Assert.Equal(1, cache.PendingUploadCount);
        Assert.Equal(2, entry.RefCount);
        Assert.NotNull(entry.CacheKey); // Generated actor textures are not pinned synthetic singletons.
        cache.Release(entry);
        cache.Release(entry);
        Assert.Equal(0, entry.RefCount);
        Assert.Equal(0, cache.PendingUploadCount);
        Assert.Equal(0, cache.GetStats().EntryCount);
        queue.Tick();
        queue.Tick();
        Assert.Equal(1U, heap.PersistentCount); // Only the shared fallback remains.
    }
}
