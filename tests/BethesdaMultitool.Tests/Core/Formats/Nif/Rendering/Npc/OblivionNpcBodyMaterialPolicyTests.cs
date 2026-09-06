using BethesdaMultitool.CLI.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class OblivionNpcBodyMaterialPolicyTests
{
    private const string Upper = "generated-upper";
    private const string Lower = "generated-lower";
    private const string Hands = "generated-hands";
    private const string Feet = "generated-feet";
    private const string Tail = "generated-tail";
    private const string OriginalDiffuse = @"textures\armor\example\authored.dds";
    private const string OriginalNormal = @"textures\armor\example\authored_n.dds";
    private static readonly NpcBodyTextureSet AllAtlases = new(Upper, Lower, Hands, Feet, Tail);

    // Installed TES4 0047AC20 compares the exact material name case-insensitively, then
    // performs prefix comparisons. These literals are independent of the production classifier.
    [Theory]
    [InlineData("skin", "UpperBody", Upper)]
    [InlineData("SKIN", "upperbody:0", Upper)]
    [InlineData("sKiN", "Arms", Upper)]
    [InlineData("skin", "aRmSLeft", Upper)]
    [InlineData("skin", "LowerBody", Lower)]
    [InlineData("SKIN", "lowerbody:1", Lower)]
    [InlineData("skin", "Hand", Hands)]
    [InlineData("Skin", "hAnDLeft", Hands)]
    [InlineData("skin", "HandAnything", Hands)]
    [InlineData("skin", "Foot", Feet)]
    [InlineData("sKIN", "fOoTRight", Feet)]
    [InlineData("skin", "Tail", Tail)]
    [InlineData("SKIN", "tAiL:2", Tail)]
    public void ExactSkinMaterialUsesRetailShapePrefixes(string material, string shape, string expected)
    {
        Assert.Equal(expected, OblivionNpcBodyMaterialPolicy.ResolveTextureOverride(
            Mesh(material, shape), AllAtlases));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" skin")]
    [InlineData("skin ")]
    [InlineData("skins")]
    [InlineData("skin/body")]
    [InlineData("armor")]
    [InlineData("hair")]
    public void MissingOrNonexactMaterialIsNotRescuedByShapeOrTextureFilename(string? material)
    {
        var mesh = Mesh(material, "HandLeft");
        mesh.DiffuseTexturePath = @"textures\characters\imperial\female\HandFemale.dds";

        Assert.Null(OblivionNpcBodyMaterialPolicy.ResolveTextureOverride(mesh, AllAtlases));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingShapeIsRejectedSafelyRatherThanInventingAnAtlas(string? shape)
    {
        Assert.Null(OblivionNpcBodyMaterialPolicy.ResolveTextureOverride(Mesh("skin", shape), AllAtlases));
    }

    [Theory]
    [InlineData("Chest")]
    [InlineData("Feet")]
    [InlineData("Forearm")]
    [InlineData("ArmorHand")]
    [InlineData("XUpperBody")]
    [InlineData("detail")]
    [InlineData(" ")]
    [InlineData(@"meshes\UpperBody")]
    public void UnknownNonemptyShapeRetainsRetailLowerBodyFallback(string shape)
    {
        // The retail function logs "Bad skin name" but retains its initialized part 3.
        Assert.Equal(Lower, OblivionNpcBodyMaterialPolicy.ResolveTextureOverride(Mesh("skin", shape), AllAtlases));
    }

    [Theory]
    [InlineData("UpperBody", @"textures\characters\imperial\female\HandFemale.dds", Upper)]
    [InlineData("Hand", @"textures\characters\imperial\female\UpperBodyFemale.dds", Hands)]
    [InlineData("Tail", @"textures\armor\iron\f\Greaves.dds", Tail)]
    [InlineData("Hand", null, Hands)]
    [InlineData("Hand", "", Hands)]
    public void ShapeIdentityWinsOverMisleadingOrMissingDiffuseFilename(string shape, string? diffuse, string expected)
    {
        var mesh = Mesh("skin", shape);
        mesh.DiffuseTexturePath = diffuse;

        Assert.Equal(expected, OblivionNpcBodyMaterialPolicy.ResolveTextureOverride(mesh, AllAtlases));
        Assert.Equal(diffuse, mesh.DiffuseTexturePath);
    }

    [Theory]
    [InlineData("UpperBody", 0)]
    [InlineData("Arms", 0)]
    [InlineData("LowerBody", 1)]
    [InlineData("Unknown", 1)]
    [InlineData("Hand", 2)]
    [InlineData("Foot", 3)]
    [InlineData("Tail", 4)]
    public void MissingSelectedAtlasDoesNotSubstituteAnotherBodyPartOrMutateTheMesh(string shape, int absent)
    {
        var textures = new NpcBodyTextureSet(
            absent == 0 ? null : Upper,
            absent == 1 ? null : Lower,
            absent == 2 ? null : Hands,
            absent == 3 ? null : Feet,
            absent == 4 ? null : Tail);
        var mesh = Mesh("skin", shape);

        Assert.Null(OblivionNpcBodyMaterialPolicy.ResolveTextureOverride(mesh, textures));
        Assert.Equal(OriginalDiffuse, mesh.DiffuseTexturePath);
        Assert.Equal(OriginalNormal, mesh.NormalMapTexturePath);
    }

    [Theory]
    [InlineData("skin", false, Hands)]
    [InlineData("skin", true, Hands)]
    [InlineData("armor", false, null)]
    [InlineData("armor", true, null)]
    public void LookupDoesNotSelectShadersRebuildTangentsOrRewriteMaterialState(
        string material, bool originalFaceGen, string? expected)
    {
        var mesh = Mesh(material, "HandLeft");
        mesh.IsFaceGen = originalFaceGen;
        mesh.UsesClassicHairMaterial = true;
        mesh.TintColor = (0.2f, 0.4f, 0.6f);
        mesh.SubsurfaceColor = (0.3f, 0.5f, 0.7f);
        var positions = mesh.Positions;
        var normals = mesh.Normals;
        var tangents = mesh.Tangents;
        var bitangents = mesh.Bitangents;
        var originalPositions = positions.ToArray();
        var originalNormals = normals!.ToArray();
        var originalTangents = tangents!.ToArray();
        var originalBitangents = bitangents!.ToArray();

        Assert.Equal(expected, OblivionNpcBodyMaterialPolicy.ResolveTextureOverride(mesh, AllAtlases));

        Assert.Same(positions, mesh.Positions);
        Assert.Same(normals, mesh.Normals);
        Assert.Same(tangents, mesh.Tangents);
        Assert.Same(bitangents, mesh.Bitangents);
        Assert.Equal(originalPositions, mesh.Positions);
        Assert.Equal(originalNormals, mesh.Normals);
        Assert.Equal(originalTangents, mesh.Tangents);
        Assert.Equal(originalBitangents, mesh.Bitangents);
        Assert.Equal(material, mesh.LegacyMaterialName);
        Assert.Equal("HandLeft", mesh.ShapeName);
        Assert.Equal(OriginalDiffuse, mesh.DiffuseTexturePath);
        Assert.Equal(OriginalNormal, mesh.NormalMapTexturePath);
        Assert.Equal(originalFaceGen, mesh.IsFaceGen);
        Assert.True(mesh.UsesClassicHairMaterial);
        Assert.Equal((0.2f, 0.4f, 0.6f), mesh.TintColor);
        Assert.Equal((0.3f, 0.5f, 0.7f), mesh.SubsurfaceColor);
        Assert.Equal((0.4f, 0.6f, 0.8f), mesh.MaterialDiffuse);
        Assert.Equal((0.5f, 0.7f, 0.9f), mesh.SpecularColor);
        Assert.Equal(23f, mesh.MaterialGlossiness);
        Assert.Equal(0.8f, mesh.MaterialAlpha);
    }

    [Theory]
    [InlineData("skin", "Arms", Upper)]
    [InlineData("SKIN", "LowerBody", Lower)]
    [InlineData("skin", "Hand", Hands)]
    [InlineData("skin", "Foot", Feet)]
    [InlineData("skin", "Tail", Tail)]
    [InlineData("skin", "Odd", Lower)]
    [InlineData("cloth", "Hand", null)]
    [InlineData(null, "Hand", null)]
    [InlineData("skin", null, null)]
    public void OblivionCallerOverloadsUseOwnerIdentityInsteadOfFilenameOrWholePartOverride(
        string? material, string? shape, string? expected)
    {
        var npc = new NpcAppearance { Game = BethesdaGame.Oblivion };
        var mesh = Mesh(material, shape);
        mesh.DiffuseTexturePath = @"textures\characters\imperial\female\HandFemale.dds";

        Assert.Equal(expected, NpcTextureHelpers.ResolveEquipmentSkinTextureOverride(npc, mesh, AllAtlases));
        Assert.Equal(expected, NpcTextureHelpers.ResolveBodyPartTextureOverride(
            npc, mesh, AllAtlases, "misleading-whole-part-override"));
        Assert.Equal(@"textures\characters\imperial\female\HandFemale.dds", mesh.DiffuseTexturePath);
        Assert.False(mesh.IsFaceGen);
    }

    [Theory]
    [InlineData("Arms", 0)]
    [InlineData("LowerBody", 1)]
    [InlineData("Hand", 2)]
    [InlineData("Foot", 3)]
    [InlineData("Tail", 4)]
    [InlineData("Odd", 1)]
    public void OblivionCallerOverloadsReturnNullForMissingAtlasWithoutWholePartFallback(string shape, int absent)
    {
        var npc = new NpcAppearance { Game = BethesdaGame.Oblivion, HandTexturePath = "unrelated-race-hand" };
        var textures = new NpcBodyTextureSet(
            absent == 0 ? null : Upper,
            absent == 1 ? null : Lower,
            absent == 2 ? null : Hands,
            absent == 3 ? null : Feet,
            absent == 4 ? null : Tail);
        var mesh = Mesh("skin", shape);

        Assert.Null(NpcTextureHelpers.ResolveEquipmentSkinTextureOverride(npc, mesh, textures));
        Assert.Null(NpcTextureHelpers.ResolveBodyPartTextureOverride(npc, mesh, textures, "unrelated-part"));
        Assert.Equal(OriginalDiffuse, mesh.DiffuseTexturePath);
    }

    [Theory]
    [InlineData(BethesdaGame.Fallout3)]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    [InlineData(BethesdaGame.Skyrim)]
    [InlineData(BethesdaGame.Fallout4)]
    public void LaterGameEquipmentPreservesFilenameSelectionAndMissingBodyUnderwearGuards(BethesdaGame game)
    {
        var npc = new NpcAppearance { Game = game };
        var mesh = Mesh("skin", "Hand");
        mesh.DiffuseTexturePath = @"textures\characters\_female\lowerbody.dds";
        Assert.Equal(Upper, NpcTextureHelpers.ResolveEquipmentSkinTextureOverride(npc, mesh, AllAtlases));

        mesh.LegacyMaterialName = "cloth";
        mesh.DiffuseTexturePath = @"textures\characters\_female\hand.dds";
        Assert.Equal(Hands, NpcTextureHelpers.ResolveEquipmentSkinTextureOverride(npc, mesh, AllAtlases));
        Assert.Equal(Upper, NpcTextureHelpers.ResolveEquipmentSkinTextureOverride(
            npc, mesh, AllAtlases with { Hands = null }));
        Assert.Null(NpcTextureHelpers.ResolveEquipmentSkinTextureOverride(
            npc, mesh, AllAtlases with { UpperBody = null }));

        mesh.DiffuseTexturePath = @"textures\characters\_female\underwear.dds";
        Assert.Null(NpcTextureHelpers.ResolveEquipmentSkinTextureOverride(npc, mesh, AllAtlases));
        mesh.DiffuseTexturePath = @"textures\armor\iron\Hand.dds";
        Assert.Null(NpcTextureHelpers.ResolveEquipmentSkinTextureOverride(npc, mesh, AllAtlases));
    }

    [Theory]
    [InlineData(BethesdaGame.Fallout3)]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    [InlineData(BethesdaGame.Skyrim)]
    [InlineData(BethesdaGame.Fallout4)]
    public void LaterGameBarePartsPreserveWholePartOverrideAndUnderwearGuard(BethesdaGame game)
    {
        const string partOverride = "explicit-part-override";
        var npc = new NpcAppearance { Game = game };
        var mesh = Mesh("cloth", "Hand");
        mesh.DiffuseTexturePath = @"textures\characters\_female\lowerbody.dds";
        Assert.Equal(partOverride, NpcTextureHelpers.ResolveBodyPartTextureOverride(
            npc, mesh, AllAtlases, partOverride));
        Assert.Null(NpcTextureHelpers.ResolveBodyPartTextureOverride(npc, mesh, AllAtlases, null));

        mesh.LegacyMaterialName = "skin";
        mesh.DiffuseTexturePath = @"textures\characters\_female\UNDERWEAR.dds";
        Assert.Null(NpcTextureHelpers.ResolveBodyPartTextureOverride(npc, mesh, AllAtlases, partOverride));
        mesh.DiffuseTexturePath = @"textures\armor\iron\Hand.dds";
        Assert.Null(NpcTextureHelpers.ResolveBodyPartTextureOverride(npc, mesh, AllAtlases, partOverride));
        mesh.DiffuseTexturePath = null;
        Assert.Equal(partOverride, NpcTextureHelpers.ResolveBodyPartTextureOverride(
            npc, mesh, default, partOverride));
        mesh.DiffuseTexturePath = "";
        Assert.Equal(partOverride, NpcTextureHelpers.ResolveBodyPartTextureOverride(
            npc, mesh, default, partOverride));
    }

    private static RenderableSubmesh Mesh(string? material, string? shape) => new()
    {
        LegacyMaterialName = material,
        ShapeName = shape,
        Positions = [1, 2, 3],
        Triangles = [],
        Normals = [0, 0, 1],
        Tangents = [2, 0.3f, 0],
        Bitangents = [0.4f, -3, 0],
        DiffuseTexturePath = OriginalDiffuse,
        NormalMapTexturePath = OriginalNormal,
        MaterialDiffuse = (0.4f, 0.6f, 0.8f),
        SpecularColor = (0.5f, 0.7f, 0.9f),
        MaterialGlossiness = 23,
        MaterialAlpha = 0.8f
    };
}
