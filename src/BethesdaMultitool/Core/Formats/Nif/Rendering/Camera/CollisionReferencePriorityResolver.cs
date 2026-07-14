using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     One collision-overlay placement before its mesh is resolved. <see cref="DistanceSquared" />
///     is XY distance from the camera; <see cref="SourceOrder" /> is the deterministic final tie-break
///     inherited from the spatial index and the cell's placed-reference list.
/// </summary>
internal readonly record struct CollisionReferenceCandidate(
    string ModelPath,
    uint FormId,
    float DistanceSquared,
    Matrix4x4 World,
    int SourceOrder);

internal readonly record struct CollisionReferencePriorityResult(
    int LineVertexCount,
    int ReferencesSelected,
    int WarmupRequests);

/// <summary>
///     Resolves collision placements in one global, deterministic nearest-first order. Resolution
///     stops as soon as the whole-triangle line budget is full, so far meshes cannot consume the
///     collision LRU or displace nearer cages that have not yet been visited. Cold paths may be
///     offered to a bounded warmup callback; duplicate model paths consume only one offer per frame.
/// </summary>
internal sealed class CollisionReferencePriorityResolver
{
    private readonly HashSet<string> _warmupPathScratch = new(StringComparer.OrdinalIgnoreCase);

    public CollisionReferencePriorityResult Resolve(
        List<CollisionReferenceCandidate> candidates,
        Func<string, CollisionMesh?> resolver,
        Func<string, float, CollisionMesh?>? warmup,
        int maxWarmupRequests,
        int maxLineVertices,
        List<CollisionWireframeInstance> destination)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(destination);
        if (maxWarmupRequests < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxWarmupRequests));
        }
        if (maxLineVertices < 6)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLineVertices), "Must fit at least one triangle.");
        }

        destination.Clear();
        _warmupPathScratch.Clear();
        candidates.Sort(CompareCandidates);

        var effectiveMaxLineVertices = maxLineVertices - maxLineVertices % 6;
        var lineVertexCount = 0;
        var warmupRequests = 0;
        foreach (var candidate in candidates)
        {
            if (lineVertexCount >= effectiveMaxLineVertices) break;

            var mesh = resolver(candidate.ModelPath);
            if (mesh is null && warmup is not null && warmupRequests < maxWarmupRequests &&
                _warmupPathScratch.Add(candidate.ModelPath))
            {
                warmupRequests++;
                mesh = warmup(candidate.ModelPath, candidate.DistanceSquared);
            }
            if (mesh is null) continue;

            var selectedLineVertices = CountValidLineVertices(
                mesh, effectiveMaxLineVertices - lineVertexCount);
            if (selectedLineVertices == 0) continue;

            destination.Add(new CollisionWireframeInstance(mesh, candidate.World));
            lineVertexCount += selectedLineVertices;
        }

        return new CollisionReferencePriorityResult(
            lineVertexCount, destination.Count, warmupRequests);
    }

    private static int CompareCandidates(CollisionReferenceCandidate left, CollisionReferenceCandidate right)
    {
        // Corrupt/non-finite placement coordinates belong at the tail, not ahead of valid nearby refs.
        var leftDistance = float.IsFinite(left.DistanceSquared) ? left.DistanceSquared : float.PositiveInfinity;
        var rightDistance = float.IsFinite(right.DistanceSquared) ? right.DistanceSquared : float.PositiveInfinity;
        var compare = leftDistance.CompareTo(rightDistance);
        if (compare != 0) return compare;

        compare = left.FormId.CompareTo(right.FormId);
        if (compare != 0) return compare;
        compare = StringComparer.OrdinalIgnoreCase.Compare(left.ModelPath, right.ModelPath);
        if (compare != 0) return compare;
        compare = StringComparer.Ordinal.Compare(left.ModelPath, right.ModelPath);
        return compare != 0 ? compare : left.SourceOrder.CompareTo(right.SourceOrder);
    }

    private static int CountValidLineVertices(CollisionMesh mesh, int availableLineVertices)
    {
        var positions = mesh.Positions;
        var triangles = mesh.Triangles;
        if (positions.Length == 0 || triangles.Length < 3) return 0;

        var lineVertexCount = 0;
        for (var triangle = 0;
             triangle + 2 < triangles.Length && lineVertexCount + 6 <= availableLineVertices;
             triangle += 3)
        {
            var a = triangles[triangle];
            var b = triangles[triangle + 1];
            var c = triangles[triangle + 2];
            if ((uint)a < (uint)positions.Length && (uint)b < (uint)positions.Length &&
                (uint)c < (uint)positions.Length)
            {
                lineVertexCount += 6;
            }
        }

        return lineVertexCount;
    }
}
