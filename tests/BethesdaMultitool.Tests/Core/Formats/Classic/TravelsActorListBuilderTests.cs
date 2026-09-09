using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Projecting Travels monster records into the classic actor list.
///     <para>
///         The claim worth testing is that the projection does NOT name what the reader declined to
///         name. The monster table has 17 columns and only four have established meanings; the rest
///         are carried verbatim. A projection that quietly labelled them would launder a hypothesis
///         into the UI, where it would outlive the note explaining it — and a list of plausibly
///         named stats is indistinguishable from a correct one at a glance.
///     </para>
/// </summary>
public sealed class TravelsActorListBuilderTests
{
    private const string Signature = "SMON";

    /// <summary>The five columns the monster reader is willing to name, in display order.</summary>
    private static readonly string[] NamedColumns = ["Id", "Family", "HitPoints", "DropChance", "LootRolls"];

    /// <summary>The ordinal labels the three unnamed columns of the fixture receive.</summary>
    private static readonly string[] OrdinalLabels = ["Stat 0", "Stat 1", "Stat 2"];

    /// <summary>Those columns' verbatim values.</summary>
    private static readonly string[] OrdinalValues = ["7", "8", "9"];

    /// <summary>The two fixture monsters, in source order.</summary>
    private static readonly string[] FixtureNames = ["Skelos", "Gryphon"];

    private static GenericEsmRecord Monster(
        uint id, string name, string? stats = "7, 8, 9", string signature = Signature)
    {
        return new GenericEsmRecord
        {
            FormId = 0x52000000 | id,
            RecordType = signature,
            EditorId = "MONSTER_" + name,
            FullName = name,
            Fields = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Id"] = (int)id,
                ["Family"] = 2,
                ["HitPoints"] = 40,
                ["DropChance"] = 15,
                ["LootRolls"] = 1,
                ["Stats"] = stats
            }
        };
    }

    [Fact]
    public void MonsterRecordsBecomeActorsInSourceOrder()
    {
        var list = TravelsActorListBuilder.Build(
            [Monster(1, "Skelos"), Monster(2, "Gryphon")], Signature, "Stormhold");

        Assert.Equal("Stormhold", list.GameName);
        Assert.Equal(FixtureNames, list.Actors.Select(a => a.Name));
        Assert.Equal("MONSTER_Skelos", list.Actors[0].EditorId);
        Assert.Equal(0x52000001u, list.Actors[0].FormId);
        Assert.Equal("Monster", list.Actors[0].Kind);
    }

    /// <summary>
    ///     ⚠ The four established columns are named; the rest keep ordinal labels and are flagged
    ///     unnamed. This is the assertion the whole projection exists to protect.
    /// </summary>
    [Fact]
    public void UnnamedStatColumnsKeepOrdinalLabelsAndAreFlagged()
    {
        var actor = Assert.Single(
            TravelsActorListBuilder.Build([Monster(1, "Skelos")], Signature, "Stormhold").Actors);

        var named = actor.Stats.Where(s => s.IsNamed).Select(s => s.Name).ToArray();
        Assert.Equal(NamedColumns, named);

        var unnamed = actor.Stats.Where(s => !s.IsNamed).ToArray();
        Assert.Equal(OrdinalLabels, unnamed.Select(s => s.Name));
        Assert.Equal(OrdinalValues, unnamed.Select(s => s.Value));
    }

    /// <summary>Named columns come first, so a list view's columns do not shuffle between rows.</summary>
    [Fact]
    public void NamedColumnsPrecedeUnnamedOnes()
    {
        var actor = Assert.Single(
            TravelsActorListBuilder.Build([Monster(1, "Skelos")], Signature, "Stormhold").Actors);

        var firstUnnamed = actor.Stats.ToList().FindIndex(s => !s.IsNamed);
        var lastNamed = actor.Stats.ToList().FindLastIndex(s => s.IsNamed);

        Assert.True(lastNamed < firstUnnamed, "A named column appeared after an unnamed one.");
    }

    [Fact]
    public void ValuesAreFormattedInvariantly()
    {
        var actor = Assert.Single(
            TravelsActorListBuilder.Build([Monster(3, "Rat")], Signature, "Stormhold").Actors);

        Assert.Equal("40", actor.Stats.Single(s => s.Name == "HitPoints").Value);
    }

    [Fact]
    public void RecordsOfOtherSignaturesAreIgnored()
    {
        var list = TravelsActorListBuilder.Build(
            [Monster(1, "Kept"), Monster(2, "Dropped", signature: "SITM")], Signature, "Stormhold");

        Assert.Equal("Kept", Assert.Single(list.Actors).Name);
    }

    /// <summary>A monster with no unnamed columns still lists its named ones.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AMonsterWithoutPackedStatsStillListsItsNamedColumns(string? stats)
    {
        var actor = Assert.Single(
            TravelsActorListBuilder.Build([Monster(1, "Skelos", stats)], Signature, "Stormhold").Actors);

        Assert.Equal(5, actor.Stats.Count);
        Assert.All(actor.Stats, s => Assert.True(s.IsNamed));
    }

    /// <summary>A record missing a named field omits that column rather than inventing a zero.</summary>
    [Fact]
    public void AMissingNamedFieldIsOmittedNotZeroed()
    {
        var record = new GenericEsmRecord
        {
            FormId = 1,
            RecordType = Signature,
            EditorId = "MONSTER_Partial",
            FullName = "Partial",
            Fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Id"] = 1 }
        };

        var actor = Assert.Single(TravelsActorListBuilder.Build([record], Signature, "Stormhold").Actors);

        Assert.Equal("Id", Assert.Single(actor.Stats).Name);
        Assert.DoesNotContain(actor.Stats, s => s.Name == "HitPoints");
    }

    [Fact]
    public void Build_RejectsNullOrBlankInputs()
    {
        Assert.Throws<ArgumentNullException>(() => TravelsActorListBuilder.Build(null!, Signature, "Stormhold"));
        Assert.Throws<ArgumentException>(() => TravelsActorListBuilder.Build([], " ", "Stormhold"));
        Assert.Throws<ArgumentException>(() => TravelsActorListBuilder.Build([], Signature, " "));
    }
}