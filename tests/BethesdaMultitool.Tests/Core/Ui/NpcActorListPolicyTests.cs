using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using BethesdaMultitool.Core.Ui;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Ui;

public sealed class NpcActorListPolicyTests
{
    private static readonly NpcListItem NamedNpc =
        new(0x10, "ReynaldJemane", "Reynald Jemane", isFemale: false, raceFormId: 1);

    private static readonly NpcListItem UnnamedNpc =
        new(0x11, "GuardKvatch", fullName: null, isFemale: false, raceFormId: 1);

    private static readonly NpcListItem Daedroth =
        new(0x20, "CreatureDaedroth", "Daedroth", @"meshes\creatures\daedroth\daedroth.nif", "Daedra");

    [Fact]
    public void Filter_NpcTabNeverLeaksCreatures()
    {
        var filtered = NpcActorListPolicy.Filter(
            [Daedroth, UnnamedNpc, NamedNpc],
            NpcActorKind.Npc,
            namedOnly: false,
            searchText: null);

        Assert.Equal([NamedNpc, UnnamedNpc], filtered);
        Assert.All(filtered, actor => Assert.False(actor.IsCreature));
    }

    [Fact]
    public void Filter_CreatureTabSearchesOnlyCreatures()
    {
        var filtered = NpcActorListPolicy.Filter(
            [NamedNpc, Daedroth],
            NpcActorKind.Creature,
            namedOnly: true,
            searchText: "daed");

        Assert.Equal(Daedroth, Assert.Single(filtered));
    }

    [Fact]
    public void BuildSelectionCountText_UsesCurrentFamilyAndNoun()
    {
        var all = new[] { NamedNpc, UnnamedNpc, Daedroth };
        var namedNpcs = new[] { NamedNpc };

        Assert.Equal(
            "1 NPC (of 2)",
            NpcActorListPolicy.BuildSelectionCountText(namedNpcs, all, NpcActorKind.Npc));
        Assert.Equal(
            "1 creature",
            NpcActorListPolicy.BuildSelectionCountText([Daedroth], all, NpcActorKind.Creature));
    }
}
