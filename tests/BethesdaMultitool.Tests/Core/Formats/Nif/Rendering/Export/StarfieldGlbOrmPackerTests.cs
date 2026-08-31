using System.Numerics;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Tests.Helpers;
using SharpGLTF.Schema2;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

public sealed class StarfieldGlbOrmPackerTests
{
    [Fact]
    public void Pack_MapsAoRoughnessMetalnessRedIntoGltfRgb()
    {
        var state = new StarfieldMaterialOrmState(
            new StarfieldMaterialSlot(@"textures\rough.dds", null),
            new StarfieldMaterialSlot(@"textures\metal.dds", null),
            new StarfieldMaterialSlot(@"textures\ao.dds", null));
        var roughness = Texture(2, 1, [11, 91, 92, 93, 22, 94, 95, 96]);
        var metalness = Texture(2, 1, [33, 81, 82, 83, 44, 84, 85, 86]);
        var ao = Texture(2, 1, [55, 71, 72, 73, 66, 74, 75, 76]);

        var result = StarfieldGlbOrmPacker.Pack(state, roughness, metalness, ao);

        Assert.True(result.Applied);
        Assert.True(result.HasAmbientOcclusion);
        Assert.Equal(1f, result.MetallicFactor);
        Assert.Equal(1f, result.RoughnessFactor);
        Assert.Equal(
            new byte[] { 55, 11, 33, 255, 66, 22, 44, 255 },
            Assert.IsType<DecodedTexture>(result.Texture).Pixels);
    }

    [Fact]
    public void Pack_BroadcastsAuthoredReplacementsWithoutResamplingImages()
    {
        var state = new StarfieldMaterialOrmState(
            new StarfieldMaterialSlot(null, 0xFF000020u),
            new StarfieldMaterialSlot(null, 0xFF000040u),
            new StarfieldMaterialSlot(null, 0xFF000080u));

        var result = StarfieldGlbOrmPacker.Pack(state, null, null, null);

        Assert.True(result.Applied);
        Assert.True(result.HasAmbientOcclusion);
        Assert.Equal(new byte[] { 128, 32, 64, 255 }, result.Texture!.Pixels);
    }

    [Fact]
    public void Pack_UsesCe2ConstructorDefaultsWhenAllThreeSlotsAreAbsent()
    {
        var result = StarfieldGlbOrmPacker.Pack(default, null, null, null);

        Assert.True(result.Applied);
        Assert.Null(result.Texture);
        Assert.False(result.HasAmbientOcclusion);
        Assert.Equal(0f, result.MetallicFactor);
        Assert.Equal(0f, result.RoughnessFactor);
    }

    [Fact]
    public void Pack_MismatchedAuthoredDimensionsFailClosed()
    {
        var state = new StarfieldMaterialOrmState(
            new StarfieldMaterialSlot(@"textures\rough.dds", null),
            new StarfieldMaterialSlot(@"textures\metal.dds", null),
            default);

        var result = StarfieldGlbOrmPacker.Pack(
            state,
            Texture(2, 1, [1, 0, 0, 0, 2, 0, 0, 0]),
            Texture(1, 2, [3, 0, 0, 0, 4, 0, 0, 0]),
            null);

        Assert.False(result.Applied);
        Assert.Null(result.Texture);
    }

    [Fact]
    public void Pack_MissingAuthoredImageFailsClosed()
    {
        var state = new StarfieldMaterialOrmState(
            new StarfieldMaterialSlot(@"textures\rough.dds", null),
            default,
            default);

        var result = StarfieldGlbOrmPacker.Pack(state, null, null, null);

        Assert.False(result.Applied);
        Assert.Null(result.Texture);
    }

    [Fact]
    public void GlbWriter_UsesPackedImageForBothCoreGltfConsumers()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Export",
            "GlbWriter.cs");

        SourceContract.AssertOrder(
            source,
            "TryResolveStaticLayer0Orm(out starfieldOrmState)",
            "StarfieldGlbOrmPacker.Pack(",
            "starfieldOrm.Texture is { } ormTexture",
            "material.WithMetallicRoughness(",
            "material.WithOcclusion(ormImage, 1f);");
    }

    [Fact]
    public void WriteToBytes_MissingAuthoredOrmFailsClosedWithoutLegacyPbrInference()
    {
        const string materialPath = @"materials\test\orm.mat";
        const string normalPath = @"textures\test\legacy_n.dds";
        var cdb = BethesdaMultitool.Tests.Core.Formats.Nif.Materials
            .StarfieldMaterialOrmPolicyTests.BuildDatabase(useDiffChunks: false);
        var normal = Texture(2, 1,
        [
            128, 128, 255, 0,
            128, 128, 255, 255
        ]);
        using var source = new FakeStarfieldSource(cdb, new Dictionary<string, DecodedTexture>(
            StringComparer.OrdinalIgnoreCase)
        {
            [normalPath] = normal
        });
        using var resolver = new NifTextureResolver(new INifTextureSource[] { source });
        var scene = new GlbScene();
        scene.MeshParts.Add(CreateTriangle(materialPath, normalPath));

        var glb = GlbWriter.WriteToBytes(scene, resolver);

        using var stream = new MemoryStream(glb, writable: false);
        var model = ModelRoot.ReadGLB(stream);
        var material = Assert.Single(model.LogicalMaterials);
        var pbr = Assert.IsType<MaterialChannel>(material.FindChannel("MetallicRoughness"));
        Assert.Null(pbr.Texture);
        Assert.Equal(
            0f,
            Assert.IsType<float>(pbr.Parameters.Single(parameter =>
                parameter.Name == "MetallicFactor").Value));
        Assert.Equal(
            0f,
            Assert.IsType<float>(pbr.Parameters.Single(parameter =>
                parameter.Name == "RoughnessFactor").Value));
        Assert.Contains(model.LogicalImages, image =>
            (image.Name ?? image.AlternateWriteFileName ?? string.Empty).Contains(
                "normal", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(model.LogicalImages, image =>
        {
            var name = image.Name ?? image.AlternateWriteFileName ?? string.Empty;
            return name.Contains("metallicRoughness", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("specular", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("occlusion", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains("starfieldOrm", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static GlbMeshPart CreateTriangle(string materialPath, string normalPath)
    {
        return new GlbMeshPart
        {
            Name = "MissingOrm",
            Submesh = new RenderableSubmesh
            {
                ShapeName = "MissingOrm",
                Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
                Triangles = [0, 1, 2],
                Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
                UVs = [0f, 0f, 1f, 0f, 0f, 1f],
                DiffuseTexturePath = materialPath,
                NormalMapTexturePath = normalPath,
                MaterialGlossiness = 80f
            }
        };
    }

    private static DecodedTexture Texture(int width, int height, byte[] pixels)
    {
        return DecodedTexture.FromBaseLevel(pixels, width, height, false);
    }

    private sealed class FakeStarfieldSource(
        byte[] database,
        IReadOnlyDictionary<string, DecodedTexture> textures) : INifTextureSource
    {
        public DecodedTexture? TryLoad(string path)
        {
            return textures.GetValueOrDefault(path);
        }

        public byte[]? TryLoadRaw(string path)
        {
            return string.Equals(
                path,
                @"materials\materialsbeta.cdb",
                StringComparison.OrdinalIgnoreCase)
                ? database
                : null;
        }

        public bool Exists(string path)
        {
            return string.Equals(
                path,
                @"materials\materialsbeta.cdb",
                StringComparison.OrdinalIgnoreCase) ||
                   textures.ContainsKey(path);
        }

        public bool TryGetAssetMetadata(
            string path,
            out NifTextureSourceAssetMetadata metadata)
        {
            metadata = default;
            return false;
        }

        public void Dispose()
        {
        }
    }
}
