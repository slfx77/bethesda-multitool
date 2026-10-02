using System.Numerics;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Composes a TexDesc NiTextureTransform into Shared's <see cref="SceneTextureTransform" /> (plan section 3,
///     NiTexturingProperty; section 7, "Texture transform formulas ... are recalled, not measured"). Everything here is
///     Assumed and recorded per layer.
/// </summary>
/// <remarks>
///     <para>
///         The method products are nif.xml's own (TransformMethod, nif.xml:6480-6496), read as 3x3 affine matrices applied
///         to column UV vectors, rightmost first. C translates by +Center, Back (B) by -Center, T by +Translation, S scales,
///         R rotates counterclockwise by Rotation radians, FromMaya (F) maps v to 1 - v:
///         0 Maya (deprecated) C R B T S; 1 Max C S R T B; 2 Maya C R B F T S.
///     </para>
///     <para>
///         Shared's transform is scale, then counterclockwise rotation, then offset (linear part R(theta) diag(sx, sy)).
///         The composed linear part is decomposed into that form; when its columns are not orthogonal (a non-uniform scale
///         combined with a rotation in the Max order) the transform has no Shared equivalent and none is returned, so the
///         caller reports it and keeps the stored values in native state.
///     </para>
/// </remarks>
internal static class NifModelTextureTransform
{
    /// <summary>The relative residual above which a decomposition is refused.</summary>
    public const double Tolerance = 1e-6;

    /// <summary>The formula text recorded in native state.</summary>
    public const string Formula =
        "nif.xml TransformMethod products on column UV vectors (0: C R B T S; 1: C S R T B; 2: C R B F T S; " +
        "C = +Center, B = -Center, F: v -> 1 - v), decomposed into scale, counterclockwise rotation, offset (Assumed)";

    /// <summary>Composes and decomposes one transform.</summary>
    /// <param name="method">The stored Transform Method (0, 1 or 2).</param>
    /// <param name="translation">The stored Translation.</param>
    /// <param name="scale">The stored Scale.</param>
    /// <param name="rotation">The stored Rotation (radians, counterclockwise: Assumed).</param>
    /// <param name="center">The stored Center.</param>
    /// <param name="transform">The Shared transform when representable.</param>
    /// <param name="residual">The largest absolute difference between the composed and the reconstructed linear part.</param>
    /// <returns>True when a finite, representable transform was produced.</returns>
    public static bool TryCompose(uint method, Vector2 translation, Vector2 scale, float rotation, Vector2 center,
        out SceneTextureTransform transform, out double residual)
    {
        transform = default;
        residual = double.NaN;
        if (!float.IsFinite(translation.X) || !float.IsFinite(translation.Y) || !float.IsFinite(scale.X) ||
            !float.IsFinite(scale.Y) || !float.IsFinite(rotation) || !float.IsFinite(center.X) ||
            !float.IsFinite(center.Y))
        {
            return false;
        }

        var c = Translate(center.X, center.Y);
        var b = Translate(-center.X, -center.Y);
        var t = Translate(translation.X, translation.Y);
        var s = new Affine(scale.X, 0, 0, scale.Y, 0, 0);
        var (sin, cos) = Math.SinCos((double)rotation);
        var r = new Affine(cos, -sin, sin, cos, 0, 0);
        var f = new Affine(1, 0, 0, -1, 0, 1);
        Affine m;
        switch (method)
        {
            case 0:
                m = c * r * b * t * s;
                break;
            case 1:
                m = c * s * r * t * b;
                break;
            case 2:
                m = c * r * b * f * t * s;
                break;
            default:
                return false;
        }

        // Linear part [[A, B], [C, D]] = R(theta) diag(sx, sy): column 0 = sx (cos, sin), column 1 = sy (-sin, cos).
        var theta = Math.Abs(m.A) + Math.Abs(m.C) > 0 ? Math.Atan2(m.C, m.A) : Math.Atan2(-m.B, m.D);
        var (sinTheta, cosTheta) = Math.SinCos(theta);
        var sx = m.A * cosTheta + m.C * sinTheta;
        var sy = -m.B * sinTheta + m.D * cosTheta;
        residual = Math.Max(Math.Max(Math.Abs(sx * cosTheta - m.A), Math.Abs(sx * sinTheta - m.C)),
            Math.Max(Math.Abs(-sy * sinTheta - m.B), Math.Abs(sy * cosTheta - m.D)));
        var magnitude = Math.Max(1, Math.Max(Math.Max(Math.Abs(m.A), Math.Abs(m.B)), Math.Max(Math.Abs(m.C), Math.Abs(m.D))));
        if (residual > Tolerance * magnitude)
        {
            return false;
        }

        var offset = new Vector2((float)m.X, (float)m.Y);
        var shared = new Vector2((float)sx, (float)sy);
        var angle = (float)theta;
        if (!float.IsFinite(offset.X) || !float.IsFinite(offset.Y) || !float.IsFinite(shared.X) ||
            !float.IsFinite(shared.Y) || !float.IsFinite(angle))
        {
            return false;
        }

        transform = new SceneTextureTransform(offset, shared, angle);
        return true;
    }

    private static Affine Translate(double x, double y)
    {
        return new Affine(1, 0, 0, 1, x, y);
    }

    /// <summary>A 2D affine map on column vectors: [[A, B, X], [C, D, Y], [0, 0, 1]].</summary>
    private readonly record struct Affine(double A, double B, double C, double D, double X, double Y)
    {
        public static Affine operator *(Affine left, Affine right)
        {
            return new Affine(
                left.A * right.A + left.B * right.C,
                left.A * right.B + left.B * right.D,
                left.C * right.A + left.D * right.C,
                left.C * right.B + left.D * right.D,
                left.A * right.X + left.B * right.Y + left.X,
                left.C * right.X + left.D * right.Y + left.Y);
        }
    }
}
