using BethesdaMultitool.CLI.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class OblivionNpcBodySkinMaterialResolverTests
{
    private const string Original = "textures/imperial/hand.dds";
    private const string Normal = "textures/imperial/hand_n.dds";
    private const string Race = "textures/orc/hand.dds";
    private const string RaceNormal = "textures/orc/hand_n.dds";
    private const string Atlas = "body_egt/00085969_hands.dds";
    private const string Skin = @"body_skin\00085969_hands.dds";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProvenSkin_PreservesBasisAndComposesSeparateDetailOnce(bool raceNormalAvailable)
    {
        var mesh = Mesh();
        var source = Texture();
        var sourcePixels = source.Pixels.ToArray();
        using var resolver = Resolver(source, raceNormalAvailable: raceNormalAvailable);
        var positions = mesh.Positions;
        var normals = mesh.Normals;
        var tangents = mesh.Tangents;
        var bitangents = mesh.Bitangents;

        OblivionNpcBodySkinMaterialResolver.ApplyTextureOverride(mesh, Appearance(), resolver, Atlas);

        Assert.True(mesh.IsFaceGen);
        Assert.Equal(Skin, mesh.DiffuseTexturePath);
        Assert.Equal(raceNormalAvailable ? RaceNormal : Normal, mesh.NormalMapTexturePath);
        var detail = Assert.IsType<DecodedTexture>(resolver.GetTexture(Skin));
        Assert.Equal(new byte[] { 129, 193, 255, 37 }, detail.Pixels);
        Assert.Equal(sourcePixels, source.Pixels);
        Assert.NotSame(source.Pixels, detail.Pixels);
        Assert.Same(source, resolver.GetTexture(Atlas));
        Assert.Same(positions, mesh.Positions);
        Assert.Same(normals, mesh.Normals);
        Assert.Same(tangents, mesh.Tangents);
        Assert.Same(bitangents, mesh.Bitangents);
        Assert.Equal((1f, 1f, 1f), mesh.SpecularColor);
        Assert.Equal(25f, mesh.MaterialGlossiness);

        // Reapplying and sharing the atlas with another shape must never multiply the result again.
        OblivionNpcBodySkinMaterialResolver.ApplyTextureOverride(mesh, Appearance(), resolver, Atlas);
        var sibling = Mesh();
        OblivionNpcBodySkinMaterialResolver.ApplyTextureOverride(sibling, Appearance(), resolver, Atlas);
        Assert.Equal(new byte[] { 129, 193, 255, 37 }, resolver.GetTexture(Skin)!.Pixels);
        Assert.Equal(sourcePixels, source.Pixels);
        Assert.Equal(Skin, sibling.DiffuseTexturePath);
    }

    [Theory]
    [InlineData(BethesdaGame.Fallout3)]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    [InlineData(BethesdaGame.Skyrim)]
    [InlineData(BethesdaGame.Fallout4)]
    [InlineData(BethesdaGame.Fallout76)]
    [InlineData(BethesdaGame.Starfield)]
    public void OtherGames_RetainExistingAtlasAndMaterialWithoutTextureLookups(BethesdaGame game)
    {
        var mesh = Mesh();
        using var resolver = new NifTextureResolver(_ => throw new InvalidOperationException("Unexpected lookup"));
        OblivionNpcBodySkinMaterialResolver.ApplyTextureOverride(mesh, Appearance(game), resolver, Atlas);
        AssertFallback(mesh);
    }

    [Fact]
    public void RepeatedGlowFallback_RetainsOriginalFamilyDespiteAtlasOverride()
    {
        var mesh = Mesh();
        mesh.NormalMapTexturePath = Normal;
        using var resolver = Resolver(Texture(), "original-glow");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            OblivionNpcBodySkinMaterialResolver.ApplyTextureOverride(mesh, Appearance(), resolver, Atlas);
            Assert.False(mesh.IsFaceGen);
            Assert.Equal(Atlas, mesh.DiffuseTexturePath);
            Assert.Equal(Original, mesh.AuthoredOblivionBodySkinDiffusePath);
            Assert.Equal(Normal, mesh.NormalMapTexturePath);
            Assert.Null(resolver.GetTexture(Skin));
        }
    }

    [Theory]
    [InlineData(@"body_skin\00085969_hands.dds")]
    [InlineData("textures/body_skin/00085969_hands.dds")]
    [InlineData("  textures/BODY_SKIN/00085969_hands.dds  ")]
    public void ExistingDetailKey_IsNeverModulatedAgain(string alias)
    {
        var mesh = Mesh();
        using var resolver = Resolver(Texture());
        OblivionNpcBodySkinMaterialResolver.ApplyTextureOverride(mesh, Appearance(), resolver, Atlas);
        var detail = resolver.GetTexture(Skin);
        OblivionNpcBodySkinMaterialResolver.ApplyTextureOverride(mesh, Appearance(), resolver, alias);
        Assert.True(mesh.IsFaceGen);
        Assert.Same(detail, resolver.GetTexture(Skin));
        Assert.Equal(new byte[] { 129, 193, 255, 37 }, detail!.Pixels);
    }

    [Theory]
    [InlineData("skin", false)]
    [InlineData("foot", true)]
    [InlineData("Iron Boots", true)]
    [InlineData("Skin extra", true)]
    [InlineData(null, true)]
    public void MaterialNameAndSourceProvenance_AreBothRequired(string? material, bool provenSource)
    {
        var mesh = Mesh(material, provenSource);
        using var resolver = Resolver(Texture());
        OblivionNpcBodySkinMaterialResolver.ApplyTextureOverride(mesh, Appearance(), resolver, Atlas);
        AssertFallback(mesh);
        Assert.Null(resolver.GetTexture(Skin));
    }

    [Fact]
    public void MissingOriginalTextureProvenance_DoesNotInferFromTheCurrentAtlas()
    {
        var mesh = Mesh();
        mesh.AuthoredOblivionBodySkinDiffusePath = null;
        using var resolver = Resolver(Texture());
        OblivionNpcBodySkinMaterialResolver.ApplyTextureOverride(mesh, Appearance(), resolver, Atlas);
        AssertFallback(mesh);
        Assert.Null(resolver.GetTexture(Skin));
    }

    [Theory]
    [InlineData("normal-missing")]
    [InlineData("normal-malformed")]
    [InlineData("original-glow")]
    [InlineData("diffuse-missing")]
    public void MissingOrUnsupportedTextures_DoNotPromoteOrMutateAtlas(string failure)
    {
        var mesh = Mesh();
        var source = Texture();
        using var resolver = Resolver(source, failure, true);
        OblivionNpcBodySkinMaterialResolver.ApplyTextureOverride(mesh, Appearance(), resolver, Atlas);
        AssertFallback(mesh);
        Assert.Null(resolver.GetTexture(Skin));
        Assert.Equal(new byte[] { 128, 192, 254, 37 }, source.Pixels);
    }

    [Theory]
    [InlineData("blend")]
    [InlineData("test")]
    [InlineData("alpha")]
    [InlineData("emissive")]
    [InlineData("glow")]
    [InlineData("environment")]
    [InlineData("hair")]
    [InlineData("no-tangent")]
    [InlineData("short-bitangent")]
    [InlineData("nan-normal")]
    [InlineData("zero-tangent")]
    [InlineData("infinite-uv")]
    public void UnsupportedMaterialOrBasis_KeepsTheFallback(string failure)
    {
        var mesh = Mesh();
        ApplyFailure(mesh, failure);
        var tangents = mesh.Tangents;
        var bitangents = mesh.Bitangents;
        using var resolver = Resolver(Texture());
        OblivionNpcBodySkinMaterialResolver.ApplyTextureOverride(mesh, Appearance(), resolver, Atlas);
        AssertFallback(mesh);
        Assert.Null(resolver.GetTexture(Skin));
        Assert.Same(tangents, mesh.Tangents);
        Assert.Same(bitangents, mesh.Bitangents);
    }

    [Theory]
    [InlineData(0, "upperbody")]
    [InlineData(1, "lowerbody")]
    [InlineData(2, "hands")]
    [InlineData(3, "feet")]
    [InlineData(4, "tail")]
    public void DetailKeys_AreVariantScopedCapturedAndEvictable(int part, string label)
    {
        var npc = new NpcAppearance
        {
            Game = BethesdaGame.Oblivion, NpcFormId = 0x85969, RenderVariantLabel = "preview"
        };
        var key = NpcTextureHelpers.BuildNpcBodySkinTextureKey(npc, (NpcBodyTexturePart)part);
        Assert.Equal($@"body_skin\00085969_preview_{label}.dds", key);
        Assert.Contains(key, NpcTextureHelpers.BuildNpcGeneratedTextureKeys(npc));
        using var resolver = new NifTextureResolver(_ => null);
        resolver.InjectTexture(key, Texture());
        foreach (var generated in NpcTextureHelpers.BuildNpcGeneratedTextureKeys(npc))
        {
            resolver.EvictTexture(generated);
        }

        Assert.Null(resolver.GetTexture(key));
    }

    private static void ApplyFailure(RenderableSubmesh mesh, string failure)
    {
        switch (failure)
        {
            case "blend": mesh.HasAlphaBlend = true; break;
            case "test": mesh.HasAlphaTest = true; break;
            case "alpha": mesh.MaterialAlpha = 0.5f; break;
            case "emissive": mesh.IsEmissive = true; break;
            case "glow": mesh.Lighting30GlowMapTexturePath = "glow.dds"; break;
            case "environment": mesh.ClassicEnvironmentMapTexturePath = "cube.dds"; break;
            case "hair": mesh.UsesClassicHairMaterial = true; break;
            case "no-tangent": mesh.Tangents = null; break;
            case "short-bitangent": mesh.Bitangents = [1]; break;
            case "nan-normal": mesh.Normals![0] = float.NaN; break;
            case "zero-tangent": mesh.Tangents = [0, 0, 0]; break;
            case "infinite-uv": mesh.UVs![0] = float.PositiveInfinity; break;
            default: throw new ArgumentOutOfRangeException(nameof(failure));
        }
    }

    private static void AssertFallback(RenderableSubmesh mesh)
    {
        Assert.False(mesh.IsFaceGen);
        Assert.Equal(Atlas, mesh.DiffuseTexturePath);
        Assert.Null(mesh.NormalMapTexturePath);
    }

    private static NpcAppearance Appearance(BethesdaGame game = BethesdaGame.Oblivion)
    {
        return new NpcAppearance
        {
            Game = game, NpcFormId = 0x85969, HandTexturePath = Race
        };
    }

    private static RenderableSubmesh Mesh(string? material = "skin", bool provenSource = true)
    {
        return new RenderableSubmesh
        {
            ShapeName = "Hand", LegacyMaterialName = material, HasAuthoredOblivionBodySkinInputs = provenSource,
            AuthoredOblivionBodySkinDiffusePath = Original,
            Positions = [1, 2, 3], Triangles = [], Normals = [0, 0, 1], UVs = [0.5f, 0.5f],
            Tangents = [1, 0, 0], Bitangents = [0, 1, 0], DiffuseTexturePath = Original,
            MaterialGlossiness = 25f, SpecularColor = (1, 1, 1)
        };
    }

    private static DecodedTexture Texture()
    {
        return DecodedTexture.FromBaseLevel([128, 192, 254, 37], 1, 1);
    }

    private static NifTextureResolver Resolver(
        DecodedTexture source, string? failure = null, bool raceNormalAvailable = false)
    {
        return new NifTextureResolver(path => path.Replace('\\', '/') switch
        {
            "textures/" + Atlas when failure != "diffuse-missing" => source,
            Normal when failure == "normal-malformed" => DecodedTexture.FromBaseLevel([1, 2, 3, 4], 2, 1, false),
            Normal when failure != "normal-missing" => Texture(),
            RaceNormal when raceNormalAvailable => Texture(),
            "textures/imperial/hand_g.dds" when failure == "original-glow" => Texture(),
            _ => null
        });
    }
}