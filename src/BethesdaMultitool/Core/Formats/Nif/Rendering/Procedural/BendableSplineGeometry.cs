using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Procedural;

/// <summary>
///     CPU geometry for one FO4-family BNDS/REFR bendable-spline placement. The vertex layout is
///     API-neutral even though it is already packed for the shared GPU upload path; retaining one
///     array here lets the decoded-mesh cache wrap it without keeping a second copy of every wire.
/// </summary>
internal sealed record BendableSplineRenderMesh(
    string CacheKey,
    GpuMeshUploader.GpuVertex[] Vertices,
    ushort[] Indices,
    string? DiffuseTexturePath,
    string? NormalMapTexturePath,
    Vector3 LocalBoundsCenter,
    float LocalBoundsRadius,
    int SegmentCount,
    int SliceCount,
    float TextureTileCount);

/// <summary>
///     Reconstructs <c>BSProceduralGeometry::BendableSpline</c> from the FO4 retail executable.
///     The recovered implementation uses XBSD half extents as symmetric local endpoints, a
///     quadratic Bezier control point lowered by <c>distance * 1.4 * slack</c>, distance-spaced
///     rings, and XBSD thickness as the tube diameter. See the symbol/decompile evidence under
///     <c>TestOutput/current-goal/03-fallout4/retail-research</c>.
/// </summary>
internal static class BendableSplineGeometry
{
    internal const float SagPercentage = 1.4f;
    internal const float DefaultSegmentLengthPercentage = 0.04f;
    internal const int CurveSubdivisionCount = 48;
    internal const int MinimumSegmentCount = 8;

    private const float PositionEpsilon = 1e-6f;
    private const float EndDistanceEpsilon = 0.001f;

    internal static string BuildCacheKey(uint referenceFormId) =>
        $"fallout:generated/bnds/{referenceFormId:x8}";

    internal static BendableSplineRenderMesh? TryBuild(
        uint referenceFormId,
        BendableSplineDefinitionData definition,
        BendableSplinePlacementData placement,
        TextureSetRecord? textureSet)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(placement);

        if (!IsFinite(placement.HalfExtents) ||
            !float.IsFinite(placement.Slack) ||
            !float.IsFinite(placement.Thickness) ||
            placement.Thickness <= 0f ||
            !float.IsFinite(definition.DefaultTileCount) ||
            !IsFinite(definition.DefaultColor))
        {
            return null;
        }

        var sliceCount = (int)definition.DefaultSliceCount;
        // Fewer than three sides cannot form a non-degenerate tube. The retail generator trusts
        // authored data, but rejecting malformed inputs here prevents divide-by-zero and zero-area
        // geometry from reaching the GPU without changing any valid shipped cable.
        if (sliceCount < 3)
        {
            return null;
        }

        var start = -placement.HalfExtents;
        var end = placement.HalfExtents;
        if (Vector3.DistanceSquared(start, end) <= PositionEpsilon * PositionEpsilon)
        {
            return null;
        }

        var control = ComputeControlPoint(start, end, placement.Slack);
        var curve = SampleCurve(start, control, end);
        if (!float.IsFinite(curve.TotalLength) || curve.TotalLength <= PositionEpsilon)
        {
            return null;
        }

        var segmentLength = curve.TotalLength * DefaultSegmentLengthPercentage;
        var estimatedSegments = (int)(curve.TotalLength / segmentLength);
        if (estimatedSegments < MinimumSegmentCount)
        {
            segmentLength = curve.TotalLength / MinimumSegmentCount;
        }

        var centers = BuildDistanceSpacedCenters(curve, segmentLength, end);
        var segmentCount = centers.Count - 1;
        var ringSize = sliceCount + 1;
        var vertexCount = (long)centers.Count * ringSize;
        if (segmentCount <= 0 || vertexCount > ushort.MaxValue)
        {
            return null;
        }

        var indexCount = (long)segmentCount * sliceCount * 6L;
        if (indexCount > int.MaxValue)
        {
            return null;
        }

        var vertices = new GpuMeshUploader.GpuVertex[(int)vertexCount];
        var indices = new ushort[(int)indexCount];
        var radius = placement.Thickness * 0.5f;
        var color = new Vector4(
            Math.Clamp(definition.DefaultColor.X, 0f, 1f),
            Math.Clamp(definition.DefaultColor.Y, 0f, 1f),
            Math.Clamp(definition.DefaultColor.Z, 0f, 1f),
            // Retail's non-wind path writes opaque alpha; DNAM's fourth color float is not used
            // as coverage by BSProceduralGeometry::BendableSpline::CreateInstance.
            1f);
        var tileCount = definition.TilesRelativeToLength
            ? curve.TotalLength * definition.DefaultTileCount
            : definition.DefaultTileCount;
        if (!float.IsFinite(tileCount))
        {
            return null;
        }

        // AddSegment creates both rings for the first span, then reuses its end ring as the next
        // span's start. Consequently a shared knot keeps the preceding span's frame; reproduce that
        // detail instead of independently re-facing every ring from a smoothed tangent.
        for (var segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
        {
            var segmentStart = centers[segmentIndex];
            var segmentEnd = centers[segmentIndex + 1];
            var direction = segmentEnd - segmentStart;
            var lengthSquared = direction.LengthSquared();
            if (lengthSquared <= PositionEpsilon * PositionEpsilon)
            {
                return null;
            }

            direction /= MathF.Sqrt(lengthSquared);
            BuildRingBasis(direction, out var ringX, out var ringY);
            if (segmentIndex == 0)
            {
                WriteRing(
                    vertices, 0, segmentStart, direction, ringX, ringY, radius,
                    sliceCount, 0f, color);
            }

            WriteRing(
                vertices, segmentIndex + 1, segmentEnd, direction, ringX, ringY, radius,
                sliceCount, (segmentIndex + 1f) / segmentCount * tileCount, color);
        }

        var index = 0;
        for (var segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
        {
            var startRing = segmentIndex * ringSize;
            var endRing = startRing + ringSize;
            for (var sliceIndex = 0; sliceIndex < sliceCount; sliceIndex++)
            {
                var a0 = (ushort)(startRing + sliceIndex);
                var a1 = (ushort)(a0 + 1);
                var b0 = (ushort)(endRing + sliceIndex);
                var b1 = (ushort)(b0 + 1);

                // Exact AddSegment winding: [end0,start0,end1], [start1,end1,start0].
                indices[index++] = b0;
                indices[index++] = a0;
                indices[index++] = b1;
                indices[index++] = a1;
                indices[index++] = b1;
                indices[index++] = a0;
            }
        }

        ComputeBounds(vertices, out var boundsCenter, out var boundsRadius);
        return new BendableSplineRenderMesh(
            BuildCacheKey(referenceFormId),
            vertices,
            indices,
            textureSet?.DiffuseTexture,
            textureSet?.NormalTexture,
            boundsCenter,
            boundsRadius,
            segmentCount,
            sliceCount,
            tileCount);
    }

    internal static Vector3 ComputeControlPoint(Vector3 start, Vector3 end, float slack)
    {
        var midpoint = (start + end) * 0.5f;
        // Retail leaves a strictly vertical spline unsagged; gravity only lowers spans with a
        // horizontal component.
        if (start.X != end.X || start.Y != end.Y)
        {
            midpoint.Z -= Vector3.Distance(start, end) * SagPercentage * slack;
        }

        return midpoint;
    }

    internal static Vector3 EvaluateQuadratic(
        Vector3 start,
        Vector3 control,
        Vector3 end,
        float t)
    {
        var oneMinusT = 1f - t;
        return oneMinusT * oneMinusT * start +
               2f * oneMinusT * t * control +
               t * t * end;
    }

    private static SampledCurve SampleCurve(Vector3 start, Vector3 control, Vector3 end)
    {
        var points = new Vector3[CurveSubdivisionCount + 1];
        var cumulative = new float[points.Length];
        points[0] = start;
        for (var i = 1; i < points.Length; i++)
        {
            points[i] = EvaluateQuadratic(start, control, end, i / (float)CurveSubdivisionCount);
            cumulative[i] = cumulative[i - 1] + Vector3.Distance(points[i - 1], points[i]);
        }

        return new SampledCurve(points, cumulative, cumulative[^1]);
    }

    private static List<Vector3> BuildDistanceSpacedCenters(
        in SampledCurve curve,
        float segmentLength,
        Vector3 end)
    {
        var centers = new List<Vector3>(MinimumSegmentCount + 1) { curve.Points[0] };
        var distance = segmentLength;
        while (distance + EndDistanceEpsilon < curve.TotalLength)
        {
            centers.Add(curve.GetPositionAtDistance(distance));
            distance += segmentLength;
        }

        centers.Add(end);
        return centers;
    }

    private static void BuildRingBasis(Vector3 direction, out Vector3 ringX, out Vector3 ringY)
    {
        // Rotating the retail tube's local +Z axis onto the span direction is equivalent to any
        // stable orthonormal basis of the plane perpendicular to that direction. Start from +Y,
        // matching AddSegment's theta-zero vertex (sin(0), cos(0), z).
        var seed = MathF.Abs(direction.Y) < 0.999f ? Vector3.UnitY : Vector3.UnitX;
        ringX = Vector3.Normalize(Vector3.Cross(seed, direction));
        ringY = Vector3.Normalize(Vector3.Cross(direction, ringX));
        // With a +Z span the expressions above yield ringX=+X and ringY=+Y, exactly retail's
        // (sin(theta), cos(theta)) cross-section ordering.
        if (direction.Z >= 0.999f)
        {
            ringX = Vector3.UnitX;
            ringY = Vector3.UnitY;
        }
        else if (direction.Z <= -0.999f)
        {
            // Retail treats the antiparallel axis as identity. Its ring plane is still correct.
            ringX = Vector3.UnitX;
            ringY = Vector3.UnitY;
        }
    }

    private static void WriteRing(
        GpuMeshUploader.GpuVertex[] vertices,
        int ringIndex,
        Vector3 center,
        Vector3 tangent,
        Vector3 ringX,
        Vector3 ringY,
        float radius,
        int sliceCount,
        float u,
        Vector4 color)
    {
        var ringSize = sliceCount + 1;
        var offset = ringIndex * ringSize;
        for (var sliceIndex = 0; sliceIndex <= sliceCount; sliceIndex++)
        {
            // Duplicate slice zero at the end of the ring to create the retail UV seam.
            var angle = MathF.Tau * (sliceIndex == sliceCount ? 0f : sliceIndex / (float)sliceCount);
            var normal = MathF.Sin(angle) * ringX + MathF.Cos(angle) * ringY;
            normal = Vector3.Normalize(normal);
            var bitangent = Vector3.Normalize(Vector3.Cross(normal, tangent));
            vertices[offset + sliceIndex] = new GpuMeshUploader.GpuVertex
            {
                Position = center + normal * radius,
                Normal = normal,
                TexCoord = new Vector2(u, sliceIndex / (float)sliceCount),
                VertexColor = color,
                Tangent = tangent,
                Bitangent = bitangent
            };
        }
    }

    private static void ComputeBounds(
        IReadOnlyList<GpuMeshUploader.GpuVertex> vertices,
        out Vector3 center,
        out float radius)
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var vertex in vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }

        center = (min + max) * 0.5f;
        var radiusSquared = 0f;
        foreach (var vertex in vertices)
        {
            radiusSquared = MathF.Max(radiusSquared, Vector3.DistanceSquared(center, vertex.Position));
        }

        radius = MathF.Sqrt(radiusSquared);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private readonly record struct SampledCurve(
        Vector3[] Points,
        float[] CumulativeLengths,
        float TotalLength)
    {
        internal Vector3 GetPositionAtDistance(float distance)
        {
            if (distance <= 0f) return Points[0];
            if (distance >= TotalLength) return Points[^1];

            var upper = Array.BinarySearch(CumulativeLengths, distance);
            if (upper >= 0) return Points[upper];
            upper = ~upper;
            var lower = upper - 1;
            var span = CumulativeLengths[upper] - CumulativeLengths[lower];
            if (span <= PositionEpsilon) return Points[upper];
            var t = (distance - CumulativeLengths[lower]) / span;
            return Vector3.Lerp(Points[lower], Points[upper], t);
        }
    }
}
