using BethesdaMultitool.Core.Formats.Tactics;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Tactics;

/// <summary>
///     Vectors for the <c>&lt;campaign&gt;</c> v21 state a save archives as
///     <c>user/$$current$$/save.cam</c>, shaped after <c>Snake.sav</c> measured 2026-09-07 (104,378
///     bytes walked to the last one). The fixture below is the SAME record with every list cut to
///     one entry, so the walk it exercises is the walk the retail file needs — including the two
///     places the derivation went wrong: the bracketless <c>varTableHeader</c> literal, and a
///     <c>&lt;random_force&gt;</c>'s two count-prefixed u32 arrays that each repeat its entity count.
/// </summary>
public sealed class TacticsCampaignStateTests
{
    /// <summary>The whole record with one-entry lists; the retail one differs only in its counts.</summary>
    public static byte[] MinimalCampaign(string version = "21", int valueCount = 1, uint secondArrayCount = 1)
    {
        var parts = new List<byte[]>
        {
            TacticsSyntheticBytes.Tag("campaign", version),
            new byte[] { 1 },
            TacticsSyntheticBytes.Wide("campaigns/bos.cam"),
            TacticsSyntheticBytes.U32(2),
            TacticsSyntheticBytes.U32(2),
            TacticsSyntheticBytes.U32(0),
            TacticsSyntheticBytes.U32(0),
            TacticsSyntheticBytes.U32(0),
            TacticsSyntheticBytes.U32(0),
            TacticsSyntheticBytes.Wide("user/$$current$$/mission01.sav"),
            TacticsSyntheticBytes.U32(20),
            TacticsSyntheticBytes.U32(0),
            TacticsSyntheticBytes.U32(0),
            TacticsSyntheticBytes.U32(0),
            TacticsSyntheticBytes.VariableTableHeader,
            TacticsSyntheticBytes.U32(1),
            TacticsSyntheticBytes.Wide("CVAR_M10_MUTANTLAB"),
            TacticsSyntheticBytes.U32((uint)valueCount)
        };

        for (var i = 0; i < valueCount; i++)
        {
            parts.Add(TacticsSyntheticBytes.Wide("SALVAGED"));
        }

        parts.AddRange(new[]
        {
            TacticsSyntheticBytes.F32(1431f),
            TacticsSyntheticBytes.F32(1393f),
            TacticsSyntheticBytes.F32(4322f),
            TacticsSyntheticBytes.F32(867f),

            // One world-map location bag, then no special encounters.
            TacticsSyntheticBytes.U32(1),
            TacticsSyntheticBytes.TextBag(
                ("Name", "mission_name_01"),
                ("MissionFile", "user/$$current$$/mission01.sav"),
                ("State", "Visited")),
            TacticsSyntheticBytes.U32(0),

            // One <random_force> v2.
            TacticsSyntheticBytes.U32(1),
            TacticsSyntheticBytes.Tag("random_force", "2"),
            TacticsSyntheticBytes.Wide("Radscorp01_Easy"),
            TacticsSyntheticBytes.Wide("critter"),
            TacticsSyntheticBytes.U32(2),
            TacticsSyntheticBytes.U32(4),
            new byte[] { 1 },
            TacticsSyntheticBytes.U32(1),
            TacticsSyntheticBytes.Wide("entities/Actors/Critters/radscorp_01.ent"),
            TacticsSyntheticBytes.U32(1),
            TacticsSyntheticBytes.U32(3),
            TacticsSyntheticBytes.U32(secondArrayCount)
        });

        for (var i = 0; i < secondArrayCount; i++)
        {
            parts.Add(TacticsSyntheticBytes.U32(10));
        }

        parts.AddRange(new[]
        {
            TacticsSyntheticBytes.Wide("Z"),

            TacticsSyntheticBytes.U32(1),
            TacticsSyntheticBytes.Wide("campaigns/missions/random/missionY01.mis"),
            TacticsSyntheticBytes.U32(1),
            TacticsSyntheticBytes.Wide("entities/special/prefab/prefab1.ent"),

            // One stock row, then the two zero u32 that follow the table.
            TacticsSyntheticBytes.U32(1),
            TacticsSyntheticBytes.Wide("mission_name_01"),
            TacticsSyntheticBytes.Wide("entities/items/weapons/smg.ent"),
            TacticsSyntheticBytes.I32(-1000),
            new byte[8],

            // One recruit row — TRIPLES of wide strings, not singles.
            TacticsSyntheticBytes.U32(1),
            TacticsSyntheticBytes.Wide("mission_name_01"),
            TacticsSyntheticBytes.Wide("entities/recruits/farsight.ent"),
            TacticsSyntheticBytes.Wide("add"),

            new byte[31],
            TacticsSyntheticBytes.Wide("")
        });

        for (var i = 0; i < 6; i++)
        {
            parts.Add(TacticsSyntheticBytes.Wide(""));
            parts.Add(TacticsSyntheticBytes.U32(0xCFCF_CFCF));
        }

        parts.AddRange(new[]
        {
            TacticsSyntheticBytes.F32(1f),
            new byte[] { 0 },
            TacticsSyntheticBytes.U32(1),
            TacticsSyntheticBytes.Wide("mission_name_01")
        });

        var tail = new uint[] { 10, 0, 0, 1, 70, 0, 0, 0, 0, 0, 0, 0 };
        parts.AddRange(tail.Select(TacticsSyntheticBytes.U32));

        parts.AddRange(new[]
        {
            TacticsSyntheticBytes.U32(1),
            TacticsSyntheticBytes.Wide("movie_name_secondary"),
            TacticsSyntheticBytes.U32(1),
            TacticsSyntheticBytes.Wide("movie\\secondary.bik")
        });

        return TacticsSyntheticBytes.Concat([.. parts]);
    }

    [Fact]
    public void Parse_WalksTheWholeRecordAndLandsOnItsLastByte()
    {
        var bytes = MinimalCampaign();

        var campaign = TacticsCampaignState.Parse(bytes, "save.cam");

        Assert.Equal(1, campaign.Flag);
        Assert.Equal("campaigns/bos.cam", campaign.SourceCampaign);
        Assert.Equal((2, 2), (campaign.GridWidth, campaign.GridHeight));
        Assert.Equal(4, campaign.GridCells.Count);
        Assert.All(campaign.GridCells, cell => Assert.Equal(0u, cell));
        Assert.Equal("user/$$current$$/mission01.sav", campaign.CurrentMissionFile);
        Assert.Equal(new uint[] { 20, 0, 0, 0 }, campaign.HeaderValues);
        Assert.Equal(new TacticsCampaignVariable("CVAR_M10_MUTANTLAB", "SALVAGED"), Assert.Single(campaign.Variables));
        Assert.Equal(new[] { 1431f, 1393f, 4322f, 867f }, campaign.Floats);
        Assert.Equal("mission_name_01", Assert.Single(campaign.LocationNames));
        Assert.Empty(campaign.SpecialEncounters);

        var force = Assert.Single(campaign.RandomForces);
        Assert.Equal("Radscorp01_Easy", force.Name);
        Assert.Equal("critter", force.Kind);
        Assert.Equal((2u, 4u), (force.Low, force.High));
        Assert.Equal("entities/Actors/Critters/radscorp_01.ent", Assert.Single(force.EntityPaths));
        Assert.Equal(3u, Assert.Single(force.FirstValues));
        Assert.Equal(10u, Assert.Single(force.SecondValues));
        Assert.Equal("Z", force.Zone);

        Assert.Equal("campaigns/missions/random/missionY01.mis", Assert.Single(campaign.RandomMissionPaths));
        Assert.Equal("entities/special/prefab/prefab1.ent", Assert.Single(campaign.PrefabPaths));
        Assert.Equal(-1000, Assert.Single(campaign.Stock).Quantity);
        Assert.Equal("add", Assert.Single(campaign.Recruits).Action);
        Assert.Equal(6, campaign.RosterSlots.Count);
        Assert.All(campaign.RosterSlots, slot =>
        {
            Assert.Equal(string.Empty, slot.Name);
            Assert.Equal(TacticsCampaignState.UnsetSlot, slot.Value);
        });
        Assert.Equal(1f, campaign.TailFloat);
        Assert.Equal("mission_name_01", Assert.Single(campaign.CurrentLocationIds));
        Assert.Equal(new uint[] { 10, 0, 0, 1, 70, 0, 0, 0, 0, 0, 0, 0 }, campaign.TailValues);
        Assert.Equal("movie_name_secondary", Assert.Single(campaign.MovieNames));
        Assert.Equal("movie\\secondary.bik", Assert.Single(campaign.MoviePaths));

        // The record must account for every byte handed to it.
        Assert.Equal(bytes.Length, campaign.Length);
    }

    [Fact]
    public void Parse_RefusesARecordThatDoesNotEndWhereTheWalkDoes()
    {
        var bytes = TacticsSyntheticBytes.Concat(MinimalCampaign(), [0x00]);

        var error = Assert.Throws<InvalidDataException>(() => TacticsCampaignState.Parse(bytes, "save.cam"));

        Assert.Contains("ends at", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesTheShippedCampaignVersion()
    {
        // ⚠ campaigns/bos.cam is a <campaign> v19 whose body is laid out differently (it carries
        // <campaign_tile> chunks where a save carries a zeroed grid), so sharing the tag is not
        // sharing the format.
        var error = Assert.Throws<InvalidDataException>(() =>
            TacticsCampaignState.Parse(MinimalCampaign("19"), "bos.cam"));

        Assert.Contains("'19'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesAVariableTableHeaderThatIsNotTheLiteral()
    {
        // ⚠ 'varTableHeader' NUL '1' NUL is a tag WITHOUT brackets, which TacticsTagChunk rejects —
        // it has to be matched byte for byte or the variable table is read at the wrong offset.
        var bytes = MinimalCampaign();
        var at = IndexOf(bytes, TacticsSyntheticBytes.VariableTableHeader);
        Assert.True(at > 0, "the literal should be in the fixture");
        bytes[at + 1] = (byte)'A';

        var error = Assert.Throws<InvalidDataException>(() => TacticsCampaignState.Parse(bytes, "save.cam"));

        Assert.Contains("varTableHeader", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesARandomForceWhoseSecondArrayDisagreesWithItsEntityList()
    {
        // ⛔ The reading that FAILED first was "entity list, then a string": the two count-prefixed
        // u32 arrays after the list each repeat the entity count on 109/109 retail records, and a
        // reader that does not check that walks off into the next field.
        var error = Assert.Throws<InvalidDataException>(() =>
            TacticsCampaignState.Parse(MinimalCampaign(secondArrayCount: 2), "save.cam"));

        Assert.Contains("declares 2 values for 1 entities", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesAVariableValueListShorterThanItsNameList()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            TacticsCampaignState.Parse(MinimalCampaign(valueCount: 0), "save.cam"));

        Assert.Contains("1 variable names but 0 values", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsCampaign_MatchesTheCampaignTagOnly()
    {
        Assert.True(TacticsCampaignState.IsCampaign(MinimalCampaign()));
        Assert.False(TacticsCampaignState.IsCampaign(TacticsSyntheticBytes.Tag("campaign_save", "1")));
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        return haystack.AsSpan().IndexOf(needle);
    }
}