using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

public sealed class Tes3PlacedWaterClassifierTests
{
    [Theory]
    [InlineData(@"Tx_V_water_01.tga")]
    [InlineData(@"textures\TX_V_WATER_01.TGA")]
    [InlineData("textures/tx_v_water_01.tga")]
    public void ExactCombinedSignature_IsClassifiedWithoutModelOrShapeName(string texturePath)
    {
        var candidate = CreateCandidate(texturePath: texturePath);

        Assert.True(Tes3PlacedWaterClassifier.IsWaterSurface(0x04000002u, candidate));
    }

    [Fact]
    public void SeparateHorizontalFacets_DoNotNeedOneGlobalHeight()
    {
        var candidate = CreateCandidate(
            positions:
            [
                0f, 0f, -20f,
                4f, 0f, -20f,
                0f, 4f, -20f,
                10f, 10f, 800f,
                14f, 10f, 800f,
                10f, 14f, 800f
            ],
            triangles: [0, 1, 2, 3, 4, 5],
            normals: UpNormals(6));

        Assert.True(Tes3PlacedWaterClassifier.IsWaterSurface(0x04000002u, candidate));
    }

    [Fact]
    public void OrdinaryTes3TexturedMesh_IsNotDiverted()
    {
        var ordinary = CreateCandidate(texturePath: @"textures\x\vivec_palace_wall_01.tga");
        var duplicateLeafInAnotherDirectory = CreateCandidate(
            texturePath: @"textures\architecture\tx_v_water_01.tga");

        Assert.False(Tes3PlacedWaterClassifier.IsWaterSurface(0x04000002u, ordinary));
        Assert.False(Tes3PlacedWaterClassifier.IsWaterSurface(
            0x04000002u,
            duplicateLeafInAnotherDirectory));
    }

    [Fact]
    public void SimilarMaterial_FailsClosedWhenAnyRequiredSemanticIsMissing()
    {
        Assert.False(Tes3PlacedWaterClassifier.IsWaterSurface(
            0x14000005u, CreateCandidate()));
        Assert.False(Tes3PlacedWaterClassifier.IsWaterSurface(
            0x04000002u, CreateCandidate(hasAlphaBlend: false)));
        Assert.False(Tes3PlacedWaterClassifier.IsWaterSurface(
            0x04000002u, CreateCandidate(hasAlphaTest: true)));
        Assert.False(Tes3PlacedWaterClassifier.IsWaterSurface(
            0x04000002u, CreateCandidate(sourceBlend: 1)));
        Assert.False(Tes3PlacedWaterClassifier.IsWaterSurface(
            0x04000002u, CreateCandidate(destinationBlend: 1)));
        Assert.False(Tes3PlacedWaterClassifier.IsWaterSurface(
            0x04000002u, CreateCandidate(materialAlpha: 1f)));
        Assert.False(Tes3PlacedWaterClassifier.IsWaterSurface(
            0x04000002u, CreateCandidate(materialDiffuse: (0.9f, 1f, 1f))));
        Assert.False(Tes3PlacedWaterClassifier.IsWaterSurface(
            0x04000002u, CreateCandidate(uvScroll: Vector2.Zero)));
        Assert.False(Tes3PlacedWaterClassifier.IsWaterSurface(
            0x04000002u, CreateCandidate(uvScroll: new Vector2(float.NaN, 0f))));
    }

    [Fact]
    public void NonHorizontalOrMalformedGeometry_FailsClosed()
    {
        Assert.False(Tes3PlacedWaterClassifier.IsWaterSurface(
            0x04000002u,
            CreateCandidate(positions: [0f, 0f, 0f, 4f, 0f, 0f, 0f, 4f, 1f])));
        Assert.False(Tes3PlacedWaterClassifier.IsWaterSurface(
            0x04000002u,
            CreateCandidate(normals: [0f, 0f, 1f, 0f, 0f, 1f, 1f, 0f, 0f])));
        Assert.False(Tes3PlacedWaterClassifier.IsWaterSurface(
            0x04000002u,
            CreateCandidate(positions: [0f, 0f, 0f, 4f, 0f, 0f, float.PositiveInfinity, 4f, 0f])));
        Assert.False(Tes3PlacedWaterClassifier.IsWaterSurface(
            0x04000002u,
            CreateCandidate(triangles: [0, 1, 3])));
        Assert.False(Tes3PlacedWaterClassifier.IsWaterSurface(
            0x04000002u,
            CreateCandidate(triangles: [0, 0, 2])));
    }

    private static RenderableSubmesh CreateCandidate(
        string texturePath = @"textures\tx_v_water_01.tga",
        bool hasAlphaBlend = true,
        bool hasAlphaTest = false,
        byte sourceBlend = 6,
        byte destinationBlend = 7,
        float materialAlpha = 0.5f,
        (float R, float G, float B)? materialDiffuse = null,
        Vector2? uvScroll = null,
        float[]? positions = null,
        ushort[]? triangles = null,
        float[]? normals = null) =>
        new()
        {
            Positions = positions ?? [0f, 0f, 10f, 4f, 0f, 10f, 0f, 4f, 10f],
            Triangles = triangles ?? [0, 1, 2],
            Normals = normals ?? UpNormals(3),
            DiffuseTexturePath = texturePath,
            HasAlphaBlend = hasAlphaBlend,
            HasAlphaTest = hasAlphaTest,
            SrcBlendMode = sourceBlend,
            DstBlendMode = destinationBlend,
            MaterialAlpha = materialAlpha,
            MaterialDiffuse = materialDiffuse ?? (1f, 1f, 1f),
            UvScrollVelocity = uvScroll ?? new Vector2(-0.05f, 0f)
        };

    private static float[] UpNormals(int count)
    {
        var normals = new float[count * 3];
        for (var i = 0; i < count; i++)
        {
            normals[(i * 3) + 2] = 1f;
        }

        return normals;
    }
}
