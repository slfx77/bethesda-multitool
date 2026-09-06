using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Ui;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class NpcBrowserDmpActorListTests
{
    [Fact]
    public void BuildDmpActorList_CombinesRuntimeNpcsWithGameAwareCompanionEsmCreatures()
    {
        var runtimeNpcs = new Dictionary<uint, NpcAppearance>
        {
            [0x00001234] = new()
            {
                NpcFormId = 0x00001234,
                EditorId = "RuntimeNpc",
                FullName = "Runtime NPC",
                IsFemale = true
            }
        };
        var creatures = new Dictionary<uint, CreatureScanEntry>
        {
            [0x0002B19A] = CreateCreature("CreatureDaedroth", "Daedroth", creatureType: 1)
        };

        var actors = NpcBrowserService.BuildDmpActorList(
            runtimeNpcs,
            creatures,
            BethesdaGame.Oblivion,
            namedOnly: false);

        Assert.Equal(2, actors.Count);
        var npc = Assert.Single(actors, static actor => !actor.IsCreature);
        Assert.Equal("RuntimeNpc", npc.EditorId);
        Assert.True(npc.IsFemale);

        var creature = Assert.Single(actors, static actor => actor.IsCreature);
        Assert.Equal("Daedra", creature.CreatureTypeName);
        Assert.Equal(@"meshes\creatures\daedroth\daedroth.nif", creature.ModelPath);
    }

    [Fact]
    public void BuildDmpActorList_WithNoRuntimeNpcs_StillProducesSelectableEsmCreature()
    {
        var creatures = new Dictionary<uint, CreatureScanEntry>
        {
            [0x0002B19A] = CreateCreature("CreatureDaedroth", "Daedroth", creatureType: 1)
        };

        var actors = NpcBrowserService.BuildDmpActorList(
            new Dictionary<uint, NpcAppearance>(),
            creatures,
            BethesdaGame.Oblivion,
            namedOnly: false);

        var creature = Assert.Single(actors);
        Assert.True(creature.IsCreature);

        var selection = NpcStartupActorSelector.Resolve(actors, "CreatureDaedroth");
        Assert.True(selection.IsResolved);
        Assert.Same(creature, selection.Actor);
        Assert.Equal("Daedra", creature.CreatureTypeName);
        Assert.Equal(@"meshes\creatures\daedroth\daedroth.nif", creature.ModelPath);
    }

    [Fact]
    public void BuildDmpActorList_NamedOnlyFiltersBothRuntimeAndCompanionFamilies()
    {
        var runtimeNpcs = new Dictionary<uint, NpcAppearance>
        {
            [1] = new() { NpcFormId = 1, EditorId = "UnnamedRuntimeNpc" }
        };
        var creatures = new Dictionary<uint, CreatureScanEntry>
        {
            [2] = CreateCreature("UnnamedCreature", fullName: null, creatureType: 0),
            [3] = CreateCreature("NamedCreature", "Named Creature", creatureType: 0)
        };

        var actors = NpcBrowserService.BuildDmpActorList(
            runtimeNpcs,
            creatures,
            BethesdaGame.Oblivion,
            namedOnly: true);

        Assert.Equal("NamedCreature", Assert.Single(actors).EditorId);
    }

    private static CreatureScanEntry CreateCreature(
        string editorId,
        string? fullName,
        byte creatureType)
    {
        return new CreatureScanEntry(
            editorId,
            fullName,
            @"meshes\creatures\daedroth\skeleton.nif",
            ["daedroth.nif"],
            AnimationPaths: null,
            InventoryItems: null,
            CreatureType: creatureType);
    }
}
