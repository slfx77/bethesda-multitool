using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class CreatureTypeNamePolicyTests
{
    [Theory]
    [InlineData(BethesdaGame.Oblivion, 0, "Creature")]
    [InlineData(BethesdaGame.Oblivion, 1, "Daedra")]
    [InlineData(BethesdaGame.Oblivion, 2, "Undead")]
    [InlineData(BethesdaGame.Oblivion, 3, "Humanoid")]
    [InlineData(BethesdaGame.Oblivion, 4, "Horse")]
    [InlineData(BethesdaGame.Oblivion, 5, "Giant")]
    [InlineData(BethesdaGame.Fallout3, 1, "Mutated Animal")]
    [InlineData(BethesdaGame.FalloutNewVegas, 2, "Mutated Insect")]
    [InlineData(BethesdaGame.Unknown, 1, "Creature type 1")]
    public void Resolve_UsesGameSpecificCreatureDataEnumeration(
        BethesdaGame game,
        byte creatureType,
        string expected)
    {
        Assert.Equal(expected, CreatureTypeNamePolicy.Resolve(game, creatureType));
    }

    [Fact]
    public void BrowserGameResolution_PrefersDecodedFamilyForRenamedPlugin()
    {
        Assert.Equal(
            BethesdaGame.Oblivion,
            NpcBrowserService.ResolveGame(BethesdaGame.Oblivion, "RenamedPlugin.esp"));
    }

    [Fact]
    public void BrowserGameResolution_UsesFilenameOnlyWhenDecodedFamilyIsUnknown()
    {
        Assert.Equal(
            BethesdaGame.FalloutNewVegas,
            NpcBrowserService.ResolveGame(BethesdaGame.Unknown, "FalloutNV.esm"));
    }
}
