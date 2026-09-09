using BethesdaMultitool.Core.Rendering.Level2D;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Rendering.Level2D;

public sealed class OwnedLevel2DSourceTests
{
    [Fact]
    public void ResourceRemainsOpenForRenderingAndIsReleasedOnceWithThePreview()
    {
        var resource = new TrackingResource();
        var inner = new StubSource(resource);
        var source = Assert.IsType<OwnedLevel2DSource>(OwnedLevel2DSource.Create(resource, () => inner));

        Assert.Equal(0, resource.DisposeCount);
        var render = Assert.NotNull(source.Render(Level2DLayer.Floor));
        Assert.Equal(new byte[] { 1, 2, 3, 255 }, render.Rgba);

        source.Dispose();
        source.Dispose();

        Assert.Equal(1, inner.DisposeCount);
        Assert.Equal(1, resource.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => source.Render(Level2DLayer.Floor));
    }

    [Fact]
    public void ThrowingInnerDisposalStillReleasesTheResourceExactlyOnce()
    {
        var resource = new TrackingResource();
        var failure = new InvalidOperationException("source disposal failed");
        var inner = new StubSource(resource, failure);
        var source = Assert.IsType<OwnedLevel2DSource>(OwnedLevel2DSource.Create(resource, () => inner));

        var actual = Assert.Throws<InvalidOperationException>(source.Dispose);
        source.Dispose();

        Assert.Same(failure, actual);
        Assert.Equal(1, inner.DisposeCount);
        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public void FailedOpenReleasesTheResource()
    {
        var resource = new TrackingResource();

        var source = OwnedLevel2DSource.Create(resource, () => null);

        Assert.Null(source);
        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public void ThrowingOpenReleasesTheResourceAndPreservesTheFailure()
    {
        var resource = new TrackingResource();
        var failure = new InvalidDataException("malformed map");

        var actual = Assert.Throws<InvalidDataException>(() =>
            OwnedLevel2DSource.Create(resource, () => throw failure));

        Assert.Same(failure, actual);
        Assert.Equal(1, resource.DisposeCount);
    }

    private sealed class TrackingResource : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class StubSource(TrackingResource resource, Exception? disposeFailure = null) : ILevel2DSource, IDisposable
    {
        public int DisposeCount { get; private set; }

        public IReadOnlyList<Level2DLayer> Layers => [Level2DLayer.Floor];

        public string DisplayName => "Synthetic map";

        public Level2DRender? Render(Level2DLayer layer)
        {
            ObjectDisposedException.ThrowIf(resource.DisposeCount != 0, resource);
            return new Level2DRender(1, 1, [1, 2, 3, 255]);
        }

        public void Dispose()
        {
            DisposeCount++;
            if (disposeFailure is not null)
            {
#pragma warning disable S3877 // Deliberately failing cleanup verifies that the owning wrapper still releases its resource.
                throw disposeFailure;
#pragma warning restore S3877
            }
        }
    }
}
