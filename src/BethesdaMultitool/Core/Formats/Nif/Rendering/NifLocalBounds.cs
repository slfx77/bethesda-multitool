using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     One geometry's sphere in the baked mesh-root-local coordinate system. For classic
///     NiTriShapeData/NiTriStripsData this is the serialized NiBound transformed by the same
///     scene-graph matrix as the vertices.
/// </summary>
internal readonly record struct NifLocalBounds(Vector3 Center, float Radius);

/// <summary>
///     Validates/transforms serialized NiBound data and supplies the deterministic vertex-derived
///     fallback used by generated, deformed, or malformed geometry.
/// </summary>
internal static class NifLocalBoundsResolver
{
    /// <summary>
    ///     Transforms an authored local sphere into the extractor's baked root-local frame. NIF node
    ///     transforms are uniformly scaled; taking the largest basis length remains conservative if a
    ///     malformed/non-uniform matrix reaches this path. A deformed source cannot reuse its static
    ///     serialized sphere and deliberately falls through to <see cref="FromPositions" /> later.
    /// </summary>
    internal static NifLocalBounds? TransformAuthored(
        NifLocalBounds? authored,
        Matrix4x4 transform,
        ReadOnlySpan<float> transformedPositions,
        bool sourceDeformed)
    {
        if (sourceDeformed || authored is not { } bound ||
            !IsFinite(bound.Center) || !float.IsFinite(bound.Radius) || bound.Radius < 0f)
        {
            return null;
        }

        var center = Vector3.Transform(bound.Center, transform);
        var scaleX = new Vector3(transform.M11, transform.M12, transform.M13).Length();
        var scaleY = new Vector3(transform.M21, transform.M22, transform.M23).Length();
        var scaleZ = new Vector3(transform.M31, transform.M32, transform.M33).Length();
        var scale = MathF.Max(scaleX, MathF.Max(scaleY, scaleZ));
        var radius = bound.Radius * scale;
        if (!IsFinite(center) || !float.IsFinite(radius) || radius < 0f)
        {
            return null;
        }

        // A zero-radius NiBound is valid only for genuinely point-degenerate geometry. Shipped
        // meshes occasionally contain cleared/corrupt bound bytes; do not collapse their distance
        // metric to an arbitrary point when real vertices establish a usable fallback sphere.
#pragma warning disable S1244 // cleared/corrupt bound bytes serialize a radius of exactly 0; tiny non-zero spheres are valid
        if (radius == 0f && HasPositionAwayFrom(transformedPositions, center))
#pragma warning restore S1244
        {
            return null;
        }

        return new NifLocalBounds(center, radius);
    }

    internal static NifLocalBounds Resolve(RenderableSubmesh submesh) =>
        submesh.LocalBounds is { } bounds &&
        IsFinite(bounds.Center) && float.IsFinite(bounds.Radius) && bounds.Radius >= 0f
            ? bounds
            : FromPositions(submesh.Positions);

    /// <summary>
    ///     Existing viewer fallback: AABB midpoint followed by the maximum finite vertex distance
    ///     from that midpoint. Iteration order and float operations are fixed so cold and warm-cache
    ///     realizations resolve byte-identically.
    /// </summary>
    internal static NifLocalBounds FromPositions(ReadOnlySpan<float> positions)
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        var finiteCount = 0;
        for (var i = 0; i + 2 < positions.Length; i += 3)
        {
            var p = new Vector3(positions[i], positions[i + 1], positions[i + 2]);
            if (!IsFinite(p)) continue;
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
            finiteCount++;
        }

        if (finiteCount == 0)
        {
            return new NifLocalBounds(Vector3.Zero, 0f);
        }

        var center = Vector3.Multiply(min + max, 0.5f);
        var radiusSquared = 0f;
        for (var i = 0; i + 2 < positions.Length; i += 3)
        {
            var p = new Vector3(positions[i], positions[i + 1], positions[i + 2]);
            if (!IsFinite(p)) continue;
            radiusSquared = MathF.Max(radiusSquared, Vector3.DistanceSquared(center, p));
        }

        return new NifLocalBounds(center, MathF.Sqrt(radiusSquared));
    }

    private static bool HasPositionAwayFrom(ReadOnlySpan<float> positions, Vector3 center)
    {
        const float epsilonSquared = 1e-8f;
        for (var i = 0; i + 2 < positions.Length; i += 3)
        {
            var p = new Vector3(positions[i], positions[i + 1], positions[i + 2]);
            if (IsFinite(p) && Vector3.DistanceSquared(center, p) > epsilonSquared)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
