using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class NifGeometryTransformUtilsTests
{
    [Fact]
    public void TransformNormals_RotatesAndRemovesUniformScaleWithoutDiscardingAuthoredMagnitude()
    {
        var source = new[]
        {
            0.25f, 0f, 0f,
            0f, 1f, 0f,
        };
        var transform = Matrix4x4.CreateScale(4f) *
                        Matrix4x4.CreateRotationZ(MathF.PI / 2f);

        var transformed = NifGeometryTransformUtils.TransformNormals(source, transform);

        Assert.Equal(0.25f, Read(transformed, 0).Length(), 5);
        Assert.Equal(1f, Read(transformed, 1).Length(), 5);
        AssertVector(Vector3.UnitY, Vector3.Normalize(Read(transformed, 0)));
        AssertVector(-Vector3.UnitX, Vector3.Normalize(Read(transformed, 1)));
    }

    [Fact]
    public void TransformNormals_DegenerateVectorRemainsDegenerate()
    {
        var transformed = NifGeometryTransformUtils.TransformNormals(
            [0f, 0f, 0f],
            Matrix4x4.CreateScale(3f));

        AssertVector(Vector3.Zero, Read(transformed, 0));
    }

    private static Vector3 Read(float[] values, int vertex) =>
        new(values[vertex * 3], values[vertex * 3 + 1], values[vertex * 3 + 2]);

    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 5);
        Assert.Equal(expected.Y, actual.Y, 5);
        Assert.Equal(expected.Z, actual.Z, 5);
    }
}
