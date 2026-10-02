using System.Numerics;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The cut-1a TRS rule for NiAVObject local transforms (docs/design/cut1a-nif-reader-plan-20260924.md, section 3,
///     "TRS rule"). Pure: no state, no source access.
/// </summary>
/// <remarks>
///     <para>
///         nif.xml stores <c>Matrix33</c> row-major for column vectors (nif.xml:5932-5934): the nine stored floats are
///         R11 R12 R13 R21 ... R33 and a point transforms as R v. The document composes row vectors (v' = v M), so its
///         rotation is the transpose R^T, exactly as NifObjectBlockReader.cs:130-146 already builds render matrices.
///     </para>
///     <para>
///         Let E = max |R R^T - I|, computed in double. When E &lt;= 1e-5 and det R &gt; 0 the result is TRS with the
///         quaternion of R^T and scale (s, s, s). When E &lt;= 1e-5 and det R &lt; 0 it is TRS with the quaternion of
///         (-R)^T and scale (-s, -s, -s); that is exact because (-s)(-R) = sR. Otherwise the result is a matrix node:
///         S R^T T composed in double and rounded to Float32 once per element, so every element is a bit-exact copy of
///         the stored float when s = 1 and within half an ulp of the exact product otherwise. The quaternion is
///         computed and normalized in double, so its Float32 squared length is within Shared's 1e-4 unit check.
///     </para>
/// </remarks>
internal static class NifModelTransform
{
    /// <summary>The largest orthonormality error E that is still represented as explicit TRS components.</summary>
    public const double OrthonormalTolerance = 1e-5;

    /// <summary>Applies the TRS rule to one stored NiAVObject transform.</summary>
    /// <param name="translation">The stored Translation.</param>
    /// <param name="rowMajorRotation">The nine stored Matrix33 floats in file order (row-major R).</param>
    /// <param name="scale">The stored uniform Scale.</param>
    /// <returns>The chosen representation, its row-vector matrix and the measurements behind the choice.</returns>
    /// <exception cref="ArgumentException">The rotation does not have nine elements.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A translation, rotation or scale value is not finite.</exception>
    /// <remarks>
    ///     A matrix node whose product overflows Float32 is returned as computed; callers check
    ///     <see cref="SceneAffineTransform.IsFiniteAffine" /> and report the source location.
    /// </remarks>
    public static NifModelTransformResult Resolve(Vector3 translation, ReadOnlySpan<float> rowMajorRotation, float scale)
    {
        if (rowMajorRotation.Length != 9)
        {
            throw new ArgumentException(
                $"A Matrix33 has nine elements, not {rowMajorRotation.Length}.", nameof(rowMajorRotation));
        }

        if (!float.IsFinite(translation.X) || !float.IsFinite(translation.Y) || !float.IsFinite(translation.Z))
        {
            throw new ArgumentOutOfRangeException(nameof(translation), translation, "The translation must be finite.");
        }

        if (!float.IsFinite(scale))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "The scale must be finite.");
        }

        Span<double> stored = stackalloc double[9];
        for (var i = 0; i < 9; i++)
        {
            if (!float.IsFinite(rowMajorRotation[i]))
            {
                throw new ArgumentOutOfRangeException(nameof(rowMajorRotation), rowMajorRotation[i],
                    $"Rotation element {i} must be finite.");
            }

            stored[i] = rowMajorRotation[i];
        }

        var error = OrthonormalityError(stored);
        var determinant = Determinant(stored);
        if (error <= OrthonormalTolerance)
        {
            var negated = determinant < 0;
            var sign = negated ? -1.0 : 1.0;

            // The document rotation D = (sign R)^T, row-major: D[row, column] = sign * R[column, row].
            Span<double> rotation = stackalloc double[9];
            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 3; column++)
                {
                    rotation[row * 3 + column] = sign * stored[column * 3 + row];
                }
            }

            var quaternion = ToQuaternion(rotation);
            var componentScale = negated ? -scale : scale;
            var trs = new SceneTrs(translation, quaternion, new Vector3(componentScale, componentScale, componentScale));
            return new NifModelTransformResult(
                negated ? NifModelTransformKind.TrsNegatedScale : NifModelTransformKind.Trs,
                trs,
                trs.ToMatrix(),
                error,
                determinant,
                ReconstructionError(quaternion, rotation));
        }

        var matrix = ComposeMatrix(translation, rowMajorRotation, scale);
        return new NifModelTransformResult(NifModelTransformKind.Matrix, null, matrix, error, determinant, null);
    }

    /// <summary>
    ///     S R^T T for one stored NiTransform, with no orthonormality analysis: the row-vector matrix whose linear part
    ///     is the scale times the transposed stored rotation and whose translation row is the stored translation. Each
    ///     linear element is the product computed in double and rounded to Float32 once, which equals the Float32
    ///     product because a product of two Float32 values is exact in double; every element is therefore a bit-exact
    ///     copy of the stored float when s = 1. The matrix nodes of <see cref="Resolve" /> and the NiSkinData inverse
    ///     binds (<see cref="NifModelSkinReader" />) both use it, so the two can never disagree.
    /// </summary>
    /// <param name="translation">The stored Translation.</param>
    /// <param name="rowMajorRotation">The nine stored Matrix33 floats in file order (row-major R).</param>
    /// <param name="scale">The stored uniform Scale.</param>
    /// <returns>The composed matrix; an element may overflow to infinity, which callers check.</returns>
    /// <exception cref="ArgumentException">The rotation does not have nine elements.</exception>
    internal static Matrix4x4 ComposeMatrix(Vector3 translation, ReadOnlySpan<float> rowMajorRotation, float scale)
    {
        if (rowMajorRotation.Length != 9)
        {
            throw new ArgumentException(
                $"A Matrix33 has nine elements, not {rowMajorRotation.Length}.", nameof(rowMajorRotation));
        }

        double s = scale;
        return new Matrix4x4(
            (float)(s * rowMajorRotation[0]), (float)(s * rowMajorRotation[3]), (float)(s * rowMajorRotation[6]), 0f,
            (float)(s * rowMajorRotation[1]), (float)(s * rowMajorRotation[4]), (float)(s * rowMajorRotation[7]), 0f,
            (float)(s * rowMajorRotation[2]), (float)(s * rowMajorRotation[5]), (float)(s * rowMajorRotation[8]), 0f,
            translation.X, translation.Y, translation.Z, 1f);
    }

    /// <summary>E = max |R R^T - I| over a row-major 3x3 matrix, in double.</summary>
    internal static double OrthonormalityError(ReadOnlySpan<double> rowMajor)
    {
        var error = 0.0;
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                var dot = rowMajor[i * 3] * rowMajor[j * 3] +
                          rowMajor[i * 3 + 1] * rowMajor[j * 3 + 1] +
                          rowMajor[i * 3 + 2] * rowMajor[j * 3 + 2];
                error = Math.Max(error, Math.Abs(dot - (i == j ? 1.0 : 0.0)));
            }
        }

        return error;
    }

    /// <summary>The determinant of a row-major 3x3 matrix, in double.</summary>
    internal static double Determinant(ReadOnlySpan<double> m)
    {
        return m[0] * (m[4] * m[8] - m[5] * m[7]) -
               m[1] * (m[3] * m[8] - m[5] * m[6]) +
               m[2] * (m[3] * m[7] - m[4] * m[6]);
    }

    /// <summary>
    ///     The unit quaternion of a proper rotation given as a row-major row-vector matrix (the System.Numerics
    ///     convention used by <see cref="SceneTrs.ToMatrix" />), computed with the same branch structure as
    ///     <see cref="Quaternion.CreateFromRotationMatrix" /> but in double, normalized in double, then rounded once.
    /// </summary>
    private static Quaternion ToQuaternion(ReadOnlySpan<double> m)
    {
        double m11 = m[0], m12 = m[1], m13 = m[2];
        double m21 = m[3], m22 = m[4], m23 = m[5];
        double m31 = m[6], m32 = m[7], m33 = m[8];
        double x, y, z, w;
        var trace = m11 + m22 + m33;
        if (trace > 0)
        {
            var root = Math.Sqrt(trace + 1.0);
            w = root * 0.5;
            var inverse = 0.5 / root;
            x = (m23 - m32) * inverse;
            y = (m31 - m13) * inverse;
            z = (m12 - m21) * inverse;
        }
        else if (m11 >= m22 && m11 >= m33)
        {
            var root = Math.Sqrt(1.0 + m11 - m22 - m33);
            var inverse = 0.5 / root;
            x = 0.5 * root;
            y = (m12 + m21) * inverse;
            z = (m13 + m31) * inverse;
            w = (m23 - m32) * inverse;
        }
        else if (m22 > m33)
        {
            var root = Math.Sqrt(1.0 + m22 - m11 - m33);
            var inverse = 0.5 / root;
            x = (m21 + m12) * inverse;
            y = 0.5 * root;
            z = (m32 + m23) * inverse;
            w = (m31 - m13) * inverse;
        }
        else
        {
            var root = Math.Sqrt(1.0 + m33 - m11 - m22);
            var inverse = 0.5 / root;
            x = (m31 + m13) * inverse;
            y = (m32 + m23) * inverse;
            z = 0.5 * root;
            w = (m12 - m21) * inverse;
        }

        var length = Math.Sqrt(x * x + y * y + z * z + w * w);
        return new Quaternion((float)(x / length), (float)(y / length), (float)(z / length), (float)(w / length));
    }

    /// <summary>
    ///     The largest absolute difference between the rotation matrix that <see cref="Matrix4x4.CreateFromQuaternion" />
    ///     builds from the Float32 quaternion (evaluated here in double) and the target row-major rotation.
    /// </summary>
    private static double ReconstructionError(Quaternion quaternion, ReadOnlySpan<double> target)
    {
        double x = quaternion.X, y = quaternion.Y, z = quaternion.Z, w = quaternion.W;
        double xx = x * x, yy = y * y, zz = z * z;
        double xy = x * y, wz = w * z, xz = x * z, wy = w * y, yz = y * z, wx = w * x;
        Span<double> rebuilt =
        [
            1.0 - 2.0 * (yy + zz), 2.0 * (xy + wz), 2.0 * (xz - wy),
            2.0 * (xy - wz), 1.0 - 2.0 * (zz + xx), 2.0 * (yz + wx),
            2.0 * (xz + wy), 2.0 * (yz - wx), 1.0 - 2.0 * (yy + xx)
        ];
        var error = 0.0;
        for (var i = 0; i < 9; i++)
        {
            error = Math.Max(error, Math.Abs(rebuilt[i] - target[i]));
        }

        return error;
    }
}
