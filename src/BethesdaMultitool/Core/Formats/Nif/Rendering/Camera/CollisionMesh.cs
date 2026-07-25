using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

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
///     Tri-state cache lookup: unresolved, resolved with collision, or resolved with no collision.
///     Keeping resolved-null distinct prevents permanent-null effects from consuming cold warmup
///     slots every frame.
/// </summary>
internal readonly record struct CollisionMeshResolution(
    bool IsResolved,
    CollisionMesh? Mesh,
    CollisionMeshSource Source)
{
    public static CollisionMeshResolution Unresolved =>
        new(false, null, CollisionMeshSource.None);

    public static CollisionMeshResolution From(CollisionBuildResult result) =>
        new(true, result.Mesh, result.Source);
}

/// <summary>
///     Source-priority gate shared by the D3D12 cache and platform-neutral tests. Authored Havok is
///     always authoritative, including for effect paths/categories. Visual geometry is synthesized
///     only after the effect policy has rejected presentation-only cards and volumes.
/// </summary>
internal static class CollisionMeshBuilder
{
    public static CollisionBuildResult Build(
        string? modelPath,
        PlacedObjectCategory category,
        Vector3[]? authoredPositions,
        int[]? authoredTriangles,
        Func<CollisionMesh?> visualFallbackFactory)
    {
        ArgumentNullException.ThrowIfNull(visualFallbackFactory);

        if (authoredTriangles is { Length: >= 3 } && authoredPositions is { Length: > 0 })
        {
            return new CollisionBuildResult(
                new CollisionMesh(authoredPositions, authoredTriangles),
                CollisionMeshSource.AuthoredHavok);
        }

        // Effects are presentation-only; vegetation (Plants/Trees) is walk-through unless it shipped
        // authored Havok (handled above). Neither gets render-mesh collision synthesized for it.
        if (WalkCollisionFallbackPolicy.IsEffectModel(modelPath, category)
            || WalkCollisionFallbackPolicy.IsVegetation(category))
        {
            return CollisionBuildResult.None;
        }

        var visual = visualFallbackFactory();
        return visual is null
            ? CollisionBuildResult.None
            : new CollisionBuildResult(visual, CollisionMeshSource.VisualFallback);
    }
}

/// <summary>
///     Mesh-local CPU triangle soup kept for walk-mode camera collision (ground snap). Built once per
///     placed-reference model at upload from the already-decoded geometry (positions + indices) of the
///     solid submeshes, in the SAME mesh-local space as <c>RenderableReference.WorldMatrix</c> (i.e.
///     <c>treatRootsAsIdentity</c> decode). The 3D viewer transforms a world-space down-ray into this
///     local space, raycasts, then maps the hit point back to world to read its Z.
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
