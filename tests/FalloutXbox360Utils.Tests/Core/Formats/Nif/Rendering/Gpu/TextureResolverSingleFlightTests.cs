using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering.Gpu;

public sealed class TextureResolverSingleFlightTests
{
    [Fact]
    public async Task NifGpuTextureResolver_ConcurrentColdMisses_RunOneLoadForPath()
    {
        var payload = CreateGpuPayload(0x42);
        var loadCalls = 0;
        using var loadStarted = new ManualResetEventSlim(false);
        using var releaseLoad = new ManualResetEventSlim(false);
        using var start = new ManualResetEventSlim(false);
        using var resolver = new NifGpuTextureResolver(path =>
        {
            Assert.Equal(@"textures\foo.dds", path);
            Interlocked.Increment(ref loadCalls);
            loadStarted.Set();
            Assert.True(releaseLoad.Wait(TimeSpan.FromSeconds(5)));
            return payload;
        });

        var tasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                return resolver.GetTexture(@"Textures/Foo.dds");
            }))
            .ToArray();

        start.Set();
        Assert.True(loadStarted.Wait(TimeSpan.FromSeconds(5)));
        await Task.Delay(100);
        releaseLoad.Set();

        var results = await Task.WhenAll(tasks);

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
        var texture = TestTextures.Single(1, 2, 3, 255);
        var loadCalls = 0;
        using var loadStarted = new ManualResetEventSlim(false);
        using var releaseLoad = new ManualResetEventSlim(false);
        using var start = new ManualResetEventSlim(false);
        using var resolver = new NifTextureResolver(path =>
        {
            Assert.Equal(@"textures\bar.dds", path);
            Interlocked.Increment(ref loadCalls);
            loadStarted.Set();
            Assert.True(releaseLoad.Wait(TimeSpan.FromSeconds(5)));
            return texture;
        });

        var tasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                return resolver.GetTexture(@"Textures/Bar.dds");
            }))
            .ToArray();

        start.Set();
        Assert.True(loadStarted.Wait(TimeSpan.FromSeconds(5)));
        await Task.Delay(100);
        releaseLoad.Set();

        var results = await Task.WhenAll(tasks);

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