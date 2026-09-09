using BethesdaMultitool.CLI.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class NpcEquipmentSkinTexturePolicyTests
{
    private const string Upper = @"body_egt\00085969_upperbody.dds";
    private const string Hand = @"body_egt\00085969_lefthand.dds";
    private const string Leg = @"textures\characters\orc\female\LegFemale.dds";
    private const string Foot = @"textures\characters\orc\female\FootFemale.dds";

    [Theory]
    [InlineData(@"textures\characters\imperial\female\LegFemale.dds", Leg)]
    [InlineData(@"textures/characters/imperial/male/LegMale.dds", Leg)]
    [InlineData(@"TEXTURES\CHARACTERS\IMPERIAL\FEMALE\LEGFEMALE.DDS", Leg)]
    [InlineData(@"textures\characters\imperial\female\LowerBodyFemale.dds", Leg)]
    [InlineData(@"textures\characters\imperial\female\FootFemale.dds", Foot)]
    [InlineData(@"textures/characters/imperial/female/FeetFemale.dds", Foot)]
    [InlineData(@"textures\characters\imperial\female\HandFemale.dds", Hand)]
    [InlineData(@"textures\characters\imperial\female\UpperBodyFemale.dds", Upper)]
    public void OblivionUsesMatchingBodyPartAtlas(string authored, string expected)
    {
        var npc = CreateAppearance();

        Assert.Equal(expected, NpcTextureHelpers.ResolveEquipmentSkinTextureOverride(npc, authored, Upper, Hand));
    }

    [Theory]
    [InlineData(@"textures\armor\iron\f\Greaves.dds")]
    [InlineData(@"textures\armor\iron\m\Boots.dds")]
    [InlineData(@"textures\armor\test\LegFemale.dds")]
    [InlineData(@"textures\characters\hair\HandBraids.dds")]
    [InlineData(@"textures\characters\imperial\female\HeadHuman.dds")]
    [InlineData(@"textures\characters\eyes\Foot.dds")]
    [InlineData(@"textures\characters\_female\underwear.dds")]
    [InlineData(@"body_egt\00085969_upperbody.dds")]
    [InlineData(null)]
    [InlineData("")]
    public void NonSkinOrAlreadyComposedTexturesArePreserved(string? authored)
    {
        Assert.Null(NpcTextureHelpers.ResolveEquipmentSkinTextureOverride(CreateAppearance(), authored, Upper, Hand));
    }

    [Theory]
    [InlineData(@"textures\characters\imperial\female\LegFemale.dds")]
    [InlineData(@"textures\characters\imperial\female\LowerBodyFemale.dds")]
    [InlineData(@"textures\characters\imperial\female\FootFemale.dds")]
    [InlineData(@"textures\characters\imperial\female\HandFemale.dds")]
    public void MissingClassicPartTextureNeverFallsBackToUnrelatedUpperAtlas(string authored)
    {
        var npc = new NpcAppearance { Game = BethesdaGame.Oblivion, BodyTexturePath = Upper };

        Assert.Null(NpcTextureHelpers.ResolveEquipmentSkinTextureOverride(npc, authored, Upper, null));
    }

    [Fact]
    public void ClassicLegDoesNotRequireAnUpperBodyTexture()
    {
        Assert.Equal(Leg, NpcTextureHelpers.ResolveEquipmentSkinTextureOverride(
            CreateAppearance(), @"textures\characters\imperial\female\LegFemale.dds", null, null));
    }

    [Fact]
    public void ClassicHandUsesAuthoredRaceHandWithoutGeneratedOverride()
    {
        var npc = CreateAppearance();

        Assert.Equal(npc.HandTexturePath, NpcTextureHelpers.ResolveEquipmentSkinTextureOverride(
            npc, @"textures\characters\imperial\female\HandFemale.dds", Upper, null));
    }

    [Theory]
    [InlineData((int)BethesdaGame.Fallout3)]
    [InlineData((int)BethesdaGame.FalloutNewVegas)]
    [InlineData((int)BethesdaGame.Skyrim)]
    public void OtherGamesRetainTheirBodyAtlasAndHandFallback(int gameValue)
    {
        var npc = new NpcAppearance
            { Game = (BethesdaGame)gameValue, LowerBodyTexturePath = Leg, FootTexturePath = Foot };

        Assert.Equal(Upper, NpcTextureHelpers.ResolveEquipmentSkinTextureOverride(
            npc, @"textures\characters\_female\lowerbody.dds", Upper, Hand));
        Assert.Equal(Upper, NpcTextureHelpers.ResolveEquipmentSkinTextureOverride(
            npc, @"textures\characters\_female\hand.dds", Upper, null));
        Assert.Equal(Hand, NpcTextureHelpers.ResolveEquipmentSkinTextureOverride(
            npc, @"textures\characters\_female\hand.dds", Upper, Hand));
        Assert.Null(NpcTextureHelpers.ResolveEquipmentSkinTextureOverride(
            npc, @"textures\characters\_female\hand.dds", null, Hand));
    }

    private static NpcAppearance CreateAppearance()
    {
        return new NpcAppearance
        {
            Game = BethesdaGame.Oblivion,
            BodyTexturePath = @"textures\characters\orc\female\UpperBodyFemale.dds",
            LowerBodyTexturePath = Leg,
            HandTexturePath = @"textures\characters\orc\female\HandFemale.dds",
            FootTexturePath = Foot
        };
    }
}