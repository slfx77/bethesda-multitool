using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using BethesdaMultitool.Tests.Helpers;
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
            0f, 1f, 0f
        };
        var transform = Matrix4x4.CreateScale(4f) *
                        Matrix4x4.CreateRotationZ(MathF.PI / 2f);

        var transformed = NifGeometryTransformUtils.TransformNormals(source, transform);

        Assert.Equal(0.25f, Read(transformed, 0).Length(), 5);
        Assert.Equal(1f, Read(transformed, 1).Length(), 5);
        VectorAssert.Equal(Vector3.UnitY, Vector3.Normalize(Read(transformed, 0)), 1e-5f);
        VectorAssert.Equal(-Vector3.UnitX, Vector3.Normalize(Read(transformed, 1)), 1e-5f);
    }

    [Fact]
    public void TransformNormals_DegenerateVectorRemainsDegenerate()
    {
        var transformed = NifGeometryTransformUtils.TransformNormals(
            [0f, 0f, 0f],
            Matrix4x4.CreateScale(3f));

        VectorAssert.Equal(Vector3.Zero, Read(transformed, 0), 1e-5f);
    }

    private static Vector3 Read(float[] values, int vertex)
    {
        return new Vector3(values[vertex * 3], values[vertex * 3 + 1], values[vertex * 3 + 2]);
    }
}