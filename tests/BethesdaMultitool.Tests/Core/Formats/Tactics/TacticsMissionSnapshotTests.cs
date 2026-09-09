using System.Text;
using BethesdaMultitool.Core.Formats.Tactics;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Tactics;

/// <summary>
///     Vectors for the mission snapshot a save archives as <c>user/$$current$$/missionNN.sav</c> —
///     a <c>&lt;saveh&gt;</c> header plus a <c>&lt;world&gt;</c> container whose inflated payload
///     opens with a HEAD no shipped mission has. Shaped after <c>Snake.sav</c> measured 2026-09-07.
///     <para>
///         ⚠ The container is the mission's (<see cref="TacticsMissionFile" />: the size twice, then
///         a raw zlib stream) but the version is <c>70</c> and the payload does NOT open with
///         <c>&lt;mph&gt;</c>, so <see cref="TacticsMissionFile.Teams" /> is EMPTY on a save world and
///         the roster has to come from its <c>&lt;Team&gt;</c> chunks instead.
///     </para>
/// </summary>
public sealed class TacticsMissionSnapshotTests
{
    private const string MissionPath = "campaigns/missions/core/mission01.mis";

    private const string BriefingText = "At ease, Initiate. My name is General Barnaky.";

    private static readonly string[] TeamNames =
        ["No Team", "BOS", "Tribals (T)", "Raiders (R)", "Team 4", "Sentries (B)", "Hostages", "Brahmin", "Team 8"];

    private static readonly byte[] TeamFlags = [0, 1, 1, 1, 0, 1, 1, 1, 0];

    private static byte[] World(string sgdVersion = "5", bool wideTeams = true)
    {
        var parts = new List<byte[]>
        {
            TacticsSyntheticBytes.WorldHead(MissionPath, BriefingText, ["BaseAI", "Controller", "LinkHack"],
                sgdVersion),
            TacticsSyntheticBytes.TextBag(("Weapon Item Type", "SMG"))
        };

        parts.AddRange(TeamNames.Select((name, i) => TacticsSyntheticBytes.Team(name, TeamFlags[i], wideTeams)));
        return TacticsSyntheticBytes.Concat([.. parts]);
    }

    private static byte[] Snapshot(byte[] world, string worldVersion = "70")
    {
        return TacticsSyntheticBytes.Concat(
            TacticsSyntheticBytes.SaveHeader(0, ["locale/missions/mission01/MIS_01_Speech.txt", "", "", "", ""],
                [0f, 0f, 0f, 0f, 0f, 0f]),
            TacticsSyntheticBytes.WorldContainer(world, worldVersion));
    }

    [Fact]
    public void Parse_ReadsTheHeaderTheContainerAndTheWorldHeadUpToTheFirstEntity()
    {
        var world = World();
        var snapshot = TacticsMissionSnapshot.Parse(Snapshot(world), "mission01.sav");

        Assert.Equal(0, snapshot.Header.Flag);
        Assert.Equal("locale/missions/mission01/MIS_01_Speech.txt", snapshot.Header.SpeechTextPath);
        Assert.Equal("70", snapshot.World.Version);
        Assert.Equal(world.Length, snapshot.World.World.Length);
        Assert.Empty(snapshot.World.Teams);

        Assert.True(snapshot.HeadWalked, snapshot.HeadError);
        Assert.Equal(MissionPath, snapshot.MissionPath);
        Assert.Equal("5", snapshot.SgdVersion);
        Assert.Equal(72, snapshot.SgdBody.Length);
        Assert.Equal(20, snapshot.SsgBody.Length);

        var briefing = Assert.Single(snapshot.Briefings);
        Assert.Equal("Brief", briefing.Name);
        Assert.Equal((1u, 1u), (briefing.First, briefing.Second));
        Assert.Equal(BriefingText, briefing.Text);

        Assert.Equal(new[] { "BaseAI", "Controller", "LinkHack" }, snapshot.EntityClassNames);
        Assert.Equal(new uint[] { 2000, 955, 1398, 62, 47 }, snapshot.EntityListHeader);

        // ⚑ The alignment check, made independently: the first <esh> is located by SEARCHING the
        // inflated world, and the head walk must land on exactly that offset.
        var firstBag = world.AsSpan().IndexOf("<esh>"u8);
        Assert.True(firstBag > 0, "the fixture should carry an <esh>");
        Assert.Equal(firstBag, snapshot.EntitiesOffset);
    }

    [Fact]
    public void Parse_ReadsTheTeamChunksThatPairASaveWithItsMission()
    {
        var snapshot = TacticsMissionSnapshot.Parse(Snapshot(World()), "mission01.sav");

        Assert.Equal(TeamNames, snapshot.Teams.Select(team => team.Name));
        Assert.Equal(TeamFlags, snapshot.Teams.Select(team => team.Flag));
        Assert.All(snapshot.Teams, team => Assert.Equal(0u, team.Tail));
    }

    [Fact]
    public void ScanTeams_ReadsTheSameRosterFromAWideSaveWorldAndAnAsciiMissionWorld()
    {
        // ⚑ This is the cross-file oracle in miniature: a save writes its team names WIDE and a
        // mission writes them ASCII, and both must come back as the same nine strings.
        var wide = TacticsMissionFile.ScanTeams(World(wideTeams: true));
        var ascii = TacticsMissionFile.ScanTeams(World(wideTeams: false));

        Assert.Equal(TeamNames, wide.Select(team => team.Name));
        Assert.Equal(wide.Select(team => team.Name), ascii.Select(team => team.Name));
        Assert.Equal(wide.Select(team => team.Flag), ascii.Select(team => team.Flag));
    }

    [Fact]
    public void ScanTeams_SkipsACandidateWhoseVersionOrBodyDoesNotRead()
    {
        // A stray '<Team>' in binary data must cost a skipped candidate, not a bad read: here one
        // carries an unmeasured version and another is cut off before its five trailing bytes.
        var bytes = TacticsSyntheticBytes.Concat(
            TacticsSyntheticBytes.Team("BOS", 1),
            TacticsSyntheticBytes.Tag("Team", "9"),
            TacticsSyntheticBytes.Wide("Ignored"),
            new byte[5],
            TacticsSyntheticBytes.Tag("Team", "2"),
            TacticsSyntheticBytes.Wide("Truncated"));

        var teams = TacticsMissionFile.ScanTeams(bytes);

        var only = Assert.Single(teams);
        Assert.Equal("BOS", only.Name);
        Assert.Equal(1, only.Flag);
    }

    [Fact]
    public void Parse_LeavesTheWorldAvailableWhenTheHeadUsesAnUnmeasuredSgdVersion()
    {
        // ⚠ A body whose length is unknown must NOT be guessed past: the container still parses and
        // the bytes are still handed back, but the head reports why it stopped.
        var snapshot = TacticsMissionSnapshot.Parse(Snapshot(World("6")), "mission01.sav");

        Assert.False(snapshot.HeadWalked);
        Assert.Contains("'6'", snapshot.HeadError, StringComparison.Ordinal);
        Assert.Equal(-1, snapshot.EntitiesOffset);
        Assert.NotEmpty(snapshot.World.World);
    }

    [Fact]
    public void Parse_RefusesAContainerWhoseDeclaredSizeIsNotWhatInflates()
    {
        var bytes = Snapshot(World());

        // The two size fields sit 11 and 15 bytes into the container, which starts after the
        // 309-byte embedded header (10 + 1 + a 43-character path + four empty strings + 8 * 21 + 24).
        const int container = 309;
        Assert.Equal((byte)'<', bytes[container]);
        bytes[container + 11]++;
        bytes[container + 15]++;

        var error = Assert.Throws<InvalidDataException>(() => TacticsMissionSnapshot.Parse(bytes, "mission01.sav"));

        Assert.Contains("declares", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_ReportsWhyRatherThanThrowing()
    {
        Assert.False(TacticsMissionSnapshot.TryParse(Encoding.ASCII.GetBytes("not a save"), "x.sav", out _,
            out var error));
        Assert.NotEmpty(error);
    }
}