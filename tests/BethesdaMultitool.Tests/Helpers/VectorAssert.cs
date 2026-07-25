using System.Numerics;
using Xunit;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Vector equality helpers mirroring <see cref="MatrixAssert" />. Centralizes the
///     component-wise compares that were previously redefined per test file (AssertVector /
///     AssertVec). Callers map their old precision digits to epsilon: 6 digits → 1e-6,
///     5 → 1e-5, 4 → 1e-4, 3 → 1e-3.
/// </summary>
internal static class VectorAssert
{
    public static void Equal(Vector2 expected, Vector2 actual, float epsilon = 1e-6f)
    {
        Assert.InRange(actual.X, expected.X - epsilon, expected.X + epsilon);
        Assert.InRange(actual.Y, expected.Y - epsilon, expected.Y + epsilon);
    }

    public static void Equal(Vector3 expected, Vector3 actual, float epsilon = 1e-6f)
    {
        Assert.InRange(actual.X, expected.X - epsilon, expected.X + epsilon);
        Assert.InRange(actual.Y, expected.Y - epsilon, expected.Y + epsilon);
        Assert.InRange(actual.Z, expected.Z - epsilon, expected.Z + epsilon);
    }

    public static void Equal(Vector4 expected, Vector4 actual, float epsilon = 1e-6f)
    {
        Assert.InRange(actual.X, expected.X - epsilon, expected.X + epsilon);
        Assert.InRange(actual.Y, expected.Y - epsilon, expected.Y + epsilon);
        Assert.InRange(actual.Z, expected.Z - epsilon, expected.Z + epsilon);
        Assert.InRange(actual.W, expected.W - epsilon, expected.W + epsilon);
    }

    /// <summary>Predicate variant for tests that branch on equality rather than assert it.</summary>
    public static bool NearlyEqual(Vector3 left, Vector3 right, float epsilon)
    {
        return MathF.Abs(left.X - right.X) <= epsilon &&
               MathF.Abs(left.Y - right.Y) <= epsilon &&
               MathF.Abs(left.Z - right.Z) <= epsilon;
    }
}