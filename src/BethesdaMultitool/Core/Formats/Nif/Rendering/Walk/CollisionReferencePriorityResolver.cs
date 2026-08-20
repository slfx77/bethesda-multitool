using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Walk;

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
    int SourceOrder,
    PlacedObjectCategory Category = PlacedObjectCategory.Unknown);

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
    private readonly Dictionary<string, CollisionReferenceCandidate> _warmupBestCandidateScratch =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _warmupPathScratch = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CollisionReferenceCandidate> _warmupSelectionScratch = [];

    /// <summary>
    ///     Offers at most <paramref name="maxWarmupRequests" /> unique model paths to a collision
    ///     warmup callback, globally nearest-first. Walk mode uses this before falling back to cold
    ///     object bounds so an authoritative Havok mesh can enter the collision LRU even when the
    ///     reference renderer has not made that model resident yet.
    /// </summary>
    public int WarmNearest(
        List<CollisionReferenceCandidate> candidates,
        Func<string, PlacedObjectCategory, float, CollisionMeshResolution> warmup,
        int maxWarmupRequests)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(warmup);
        ArgumentOutOfRangeException.ThrowIfNegative(maxWarmupRequests);

        _warmupBestCandidateScratch.Clear();
        foreach (var candidate in candidates)
        {
            if (!_warmupBestCandidateScratch.TryGetValue(candidate.ModelPath, out var current) ||
                CompareCandidates(candidate, current) < 0)
            {
                _warmupBestCandidateScratch[candidate.ModelPath] = candidate;
            }
        }

        // The live budget is two. Keep only that bounded sorted prefix instead of O(n log n)-sorting
        // every cold placement list (BuildRaycastCandidates can run for horizontal, ground, and ceiling
        // queries in the same dense-cell frame).
        _warmupSelectionScratch.Clear();
        foreach (var candidate in _warmupBestCandidateScratch.Values)
        {
            var insertAt = 0;
            while (insertAt < _warmupSelectionScratch.Count &&
                   CompareCandidates(_warmupSelectionScratch[insertAt], candidate) <= 0)
            {
                insertAt++;
            }

            if (insertAt >= maxWarmupRequests) continue;
            _warmupSelectionScratch.Insert(insertAt, candidate);
            if (_warmupSelectionScratch.Count > maxWarmupRequests)
            {
                _warmupSelectionScratch.RemoveAt(_warmupSelectionScratch.Count - 1);
            }
        }

        foreach (var candidate in _warmupSelectionScratch)
        {
            _ = warmup(candidate.ModelPath, candidate.Category, candidate.DistanceSquared);
        }

        return _warmupSelectionScratch.Count;
    }

    public CollisionReferencePriorityResult Resolve(
        List<CollisionReferenceCandidate> candidates,
        Func<string, PlacedObjectCategory, CollisionMeshResolution> resolver,
        Func<string, PlacedObjectCategory, float, CollisionMeshResolution>? warmup,
        int maxWarmupRequests,
        int maxLineVertices,
        List<CollisionWireframeInstance> destination)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(maxWarmupRequests);
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

            var resolution = resolver(candidate.ModelPath, candidate.Category);
            if (resolution.ShouldOfferWarmup && warmup is not null && warmupRequests < maxWarmupRequests &&
                _warmupPathScratch.Add(candidate.ModelPath))
            {
                warmupRequests++;
                resolution = warmup(
                    candidate.ModelPath,
                    candidate.Category,
                    candidate.DistanceSquared);
            }

            var mesh = resolution.Mesh;
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
