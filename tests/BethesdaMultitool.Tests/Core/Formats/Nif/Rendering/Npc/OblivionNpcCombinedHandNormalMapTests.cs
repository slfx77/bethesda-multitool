using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class OblivionNpcCombinedHandNormalMapTests
{
    private const string HandMesh = @"meshes\characters\_male\femalehand.nif";
    private const string ImperialDiffuse = @"textures\characters\imperial\female\HandFemale.dds";
    private const string ImperialNormal = @"textures\characters\imperial\female\HandFemale_n.dds";
    private const string OrcDiffuse = @"textures\characters\orc\female\HandFemale.dds";

    [Theory]
    [InlineData(HandMesh, HandMesh)]
    [InlineData(HandMesh, "MESHES/CHARACTERS/_MALE/FEMALEHAND.NIF")]
    [InlineData(HandMesh, @"characters\_male\femalehand.nif")]
    [InlineData(@"characters\_male\femalehand.nif", HandMesh)]
    public void ExactCombinedHandRecoversOriginalFamilyBeforeRaceOverride(string appearancePath, string bodyPath)
    {
        using var resolver = new NifTextureResolver();
        resolver.InjectTexture(ImperialNormal, TestTextures.Single(127, 127, 255, 255));
        resolver.InjectTexture(@"textures\characters\orc\female\HandFemale_n.dds",
            TestTextures.Single(255, 0, 0, 255));
        var appearance = new NpcAppearance { Game = BethesdaGame.Oblivion, HandNifPath = appearancePath };
        var submesh = CreateHand();
        var tangents = submesh.Tangents;
        var bitangents = submesh.Bitangents;

        OblivionNpcNormalMapResolver.ApplyMissingCombinedHandNormalMap(submesh, appearance, bodyPath, resolver);

        Assert.Equal(ImperialDiffuse, submesh.DiffuseTexturePath);
        submesh.DiffuseTexturePath = OrcDiffuse;
        Assert.Equal(ImperialNormal, submesh.NormalMapTexturePath);
        Assert.Same(tangents, submesh.Tangents);
        Assert.Same(bitangents, submesh.Bitangents);
        Assert.Equal((1f, 1f, 1f), submesh.SpecularColor);
        Assert.Equal((1f, 1f, 1f), submesh.MaterialDiffuse);
        Assert.Equal(10f, submesh.MaterialGlossiness);
        Assert.Equal(1f, submesh.MaterialAlpha);
        Assert.Null(submesh.TintColor);
        Assert.False(submesh.IsFaceGen);
        Assert.False(submesh.UsesClassicHairMaterial);
        var decoded = ReferenceSubmeshDecoder12.Decode(submesh,
            new ReferenceSubmeshDecodeOptions12(submesh.DiffuseTexturePath, submesh.NormalMapTexturePath));
        Assert.True(decoded.HasBump);
        Assert.Equal(ImperialNormal, decoded.NormalMapTexturePath);
    }

    [Theory]
    [InlineData(null, HandMesh)]
    [InlineData("", HandMesh)]
    [InlineData(HandMesh, @"meshes\different\femalehand.nif")]
    [InlineData(HandMesh, @"meshes\characters\_male\femaleupperbody.nif")]
    [InlineData(HandMesh, @"meshes\characters\imperial\headhuman.nif")]
    [InlineData(HandMesh, @"meshes\characters\imperial\earshuman.nif")]
    [InlineData(HandMesh, @"meshes\characters\hair\hairhuman.nif")]
    [InlineData(HandMesh, @"meshes\characters\_male\lefthandfemale.nif")]
    public void OtherBodyPartsAndAbsentCombinedHandIdentityDoNotProbe(string? appearancePath, string bodyPath)
    {
        using var resolver = new NifTextureResolver();
        resolver.InjectTexture(ImperialNormal, TestTextures.Single(127, 127, 255, 255));
        var appearance = new NpcAppearance { Game = BethesdaGame.Oblivion, HandNifPath = appearancePath };
        var submesh = CreateHand();

        OblivionNpcNormalMapResolver.ApplyMissingCombinedHandNormalMap(submesh, appearance, bodyPath, resolver);

        Assert.Null(submesh.NormalMapTexturePath);
        Assert.Equal(ImperialDiffuse, submesh.DiffuseTexturePath);
        Assert.Equal(0, resolver.CacheHits);
        Assert.Equal(0, resolver.CacheMisses);
    }

    [Theory]
    [InlineData((int)BethesdaGame.Unknown)]
    [InlineData((int)BethesdaGame.Fallout3)]
    [InlineData((int)BethesdaGame.FalloutNewVegas)]
    [InlineData((int)BethesdaGame.Skyrim)]
    [InlineData((int)BethesdaGame.Fallout4)]
    public void OtherGamesDoNotChangeCombinedHandMaterial(int game)
    {
        using var resolver = new NifTextureResolver();
        resolver.InjectTexture(ImperialNormal, TestTextures.Single(127, 127, 255, 255));
        var appearance = new NpcAppearance { Game = (BethesdaGame)game, HandNifPath = HandMesh };
        var submesh = CreateHand();

        OblivionNpcNormalMapResolver.ApplyMissingCombinedHandNormalMap(submesh, appearance, HandMesh, resolver);

        Assert.Null(submesh.NormalMapTexturePath);
        Assert.Equal(0, resolver.CacheHits);
        Assert.Equal(0, resolver.CacheMisses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitNormalWinsEvenWhenUnavailable(bool fromMetadata)
    {
        const string explicitNormal = @"textures\custom\missing_normal.dds";
        using var resolver = new NifTextureResolver();
        resolver.InjectTexture(ImperialNormal, TestTextures.Single(127, 127, 255, 255));
        var submesh = CreateHand(fromMetadata
            ? new NifShaderTextureMetadata { TextureSlots = [ImperialDiffuse, explicitNormal] }
            : null);
        if (!fromMetadata)
        {
            submesh.NormalMapTexturePath = explicitNormal;
        }

        OblivionNpcNormalMapResolver.ApplyMissingCombinedHandNormalMap(submesh, CreateAppearance(), HandMesh, resolver);

        Assert.Equal(explicitNormal, submesh.NormalMapTexturePath);
        Assert.Equal(0, resolver.CacheHits);
        Assert.Equal(0, resolver.CacheMisses);
    }

    [Fact]
    public void MissingOriginalSiblingDoesNotUseRaceSiblingOrInventTangentFrame()
    {
        using var resolver = new NifTextureResolver();
        resolver.InjectTexture(@"textures\characters\orc\female\HandFemale_n.dds",
            TestTextures.Single(127, 127, 255, 255));
        var submesh = CreateHand();
        submesh.Tangents = null;
        submesh.Bitangents = null;

        OblivionNpcNormalMapResolver.ApplyMissingCombinedHandNormalMap(submesh, CreateAppearance(), HandMesh, resolver);

        Assert.Null(submesh.NormalMapTexturePath);
        Assert.Null(submesh.Tangents);
        Assert.Null(submesh.Bitangents);
        Assert.Equal(ImperialDiffuse, submesh.DiffuseTexturePath);
    }

    [Theory]
    [InlineData(0u, 1)]
    [InlineData(0x10u, 0)]
    public void CombinedHandPlanKeepsOwnAtlasAndHonorsGloveCoverage(uint coveredSlots, int expectedParts)
    {
        var appearance = CreateAppearance();
        var parts = NpcCompositionPlanner.BuildBodyParts(appearance, new NpcCompositionOptions(), coveredSlots,
            @"body_egt\00085969_upperbody.dds", OrcDiffuse);

        Assert.Equal(expectedParts, parts.Count);
        Assert.All(parts, static part =>
        {
            Assert.Equal(HandMesh, part.MeshPath);
            Assert.Equal(OrcDiffuse, part.TextureOverride);
        });
    }

    private static NpcAppearance CreateAppearance()
    {
        return new NpcAppearance
        {
            Game = BethesdaGame.Oblivion,
            HandNifPath = HandMesh,
            HandTexturePath = OrcDiffuse
        };
    }

    private static RenderableSubmesh CreateHand(NifShaderTextureMetadata? metadata = null)
    {
        return new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
            Triangles = [0, 1, 2],
            Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
            UVs = [0f, 0f, 1f, 0f, 0f, 1f],
            Tangents = [1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f],
            Bitangents = [0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f],
            DiffuseTexturePath = ImperialDiffuse,
            SpecularColor = (1f, 1f, 1f),
            MaterialDiffuse = (1f, 1f, 1f),
            MaterialGlossiness = 10f,
            MaterialAlpha = 1f,
            ShaderMetadata = metadata
        };
    }
}