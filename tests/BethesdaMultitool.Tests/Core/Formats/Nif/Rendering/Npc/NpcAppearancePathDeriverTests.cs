using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class NpcAppearancePathDeriverTests
{
    [Theory]
    [InlineData("Oblivion.esm", @"textures\faces\Oblivion.esm\000222A8_0.dds")]
    [InlineData(@"C:\Games\Oblivion\Data\ObLiViOn.EsM", @"textures\faces\ObLiViOn.EsM\000222A8_0.dds")]
    [InlineData("C:/Games/Oblivion/Data/My Faces.esp", @"textures\faces\My Faces.esp\000222A8_0.dds")]
    public void BuildAuthoredFaceGenMap0Path_FramesPluginAndPreservesItsCasing(
        string pluginName,
        string expected)
    {
        var path = NpcAppearancePathDeriver.BuildAuthoredFaceGenMap0Path(
            BethesdaGame.Oblivion,
            pluginName,
            0x000222A8);

        Assert.Equal(expected, path);
    }

    [Fact]
    public void BuildAuthoredFaceGenMap0Path_IsOblivionOnly()
    {
        Assert.Null(NpcAppearancePathDeriver.BuildAuthoredFaceGenMap0Path(
            BethesdaGame.Fallout3,
            "Fallout3.esm",
            0x000222A8));
        Assert.Null(NpcAppearancePathDeriver.BuildAuthoredFaceGenMap0Path(
            BethesdaGame.Oblivion,
            "  ",
            0x000222A8));
    }

    [Fact]
    public void FactoryStoresAuthoredMap0AndTextureComparisonCloneRetainsIt()
    {
        var index = new NpcAppearanceIndex { Game = BethesdaGame.Oblivion };
        var factory = new NpcAppearanceFactory(index);

        var appearance = factory.Build(
            0x000222A8,
            new NpcScanEntry { EditorId = "ReynaldJemane" },
            "Oblivion.esm");
        var comparison = appearance.CloneWithTextureVariant([0.25f], "npc_only");

        const string expected = @"textures\faces\Oblivion.esm\000222A8_0.dds";
        Assert.Equal(expected, appearance.AuthoredFaceGenMap0Path);
        Assert.Equal(expected, comparison.AuthoredFaceGenMap0Path);
    }

    [Fact]
    public void DmpFactoryStoresAuthoredMapOnlyForPluginBackedNonPlayerActors()
    {
        const uint stockNpcFormId = 0x000222A8;
        const uint runtimeOnlyFormId = 0xFF0022A8;
        var index = new NpcAppearanceIndex { Game = BethesdaGame.Oblivion };
        index.Npcs[stockNpcFormId] = new NpcScanEntry { EditorId = "ReynaldJemane" };
        index.Npcs[0x7] = new NpcScanEntry { EditorId = "PlayerBase" };
        var factory = new NpcAppearanceFactory(index);

        var stock = factory.BuildFromDmpRecord(
            new NpcRecord { FormId = stockNpcFormId, EditorId = "ReynaldJemane" },
            "Oblivion.esm");
        var runtimeOnly = factory.BuildFromDmpRecord(
            new NpcRecord { FormId = runtimeOnlyFormId, EditorId = "RuntimeOnly" },
            "Oblivion.esm");
        var player = factory.BuildFromDmpRecord(
            new NpcRecord { FormId = 0x7, EditorId = "PlayerBase" },
            "Oblivion.esm");

        Assert.Equal(@"textures\faces\Oblivion.esm\000222A8_0.dds", stock.AuthoredFaceGenMap0Path);
        Assert.Null(runtimeOnly.AuthoredFaceGenMap0Path);
        Assert.Null(player.AuthoredFaceGenMap0Path);
    }
}