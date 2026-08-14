using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Walk;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Walk;

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
            Candidate("far.nif", 30, 900, 0),
            Candidate("middle.nif", 20, 100, 1),
            Candidate("near.nif", 10, 1, 2)
        ];
        var selected = new List<CollisionWireframeInstance>();

        var result = resolver.Resolve(
            candidates,
            (path, _) =>
            {
                calls.Add(path);
                return Resolved(mesh);
            },
            null,
            0,
            6,
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
            ["z.nif"] = meshZ
        };
        List<CollisionReferenceCandidate> candidates =
        [
            Candidate("z.nif", 2, 25, 0),
            Candidate("b.nif", 1, 25, 2),
            Candidate("a.nif", 1, 25, 3, 3),
            Candidate("a.nif", 1, 25, 1, 1)
        ];
        var selected = new List<CollisionWireframeInstance>();

        resolver.Resolve(candidates, (path, _) => Resolved(meshes[path]), null, 0, 24, selected);
        var firstOrder = selected.Select(static item => (item.Mesh, item.World.Translation.X)).ToArray();
        resolver.Resolve(candidates, (path, _) => Resolved(meshes[path]), null, 0, 24, selected);

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
            Candidate("third.nif", 4, 16, 0),
            Candidate("SHARED.NIF", 2, 4, 1),
            Candidate("second.nif", 3, 9, 2),
            Candidate("shared.nif", 1, 1, 3)
        ];
        var selected = new List<CollisionWireframeInstance>();

        var result = resolver.Resolve(
            candidates,
            static (_, _) => CollisionMeshResolution.Unresolved,
            (path, _, priority) =>
            {
                warmups.Add((path, priority));
                return path.Equals("second.nif", StringComparison.Ordinal)
                    ? Resolved(warmedMesh)
                    : CollisionMeshResolution.Unresolved;
            },
            2,
            60,
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
            Candidate("far.nif", 30, 900, 0),
            Candidate("SHARED.NIF", 20, 4, 1),
            Candidate("second.nif", 10, 9, 2),
            Candidate("shared.nif", 5, 1, 3)
        ];

        var requests = resolver.WarmNearest(
            candidates,
            (path, _, priority) =>
            {
                warmups.Add((path, priority));
                return CollisionMeshResolution.Unresolved;
            },
            2);

        Assert.Equal(2, requests);
        Assert.Equal([("shared.nif", 1f), ("second.nif", 9f)], warmups);
    }

    [Fact]
    public void Resolve_KnownNullSkipsWarmupWithoutConsumingItsBudget()
    {
        var resolver = new CollisionReferencePriorityResolver();
        var warmedMesh = TriangleMesh();
        var warmups = new List<string>();
        List<CollisionReferenceCandidate> candidates =
        [
            Candidate("known-null.nif", 1, 1, 0),
            Candidate("cold.nif", 2, 4, 1)
        ];
        var selected = new List<CollisionWireframeInstance>();

        var result = resolver.Resolve(
            candidates,
            (path, _) => path == "known-null.nif"
                ? CollisionMeshResolution.From(CollisionBuildResult.None)
                : CollisionMeshResolution.Unresolved,
            (path, _, _) =>
            {
                warmups.Add(path);
                return Resolved(warmedMesh);
            },
            1,
            6,
            selected);

        Assert.Equal(["cold.nif"], warmups);
        Assert.Equal(1, result.WarmupRequests);
        Assert.Single(selected);
        Assert.Same(warmedMesh, selected[0].Mesh);
    }

    [Fact]
    public void Resolve_TerminalUnavailableRetainsColdFallbackStateWithoutConsumingWarmupBudget()
    {
        var resolver = new CollisionReferencePriorityResolver();
        var warmedMesh = TriangleMesh();
        var warmups = new List<string>();
        List<CollisionReferenceCandidate> candidates =
        [
            Candidate("terminal.nif", 1, 1, 0),
            Candidate("cold.nif", 2, 4, 1)
        ];
        var selected = new List<CollisionWireframeInstance>();

        var result = resolver.Resolve(
            candidates,
            (path, _) => path == "terminal.nif"
                ? CollisionMeshResolution.TerminalUnavailable
                : CollisionMeshResolution.Unresolved,
            (path, _, _) =>
            {
                warmups.Add(path);
                return Resolved(warmedMesh);
            },
            1,
            6,
            selected);

        Assert.Equal(["cold.nif"], warmups);
        Assert.Equal(1, result.WarmupRequests);
        Assert.Single(selected);
        Assert.Same(warmedMesh, selected[0].Mesh);
    }

    [Fact]
    public void Resolve_PreservesCandidateCategoryThroughResolverAndWarmup()
    {
        var resolver = new CollisionReferencePriorityResolver();
        var mesh = TriangleMesh();
        var resolvedCategories = new List<PlacedObjectCategory>();
        var warmedCategories = new List<PlacedObjectCategory>();
        List<CollisionReferenceCandidate> candidates =
        [
            Candidate(
                "category-effect.nif",
                1,
                1,
                0,
                category: PlacedObjectCategory.Effects)
        ];
        var selected = new List<CollisionWireframeInstance>();

        resolver.Resolve(
            candidates,
            (_, category) =>
            {
                resolvedCategories.Add(category);
                return CollisionMeshResolution.Unresolved;
            },
            (_, category, _) =>
            {
                warmedCategories.Add(category);
                return Resolved(mesh, CollisionMeshSource.AuthoredHavok);
            },
            1,
            6,
            selected);

        Assert.Equal([PlacedObjectCategory.Effects], resolvedCategories);
        Assert.Equal([PlacedObjectCategory.Effects], warmedCategories);
        Assert.Single(selected);
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
            ["valid.nif"] = valid
        };
        List<CollisionReferenceCandidate> candidates =
        [
            Candidate("valid.nif", 2, 4, 0),
            Candidate("invalid.nif", 1, 1, 1)
        ];
        var selected = new List<CollisionWireframeInstance>();

        var result = resolver.Resolve(
            candidates,
            (path, _) => Resolved(meshes[path]),
            null,
            0,
            6,
            selected);

        Assert.Single(selected);
        Assert.Same(valid, selected[0].Mesh);
        Assert.Equal(6, result.LineVertexCount);
    }

    private static CollisionReferenceCandidate Candidate(
        string path,
        uint formId,
        float distanceSquared,
        int sourceOrder,
        float worldX = 0,
        PlacedObjectCategory category = PlacedObjectCategory.Unknown)
    {
        return new CollisionReferenceCandidate(
            path,
            formId,
            distanceSquared,
            Matrix4x4.CreateTranslation(worldX, 0, 0),
            sourceOrder,
            category);
    }

    private static CollisionMesh TriangleMesh()
    {
        return new CollisionMesh(
            [new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0)],
            [0, 1, 2]);
    }

    private static CollisionMeshResolution Resolved(
        CollisionMesh mesh,
        CollisionMeshSource source = CollisionMeshSource.VisualFallback)
    {
        return CollisionMeshResolution.From(new CollisionBuildResult(mesh, source));
    }
}
