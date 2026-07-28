using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Particles;

public sealed class ParticleDepthSortTests
{
    [Fact]
    public void WriteBackToFrontQuadIndices_SortsTransformedCentersAndKeepsQuadTopology()
    {
        Vector3[] centers =
        [
            new(0f, 0f, 2f),
            new(0f, 0f, 8f),
            new(0f, 0f, 5f)
        ];
        var world = Matrix4x4.CreateTranslation(0f, 0f, 10f);
        var scratch = new ParticleDepthSort.Entry[centers.Length];
        var indices = new ushort[centers.Length * ParticleDepthSort.IndicesPerQuad];

        ParticleDepthSort.WriteBackToFrontQuadIndices(
            centers, world, Vector3.Zero, scratch, indices);

        Assert.Equal<ushort>(
        [
            4, 5, 6, 4, 6, 7,
            8, 9, 10, 8, 10, 11,
            0, 1, 2, 0, 2, 3
        ], indices);
    }

    [Fact]
    public void WriteBackToFrontQuadIndices_UsesStableSourceOrderForEqualDepth()
    {
        Vector3[] centers = [new(-2f, 0f, 0f), new(2f, 0f, 0f), new(1f, 0f, 0f)];
        var scratch = new ParticleDepthSort.Entry[centers.Length];
        var indices = new ushort[centers.Length * ParticleDepthSort.IndicesPerQuad];

        ParticleDepthSort.WriteBackToFrontQuadIndices(
            centers, Matrix4x4.Identity, Vector3.Zero, scratch, indices);

        Assert.Equal<ushort>(
        [
            0, 1, 2, 0, 2, 3,
            4, 5, 6, 4, 6, 7,
            8, 9, 10, 8, 10, 11
        ], indices);
    }
}