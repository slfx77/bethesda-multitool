using System.Numerics;
using BethesdaMultitool.Core.Modeling.Nif;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     The cut-1a TRS rule (plan section 3): the stored Matrix33 is row-major for column vectors, the document rotation
///     is its transpose, orthonormal rotations within 1e-5 become TRS (a reflection through a negated uniform scale),
///     anything else becomes a matrix node with exact element copies.
/// </summary>
public class NifModelTransformTests
{
    private static readonly float[] Identity = [1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f];

    /// <summary>+90 degrees about Z for column vectors: R (1, 0, 0) = (0, 1, 0). Row-major.</summary>
    private static readonly float[] QuarterTurnAboutZ = [0f, -1f, 0f, 1f, 0f, 0f, 0f, 0f, 1f];

    [Fact]
    public void Identity_IsTrs_WithTheExactIdentityQuaternion()
    {
        var result = NifModelTransform.Resolve(new Vector3(1f, 2f, 3f), Identity, 1f);

        Assert.Equal(NifModelTransformKind.Trs, result.Kind);
        var trs = Assert.NotNull(result.Trs);
        Assert.Equal(Quaternion.Identity, trs.Rotation);
        Assert.Equal(Vector3.One, trs.Scale);
        Assert.Equal(new Vector3(1f, 2f, 3f), trs.Translation);
        Assert.Equal(0.0, result.OrthonormalityError);
        Assert.Equal(1.0, result.Determinant);
        Assert.Equal(0.0, result.QuaternionReconstructionError);
        Assert.Equal(Matrix4x4.CreateTranslation(1f, 2f, 3f), result.Matrix);
    }

    /// <summary>
    ///     The transpose is what makes the row-vector document agree with the column-vector file. Control: reading the
    ///     nine floats straight into the row-vector matrix (no transpose) sends +X to -Y instead of +Y.
    /// </summary>
    [Fact]
    public void QuarterTurn_IsTransposedIntoTheRowVectorConvention()
    {
        var result = NifModelTransform.Resolve(Vector3.Zero, QuarterTurnAboutZ, 1f);

        Assert.Equal(NifModelTransformKind.Trs, result.Kind);
        var mapped = Vector3.Transform(Vector3.UnitX, result.Matrix);
        Assert.Equal(0f, mapped.X, 1e-6f);
        Assert.Equal(1f, mapped.Y, 1e-6f);
        Assert.Equal(0f, mapped.Z, 1e-6f);
        var rotation = result.Trs!.Value.Rotation;
        Assert.Equal(MathF.Sqrt(0.5f), rotation.Z, 1e-6f);
        Assert.Equal(MathF.Sqrt(0.5f), rotation.W, 1e-6f);

        var untransposed = new Matrix4x4(
            QuarterTurnAboutZ[0], QuarterTurnAboutZ[1], QuarterTurnAboutZ[2], 0f,
            QuarterTurnAboutZ[3], QuarterTurnAboutZ[4], QuarterTurnAboutZ[5], 0f,
            QuarterTurnAboutZ[6], QuarterTurnAboutZ[7], QuarterTurnAboutZ[8], 0f,
            0f, 0f, 0f, 1f);
        Assert.Equal(-1f, Vector3.Transform(Vector3.UnitX, untransposed).Y, 1e-6f);
    }

    [Fact]
    public void Float32Rotation_WithinTolerance_IsTrs_WithAUnitQuaternion()
    {
        // A 30-degree turn about a skew axis, rounded to Float32: close to, not exactly, orthonormal.
        var rowVector = Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1f, 2f, 3f)), MathF.PI / 6f);
        float[] stored =
        [
            rowVector.M11, rowVector.M21, rowVector.M31,
            rowVector.M12, rowVector.M22, rowVector.M32,
            rowVector.M13, rowVector.M23, rowVector.M33
        ];

        var result = NifModelTransform.Resolve(new Vector3(0.25f, -8f, 100f), stored, 0.75f);

        Assert.Equal(NifModelTransformKind.Trs, result.Kind);
        Assert.InRange(result.OrthonormalityError, 0.0, NifModelTransform.OrthonormalTolerance);
        var trs = result.Trs!.Value;
        Assert.InRange(MathF.Abs(trs.Rotation.LengthSquared() - 1f), 0f, 1e-6f);
        Assert.InRange(result.QuaternionReconstructionError!.Value, 0.0, 1e-6);
        Assert.Equal(new Vector3(0.75f, 0.75f, 0.75f), trs.Scale);
        AssertLinearClose(Matrix4x4.CreateScale(0.75f) * rowVector, result.Matrix, 1e-6f);
        Assert.Equal(new Vector3(0.25f, -8f, 100f), result.Matrix.Translation);
    }

    /// <summary>
    ///     det R &lt; 0: the quaternion of (-R)^T with scale (-s, -s, -s) composes to exactly s R^T. Control: the same
    ///     quaternion with the positive scale composes to the wrong matrix, so the negated scale is what makes it exact.
    /// </summary>
    [Fact]
    public void Reflection_IsTrsWithNegatedScale_AndComposesToExactlySR()
    {
        float[] mirrorZ = [1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, -1f];

        var result = NifModelTransform.Resolve(Vector3.Zero, mirrorZ, 2f);

        Assert.Equal(NifModelTransformKind.TrsNegatedScale, result.Kind);
        Assert.Equal(-1.0, result.Determinant);
        var trs = result.Trs!.Value;
        Assert.Equal(new Vector3(-2f, -2f, -2f), trs.Scale);
        Assert.Equal(new Quaternion(0f, 0f, 1f, 0f), trs.Rotation);
        Assert.Equal(Matrix4x4.CreateScale(2f, 2f, -2f), result.Matrix);

        var positiveScale = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateFromQuaternion(trs.Rotation);
        Assert.NotEqual(result.Matrix, positiveScale);
    }

    [Fact]
    public void Shear_IsAMatrixNode_WithBitExactTransposedCopies()
    {
        float[] shear = [1f, 0.5f, 0f, 0f, 1f, 0f, 0f, 0f, 1f];

        var result = NifModelTransform.Resolve(new Vector3(4f, 5f, 6f), shear, 1f);

        Assert.Equal(NifModelTransformKind.Matrix, result.Kind);
        Assert.Null(result.Trs);
        Assert.Null(result.QuaternionReconstructionError);
        // Rows (1, 0.5, 0) and (0, 1, 0) have dot product 0.5, the largest deviation of R R^T from I.
        Assert.Equal(0.5, result.OrthonormalityError);
        var expected = new Matrix4x4(
            1f, 0f, 0f, 0f,
            0.5f, 1f, 0f, 0f,
            0f, 0f, 1f, 0f,
            4f, 5f, 6f, 1f);
        Assert.Equal(expected, result.Matrix);
    }

    /// <summary>
    ///     Control for the 1e-5 threshold: perturbing one element of an exact quarter turn by d makes E = d, so 1e-4
    ///     must route to a matrix node while 1e-6 stays TRS.
    /// </summary>
    [Theory]
    [InlineData(1e-4f, NifModelTransformKind.Matrix)]
    [InlineData(1e-6f, NifModelTransformKind.Trs)]
    internal void Perturbation_RoutesAtTheToleranceBoundary(float delta, NifModelTransformKind expected)
    {
        var perturbed = (float[])QuarterTurnAboutZ.Clone();
        perturbed[0] += delta;

        var result = NifModelTransform.Resolve(Vector3.Zero, perturbed, 1f);

        Assert.Equal(expected, result.Kind);
        Assert.Equal(delta, result.OrthonormalityError, 1e-9);
    }

    [Fact]
    public void NonFiniteInput_IsRejected()
    {
        var withNan = (float[])Identity.Clone();
        withNan[4] = float.NaN;

        Assert.Throws<ArgumentOutOfRangeException>(() => NifModelTransform.Resolve(Vector3.Zero, withNan, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NifModelTransform.Resolve(Vector3.Zero, Identity, float.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => NifModelTransform.Resolve(Vector3.Zero, new float[8], 1f));

        // Control: the same inputs with finite values resolve.
        Assert.Equal(NifModelTransformKind.Trs, NifModelTransform.Resolve(Vector3.Zero, Identity, 1f).Kind);
    }

    private static void AssertLinearClose(Matrix4x4 expected, Matrix4x4 actual, float tolerance)
    {
        Assert.Equal(expected.M11, actual.M11, tolerance);
        Assert.Equal(expected.M12, actual.M12, tolerance);
        Assert.Equal(expected.M13, actual.M13, tolerance);
        Assert.Equal(expected.M21, actual.M21, tolerance);
        Assert.Equal(expected.M22, actual.M22, tolerance);
        Assert.Equal(expected.M23, actual.M23, tolerance);
        Assert.Equal(expected.M31, actual.M31, tolerance);
        Assert.Equal(expected.M32, actual.M32, tolerance);
        Assert.Equal(expected.M33, actual.M33, tolerance);
    }
}
