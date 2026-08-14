using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Collision;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Walk;

internal enum CollisionMeshSource : byte
{
    None = 0,
    AuthoredHavok = 1,
    VisualFallback = 2
}

/// <summary>
///     The immutable result of building collision for one model/category policy. A null mesh with
///     <see cref="CollisionMeshSource.None" /> is a successful, authoritative "no collision" result,
///     not a cache miss.
/// </summary>
internal readonly record struct CollisionBuildResult(
    CollisionMesh? Mesh,
    CollisionMeshSource Source)
{
    public static CollisionBuildResult None => new(null, CollisionMeshSource.None);
}

/// <summary>
///     Collision lookup with two independent decisions: whether the collision result is authoritative
///     and whether a cold decode should be offered. A terminal decode failure stays unresolved so
///     callers retain conservative OBND fallback, but it must not consume a warmup slot every frame.
/// </summary>
internal readonly record struct CollisionMeshResolution(
    bool IsResolved,
    CollisionMesh? Mesh,
    CollisionMeshSource Source,
    bool ShouldOfferWarmup)
{
    public static CollisionMeshResolution Unresolved =>
        new(false, null, CollisionMeshSource.None, true);

    /// <summary>
    ///     The model decode is terminally unavailable, so callers may retain their conservative OBND
    ///     fallback but must not spend another bounded cold-warmup slot on it every frame.
    /// </summary>
    public static CollisionMeshResolution TerminalUnavailable =>
        new(false, null, CollisionMeshSource.None, false);

    public static CollisionMeshResolution From(CollisionBuildResult result) =>
        new(true, result.Mesh, result.Source, false);
}

/// <summary>
///     Source-separated collision payload stored once per decoded model path. The authored Havok
///     result is category-independent and always wins. Visual triangle soup is retained separately so
///     the same model can resolve differently for ordinary and vegetation/effect placements without
///     baking a guessed category into the cache key.
/// </summary>
internal readonly record struct CollisionCacheEntry(
    CollisionBuildResult Authored,
    CollisionBuildResult VisualFallback)
{
    public static CollisionCacheEntry ResolvedNone =>
        new(CollisionBuildResult.None, CollisionBuildResult.None);

    /// <summary>
    ///     Builds the category-independent cache payload. Visual soup is built exactly once when
    ///     authored Havok is absent and the normalized path can admit it for at least one placement.
    ///     When Havok exists, no caller can legally select the visual fallback, so retaining that
    ///     duplicate geometry would only consume the collision byte budget.
    /// </summary>
    public static CollisionCacheEntry Create(
        string? normalizedModelPath,
        HavokCollisionProvenance authoredProvenance,
        Vector3[]? authoredPositions,
        int[]? authoredTriangles,
        Func<CollisionMesh?> visualFallbackFactory)
    {
        ArgumentNullException.ThrowIfNull(visualFallbackFactory);

        if (!Enum.IsDefined(authoredProvenance))
        {
            throw new ArgumentOutOfRangeException(nameof(authoredProvenance));
        }

        var hasAuthoredPositions = authoredPositions is { Length: > 0 };
        var hasAuthoredTriangles = authoredTriangles is { Length: > 0 };
        if (authoredProvenance == HavokCollisionProvenance.AuthoredMesh)
        {
            if (!hasAuthoredPositions || authoredTriangles is not { Length: >= 3 } ||
                authoredTriangles.Length % 3 != 0)
            {
                throw new ArgumentException(
                    "Authored-mesh provenance requires collision positions and complete triangles.");
            }

            foreach (var position in authoredPositions!)
            {
                if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
                    !float.IsFinite(position.Z))
                {
                    throw new ArgumentException("Authored collision positions must be finite.");
                }
            }

            foreach (var index in authoredTriangles)
            {
                if ((uint)index >= (uint)authoredPositions!.Length)
                {
                    throw new ArgumentException(
                        "Authored collision triangle index is outside the position array.");
                }
            }

            return new CollisionCacheEntry(
                new CollisionBuildResult(
                    new CollisionMesh(authoredPositions!, authoredTriangles),
                    CollisionMeshSource.AuthoredHavok),
                CollisionBuildResult.None);
        }

        if (hasAuthoredPositions || hasAuthoredTriangles)
        {
            throw new ArgumentException(
                "Collision arrays require authored-mesh provenance.");
        }

        if (authoredProvenance == HavokCollisionProvenance.AuthoredNoncollidable)
        {
            // An explicit layer-15 body is authoritative even for an ordinary placement. Do not
            // materialize visual soup that could silently undo the authored no-collision verdict.
            return ResolvedNone;
        }

        // Effects-folder and SpeedTree paths are presentation-only for every placement category.
        // Reject them before materializing what can be a very large card/frond soup. Category-only
        // vegetation cannot be rejected here: one shared model path may also have an ordinary
        // placement, so that decision remains in Resolve.
        if (!WalkCollisionFallbackPolicy.AllowsVisualMeshFallback(normalizedModelPath))
        {
            return ResolvedNone;
        }

        var visual = visualFallbackFactory();
        return new CollisionCacheEntry(
            CollisionBuildResult.None,
            visual is null
                ? CollisionBuildResult.None
                : new CollisionBuildResult(visual, CollisionMeshSource.VisualFallback));
    }

    /// <summary>
    ///     Resolves this stored payload for one placement. A policy rejection is an authoritative
    ///     resolved-null result, not a cache miss, so callers do not retry decoding or fall back to
    ///     speculative object bounds.
    /// </summary>
    public CollisionBuildResult Resolve(string? modelPath, PlacedObjectCategory category)
    {
        if (Authored.Source == CollisionMeshSource.AuthoredHavok)
        {
            return Authored;
        }

        return WalkCollisionFallbackPolicy.AllowsResolvedCollisionMesh(
            VisualFallback.Source, modelPath, category)
            ? VisualFallback
            : CollisionBuildResult.None;
    }

    // Even resolved-null entries carry dictionary/list/key overhead. Charging a non-zero floor keeps
    // the negative cache genuinely bounded. Sum both fields so future representation changes cannot
    // silently under-account retained geometry.
    public long ByteSize => Math.Max(
        128L,
        (Authored.Mesh?.ByteSize ?? 0L) + (VisualFallback.Mesh?.ByteSize ?? 0L));

    /// <summary>
    ///     Whether recreating this entry after collision-LRU eviction requires decoded geometry.
    ///     A source-independent resolved-none result can instead remain as lightweight node state.
    /// </summary>
    public bool HasRetainedGeometry => Authored.Mesh is not null || VisualFallback.Mesh is not null;
}

/// <summary>
///     Mesh-local CPU triangle soup kept for placed-reference collision queries (walk mode, selection,
///     and the collision overlay). Built once per model at upload from authored Havok or the
///     already-decoded solid render geometry, in the SAME mesh-local space as
///     <c>RenderableReference.WorldMatrix</c> (i.e. <c>treatRootsAsIdentity</c> decode).
///     <para>
///         Positions-only (12 B/vertex) + an <see cref="int" /> index triple per triangle — a fraction
///         of the GPU vertex footprint, so the collision LRU can stay warm for far more meshes than the
///         resident GPU set. No BVH (v1): only the 1–few objects directly under the camera footprint
///         survive the per-object local-AABB slab reject, so a brute-force triangle loop is cheap.
///     </para>
/// </summary>
internal sealed class CollisionMesh
{
    /// <summary>Mesh-local vertex positions.</summary>
    public Vector3[] Positions { get; }

    /// <summary>Triangle index triples into <see cref="Positions" /> (length is a multiple of 3).</summary>
    public int[] Triangles { get; }

    /// <summary>Mesh-local axis-aligned bounds (inclusive) used for the cheap pre-ray slab reject.</summary>
    public Vector3 LocalMin { get; }
    public Vector3 LocalMax { get; }

    /// <summary>Approximate retained-byte size for the byte-budgeted LRU.</summary>
    public long ByteSize { get; }

    public CollisionMesh(Vector3[] positions, int[] triangles)
    {
        Positions = positions;
        Triangles = triangles;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in positions)
        {
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        // Degenerate (no vertices) — collapse to origin so the slab test rejects every ray.
        if (positions.Length == 0)
        {
            min = Vector3.Zero;
            max = Vector3.Zero;
        }

        LocalMin = min;
        LocalMax = max;
        ByteSize = (long)positions.Length * 12 + (long)triangles.Length * sizeof(int) + 128;
    }

    /// <summary>
    ///     Returns the nearest forward (<c>t &gt;= 0</c>) intersection of the local-space ray
    ///     <paramref name="localOrigin" /> + t·<paramref name="localDir" /> with this mesh, or
    ///     <c>false</c> when the ray misses. <paramref name="t" /> is in the same units as
    ///     <paramref name="localDir" /> (so the caller recovers the hit point as
    ///     <c>localOrigin + t · localDir</c>).
    /// </summary>
    public bool RaycastNearest(Vector3 localOrigin, Vector3 localDir, out float t)
    {
        t = 0f;
        if (!RayHitsLocalAabb(localOrigin, localDir)) return false;

        var positions = Positions;
        var triangles = Triangles;
        var best = float.MaxValue;
        var hit = false;
        for (var i = 0; i + 2 < triangles.Length; i += 3)
        {
            if (RayTriangleIntersector.Intersect(
                    localOrigin, localDir,
                    positions[triangles[i]], positions[triangles[i + 1]], positions[triangles[i + 2]],
                    out var tt)
                && tt < best)
            {
                best = tt;
                hit = true;
            }
        }

        if (!hit) return false;
        t = best;
        return true;
    }

    /// <summary>
    ///     Nearest forward hit whose face is shallow enough to STAND on once placed by
    ///     <paramref name="world" /> (see <see cref="WalkSurfaceSlopePolicy.MinGroundNormalZ" />).
    ///     Steep faces are walls owned by <see cref="WalkHorizontalCollision" />; the search continues
    ///     PAST them to the real floor underneath rather than reporting the wall as ground. Accepting a
    ///     wall face here let the walk capsule's ring samples ratchet the camera one step height per
    ///     frame up a cave wall and out through it.
    /// </summary>
    public bool RaycastNearestWalkable(Vector3 localOrigin, Vector3 localDir, Matrix4x4 world, out float t)
    {
        t = 0f;
        if (!RayHitsLocalAabb(localOrigin, localDir)) return false;

        var positions = Positions;
        var triangles = Triangles;
        var best = float.MaxValue;
        var hit = false;
        for (var i = 0; i + 2 < triangles.Length; i += 3)
        {
            var a = positions[triangles[i]];
            var b = positions[triangles[i + 1]];
            var c = positions[triangles[i + 2]];
            if (!RayTriangleIntersector.Intersect(localOrigin, localDir, a, b, c, out var tt) || tt >= best)
            {
                continue;
            }

            // The placement can rotate a locally-flat face into a wall (and vice versa), so the slope
            // test must run on the WORLD normal. |n.z| keeps it winding-agnostic.
            if (!WalkSurfaceSlopePolicy.IsGround(
                    Vector3.TransformNormal(Vector3.Cross(b - a, c - a), world)))
            {
                continue;
            }

            best = tt;
            hit = true;
        }

        if (!hit) return false;
        t = best;
        return true;
    }

    /// <summary>
    ///     Slab test: does the forward ray (t &gt;= 0) enter the local AABB? Cheap reject so objects
    ///     not under the camera footprint never reach the triangle loop. A zero direction component is
    ///     handled by the origin-inside-slab check.
    /// </summary>
    private bool RayHitsLocalAabb(Vector3 origin, Vector3 dir)
    {
        var tMin = 0f;
        var tMax = float.MaxValue;

        if (!SlabHit(origin.X, dir.X, LocalMin.X, LocalMax.X, ref tMin, ref tMax)) return false;
        if (!SlabHit(origin.Y, dir.Y, LocalMin.Y, LocalMax.Y, ref tMin, ref tMax)) return false;
        if (!SlabHit(origin.Z, dir.Z, LocalMin.Z, LocalMax.Z, ref tMin, ref tMax)) return false;
        return true;

        static bool SlabHit(float o, float d, float lo, float hi, ref float tMin, ref float tMax)
        {
            const float parallelEps = 1e-12f;
            if (d > -parallelEps && d < parallelEps)
            {
                // Ray parallel to this slab — only possible if the origin is already inside it.
                return o >= lo && o <= hi;
            }

            var inv = 1f / d;
            var t1 = (lo - o) * inv;
            var t2 = (hi - o) * inv;
            if (t1 > t2) (t1, t2) = (t2, t1);
            if (t1 > tMin) tMin = t1;
            if (t2 < tMax) tMax = t2;
            return tMin <= tMax;
        }
    }
}
