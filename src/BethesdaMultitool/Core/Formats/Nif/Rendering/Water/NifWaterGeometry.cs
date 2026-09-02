using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     Immutable triangle geometry for a NIF surface classified onto the placed-water route. Most
///     sources use <c>WaterShaderProperty</c>; the TES3 Vivec classifier also supplies legacy textured
///     triangles, whose material state is not retained here. The retail FNV <c>WATER000</c> vertex
///     shader consumes each authored position directly through
///     <c>ModelViewProj</c> / <c>WorldMat</c> (see
///     <c>tools/GhidraProject/fnv_water_vertex_disassembly.txt</c>), so placeable water must retain
///     the source positions and indices rather than reducing them to an axis-aligned flat quad.
/// </summary>
internal sealed class NifWaterGeometry
{
    private readonly ushort[] _indices;
    private readonly Vector3[] _positions;

    private NifWaterGeometry(
        Vector3[] positions,
        ushort[] indices,
        Vector3 boundsMin,
        Vector3 boundsMax)
    {
        _positions = positions;
        _indices = indices;
        BoundsMin = boundsMin;
        BoundsMax = boundsMax;
    }

    /// <summary>
    ///     WATR FormID this surface renders with — resolved at PLACEMENT time from where the water
    ///     IS (its own cell's XCWT), never from the camera's cell. 0 = unresolved; the renderer then
    ///     falls back to the worldspace default (WRLD NAM2).
    /// </summary>
    public uint WaterFormId { get; private init; }

    /// <summary>The authored (or placement-transformed) vertex positions.</summary>
    public ReadOnlyMemory<Vector3> Positions => _positions;

    /// <summary>The authored triangle-list indices, retained in their original order and winding.</summary>
    public ReadOnlyMemory<ushort> Indices => _indices;

    public int TriangleCount => _indices.Length / 3;

    /// <summary>Number of six-vertex draw packets required when two triangles share one instance.</summary>
    public int TrianglePacketCount => (TriangleCount + 1) / 2;

    /// <summary>Axis-aligned bounds over every retained position, used only for conservative culling.</summary>
    public Vector3 BoundsMin { get; }

    /// <inheritdoc cref="BoundsMin" />
    public Vector3 BoundsMax { get; }

    /// <summary>Copy sharing the vertex/index payloads, stamped with the resolved water identity.</summary>
    public NifWaterGeometry WithWaterFormId(uint waterFormId)
    {
        return new NifWaterGeometry(_positions, _indices, BoundsMin, BoundsMax) { WaterFormId = waterFormId };
    }

    /// <summary>
    ///     Validates and copies one triangle list. Malformed geometry is rejected as a unit: silently
    ///     dropping a bad triangle would change the authored water outline and conceal decoder errors.
    /// </summary>
    public static bool TryCreate(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<ushort> indices,
        out NifWaterGeometry? geometry)
    {
        geometry = null;
        if (positions.IsEmpty || indices.Length < 3 || indices.Length % 3 != 0)
        {
            return false;
        }

        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var position in positions)
        {
            if (!IsFinite(position))
            {
                return false;
            }

            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        foreach (var index in indices)
        {
            if (index >= positions.Length)
            {
                return false;
            }
        }

        geometry = new NifWaterGeometry(positions.ToArray(), indices.ToArray(), min, max);
        return true;
    }

    /// <summary>
    ///     Applies a complete placement transform to every authored vertex. Rotation, non-uniform
    ///     scale, and per-vertex Z are therefore retained; bounds are recomputed from the transformed
    ///     positions and never substituted for the geometry.
    /// </summary>
    public NifWaterGeometry? Transform(Matrix4x4 transform)
    {
        if (!IsFinite(transform))
        {
            return null;
        }

        var transformed = new Vector3[_positions.Length];
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        for (var i = 0; i < _positions.Length; i++)
        {
            var position = Vector3.Transform(_positions[i], transform);
            if (!IsFinite(position))
            {
                return null;
            }

            transformed[i] = position;
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        // Indices are immutable and the transform cannot change topology, so transformed placements
        // safely share the validated index payload instead of cloning it once per placed reference.
        return new NifWaterGeometry(transformed, _indices, min, max);
    }

    /// <summary>
    ///     Resolves up to two consecutive authored triangles into one six-vertex draw packet. Index
    ///     order is copied exactly, so each triangle retains its source winding. When the triangle
    ///     count is odd, the second triangle is degenerate and therefore produces no fragments.
    /// </summary>
    public NifWaterTrianglePacket GetTrianglePacket(int packetIndex)
    {
        if ((uint)packetIndex >= (uint)TrianglePacketCount)
        {
            throw new ArgumentOutOfRangeException(nameof(packetIndex));
        }

        var firstIndex = packetIndex * 6;
        var v0 = _positions[_indices[firstIndex]];
        var v1 = _positions[_indices[firstIndex + 1]];
        var v2 = _positions[_indices[firstIndex + 2]];
        if (firstIndex + 5 >= _indices.Length)
        {
            return new NifWaterTrianglePacket(v0, v1, v2, v2, v2, v2);
        }

        return new NifWaterTrianglePacket(
            v0,
            v1,
            v2,
            _positions[_indices[firstIndex + 3]],
            _positions[_indices[firstIndex + 4]],
            _positions[_indices[firstIndex + 5]]);
    }

    /// <summary>
    ///     Conservative XY AABB-versus-square test for the placed-water streaming footprint. Despite
    ///     its historical name, <c>VisibilityCylinder</c> uses Chebyshev distance so corner geometry
    ///     must not disappear merely because it lies outside an inscribed circle.
    /// </summary>
    public bool IntersectsXY(Vector2 center, float radius)
    {
        if (!float.IsFinite(radius) || radius < 0f || !float.IsFinite(center.X) || !float.IsFinite(center.Y))
        {
            return false;
        }

        return BoundsMax.X >= center.X - radius
               && BoundsMin.X <= center.X + radius
               && BoundsMax.Y >= center.Y - radius
               && BoundsMin.Y <= center.Y + radius;
    }

    /// <summary>
    ///     Resolves the highest authored triangle surface covering one XY point. This keeps the
    ///     transparency partition honest for placed/NIF water: those surfaces are not represented in
    ///     the CELL-height lookup and may be sloped, so substituting an AABB plane would classify
    ///     geometry outside the actual outline or at the wrong height.
    /// </summary>
    internal bool TryGetHeightAtXY(float x, float y, out float height)
    {
        height = float.NegativeInfinity;
        if (!float.IsFinite(x) || !float.IsFinite(y) ||
            x < BoundsMin.X || x > BoundsMax.X ||
            y < BoundsMin.Y || y > BoundsMax.Y)
        {
            return false;
        }

        var found = false;
        const float barycentricEpsilon = 1e-5f;
        for (var index = 0; index < _indices.Length; index += 3)
        {
            var a = _positions[_indices[index]];
            var b = _positions[_indices[index + 1]];
            var c = _positions[_indices[index + 2]];
            var denominator = ((b.Y - c.Y) * (a.X - c.X)) +
                              ((c.X - b.X) * (a.Y - c.Y));
            if (!float.IsFinite(denominator) || MathF.Abs(denominator) <= float.Epsilon)
            {
                // Edge-on/degenerate in XY covers no surface area for a point-height query.
                continue;
            }

            var wa = (((b.Y - c.Y) * (x - c.X)) + ((c.X - b.X) * (y - c.Y))) /
                     denominator;
            var wb = (((c.Y - a.Y) * (x - c.X)) + ((a.X - c.X) * (y - c.Y))) /
                     denominator;
            var wc = 1f - wa - wb;
            if (wa < -barycentricEpsilon || wb < -barycentricEpsilon || wc < -barycentricEpsilon)
            {
                continue;
            }

            var surface = (wa * a.Z) + (wb * b.Z) + (wc * c.Z);
            if (!float.IsFinite(surface))
            {
                continue;
            }

            height = MathF.Max(height, surface);
            found = true;
        }

        if (!found)
        {
            height = 0f;
        }
        return found;
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private static bool IsFinite(Matrix4x4 value)
    {
        return float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) &&
               float.IsFinite(value.M14) &&
               float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) &&
               float.IsFinite(value.M24) &&
               float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) &&
               float.IsFinite(value.M34) &&
               float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) &&
               float.IsFinite(value.M44);
    }
}

/// <summary>Two triangle-list primitives prepared for one six-vertex water draw instance.</summary>
internal readonly record struct NifWaterTrianglePacket(
    Vector3 Vertex0,
    Vector3 Vertex1,
    Vector3 Vertex2,
    Vector3 Vertex3,
    Vector3 Vertex4,
    Vector3 Vertex5);
