using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Walk;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Walk;

public sealed class CollisionWireframeGeometryCacheTests
{
    [Fact]
    public void Resolve_UnchangedFrame_ReusesPersistentBufferWithoutRebuildOrUpload()
    {
        var harness = new BufferHarness();
        using var cache = harness.CreateCache();
        var mesh = TriangleMesh();
        CollisionWireframeInstance[] instances = [new(mesh, Matrix4x4.Identity)];

        var first = cache.Resolve(instances);
        var second = cache.Resolve([new CollisionWireframeInstance(mesh, Matrix4x4.Identity)]);

        Assert.True(first.Rebuilt);
        Assert.True(first.Uploaded);
        Assert.False(second.Rebuilt);
        Assert.False(second.Uploaded);
        Assert.Same(first.Buffer, second.Buffer);
        Assert.Equal(1, cache.BuildCount);
        Assert.Equal(1, cache.UploadCount);
        Assert.Equal(1, harness.UploadCalls);
        Assert.Equal(0, harness.RetireCalls);
        Assert.Equal(6, second.LineVertexCount);
        Assert.Equal(1, second.ReferencesDrawn);
        Assert.Equal(
            [
                new Vector3(0, 0, 0), new Vector3(1, 0, 0),
                new Vector3(1, 0, 0), new Vector3(0, 1, 0),
                new Vector3(0, 1, 0), new Vector3(0, 0, 0)
            ],
            harness.UploadedVertices[0]);
    }

    [Fact]
    public void Resolve_GeometryOrPlacementContentChange_RebuildsUploadsAndRetiresPriorBuffer()
    {
        var harness = new BufferHarness();
        using var cache = harness.CreateCache();
        var mesh = TriangleMesh();

        var first = cache.Resolve([new CollisionWireframeInstance(mesh, Matrix4x4.Identity)]);
        mesh.Positions[0] = new Vector3(2, 0, 0); // same mesh object, changed content
        var geometryChanged = cache.Resolve([new CollisionWireframeInstance(mesh, Matrix4x4.Identity)]);
        var placementChanged = cache.Resolve(
            [new CollisionWireframeInstance(mesh, Matrix4x4.CreateTranslation(10, 20, 30))]);
        mesh.Triangles[1] = 2; // index content changes even though the array identity does not
        mesh.Triangles[2] = 1;
        var indicesChanged = cache.Resolve(
            [new CollisionWireframeInstance(mesh, Matrix4x4.CreateTranslation(10, 20, 30))]);

        Assert.True(geometryChanged.Rebuilt);
        Assert.True(placementChanged.Rebuilt);
        Assert.True(indicesChanged.Rebuilt);
        Assert.NotSame(first.Buffer, geometryChanged.Buffer);
        Assert.NotSame(geometryChanged.Buffer, placementChanged.Buffer);
        Assert.NotSame(placementChanged.Buffer, indicesChanged.Buffer);
        Assert.Equal(4, cache.BuildCount);
        Assert.Equal(4, cache.UploadCount);
        Assert.Equal(4, harness.UploadCalls);
        Assert.Equal(3, harness.RetireCalls);
        Assert.All(harness.Buffers.Take(3), static buffer => Assert.True(buffer.Retired));
        Assert.False(harness.Buffers[3].Retired);
    }

    [Fact]
    public void Invalidate_RetiresBufferAndForcesIdenticalContentToUploadAgain()
    {
        var harness = new BufferHarness();
        using var cache = harness.CreateCache();
        var instances = new[] { new CollisionWireframeInstance(TriangleMesh(), Matrix4x4.Identity) };

        var first = cache.Resolve(instances);
        cache.Invalidate();
        var rebuilt = cache.Resolve(instances);

        Assert.True(first.Buffer!.Retired);
        Assert.True(rebuilt.Rebuilt);
        Assert.True(rebuilt.Uploaded);
        Assert.NotSame(first.Buffer, rebuilt.Buffer);
        Assert.Equal(2, harness.UploadCalls);
        Assert.Equal(1, harness.RetireCalls);
    }

    [Fact]
    public void Resolve_WarmSetChange_InvalidatesOrderedContentKey()
    {
        var harness = new BufferHarness();
        using var cache = harness.CreateCache();
        var mesh = TriangleMesh();
        var first = new CollisionWireframeInstance(mesh, Matrix4x4.Identity);
        var second = new CollisionWireframeInstance(mesh, Matrix4x4.CreateTranslation(5, 0, 0));

        var oneWarm = cache.Resolve([first]);
        var twoWarm = cache.Resolve([first, second]);
        var reordered = cache.Resolve([second, first]);

        Assert.Equal(6, oneWarm.LineVertexCount);
        Assert.Equal(12, twoWarm.LineVertexCount);
        Assert.True(twoWarm.Rebuilt);
        Assert.True(reordered.Rebuilt);
        Assert.Equal(3, harness.UploadCalls);
        Assert.Equal(2, harness.RetireCalls);
    }

    [Fact]
    public void Resolve_EmptyFrame_RetiresPriorGeometryAndThenCachesTheEmptyState()
    {
        var harness = new BufferHarness();
        using var cache = harness.CreateCache();
        var populated = cache.Resolve(
            [new CollisionWireframeInstance(TriangleMesh(), Matrix4x4.Identity)]);

        var firstEmpty = cache.Resolve([]);
        var secondEmpty = cache.Resolve([]);

        Assert.True(populated.Buffer!.Retired);
        Assert.True(firstEmpty.Rebuilt);
        Assert.False(firstEmpty.Uploaded);
        Assert.Null(firstEmpty.Buffer);
        Assert.Equal(0, firstEmpty.LineVertexCount);
        Assert.False(secondEmpty.Rebuilt);
        Assert.False(secondEmpty.Uploaded);
        Assert.Equal(1, harness.UploadCalls);
        Assert.Equal(1, harness.RetireCalls);
    }

    [Fact]
    public void Resolve_VertexCap_StopsAtWholeTriangleAndIgnoresNonRenderedTail()
    {
        var harness = new BufferHarness();
        using var cache = harness.CreateCache(8); // effective whole-triangle cap = 6
        var firstMesh = TriangleMesh();
        var cappedTail = TriangleMesh();
        CollisionWireframeInstance[] instances =
        [
            new(firstMesh, Matrix4x4.Identity),
            new(cappedTail, Matrix4x4.CreateTranslation(5, 0, 0))
        ];

        var first = cache.Resolve(instances);
        cappedTail.Positions[0] = new Vector3(999, 999, 999);
        var unchangedRenderedPrefix = cache.Resolve(instances);

        Assert.Equal(6, first.LineVertexCount);
        Assert.Equal(1, first.ReferencesDrawn);
        Assert.False(unchangedRenderedPrefix.Rebuilt);
        Assert.Equal(1, harness.UploadCalls);
    }

    private static CollisionMesh TriangleMesh()
    {
        return new CollisionMesh(
            [new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)],
            [0, 1, 2]);
    }

    private sealed class BufferHarness
    {
        public int UploadCalls { get; private set; }
        public int RetireCalls { get; private set; }
        public List<Vector3[]> UploadedVertices { get; } = [];
        public List<FakeBuffer> Buffers { get; } = [];

        public CollisionWireframeGeometryCache<FakeBuffer> CreateCache(int maxLineVertices = 500_000)
        {
            return new CollisionWireframeGeometryCache<FakeBuffer>(Upload, Retire, maxLineVertices);
        }

        private FakeBuffer Upload(Vector3[] vertices, int count)
        {
            UploadCalls++;
            UploadedVertices.Add(vertices.AsSpan(0, count).ToArray());
            var buffer = new FakeBuffer(UploadCalls);
            Buffers.Add(buffer);
            return buffer;
        }

        private void Retire(FakeBuffer buffer)
        {
            RetireCalls++;
            buffer.Retired = true;
        }
    }

    private sealed record FakeBuffer(int Id)
    {
        public bool Retired { get; set; }
    }
}