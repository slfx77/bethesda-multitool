using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class FaceGenHeadShaderFamilyResolverTests
{
    [Fact]
    public void ApplyToSubmeshes_DerivesHeadFamilyTexturesAndInjectsComposedDiffuse()
    {
        const string diffusePath = @"textures\characters\male\headhuman.dds";
        const string generatedPath = @"facegen_egt\00000001.dds";

        using var resolver = new NifTextureResolver();
        resolver.InjectTexture(diffusePath, CreateTexture(10, 20, 30, 255));
        resolver.InjectTexture(
            FaceGenHeadShaderFamilyResolver.BuildSiblingPath(diffusePath, "_n")!,
            CreateTexture(127, 127, 255, 255));
        resolver.InjectTexture(
            FaceGenHeadShaderFamilyResolver.BuildSiblingPath(diffusePath, "_sk")!,
            CreateTexture(64, 32, 16, 255));

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
        Assert.Equal<byte>(10, composed.Pixels[0]);
        Assert.Equal<byte>(20, composed.Pixels[1]);
        Assert.Equal<byte>(29, composed.Pixels[2]);
        Assert.Equal<byte>(255, composed.Pixels[3]);
    }

    private static DecodedTexture CreateTexture(byte r, byte g, byte b, byte a)
    {
        return DecodedTexture.FromBaseLevel([r, g, b, a], 1, 1);
    }
}
