using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class OblivionNpcEquipmentNormalMapResolverTests
{
    private const string AuthoredDiffuse = @"textures\characters\imperial\female\LegFemale.dds";
    private const string AuthoredNormal = @"textures\characters\imperial\female\LegFemale_n.dds";

    [Theory]
    [InlineData(@"textures\characters\orc\female\LegFemale.dds")]
    [InlineData(@"body_egt\00085969_upperbody.dds")]
    public void OriginalFamilySurvivesLaterRaceOrGeneratedDiffuseSubstitution(string effectiveDiffuse)
    {
        using var resolver = new NifTextureResolver();
        resolver.InjectTexture(AuthoredNormal, TestTextures.Single(127, 127, 255, 255));
        var submesh = CreateTriangle();
        var tangents = submesh.Tangents;
        var bitangents = submesh.Bitangents;

        OblivionNpcNormalMapResolver.ApplyMissingNormalMap(submesh, BethesdaGame.Oblivion, resolver);

        Assert.Equal(AuthoredDiffuse, submesh.DiffuseTexturePath);
        submesh.DiffuseTexturePath = effectiveDiffuse;
        Assert.Equal(AuthoredNormal, submesh.NormalMapTexturePath);
        Assert.Same(tangents, submesh.Tangents);
        Assert.Same(bitangents, submesh.Bitangents);
        Assert.False(submesh.IsFaceGen);
        Assert.False(submesh.UsesClassicHairMaterial);
        var decoded = ReferenceSubmeshDecoder12.Decode(submesh,
            new ReferenceSubmeshDecodeOptions12(effectiveDiffuse, submesh.NormalMapTexturePath));
        Assert.True(decoded.HasBump);
        Assert.Equal(AuthoredNormal, decoded.NormalMapTexturePath);
    }

    [Fact]
    public void ExplicitNormalWinsWithoutProbingImplicitSibling()
    {
        using var resolver = new NifTextureResolver();
        resolver.InjectTexture(AuthoredNormal, TestTextures.Single(127, 127, 255, 255));
        var submesh = CreateTriangle();
        submesh.NormalMapTexturePath = @"textures\custom\unavailable_explicit_n.dds";

        OblivionNpcNormalMapResolver.ApplyMissingNormalMap(submesh, BethesdaGame.Oblivion, resolver);

        Assert.Equal(@"textures\custom\unavailable_explicit_n.dds", submesh.NormalMapTexturePath);
        Assert.Equal(0, resolver.CacheHits);
        Assert.Equal(0, resolver.CacheMisses);
    }

    [Fact]
    public void ExplicitMetadataNormalWinsWithoutProbingImplicitSibling()
    {
        using var resolver = new NifTextureResolver();
        var submesh = CreateTriangle(new NifShaderTextureMetadata
        {
            TextureSlots = [AuthoredDiffuse, @"textures\custom\metadata_n.dds"]
        });

        OblivionNpcNormalMapResolver.ApplyMissingNormalMap(submesh, BethesdaGame.Oblivion, resolver);

        Assert.Equal(@"textures\custom\metadata_n.dds", submesh.NormalMapTexturePath);
        Assert.Equal(0, resolver.CacheMisses);
    }

    [Fact]
    public void MissingSiblingDoesNotAssignGuessedFilenameOrChangeAuthoredTangents()
    {
        using var resolver = new NifTextureResolver();
        var submesh = CreateTriangle();
        var tangents = submesh.Tangents;
        var bitangents = submesh.Bitangents;

        OblivionNpcNormalMapResolver.ApplyMissingNormalMap(submesh, BethesdaGame.Oblivion, resolver);

        Assert.Null(submesh.NormalMapTexturePath);
        Assert.Equal(AuthoredDiffuse, submesh.DiffuseTexturePath);
        Assert.Same(tangents, submesh.Tangents);
        Assert.Same(bitangents, submesh.Bitangents);
    }

    [Theory]
    [InlineData(0, 1, 4)]
    [InlineData(1, 0, 4)]
    [InlineData(2, 2, 4)]
    [InlineData(1, 1, 3)]
    public void UnusableSiblingDoesNotAssignNormal(int width, int height, int pixelBytes)
    {
        using var resolver = new NifTextureResolver();
        resolver.InjectTexture(AuthoredNormal, new DecodedTexture
        {
            MipLevels = [new DecodedTextureMipLevel { Width = width, Height = height, Pixels = new byte[pixelBytes] }]
        });
        var submesh = CreateTriangle();

        OblivionNpcNormalMapResolver.ApplyMissingNormalMap(submesh, BethesdaGame.Oblivion, resolver);

        Assert.Null(submesh.NormalMapTexturePath);
    }

    [Fact]
    public void EmptyMipChainDoesNotAssignNormal()
    {
        using var resolver = new NifTextureResolver();
        resolver.InjectTexture(AuthoredNormal, new DecodedTexture { MipLevels = [] });
        var submesh = CreateTriangle();

        OblivionNpcNormalMapResolver.ApplyMissingNormalMap(submesh, BethesdaGame.Oblivion, resolver);

        Assert.Null(submesh.NormalMapTexturePath);
    }

    [Fact]
    public void ExistingNormalDoesNotInventMissingTangentFrame()
    {
        using var resolver = new NifTextureResolver();
        resolver.InjectTexture(AuthoredNormal, TestTextures.Single(127, 127, 255, 255));
        var submesh = CreateTriangle();
        submesh.Tangents = null;
        submesh.Bitangents = null;

        OblivionNpcNormalMapResolver.ApplyMissingNormalMap(submesh, BethesdaGame.Oblivion, resolver);

        Assert.Equal(AuthoredNormal, submesh.NormalMapTexturePath);
        Assert.Null(submesh.Tangents);
        Assert.Null(submesh.Bitangents);
        var decoded = ReferenceSubmeshDecoder12.Decode(submesh,
            new ReferenceSubmeshDecodeOptions12(submesh.DiffuseTexturePath, submesh.NormalMapTexturePath));
        Assert.False(decoded.HasBump);
    }

    [Theory]
    [InlineData((int)BethesdaGame.Fallout3)]
    [InlineData((int)BethesdaGame.FalloutNewVegas)]
    [InlineData((int)BethesdaGame.Skyrim)]
    [InlineData((int)BethesdaGame.Fallout4)]
    public void OtherGamesKeepTheirExistingMaterialPolicy(int game)
    {
        using var resolver = new NifTextureResolver();
        resolver.InjectTexture(AuthoredNormal, TestTextures.Single(127, 127, 255, 255));
        var submesh = CreateTriangle();

        OblivionNpcNormalMapResolver.ApplyMissingNormalMap(submesh, (BethesdaGame)game, resolver);

        Assert.Null(submesh.NormalMapTexturePath);
        Assert.Equal(AuthoredDiffuse, submesh.DiffuseTexturePath);
        Assert.Equal(0, resolver.CacheHits);
        Assert.Equal(0, resolver.CacheMisses);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no_extension")]
    public void MissingFamilyDoesNotProbeNormal(string? diffuse)
    {
        using var resolver = new NifTextureResolver();
        var submesh = CreateTriangle();
        submesh.DiffuseTexturePath = diffuse;

        OblivionNpcNormalMapResolver.ApplyMissingNormalMap(submesh, BethesdaGame.Oblivion, resolver);

        Assert.Null(submesh.NormalMapTexturePath);
        Assert.Equal(0, resolver.CacheMisses);
    }

    private static RenderableSubmesh CreateTriangle(NifShaderTextureMetadata? metadata = null) => new()
    {
        Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
        Triangles = [0, 1, 2],
        Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
        UVs = [0f, 0f, 1f, 0f, 0f, 1f],
        Tangents = [1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f],
        Bitangents = [0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f],
        DiffuseTexturePath = AuthoredDiffuse,
        ShaderMetadata = metadata
    };
}
