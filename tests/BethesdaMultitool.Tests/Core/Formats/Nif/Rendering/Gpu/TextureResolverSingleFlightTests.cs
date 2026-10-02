using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

public sealed class TextureResolverSingleFlightTests
{
    [Fact]
    public void NifGpuTextureResolver_GeneratedSceneTexturePrecedesArchiveSources()
    {
        var texture = TestTextures.Single(12, 34, 56, 78);
        var generated = new Dictionary<string, DecodedTexture>(StringComparer.OrdinalIgnoreCase)
        {
            [@"textures\facegen_egt\actor.dds"] = texture
        };
        using var resolver = new NifGpuTextureResolver(Array.Empty<string>(), generated);

        var payload = resolver.GetTexture(@"Textures/FaceGen_EGT/ACTOR.DDS");

        var resolved = Assert.IsType<GpuTexturePayload>(payload);
        Assert.Equal(GpuTexturePayloadFormat.Rgba8, resolved.Format);
        Assert.Same(texture.Pixels, resolved.MipLevels[0].Bytes);
    }

    [Fact]
    public async Task NifGpuTextureResolver_ConcurrentColdMisses_RunOneLoadForPath()
    {
        const int callerCount = 32;
        var payload = CreateGpuPayload(0x42);
        var loadCalls = 0;
        using var loadStarted = new ManualResetEventSlim(false);
        using var releaseLoad = new ManualResetEventSlim(false);
        using var resolver = new NifGpuTextureResolver(path =>
        {
            Assert.Equal(@"textures\foo.dds", path);
            Interlocked.Increment(ref loadCalls);
            loadStarted.Set();
            Assert.True(releaseLoad.Wait(TimeSpan.FromSeconds(5)));
            return payload;
        });

        var tasks = Enumerable.Range(0, callerCount)
            .Select(_ => Task.Factory.StartNew(
                () => resolver.GetTexture(@"Textures/Foo.dds"),
                TestContext.Current.CancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();
        var allLoads = Task.WhenAll(tasks);

        try
        {
            Assert.True(loadStarted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            // Hits are recorded before waiting on the in-flight value: every other caller
            // has reached the cache while the first load is still held. Dedicated workers
            // avoid thread-pool starvation. A duplicate load also releases this wait, then
            // fails the load-count assertion below.
            Assert.True(SpinWait.SpinUntil(
                () => resolver.CacheHits == callerCount - 1 || Volatile.Read(ref loadCalls) > 1,
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releaseLoad.Set();
            await allLoads;
        }

        var results = await allLoads;

        Assert.Equal(1, loadCalls);
        Assert.Equal(1, resolver.CacheMisses);
        Assert.All(results, result => Assert.Same(payload, result));
    }

    [Fact]
    public void NifGpuTextureResolver_Release_RemovesOnlyPositivePayloads()
    {
        var positiveCalls = 0;
        using (var resolver = new NifGpuTextureResolver(_ =>
               {
                   var call = Interlocked.Increment(ref positiveCalls);
                   return CreateGpuPayload((byte)call);
               }))
        {
            var first = resolver.GetTexture(@"textures\foo.dds");
            resolver.Release(@"textures\foo.dds");
            var second = resolver.GetTexture(@"textures\foo.dds");

            Assert.NotSame(first, second);
            Assert.Equal(2, positiveCalls);
        }

        var negativeCalls = 0;
        using (var resolver = new NifGpuTextureResolver(_ =>
               {
                   Interlocked.Increment(ref negativeCalls);
                   return null;
               }))
        {
            Assert.Null(resolver.GetTexture(@"textures\missing.dds"));
            resolver.Release(@"textures\missing.dds");
            Assert.Null(resolver.GetTexture(@"textures\missing.dds"));

            Assert.Equal(1, negativeCalls);
        }
    }

    [Fact]
    public async Task NifTextureResolver_ConcurrentColdMisses_RunOneLoadForPath()
    {
        const int callerCount = 32;
        var texture = TestTextures.Single(1, 2, 3, 255);
        var loadCalls = 0;
        using var loadStarted = new ManualResetEventSlim(false);
        using var releaseLoad = new ManualResetEventSlim(false);
        using var resolver = new NifTextureResolver(path =>
        {
            Assert.Equal(@"textures\bar.dds", path);
            Interlocked.Increment(ref loadCalls);
            loadStarted.Set();
            Assert.True(releaseLoad.Wait(TimeSpan.FromSeconds(5)));
            return texture;
        });

        var tasks = Enumerable.Range(0, callerCount)
            .Select(_ => Task.Factory.StartNew(
                () => resolver.GetTexture(@"Textures/Bar.dds"),
                TestContext.Current.CancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default))
            .ToArray();
        var allLoads = Task.WhenAll(tasks);

        try
        {
            Assert.True(loadStarted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            // Wait for all other dedicated callers to reach the held in-flight cache entry.
            Assert.True(SpinWait.SpinUntil(
                () => resolver.CacheHits == callerCount - 1 || Volatile.Read(ref loadCalls) > 1,
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releaseLoad.Set();
            await allLoads;
        }

        var results = await allLoads;

        Assert.Equal(1, loadCalls);
        Assert.Equal(1, resolver.CacheMisses);
        Assert.All(results, result => Assert.Same(texture, result));
    }

    [Fact]
    public void NifTextureResolver_FaultedLoad_DoesNotPoisonCache()
    {
        var texture = TestTextures.Single(5, 6, 7, 255);
        var loadCalls = 0;
        using var resolver = new NifTextureResolver(_ =>
        {
            if (Interlocked.Increment(ref loadCalls) == 1)
            {
                throw new InvalidOperationException("synthetic load failure");
            }

            return texture;
        });

        Assert.Throws<InvalidOperationException>(() => resolver.GetTexture(@"textures\retry.dds"));
        Assert.Same(texture, resolver.GetTexture(@"textures\retry.dds"));
        Assert.Equal(2, loadCalls);
    }

    private static GpuTexturePayload CreateGpuPayload(byte marker)
    {
        return new GpuTexturePayload(
            GpuTexturePayloadFormat.Rgba8,
            1,
            1,
            [new GpuTextureMipPayload(1, 1, [marker, marker, marker, 255])]);
    }
}
