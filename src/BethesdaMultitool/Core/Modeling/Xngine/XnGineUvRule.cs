using System.Numerics;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Modeling.Xngine;

/// <summary>
///     The portable UV of an XnGine plane corner (cut-1c plan section 3.2, "Portable UVs"): an independent
///     re-implementation of the reference rule the legacy decomposer ports from Daggerfall Unity's FaceUVTool, computed
///     from the STORED int16 values (<see cref="XnGineUvHandling.Stored" />) so the same parse also feeds the
///     <c>xngine.uv16</c> stream. Marked Assumed: the rule is the reference's, not established from the engine.
/// </summary>
/// <remarks>
///     <para>
///         The rule: the packed-UV unfold (<see cref="XnGineMesh.UnpackUv" />) applies to corners 0 to 2 only when the
///         caller says so (a Daggerfall record with an object id below 905, plan decision D2; never Redguard or
///         Battlespire); a 3-corner plane accumulates corners 1 and 2 as deltas; an n-gon fits <c>U = A x + B y + D</c>
///         (and V) through its first three accumulated corners in the polygon's own plane and evaluates it with the
///         reference's int truncation for every later corner; a singular fit leaves EVERY corner at its raw value.
///     </para>
///     <para>
///         The arithmetic is the reference's, operation for operation in <see cref="Vector3" /> float32 and int
///         truncation, in SOURCE coordinates (before the Y negation), so a corner's value agrees exactly with the legacy
///         decomposer's on the same parse; the A3x oracle compares the two per plane. Measured (plan section 0.2): the
///         stored later corners agree with this rule's fit within 2 units on 92,554 of 116,785 Daggerfall values at ids
///         of 905 and above and 40,416 of 45,868 below; on polygons whose first corners lie in the fold range the stored
///         data agrees with the RAW reading and contradicts the unfold, which is why the unfold stays Assumed.
///     </para>
/// </remarks>
internal static class XnGineUvRule
{
    /// <summary>The stable identity of this UV rule, recorded in the native state.</summary>
    public const string RuleId = "bmt.xngine.uv-reference/1";

    /// <summary>The corners the packed-UV unfold may touch (0, 1 and 2).</summary>
    public const int PackedCorners = 3;

    /// <summary>
    ///     Derives the rule's u and v for every corner of one plane.
    /// </summary>
    /// <param name="corners">The corner positions in source coordinates (the float32 vectors the fit reads).</param>
    /// <param name="stored">The stored u and v per corner, in the same order.</param>
    /// <param name="unfold">Whether the packed-UV unfold applies to corners 0 to 2 (see the remarks).</param>
    /// <exception cref="ArgumentException">The two spans differ in length.</exception>
    public static XnGineUvRuleResult Compute(ReadOnlySpan<Vector3> corners, ReadOnlySpan<XnGineCornerUv> stored,
        bool unfold)
    {
        if (corners.Length != stored.Length)
        {
            throw new ArgumentException("A UV per corner is required.", nameof(stored));
        }

        var raw = stored.ToArray();
        var unfolded = 0;
        if (unfold)
        {
            for (var q = 0; q < Math.Min(PackedCorners, raw.Length); q++)
            {
                var u = XnGineMesh.UnpackUv(raw[q].U);
                var v = XnGineMesh.UnpackUv(raw[q].V);
                unfolded += (u != raw[q].U ? 1 : 0) + (v != raw[q].V ? 1 : 0);
                raw[q] = new XnGineCornerUv(u, v);
            }
        }

        if (raw.Length < 3)
        {
            return new XnGineUvRuleResult(raw, false, unfolded);
        }

        if (raw.Length == 3)
        {
            var second = new XnGineCornerUv(raw[1].U + raw[0].U, raw[1].V + raw[0].V);
            var third = new XnGineCornerUv(raw[2].U + second.U, raw[2].V + second.V);
            return new XnGineUvRuleResult([raw[0], second, third], false, unfolded);
        }

        var fitted = Fit(corners, raw);
        return fitted is null
            ? new XnGineUvRuleResult(raw, true, unfolded)
            : new XnGineUvRuleResult(fitted, false, unfolded);
    }

    /// <summary>
    ///     The n-gon plane fit (see the type remarks): project onto the polygon's plane through its first three corners,
    ///     solve the 3x3 system for the accumulated first three UVs, evaluate later corners with int truncation. Null when
    ///     the first edge has no length or the system is exactly singular.
    /// </summary>
    private static XnGineCornerUv[]? Fit(ReadOnlySpan<Vector3> corners, XnGineCornerUv[] raw)
    {
        var p0 = corners[0];
        var p1 = corners[1];
        var p2 = corners[2];

        var axisU = p1 - p0;
        var axisV = p2 - p0;
        var axisUDot = Vector3.Dot(axisU, axisU);
        if (axisUDot <= 0)
        {
            return null;
        }

        axisV -= axisU * (Vector3.Dot(axisV, axisU) / axisUDot);
        axisU = Vector3.Normalize(axisU);
        axisV = Vector3.Normalize(axisV);

        var x0 = (int)Vector3.Dot(p0, axisU);
        var y0 = (int)Vector3.Dot(p0, axisV);
        var x1 = (int)Vector3.Dot(p1, axisU);
        var y1 = (int)Vector3.Dot(p1, axisV);
        var x2 = (int)Vector3.Dot(p2, axisU);
        var y2 = (int)Vector3.Dot(p2, axisV);

        float u0 = raw[0].U, v0 = raw[0].V;
        float u1 = raw[1].U + u0, v1 = raw[1].V + v0;
        float u2 = raw[2].U + u1, v2 = raw[2].V + v1;

        // An exactly singular system, spelled without a float equality test: float.Epsilon is the smallest
        // subnormal, so the open interval admits nothing but zero.
        var determinant = x0 * (float)y1 + y0 * (float)x2 + x1 * (float)y2 - y1 * (float)x2 - y0 * (float)x1 -
                          x0 * (float)y2;
        if (determinant is > -float.Epsilon and < float.Epsilon)
        {
            return null;
        }

        var xi0 = (y1 - y2) / determinant;
        var xi1 = (-x1 + x2) / determinant;
        var xi2 = (x1 * (float)y2 - x2 * (float)y1) / determinant;
        var yi0 = (-y0 + y2) / determinant;
        var yi1 = (x0 - x2) / determinant;
        var yi2 = (-x0 * (float)y2 + x2 * (float)y0) / determinant;
        var zi0 = (y0 - y1) / determinant;
        var zi1 = (-x0 + x1) / determinant;
        var zi2 = (x0 * (float)y1 - x1 * (float)y0) / determinant;

        var ua = u0 * xi0 + u1 * yi0 + u2 * zi0;
        var ub = u0 * xi1 + u1 * yi1 + u2 * zi1;
        var ud = u0 * xi2 + u1 * yi2 + u2 * zi2;
        var va = v0 * xi0 + v1 * yi0 + v2 * zi0;
        var vb = v0 * xi1 + v1 * yi1 + v2 * zi1;
        var vd = v0 * xi2 + v1 * yi2 + v2 * zi2;

        var result = new XnGineCornerUv[raw.Length];
        result[0] = new XnGineCornerUv((int)u0, (int)v0);
        result[1] = new XnGineCornerUv((int)u1, (int)v1);
        result[2] = new XnGineCornerUv((int)u2, (int)v2);
        for (var i = 3; i < raw.Length; i++)
        {
            var x = (int)Vector3.Dot(corners[i], axisU);
            var y = (int)Vector3.Dot(corners[i], axisV);
            result[i] = new XnGineCornerUv((int)(x * ua + y * ub + ud), (int)(x * va + y * vb + vd));
        }

        return result;
    }
}
