using System.Numerics;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using ImageMagick;
using SharpGLTF.Schema2;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

public sealed class StarfieldGlbColorLerpBakerTests
{
    private static readonly byte[] SourcePixels =
    [
        0, 128, 255, 17,
        255, 64, 32, 201
    ];

    private static readonly byte[] ExpectedPrimaryBake =
    [
        39, 128, 232, 17,
        206, 96, 128, 201
    ];

    [Fact]
    public void BakeDiffuseTexture_MixesRgbInLinearSpaceAndPreservesAlpha()
    {
        var sourceBytes = (byte[])SourcePixels.Clone();
        var source = DecodedTexture.FromBaseLevel(sourceBytes, 2, 1, generateMipChain: true);
        var state = CreateConstantLerpState(new Vector4(0.25f, 0.5f, 0.75f, 0.4f));

        var baked = Assert.IsType<DecodedTexture>(
            StarfieldGlbColorLerpBaker.BakeDiffuseTexture(source, state));

        Assert.NotSame(source, baked);
        Assert.Equal(ExpectedPrimaryBake, baked.Pixels);
        Assert.Equal(SourcePixels, source.Pixels);
        Assert.Equal(1, baked.MipCount);
        Assert.Equal(SourcePixels[3], baked.Pixels[3]);
        Assert.Equal(SourcePixels[7], baked.Pixels[7]);
    }

    [Fact]
    public void VertexLerpPolicy_RemainsFailClosed()
    {
        var policy = new StarfieldMaterialColorPolicy(
            true,
            true,
            StarfieldMaterialColorOverrideMode.Lerp,
            new Vector4(0.25f, 0.5f, 0.75f, 0.4f));
        Assert.False(policy.TryResolveConstantLerp(out _));
        var state = policy.ResolveRenderState();
        var source = DecodedTexture.FromBaseLevel((byte[])SourcePixels.Clone(), 2, 1, false);
        var fallback = new Vector4(0.2f, 0.3f, 0.4f, 0.5f);

        Assert.Equal(default, state);
        Assert.Same(source, StarfieldGlbColorLerpBaker.BakeDiffuseTexture(source, state));
        Assert.Equal(fallback, StarfieldGlbColorLerpBaker.BuildBaseColor(fallback, true, state));
    }

    [Fact]
    public void WriteToBytes_ConstantLerpBakesMeshViewerTextureAndSeparatesCacheIdentity()
    {
        const string sharedMaterialPath = @"materials\test\shared.mat";
        var source = DecodedTexture.FromBaseLevel((byte[])SourcePixels.Clone(), 2, 1, false);
        using var textureResolver = new NifTextureResolver(
            path => string.Equals(path, sharedMaterialPath, StringComparison.OrdinalIgnoreCase)
                ? source
                : null);
        var scene = new GlbScene();
        scene.MeshParts.Add(CreateTriangle(
            "PrimaryLerp",
            0f,
            sharedMaterialPath,
            CreateConstantLerpState(new Vector4(0.25f, 0.5f, 0.75f, 0.4f))));
        scene.MeshParts.Add(CreateTriangle(
            "SecondLerp",
            2f,
            sharedMaterialPath,
            CreateConstantLerpState(new Vector4(0.75f, 0.25f, 0.5f, 0.7f))));

        var glb = GlbWriter.WriteToBytes(scene, textureResolver);

        using var stream = new MemoryStream(glb, writable: false);
        var model = ModelRoot.ReadGLB(stream);
        Assert.Equal(2, model.LogicalMaterials.Count);
        Assert.All(model.LogicalMaterials, material =>
        {
            var baseColor = Assert.IsType<MaterialChannel>(material.FindChannel("BaseColor"));
            Assert.NotNull(baseColor.Texture);
            Assert.Equal(Vector4.One, baseColor.Color);
        });
        Assert.Equal(2, model.LogicalImages.Count);
        Assert.All(model.LogicalImages, image =>
            Assert.Contains("starfieldLerp", image.Name ?? image.AlternateWriteFileName ?? string.Empty));

        var exportedPixels = model.LogicalImages
            .Select(ReadRgbaPixels)
            .ToArray();
        Assert.Contains(exportedPixels, pixels => pixels.SequenceEqual(ExpectedPrimaryBake));
        Assert.All(exportedPixels, pixels =>
        {
            Assert.Equal(SourcePixels[3], pixels[3]);
            Assert.Equal(SourcePixels[7], pixels[7]);
        });
    }

    [Fact]
    public void WriteToBytes_ConstantLerpWithoutDiffuseUsesOpaqueWhiteSampleFactor()
    {
        var state = CreateConstantLerpState(new Vector4(0.25f, 0.5f, 0.75f, 0.4f));
        var scene = new GlbScene();
        scene.MeshParts.Add(CreateTriangle("UntexturedLerp", 0f, null, state));
        using var textureResolver = new NifTextureResolver();

        var glb = GlbWriter.WriteToBytes(scene, textureResolver);

        using var stream = new MemoryStream(glb, writable: false);
        var material = ModelRoot.ReadGLB(stream).LogicalMaterials.Single();
        var baseColor = Assert.IsType<MaterialChannel>(material.FindChannel("BaseColor"));
        Assert.Null(baseColor.Texture);
        Assert.Equal(1f, baseColor.Color.W);
        Assert.Equal(0.6205604f, baseColor.Color.X, 6);
        Assert.Equal(0.6855138f, baseColor.Color.Y, 6);
        Assert.Equal(0.80902135f, baseColor.Color.Z, 6);
    }

    private static StarfieldMaterialColorRenderState CreateConstantLerpState(Vector4 authoredColor)
    {
        var policy = new StarfieldMaterialColorPolicy(
            true,
            false,
            StarfieldMaterialColorOverrideMode.Lerp,
            authoredColor);
        Assert.True(policy.TryResolveConstantLerp(out var linearTint));
        var state = policy.ResolveRenderState();
        Assert.Equal(linearTint, state.LinearTint);
        return state;
    }

    private static GlbMeshPart CreateTriangle(
        string name,
        float xOffset,
        string? diffusePath,
        StarfieldMaterialColorRenderState state)
    {
        return new GlbMeshPart
        {
            Name = name,
            Submesh = new RenderableSubmesh
            {
                ShapeName = name,
                Positions =
                [
                    xOffset, 0f, 0f,
                    xOffset + 1f, 0f, 0f,
                    xOffset, 1f, 0f
                ],
                Triangles = [0, 1, 2],
                Normals =
                [
                    0f, 0f, 1f,
                    0f, 0f, 1f,
                    0f, 0f, 1f
                ],
                UVs =
                [
                    0f, 0f,
                    1f, 0f,
                    0f, 1f
                ],
                DiffuseTexturePath = diffusePath,
                StarfieldMaterialColor = state
            }
        };
    }

    private static byte[] ReadRgbaPixels(Image image)
    {
        using var decoded = new MagickImage(image.Content.Content.ToArray());
        return Assert.IsType<byte[]>(decoded.GetPixels().ToByteArray(PixelMapping.RGBA));
    }
}
