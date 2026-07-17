using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class CollisionReferencePriorityResolverTests
{
    [Fact]
    public void Resolve_GlobalDistanceOrderSpendsCapOnNearestReferenceAndStopsResolvingFarTail()
    {
        var resolver = new CollisionReferencePriorityResolver();
        var mesh = TriangleMesh();
        var calls = new List<string>();
        List<CollisionReferenceCandidate> candidates =
        [
            Candidate("far.nif", formId: 30, distanceSquared: 900, sourceOrder: 0),
            Candidate("middle.nif", formId: 20, distanceSquared: 100, sourceOrder: 1),
            Candidate("near.nif", formId: 10, distanceSquared: 1, sourceOrder: 2),
        ];
        var selected = new List<CollisionWireframeInstance>();

        var result = resolver.Resolve(
            candidates,
            path =>
            {
                calls.Add(path);
                return mesh;
            },
            warmup: null,
            maxWarmupRequests: 0,
            maxLineVertices: 6,
            selected);

        Assert.Equal(["near.nif"], calls);
        Assert.Single(selected);
        Assert.Same(mesh, selected[0].Mesh);
        Assert.Equal(6, result.LineVertexCount);
        Assert.Equal(1, result.ReferencesSelected);
        Assert.Equal(0, result.WarmupRequests);
    }

    [Fact]
    public void Resolve_EqualDistanceUsesFormPathAndSourceTieBreakersDeterministically()
    {
        var resolver = new CollisionReferencePriorityResolver();
        var meshA = TriangleMesh();
        var meshB = TriangleMesh();
        var meshZ = TriangleMesh();
        var meshes = new Dictionary<string, CollisionMesh>(StringComparer.Ordinal)
        {
            ["a.nif"] = meshA,
            ["b.nif"] = meshB,
            ["z.nif"] = meshZ,
        };
        List<CollisionReferenceCandidate> candidates =
        [
            Candidate("z.nif", formId: 2, distanceSquared: 25, sourceOrder: 0),
            Candidate("b.nif", formId: 1, distanceSquared: 25, sourceOrder: 2),
            Candidate("a.nif", formId: 1, distanceSquared: 25, sourceOrder: 3, worldX: 3),
            Candidate("a.nif", formId: 1, distanceSquared: 25, sourceOrder: 1, worldX: 1),
        ];
        var selected = new List<CollisionWireframeInstance>();

        resolver.Resolve(candidates, path => meshes[path], null, 0, 24, selected);
        var firstOrder = selected.Select(static item => (item.Mesh, item.World.Translation.X)).ToArray();
        resolver.Resolve(candidates, path => meshes[path], null, 0, 24, selected);

        Assert.Equal([(meshA, 1f), (meshA, 3f), (meshB, 0f), (meshZ, 0f)], firstOrder);
        Assert.Equal(firstOrder, selected.Select(static item => (item.Mesh, item.World.Translation.X)));
    }

    [Fact]
    public void Resolve_ColdWarmupIsNearestFirstUniqueAndBounded()
    {
        var resolver = new CollisionReferencePriorityResolver();
        var warmedMesh = TriangleMesh();
        var warmups = new List<(string Path, float Priority)>();
        List<CollisionReferenceCandidate> candidates =
        [
            Candidate("third.nif", formId: 4, distanceSquared: 16, sourceOrder: 0),
            Candidate("SHARED.NIF", formId: 2, distanceSquared: 4, sourceOrder: 1),
            Candidate("second.nif", formId: 3, distanceSquared: 9, sourceOrder: 2),
            Candidate("shared.nif", formId: 1, distanceSquared: 1, sourceOrder: 3),
        ];
        var selected = new List<CollisionWireframeInstance>();

        var result = resolver.Resolve(
            candidates,
            static _ => null,
            (path, priority) =>
            {
                warmups.Add((path, priority));
                return path.Equals("second.nif", StringComparison.Ordinal) ? warmedMesh : null;
            },
            maxWarmupRequests: 2,
            maxLineVertices: 60,
            selected);

        Assert.Equal([("shared.nif", 1f), ("second.nif", 9f)], warmups);
        Assert.Equal(2, result.WarmupRequests);
        Assert.Single(selected);
        Assert.Same(warmedMesh, selected[0].Mesh);
    }

    [Fact]
    public void WarmNearest_WalkPromotionIsNearestFirstUniqueAndBounded()
    {
        var resolver = new CollisionReferencePriorityResolver();
        var warmups = new List<(string Path, float Priority)>();
        List<CollisionReferenceCandidate> candidates =
        [
            Candidate("far.nif", formId: 30, distanceSquared: 900, sourceOrder: 0),
            Candidate("SHARED.NIF", formId: 20, distanceSquared: 4, sourceOrder: 1),
            Candidate("second.nif", formId: 10, distanceSquared: 9, sourceOrder: 2),
            Candidate("shared.nif", formId: 5, distanceSquared: 1, sourceOrder: 3),
        ];

        var requests = resolver.WarmNearest(
            candidates,
            (path, priority) =>
            {
                warmups.Add((path, priority));
                return null;
            },
            maxWarmupRequests: 2);

        Assert.Equal(2, requests);
        Assert.Equal([("shared.nif", 1f), ("second.nif", 9f)], warmups);
    }

    [Fact]
    public void Resolve_InvalidNearMeshDoesNotConsumeWholeTriangleBudget()
    {
        var resolver = new CollisionReferencePriorityResolver();
        var invalid = new CollisionMesh([Vector3.Zero], [0, 1, 2]);
        var valid = TriangleMesh();
        var meshes = new Dictionary<string, CollisionMesh>
        {
            ["invalid.nif"] = invalid,
            ["valid.nif"] = valid,
        };
        List<CollisionReferenceCandidate> candidates =
        [
            Candidate("valid.nif", formId: 2, distanceSquared: 4, sourceOrder: 0),
            Candidate("invalid.nif", formId: 1, distanceSquared: 1, sourceOrder: 1),
        ];
        var selected = new List<CollisionWireframeInstance>();

        var result = resolver.Resolve(candidates, path => meshes[path], null, 0, 6, selected);

        Assert.Single(selected);
        Assert.Same(valid, selected[0].Mesh);
        Assert.Equal(6, result.LineVertexCount);
    }

    private static CollisionReferenceCandidate Candidate(
        string path,
        uint formId,
        float distanceSquared,
        int sourceOrder,
        float worldX = 0)
        => new(path, formId, distanceSquared, Matrix4x4.CreateTranslation(worldX, 0, 0), sourceOrder);

    private static CollisionMesh TriangleMesh()
        => new(
            [new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)],
            [0, 1, 2]);
}
