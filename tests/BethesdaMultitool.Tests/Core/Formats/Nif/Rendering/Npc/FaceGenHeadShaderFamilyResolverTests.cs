using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class FaceGenHeadShaderFamilyResolverTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(63, 63)]
    [InlineData(64, 64)]
    [InlineData(127, 127)]
    [InlineData(128, 129)]
    [InlineData(129, 130)]
    [InlineData(192, 193)]
    [InlineData(254, 255)]
    [InlineData(255, 255)]
    public void ApplyDefaultDetailModulation_UsesPcOblivionRgb64WithoutAColorCast(byte source, byte expected)
    {
        // PC 00553140: every RGB channel in the manager+DB8 texture is 64.
        // SKIN2000 multiplies by 4 * 64/255, not 1 and not the Fallout Xenon colored tile.
        var texture = TestTextures.Single(source, source, source, 37);

        var result = FaceGenHeadShaderFamilyResolver.ApplyDefaultDetailModulation(texture);

        Assert.Equal<byte>([expected, expected, expected, 37], result.Pixels);
        Assert.Equal<byte>([source, source, source, 37], texture.Pixels);
    }

    [Fact]
    public void ApplyDefaultDetailModulation_PreservesAlphaAndSourceAcrossIndependentResults()
    {
        byte[] pixels = [0, 127, 128, 0, 129, 192, 254, 7, 1, 64, 255, 128, 17, 85, 170, 255];
        var original = pixels.ToArray();
        var texture = DecodedTexture.FromBaseLevel(pixels, 2, 2);
        var result = FaceGenHeadShaderFamilyResolver.ApplyDefaultDetailModulation(texture);

        Assert.Equal(2, result.Width);
        Assert.Equal(2, result.Height);
        Assert.NotSame(texture.Pixels, result.Pixels);
        Assert.Equal<byte>([0, 127, 129, 0, 130, 193, 255, 7, 1, 64, 255, 128, 17, 85, 171, 255], result.Pixels);
        Assert.Equal(original, texture.Pixels);
        result.Pixels[0] = 42;

        var repeated = FaceGenHeadShaderFamilyResolver.ApplyDefaultDetailModulation(texture);

        Assert.NotSame(result.Pixels, repeated.Pixels);
        Assert.Equal<byte>([0, 127, 129, 0, 130, 193, 255, 7, 1, 64, 255, 128, 17, 85, 171, 255], repeated.Pixels);
        Assert.Equal(original, texture.Pixels);
    }

    [Theory]
    [InlineData(3, 5)]
    [InlineData(5, 3)]
    [InlineData(7, 9)]
    [InlineData(4, 4)]
    public void ApplyDefaultDetailModulation_PreservesTheConstantForEveryPixel(int width, int height)
    {
        var sourcePixels = new byte[width * height * 4];
        var expectedPixels = new byte[sourcePixels.Length];
        for (var index = 0; index < sourcePixels.Length; index += 4)
        {
            sourcePixels[index] = 128;
            sourcePixels[index + 1] = 192;
            sourcePixels[index + 2] = 254;
            sourcePixels[index + 3] = (byte)(index / 4);
            // Literal RGB64 results, independent of the production sampler and multiplier.
            expectedPixels[index] = 129;
            expectedPixels[index + 1] = 193;
            expectedPixels[index + 2] = 255;
            expectedPixels[index + 3] = sourcePixels[index + 3];
        }

        var original = sourcePixels.ToArray();
        var texture = DecodedTexture.FromBaseLevel(sourcePixels, width, height);

        var result = FaceGenHeadShaderFamilyResolver.ApplyDefaultDetailModulation(texture);

        Assert.Equal(width, result.Width);
        Assert.Equal(height, result.Height);
        Assert.NotSame(texture.Pixels, result.Pixels);
        Assert.Equal(expectedPixels, result.Pixels);
        Assert.Equal(original, texture.Pixels);
    }

    [Fact]
    public void ApplyClassicSkin2000Material_PreservesPrecomposedAlbedoAndResolvesFamilyNormal()
    {
        const string familyDiffusePath = @"textures\characters\male\headhuman.dds";
        const string effectiveDiffusePath = @"facegen_egt\00000001.dds";
        var normalPath = FaceGenHeadShaderFamilyResolver.BuildSiblingPath(familyDiffusePath, "_n")!;

        using var resolver = new NifTextureResolver();
        resolver.InjectTexture(effectiveDiffusePath, TestTextures.Single(97, 112, 117, 255));
        resolver.InjectTexture(normalPath, TestTextures.Single(127, 127, 255, 255));

        var submesh = new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
            Triangles = [0, 1, 2],
            Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
            UVs = [0f, 0f, 1f, 0f, 0f, 1f],
            DiffuseTexturePath = familyDiffusePath
        };

        FaceGenHeadShaderFamilyResolver.ApplyClassicSkin2000Material(
            [submesh],
            resolver,
            familyDiffusePath,
            effectiveDiffusePath);

        Assert.Equal(effectiveDiffusePath, submesh.DiffuseTexturePath);
        Assert.Equal(normalPath, submesh.NormalMapTexturePath);
        Assert.Equal([1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f], submesh.Tangents!);
        Assert.Equal([0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f], submesh.Bitangents!);
        Assert.True(submesh.IsFaceGen);
        Assert.Equal<byte>([97, 112, 117, 255], resolver.GetTexture(effectiveDiffusePath)!.Pixels);
    }

    [Fact]
    public void ApplyToSubmeshes_DerivesHeadFamilyTexturesAndInjectsComposedDiffuse()
    {
        const string diffusePath = @"textures\characters\male\headhuman.dds";
        const string generatedPath = @"facegen_egt\00000001.dds";

        using var resolver = new NifTextureResolver();
        resolver.InjectTexture(diffusePath, TestTextures.Single(10, 20, 30, 255));
        resolver.InjectTexture(
            FaceGenHeadShaderFamilyResolver.BuildSiblingPath(diffusePath, "_n")!,
            TestTextures.Single(127, 127, 255, 255));
        resolver.InjectTexture(
            FaceGenHeadShaderFamilyResolver.BuildSiblingPath(diffusePath, "_sk")!,
            TestTextures.Single(64, 32, 16, 255));

        var submesh = new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
            Triangles = [0, 1, 2],
            Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
            DiffuseTexturePath = diffusePath
        };

        var finalPath = FaceGenHeadShaderFamilyResolver.ApplyToSubmeshes(
            [submesh],
            resolver,
            diffusePath,
            diffusePath,
            generatedPath);

        Assert.Equal(generatedPath, finalPath);
        Assert.Equal(generatedPath, submesh.DiffuseTexturePath);
        Assert.Equal(
            FaceGenHeadShaderFamilyResolver.BuildSiblingPath(diffusePath, "_n"),
            submesh.NormalMapTexturePath);
        Assert.True(submesh.IsFaceGen);
        Assert.InRange(submesh.SubsurfaceColor.R, 64f / 255f - 0.001f, 64f / 255f + 0.001f);
        Assert.InRange(submesh.SubsurfaceColor.G, 32f / 255f - 0.001f, 32f / 255f + 0.001f);
        Assert.InRange(submesh.SubsurfaceColor.B, 16f / 255f - 0.001f, 16f / 255f + 0.001f);

        var composed = Assert.IsType<DecodedTexture>(resolver.GetTexture(generatedPath));
        // The generic family's retained Fallout-lineage tile is deliberately unchanged.
        Assert.Equal<byte>(10, composed.Pixels[0]);
        Assert.Equal<byte>(20, composed.Pixels[1]);
        Assert.Equal<byte>(29, composed.Pixels[2]);
        Assert.Equal<byte>(255, composed.Pixels[3]);
    }
}