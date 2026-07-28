using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Walk;

/// <summary>
///     One placed walk-collision source. A mesh source is the cache's Havok-first triangle soup;
///     a box source is the policy-gated cold-OBND fallback. Bounds are world-space and are
///     retained for the swept broadphase so a long frame cannot tunnel merely because neither end
///     point overlaps the object. A placed box additionally carries its ROTATED XY footprint
///     corners: walling the axis-aligned bounds of a yaw-rotated long piece (e.g. a 768-unit
///     sidewalk median at 45°) blocked walkable road far outside the object's true silhouette.
/// </summary>
internal readonly record struct WalkCollisionInstance(
    CollisionMesh? Mesh,
    Matrix4x4 World,
    Vector3 WorldMin,
    Vector3 WorldMax)
{
    public bool IsAxisAlignedBox => Mesh is null;

    /// <summary>
    ///     World-space XY corners of the placed box footprint, in winding order (null for mesh
    ///     sources and for legacy axis-aligned boxes, whose footprint is the world AABB).
    /// </summary>
    public Vector2[]? FootprintCorners { get; private init; }

    public static WalkCollisionInstance FromMesh(CollisionMesh mesh, Matrix4x4 world)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        var (min, max) = TransformedCornerBounds(mesh.LocalMin, mesh.LocalMax, world);
        return new WalkCollisionInstance(mesh, world, min, max);
    }

    public static WalkCollisionInstance FromAxisAlignedBox(Vector3 min, Vector3 max) =>
        new(null, Matrix4x4.Identity, Vector3.Min(min, max), Vector3.Max(min, max));

    /// <summary>
    ///     Cold-OBND box honoring the full placement transform: the broadphase AABB comes from the
    ///     eight transformed corners (correct Z range under tilt), and the wall sweep tests the
    ///     ROTATED footprint quad instead of the axis-aligned bounds.
    /// </summary>
    public static WalkCollisionInstance FromPlacedBounds(Vector3 localMin, Vector3 localMax, in Matrix4x4 world)
    {
        var orderedMin = Vector3.Min(localMin, localMax);
        var orderedMax = Vector3.Max(localMin, localMax);
        localMin = orderedMin;
        localMax = orderedMax;
        var (min, max) = TransformedCornerBounds(localMin, localMax, world);
        // The top-face corners in local winding order project to the placed footprint quad. Under
        // the upright placements the cold path serves (yaw-only road furniture) this is the exact
        // silhouette; under tilt it stays a sane quad while the AABB above stays conservative.
        var corners = new[]
        {
            ProjectXY(new Vector3(localMin.X, localMin.Y, localMax.Z), world),
            ProjectXY(new Vector3(localMax.X, localMin.Y, localMax.Z), world),
            ProjectXY(new Vector3(localMax.X, localMax.Y, localMax.Z), world),
            ProjectXY(new Vector3(localMin.X, localMax.Y, localMax.Z), world),
        };
        return new WalkCollisionInstance(null, world, min, max) { FootprintCorners = corners };
    }

    private static Vector2 ProjectXY(Vector3 local, in Matrix4x4 world)
    {
        var placed = Vector3.Transform(local, world);
        return new Vector2(placed.X, placed.Y);
    }

    private static (Vector3 Min, Vector3 Max) TransformedCornerBounds(
        Vector3 localMin, Vector3 localMax, in Matrix4x4 world)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var z = 0; z < 2; z++)
        {
            for (var y = 0; y < 2; y++)
            {
                for (var x = 0; x < 2; x++)
                {
                    var local = new Vector3(
                        x == 0 ? localMin.X : localMax.X,
                        y == 0 ? localMin.Y : localMax.Y,
                        z == 0 ? localMin.Z : localMax.Z);
                    var placed = Vector3.Transform(local, world);
                    min = Vector3.Min(min, placed);
                    max = Vector3.Max(max, placed);
                }
            }
        }

        return (min, max);
    }
}

/// <summary>
///     Continuous horizontal collision for the walk camera. The player is represented by a vertical
///     cylinder: a circular XY footprint swept along the requested displacement, with a caller-owned
///     body-height interval. Steep collision triangles are clipped to that interval and projected to
///     XY; the circle is swept analytically against their edges. On contact, the remaining displacement
///     is projected onto the wall tangent, providing ordinary wall sliding without discrete substeps.
/// </summary>
internal static class WalkHorizontalCollision
{
    /// <summary>
    ///     Surfaces at or below 45 degrees are walkable and remain the ground sampler's responsibility;
    ///     steeper faces participate in the horizontal wall sweep.
    /// </summary>
    private const float WalkableNormalZ = 0.70710677f;

    // A tiny separation prevents the next slide iteration from rediscovering the same wall at t=0.
    private const float ContactSkin = 0.05f;
    private const float GeometryEpsilon = 1e-6f;
    private const int MaxSlideIterations = 4;

    /// <summary>
    ///     Resolves a requested XY displacement against every candidate and returns the allowed
    ///     displacement. Collision is continuous over the complete segment, so even a displacement
    ///     much longer than a wall's thickness cannot tunnel through it.
    /// </summary>
    public static Vector2 Resolve(
        Vector2 start,
        Vector2 requestedDisplacement,
        float bodyMinZ,
        float bodyMaxZ,
        float radius,
        IReadOnlyList<WalkCollisionInstance> candidates)
    {
        if (!IsFinite(start) || !IsFinite(requestedDisplacement) ||
            !float.IsFinite(bodyMinZ) || !float.IsFinite(bodyMaxZ) ||
            !float.IsFinite(radius) || radius <= 0f || requestedDisplacement.LengthSquared() <= GeometryEpsilon)
        {
            return Vector2.Zero;
        }

        if (bodyMinZ > bodyMaxZ) (bodyMinZ, bodyMaxZ) = (bodyMaxZ, bodyMinZ);

        var position = start;
        var remaining = requestedDisplacement;
        for (var iteration = 0; iteration < MaxSlideIterations; iteration++)
        {
            var remainingLengthSquared = remaining.LengthSquared();
            if (remainingLengthSquared <= GeometryEpsilon) break;

            if (!TryFindEarliestContact(
                    position, remaining, bodyMinZ, bodyMaxZ, radius, candidates,
                    out var hitTime, out var hitNormal))
            {
                position += remaining;
                break;
            }

            var remainingLength = MathF.Sqrt(remainingLengthSquared);
            var skinTime = ContactSkin / remainingLength;
            var travelTime = MathF.Max(0f, hitTime - skinTime);
            position += remaining * travelTime;

            // Preserve the fraction after the actual contact (not after the skin retreat), then remove
            // only the component entering the wall. Tangential motion survives as the slide.
            var afterContact = remaining * MathF.Max(0f, 1f - hitTime);
            var entering = Vector2.Dot(afterContact, hitNormal);
            if (entering < 0f) afterContact -= hitNormal * entering;

            // The travel retreat supplies the normal separation for an ordinary hit. At t=0 there is
            // no segment to retreat along, so explicitly nudge out of the contact instead.
            if (travelTime <= GeometryEpsilon) position += hitNormal * ContactSkin;
            remaining = afterContact;
        }

        return position - start;
    }

    private static bool TryFindEarliestContact(
        Vector2 start,
        Vector2 displacement,
        float bodyMinZ,
        float bodyMaxZ,
        float radius,
        IReadOnlyList<WalkCollisionInstance> candidates,
        out float hitTime,
        out Vector2 hitNormal)
    {
        var bestTime = float.MaxValue;
        var bestNormal = Vector2.Zero;

        var end = start + displacement;
        var sweepMin = Vector2.Min(start, end) - new Vector2(radius);
        var sweepMax = Vector2.Max(start, end) + new Vector2(radius);
        Span<Vector3> clippedA = stackalloc Vector3[6];
        Span<Vector3> clippedB = stackalloc Vector3[6];

        foreach (var candidate in candidates)
        {
            if (candidate.WorldMax.X < sweepMin.X || candidate.WorldMin.X > sweepMax.X ||
                candidate.WorldMax.Y < sweepMin.Y || candidate.WorldMin.Y > sweepMax.Y ||
                candidate.WorldMax.Z < bodyMinZ || candidate.WorldMin.Z > bodyMaxZ)
            {
                continue;
            }

            if (candidate.IsAxisAlignedBox)
            {
                // Placed boxes wall their ROTATED footprint quad; only the legacy axis-aligned
                // form (and tests) fall back to the broadphase AABB rectangle.
                if (candidate.FootprintCorners is { Length: 4 } corners)
                {
                    TestSegment(corners[0], corners[1]);
                    TestSegment(corners[1], corners[2]);
                    TestSegment(corners[2], corners[3]);
                    TestSegment(corners[3], corners[0]);
                }
                else
                {
                    TestBox(candidate.WorldMin, candidate.WorldMax);
                }
                continue;
            }

            var mesh = candidate.Mesh!;
            var positions = mesh.Positions;
            var triangles = mesh.Triangles;
            for (var i = 0; i + 2 < triangles.Length; i += 3)
            {
                var i0 = triangles[i];
                var i1 = triangles[i + 1];
                var i2 = triangles[i + 2];
                if ((uint)i0 >= (uint)positions.Length ||
                    (uint)i1 >= (uint)positions.Length ||
                    (uint)i2 >= (uint)positions.Length)
                {
                    continue;
                }

                var a = Vector3.Transform(positions[i0], candidate.World);
                var b = Vector3.Transform(positions[i1], candidate.World);
                var c = Vector3.Transform(positions[i2], candidate.World);

                var normal = Vector3.Cross(b - a, c - a);
                var normalLength = normal.Length();
                if (normalLength <= GeometryEpsilon || MathF.Abs(normal.Z) / normalLength >= WalkableNormalZ)
                {
                    // Floors, ramps, and stairs remain owned by SampleGroundHeightCapsule. Treating
                    // their projected perimeter as a wall would stop the player at every floor seam.
                    continue;
                }

                var triangleMinZ = MathF.Min(a.Z, MathF.Min(b.Z, c.Z));
                var triangleMaxZ = MathF.Max(a.Z, MathF.Max(b.Z, c.Z));
                if (triangleMaxZ < bodyMinZ || triangleMinZ > bodyMaxZ) continue;

                // Clip before projecting. Without this, a steep triangle whose high tip reaches the
                // player's body could incorrectly contribute a distant low edge below the step height.
                clippedA[0] = a;
                clippedA[1] = b;
                clippedA[2] = c;
                var count = ClipAgainstZ(clippedA[..3], clippedB, bodyMinZ, keepAbove: true);
                if (count < 2) continue;
                count = ClipAgainstZ(clippedB[..count], clippedA, bodyMaxZ, keepAbove: false);
                if (count < 2) continue;

                for (var edge = 0; edge < count; edge++)
                {
                    var p0 = new Vector2(clippedA[edge].X, clippedA[edge].Y);
                    var p1v = clippedA[(edge + 1) % count];
                    var p1 = new Vector2(p1v.X, p1v.Y);
                    TestSegment(p0, p1);
                }
            }
        }

        hitTime = bestTime;
        hitNormal = bestNormal;
#pragma warning disable S1244 // float.MaxValue is the verbatim "no hit" sentinel; exact comparison intended
        return bestTime != float.MaxValue;
#pragma warning restore S1244

        void TestBox(Vector3 min, Vector3 max)
        {
            var a = new Vector2(min.X, min.Y);
            var b = new Vector2(max.X, min.Y);
            var c = new Vector2(max.X, max.Y);
            var d = new Vector2(min.X, max.Y);
            TestSegment(a, b);
            TestSegment(b, c);
            TestSegment(c, d);
            TestSegment(d, a);
        }

        void TestSegment(Vector2 a, Vector2 b)
        {
            if (TrySweepCircleAgainstSegment(start, displacement, radius, a, b, out var t, out var n) &&
                t < bestTime)
            {
                bestTime = t;
                bestNormal = n;
            }
        }
    }

    private static int ClipAgainstZ(
        ReadOnlySpan<Vector3> input,
        Span<Vector3> output,
        float planeZ,
        bool keepAbove)
    {
        if (input.Length == 0) return 0;
        var outputCount = 0;
        var previous = input[^1];
        var previousInside = keepAbove ? previous.Z >= planeZ : previous.Z <= planeZ;

        foreach (var current in input)
        {
            var currentInside = keepAbove ? current.Z >= planeZ : current.Z <= planeZ;
            if (currentInside != previousInside)
            {
                var dz = current.Z - previous.Z;
                if (MathF.Abs(dz) > GeometryEpsilon)
                {
                    var t = Math.Clamp((planeZ - previous.Z) / dz, 0f, 1f);
                    output[outputCount++] = Vector3.Lerp(previous, current, t);
                }
            }

            if (currentInside) output[outputCount++] = current;
            previous = current;
            previousInside = currentInside;
        }

        return outputCount;
    }

    /// <summary>
    ///     Analytic moving-circle versus line-segment test. The segment's Minkowski expansion is a
    ///     capsule: two parallel side lines plus endpoint circles. Testing those features continuously
    ///     yields the first contact time in [0,1], independent of displacement magnitude.
    /// </summary>
    private static bool TrySweepCircleAgainstSegment(
        Vector2 start,
        Vector2 displacement,
        float radius,
        Vector2 segmentStart,
        Vector2 segmentEnd,
        out float hitTime,
        out Vector2 hitNormal)
    {
        var bestTime = float.MaxValue;
        var bestNormal = Vector2.Zero;

        var edge = segmentEnd - segmentStart;
        var edgeLengthSquared = edge.LengthSquared();
        if (edgeLengthSquared <= GeometryEpsilon)
        {
            TestEndpoint(segmentStart);
            hitTime = bestTime;
            hitNormal = bestNormal;
#pragma warning disable S1244 // float.MaxValue is the verbatim "no hit" sentinel; exact comparison intended
            return bestTime != float.MaxValue;
#pragma warning restore S1244
        }

        var edgeLength = MathF.Sqrt(edgeLengthSquared);
        var tangent = edge / edgeLength;
        var sideNormal = new Vector2(-tangent.Y, tangent.X);

        var closestT = Math.Clamp(Vector2.Dot(start - segmentStart, edge) / edgeLengthSquared, 0f, 1f);
        var closest = segmentStart + edge * closestT;
        var initialOffset = start - closest;
        var initialDistanceSquared = initialOffset.LengthSquared();
        if (initialDistanceSquared <= radius * radius + GeometryEpsilon)
        {
            Vector2 normal;
            if (initialDistanceSquared > GeometryEpsilon)
            {
                normal = initialOffset / MathF.Sqrt(initialDistanceSquared);
            }
            else
            {
                normal = sideNormal;
                if (Vector2.Dot(displacement, normal) > 0f) normal = -normal;
            }

            if (Vector2.Dot(displacement, normal) < -GeometryEpsilon)
            {
                bestTime = 0f;
                bestNormal = normal;
            }
        }

        var signedDistance = Vector2.Dot(start - segmentStart, sideNormal);
        var signedVelocity = Vector2.Dot(displacement, sideNormal);
        if (MathF.Abs(signedVelocity) > GeometryEpsilon)
        {
            TestSide(+radius, sideNormal);
            TestSide(-radius, -sideNormal);
        }

        TestEndpoint(segmentStart);
        TestEndpoint(segmentEnd);
        hitTime = bestTime;
        hitNormal = bestNormal;
#pragma warning disable S1244 // float.MaxValue is the verbatim "no hit" sentinel; exact comparison intended
        return bestTime != float.MaxValue;
#pragma warning restore S1244

        void TestSide(float targetDistance, Vector2 normal)
        {
            var t = (targetDistance - signedDistance) / signedVelocity;
            if (t < 0f || t > 1f || t >= bestTime || Vector2.Dot(displacement, normal) >= -GeometryEpsilon)
            {
                return;
            }

            var center = start + displacement * t;
            var along = Vector2.Dot(center - segmentStart, tangent);
            if (along < 0f || along > edgeLength) return;
            bestTime = t;
            bestNormal = normal;
        }

        void TestEndpoint(Vector2 endpoint)
        {
            var relative = start - endpoint;
            var a = displacement.LengthSquared();
            if (a <= GeometryEpsilon) return;
            var b = 2f * Vector2.Dot(relative, displacement);
            var c = relative.LengthSquared() - radius * radius;
            var discriminant = b * b - 4f * a * c;
            if (discriminant < 0f) return;

            var sqrt = MathF.Sqrt(discriminant);
            var t = (-b - sqrt) / (2f * a);
            if (t < 0f || t > 1f || t >= bestTime) return;

            var offset = start + displacement * t - endpoint;
            var length = offset.Length();
            if (length <= GeometryEpsilon) return;
            var normal = offset / length;
            if (Vector2.Dot(displacement, normal) >= -GeometryEpsilon) return;
            bestTime = t;
            bestNormal = normal;
        }
    }

    private static bool IsFinite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);
}
