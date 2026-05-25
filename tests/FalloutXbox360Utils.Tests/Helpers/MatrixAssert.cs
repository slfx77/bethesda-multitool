using System.Numerics;
using Xunit;

namespace FalloutXbox360Utils.Tests.Helpers;

/// <summary>
///     Matrix4x4 equality helpers used by NIF transform / NPC head bone correction tests.
///     Centralizes the 16-element element-wise compare so individual tests don't reinvent it.
/// </summary>
internal static class MatrixAssert
{
    /// <summary>
    ///     Assert two Matrix4x4 values are equal element-wise within <paramref name="epsilon"/>.
    ///     Each element is checked with <see cref="Assert.InRange{T}"/> so a failure points at
    ///     the specific offending element.
    /// </summary>
    public static void Equal(Matrix4x4 expected, Matrix4x4 actual, float epsilon = 0.001f)
    {
        Assert.InRange(actual.M11, expected.M11 - epsilon, expected.M11 + epsilon);
        Assert.InRange(actual.M12, expected.M12 - epsilon, expected.M12 + epsilon);
        Assert.InRange(actual.M13, expected.M13 - epsilon, expected.M13 + epsilon);
        Assert.InRange(actual.M14, expected.M14 - epsilon, expected.M14 + epsilon);
        Assert.InRange(actual.M21, expected.M21 - epsilon, expected.M21 + epsilon);
        Assert.InRange(actual.M22, expected.M22 - epsilon, expected.M22 + epsilon);
        Assert.InRange(actual.M23, expected.M23 - epsilon, expected.M23 + epsilon);
        Assert.InRange(actual.M24, expected.M24 - epsilon, expected.M24 + epsilon);
        Assert.InRange(actual.M31, expected.M31 - epsilon, expected.M31 + epsilon);
        Assert.InRange(actual.M32, expected.M32 - epsilon, expected.M32 + epsilon);
        Assert.InRange(actual.M33, expected.M33 - epsilon, expected.M33 + epsilon);
        Assert.InRange(actual.M34, expected.M34 - epsilon, expected.M34 + epsilon);
        Assert.InRange(actual.M41, expected.M41 - epsilon, expected.M41 + epsilon);
        Assert.InRange(actual.M42, expected.M42 - epsilon, expected.M42 + epsilon);
        Assert.InRange(actual.M43, expected.M43 - epsilon, expected.M43 + epsilon);
        Assert.InRange(actual.M44, expected.M44 - epsilon, expected.M44 + epsilon);
    }

    /// <summary>
    ///     Predicate variant — returns true when every element of <paramref name="left"/> and
    ///     <paramref name="right"/> agrees within <paramref name="epsilon"/>. Use this when
    ///     the test needs to branch on equality (e.g. Assert.False after computing a delta).
    /// </summary>
    public static bool NearlyEqual(Matrix4x4 left, Matrix4x4 right, float epsilon)
    {
        return MathF.Abs(left.M11 - right.M11) <= epsilon &&
               MathF.Abs(left.M12 - right.M12) <= epsilon &&
               MathF.Abs(left.M13 - right.M13) <= epsilon &&
               MathF.Abs(left.M14 - right.M14) <= epsilon &&
               MathF.Abs(left.M21 - right.M21) <= epsilon &&
               MathF.Abs(left.M22 - right.M22) <= epsilon &&
               MathF.Abs(left.M23 - right.M23) <= epsilon &&
               MathF.Abs(left.M24 - right.M24) <= epsilon &&
               MathF.Abs(left.M31 - right.M31) <= epsilon &&
               MathF.Abs(left.M32 - right.M32) <= epsilon &&
               MathF.Abs(left.M33 - right.M33) <= epsilon &&
               MathF.Abs(left.M34 - right.M34) <= epsilon &&
               MathF.Abs(left.M41 - right.M41) <= epsilon &&
               MathF.Abs(left.M42 - right.M42) <= epsilon &&
               MathF.Abs(left.M43 - right.M43) <= epsilon &&
               MathF.Abs(left.M44 - right.M44) <= epsilon;
    }
}
