using System.Text;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Tactics;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Tactics;

/// <summary>
///     Opt-in (<c>RUN_BUCKET_B=1</c>) checks of a real Fallout Tactics save. Every number pinned
///     below was measured 2026-09-07 by an INDEPENDENT Python walk of <c>Snake.sav</c>
///     (scratchpad <c>tile_all.py</c>) and re-measured against the shipped archives, not produced by
///     the readers under test.
///     <para>
///         ⚑⚑ <b>The save is an ARCHIVE of the game's <c>user/$$current$$</c> directory</b>: a
///         <c>&lt;saveh&gt;</c> header of 65,019 bytes (five wide strings and eight
///         <c>&lt;zar&gt;</c> slots), then a <c>&lt;campaign_save&gt;</c> directory of two whole
///         files — <c>mission01.sav</c> (104,330 B) and <c>save.cam</c> (104,378 B) — summing to
///         273,875, the file's exact length. Parsing IS the proof: the reader refuses anything that
///         does not land on the last byte.
///     </para>
///     <para>
///         ⚠ The fixture is the user's own save. The structural test runs on whatever save is
///         installed; the tests that pin content (names, counts, image sizes) skip unless the file
///         is the measured <c>Snake.sav</c> of 273,875 bytes, because a later save is a different
///         measurement, not a failure.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class TacticsSaveRetailTests
{
    private const int MeasuredLength = 273_875;

    private static readonly string[] MeasuredTeams =
        ["No Team", "BOS", "Tribals (T)", "Raiders (R)", "Team 4", "Sentries (B)", "Hostages", "Brahmin", "Team 8"];

    private static readonly byte[] MeasuredTeamFlags = [0, 1, 1, 1, 0, 1, 1, 1, 0];

    private static (string Path, byte[] Bytes) RequireSave()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var path = RealAssetPaths.Classics.FalloutTacticsSave();
        Assert.SkipWhen(path is null, RealAssetPaths.SkipMessage("a Fallout Tactics save under core/user/save"));
        return (path, File.ReadAllBytes(path));
    }

    private static (byte[] Bytes, TacticsSaveFile Save) RequireMeasuredSave()
    {
        var (path, bytes) = RequireSave();
        Assert.SkipWhen(
            !string.Equals(Path.GetFileName(path), "Snake.sav", StringComparison.OrdinalIgnoreCase) ||
            bytes.Length != MeasuredLength,
            $"the installed save is {Path.GetFileName(path)} ({bytes.Length} B), not the measured Snake.sav ({MeasuredLength} B)");
        return (bytes, TacticsSaveFile.Parse(bytes, Path.GetFileName(path)));
    }

    private static string RequireArchive(string fileName)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var core = RealAssetPaths.Classics.FalloutTactics();
        Assert.SkipWhen(core is null, RealAssetPaths.SkipMessage("Fallout Tactics"));
        var path = Path.Combine(core, fileName);
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage(fileName));
        return path;
    }

    /// <summary>
    ///     The shipped speech files wrap their lines and write paragraph breaks as literal
    ///     <c>\n</c> escapes; the save stores the expanded text. Comparing them means dropping the
    ///     escapes and the layout whitespace from both — what is left is the prose itself.
    /// </summary>
    private static string Squeeze(string text)
    {
        var builder = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i++];
            if (c == '\\' && i < text.Length && text[i] == 'n')
            {
                i++;
                continue;
            }

            if (c is '\r' or '\n' or ' ' or '\t')
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    ///     A walk of the <c>&lt;campaign_save&gt;</c> directory written HERE from the measured
    ///     layout and independent of <see cref="TacticsSaveFile" />: the 18-byte tag
    ///     (<c>"&lt;campaign_save&gt;"</c> + NUL, version <c>"1"</c>, NUL), a u32 entry count, then
    ///     per entry a u32 whose bit 31 is set and whose low bits are the character count, that many
    ///     UTF-16LE units, a u32 payload length and that many bytes. Returns what it read and the
    ///     offset it lands on — a second reading of the same bytes, so a mis-slice in the reader
    ///     shows up as a disagreement rather than cancelling out of an arithmetic sum.
    /// </summary>
    private static (List<(string Path, int Length)> Entries, int End) WalkDirectory(byte[] bytes, int start)
    {
        var offset = start + 18;
        var count = BitConverter.ToInt32(bytes, offset);
        offset += 4;

        var entries = new List<(string Path, int Length)>();
        for (var i = 0; i < count; i++)
        {
            var prefix = BitConverter.ToUInt32(bytes, offset);
            offset += 4;
            Assert.Equal(0x8000_0000u, prefix & 0x8000_0000u);

            var characters = (int)(prefix & 0x7FFF_FFFF);
            var path = Encoding.Unicode.GetString(bytes, offset, characters * 2);
            offset += characters * 2;

            var length = BitConverter.ToInt32(bytes, offset);
            offset += 4;
            entries.Add((path, length));
            offset += length;
        }

        return (entries, offset);
    }

    [Fact]
    public void AnyInstalledSave_TilesExactlyAndRoutesItsTwoArchivedFiles()
    {
        var (path, bytes) = RequireSave();

        // Parse refuses unless the archive directory accounts for every byte of the file.
        var save = TacticsSaveFile.Parse(bytes, Path.GetFileName(path));

        Assert.Equal(TacticsSaveHeader.StringCount, save.Header.Strings.Count);
        Assert.Equal(TacticsSaveHeader.ImageSlotCount, save.Header.Images.Count);
        Assert.Equal(TacticsSaveHeader.FloatCount, save.Header.Floats.Count);
        Assert.Equal(Path.GetFileNameWithoutExtension(path), save.Header.SaveName);

        // ⚠ Summing the reader's own lengths back up would be near-tautological — Parse already
        // refuses a file its cursor does not land on the end of. These two pins are not: the
        // <campaign_save> tag is FOUND in the file's raw bytes and must sit at exactly
        // Header.Length, and the directory walk written above (in this test, from the layout) must
        // land on the file's last byte and report the same paths and payload lengths the reader
        // does. A reader that mis-sized the header or a payload disagrees here.
        var directoryOffset = bytes.AsSpan().IndexOf("<campaign_save>\0"u8);
        Assert.Equal(save.Header.Length, directoryOffset);

        var walked = WalkDirectory(bytes, directoryOffset);
        Assert.Equal(bytes.Length, walked.End);
        Assert.Equal(
            save.Entries.Select(entry => (entry.Path, entry.Bytes.Length)),
            walked.Entries);

        Assert.Empty(save.SnapshotError);
        Assert.Empty(save.CampaignError);
        Assert.NotNull(save.Snapshot);
        Assert.NotNull(save.Campaign);
        Assert.True(save.Snapshot!.HeadWalked, save.Snapshot.HeadError);
    }

    [Fact]
    public void SnakeSave_CarriesTheMeasuredHeaderStringsFloatsAndEntryLengths()
    {
        var (bytes, save) = RequireMeasuredSave();

        Assert.Equal(1, save.Header.Flag);
        Assert.Equal(
            new[] { string.Empty, "New Save Game", "Snake", "Brahmin Wood", "Jan 1 2197.  06:29" },
            save.Header.Strings);
        Assert.Equal(new[] { 30f, 30f, 36f, 0f, 0f, 0f }, save.Header.Floats);

        // 10 (tag) + 1 + the five strings (127 in) + 5 images + 3 empty slots + 6 floats.
        Assert.Equal(65_019, save.Header.Length);

        Assert.Equal(
            new[] { "user/$$current$$/mission01.sav", "user/$$current$$/save.cam" },
            save.Entries.Select(entry => entry.Path));
        Assert.Equal(new[] { 104_330, 104_378 }, save.Entries.Select(entry => entry.Bytes.Length));

        // The layout, pinned against the fixture's raw bytes rather than against the reader: the
        // <campaign_save> tag is at byte 65,019 (so that IS the header's length), and the walk
        // written in this test reads the two entries and lands on 273,875 — the file's own length.
        // 65,019 + 18 + 4 + (68 + 104,330) + (58 + 104,378) = 273,875.
        var directoryOffset = bytes.AsSpan().IndexOf("<campaign_save>\0"u8);
        Assert.Equal(65_019, directoryOffset);
        Assert.Equal(save.Header.Length, directoryOffset);

        var walked = WalkDirectory(bytes, directoryOffset);
        Assert.Equal(MeasuredLength, walked.End);
        Assert.Equal(MeasuredLength, bytes.Length);
        Assert.Equal(
            new[] { ("user/$$current$$/mission01.sav", 104_330), ("user/$$current$$/save.cam", 104_378) },
            walked.Entries);
    }

    [Fact]
    public void SnakeSave_EightImageSlotsDecodeAtTheirMeasuredSizes()
    {
        var save = RequireMeasuredSave().Save;
        var images = save.Header.Images;

        Assert.Equal(
            new[] { (280, 165), (128, 76), (25, 33), (25, 33), (25, 33), (0, 0), (0, 0), (0, 0) },
            images.Select(image => (image.Width, image.Height)));
        Assert.Equal(
            new[] { 47_025, 9_956, 858, 858, 858, 0, 0, 0 },
            images.Select(image => image.PixelBlock.Length));
        Assert.Equal(
            new[] { 48_075, 11_006, 1_908, 1_908, 1_908, 21, 21, 21 },
            images.Select(image => image.RecordLength));

        foreach (var image in images.Where(image => image.HasImage))
        {
            Assert.Equal(4, image.Version);
            Assert.Equal(256, image.PaletteEntries);
            Assert.Equal(0, image.ShadowIndex);

            // Decode refuses a run past a row's edge, a payload past the block or bytes left over:
            // width * height pixels out and the block consumed to its last byte.
            var texture = image.Decode();
            Assert.Equal(image.Width * image.Height * 4, texture.Pixels.Length);

            // The save's images are pure mode-1 literal runs: no skips, no index/alpha pairs and no
            // shadow runs, which is why every pixel below comes out opaque.
            var census = image.CountRuns();
            Assert.Equal((0, 0, 0), (census.Transparent, census.Translucent, census.Shadow));
            Assert.True(census.Opaque > 0, "an image with pixels must carry opaque runs");
        }

        // ⚑ The three 25x33 slots are three DIFFERENT pictures, not one record read three times.
        var portraits = images.Skip(2).Take(3).Select(image => Convert.ToHexString(image.PixelBlock.Span)).ToList();
        Assert.Equal(3, portraits.Distinct(StringComparer.Ordinal).Count());

        // Every pixel of the 280x165 screenshot is opaque: it is a framebuffer grab, not a sprite.
        var screenshot = images[0].Decode().Pixels;
        Assert.Equal(280 * 165, Enumerable.Range(0, 280 * 165).Count(i => screenshot[i * 4 + 3] == 255));
    }

    [Fact]
    public void SnakeSave_ArchivesAMissionSnapshotWhoseWorldInflatesToTheDeclaredSize()
    {
        var save = RequireMeasuredSave().Save;
        var snapshot = save.Snapshot;
        Assert.NotNull(snapshot);

        Assert.Equal(0, snapshot.Header.Flag);
        Assert.Equal("locale/missions/mission01/MIS_01_Speech.txt", snapshot.Header.SpeechTextPath);
        Assert.Equal(309, snapshot.Header.Length);
        Assert.All(snapshot.Header.Images, image => Assert.False(image.HasImage));

        // ⚠ Version 70 — outside the retail 68/69 set — and NO <mph>, so the container's own team
        // roster is empty on a save world.
        Assert.Equal("70", snapshot.World.Version);
        Assert.Equal(2_607_657, snapshot.World.World.Length);
        Assert.Empty(snapshot.World.Teams);

        // ⚑ The zlib stream ends at the archived entry's LAST byte, and that is now enforced rather
        // than assumed: the very same entry with one byte appended is refused, because the trailing
        // four bytes are no longer the inflated world's Adler-32. (Measured independently in Python:
        // decompressobj leaves unused_data empty, and the trailer matches, on 129/129 real worlds —
        // 103 archived .mis, 25 loose .mis and this one.)
        var padded = new byte[save.Entries[0].Bytes.Length + 1];
        save.Entries[0].Bytes.Span.CopyTo(padded);
        Assert.False(TacticsMissionSnapshot.TryParse(padded, "padded.sav", out _, out var padError));
        Assert.Contains("does not end at the last byte", padError, StringComparison.Ordinal);

        Assert.True(snapshot.HeadWalked, snapshot.HeadError);
        Assert.Equal("campaigns/missions/core/mission01.mis", snapshot.MissionPath);
        Assert.Equal("5", snapshot.SgdVersion);
        Assert.Equal(72, snapshot.SgdBody.Length);
        Assert.Equal(20, snapshot.SsgBody.Length);
        Assert.Equal(52, snapshot.EntityClassNames.Count);
        Assert.Equal("BaseAI", snapshot.EntityClassNames[0]);
        Assert.Equal("LinkHack", snapshot.EntityClassNames[^1]);
        Assert.Equal(new uint[] { 2000, 955, 1398, 62, 47 }, snapshot.EntityListHeader);
        Assert.Equal(4_820, snapshot.EntitiesOffset);
        Assert.Equal(snapshot.EntitiesOffset, snapshot.World.World.AsSpan().IndexOf("<esh>"u8));

        var briefing = Assert.Single(snapshot.Briefings);
        Assert.Equal("Brief", briefing.Name);
        Assert.Equal((1u, 1u), (briefing.First, briefing.Second));
        Assert.Equal(1_732, briefing.Text.Length);
        Assert.StartsWith("At ease, Initiate. My name is General Barnaky.", briefing.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SnakeSave_TeamRosterEqualsTheShippedMissionItNames()
    {
        // ⚑ THE ORACLE FROM OUTSIDE THE SAVE: the save's own world and the shipped mission it names
        // carry the same nine <Team> chunks, the save's written WIDE and the mission's ASCII.
        var save = RequireMeasuredSave().Save;
        var snapshot = save.Snapshot;
        Assert.NotNull(snapshot);

        Assert.Equal(MeasuredTeams, snapshot.Teams.Select(team => team.Name));
        Assert.Equal(MeasuredTeamFlags, snapshot.Teams.Select(team => team.Flag));

        using var archive = ArchiveReader.Open(RequireArchive("mis-core_A.bos"));
        var missionBytes = archive.ReadFile(snapshot.MissionPath);
        Assert.SkipWhen(missionBytes is null, $"{snapshot.MissionPath} is not in mis-core_A.bos");

        var mission = TacticsMissionFile.Parse(missionBytes, snapshot.MissionPath);
        Assert.Equal("68", mission.Version);
        Assert.Equal(3_925_680, mission.World.Length);

        // The mission DOES open with <mph>, whose roster is the six playable teams.
        Assert.Equal(
            new[] { "BOS", "Tribals (T)", "Raiders (R)", "Sentries (B)", "Hostages", "Brahmin" },
            mission.Teams);

        var missionTeams = TacticsMissionFile.ScanTeams(mission.World);
        Assert.Equal(MeasuredTeams, missionTeams.Select(team => team.Name));
        Assert.Equal(MeasuredTeamFlags, missionTeams.Select(team => team.Flag));
        Assert.Equal(snapshot.Teams.Select(team => (team.Name, team.Flag)),
            missionTeams.Select(team => (team.Name, team.Flag)));
    }

    [Fact]
    public void SnakeSave_BriefingIsTheTextOfTheSpeechFileItsHeaderNames()
    {
        // ⚠ NOT byte-for-byte: the shipped file wraps its lines and writes the paragraph breaks as
        // literal \n escapes, so the two agree once escapes and layout whitespace are dropped —
        // 1,431 characters of prose, looked up by the path the SAVE states.
        var save = RequireMeasuredSave().Save;
        var snapshot = save.Snapshot;
        Assert.NotNull(snapshot);

        using var archive = ArchiveReader.Open(RequireArchive("loc-mis_A.bos"));
        var speechBytes = archive.ReadFile(snapshot.Header.SpeechTextPath);
        Assert.SkipWhen(speechBytes is null, $"{snapshot.Header.SpeechTextPath} is not in loc-mis_A.bos");

        var speech = Squeeze(Encoding.Latin1.GetString(speechBytes));
        var briefing = Squeeze(Assert.Single(snapshot.Briefings).Text);

        Assert.Equal(1_431, briefing.Length);
        Assert.Contains(briefing, speech, StringComparison.Ordinal);
    }

    [Fact]
    public void SnakeSave_ArchivesACampaignStateThatWalksToItsLastByte()
    {
        var save = RequireMeasuredSave().Save;
        var campaign = save.Campaign;
        Assert.NotNull(campaign);

        Assert.Equal(104_378, campaign.Length);
        Assert.Equal("campaigns/bos.cam", campaign.SourceCampaign);
        Assert.Equal((65, 34), (campaign.GridWidth, campaign.GridHeight));
        Assert.Equal(65 * 34, campaign.GridCells.Count);
        Assert.All(campaign.GridCells, cell => Assert.Equal(0u, cell));

        // The campaign names the sibling entry of the archive it lives in.
        Assert.Equal(save.Entries[0].Path, campaign.CurrentMissionFile);

        Assert.Equal(95, campaign.Variables.Count);
        Assert.Equal(94, campaign.Variables.Count(variable => variable.Value == "FALSE"));
        Assert.Equal(
            new TacticsCampaignVariable("CVAR_M10_MUTANTLAB", "SALVAGED"),
            Assert.Single(campaign.Variables, variable => variable.Value != "FALSE"));

        Assert.Equal(27, campaign.Locations.Count);
        Assert.Equal(30, campaign.SpecialEncounters.Count);
        Assert.Equal("mission_name_01", campaign.LocationNames.First());
        Assert.All(campaign.SpecialEncounterNames,
            name => Assert.StartsWith("mission_name_", name, StringComparison.Ordinal));

        Assert.Equal(109, campaign.RandomForces.Count);
        Assert.Equal("Radscorp01_Easy", campaign.RandomForces[0].Name);
        Assert.Equal(
            new[] { ("bad", 55), ("critter", 39), ("good", 15) },
            campaign.RandomForces.GroupBy(force => force.Kind, StringComparer.Ordinal)
                .Select(group => (group.Key, group.Count()))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.All(campaign.RandomForces, force =>
        {
            Assert.True(force.Low <= force.High, $"{force.Name}: {force.Low} > {force.High}");
            Assert.Equal(force.EntityPaths.Count, force.FirstValues.Count);
            Assert.Equal(force.EntityPaths.Count, force.SecondValues.Count);
        });

        Assert.Equal(42, campaign.RandomMissionPaths.Count);
        Assert.Equal(5, campaign.PrefabPaths.Count);
        Assert.Equal(232, campaign.Stock.Count);
        Assert.Equal(45, campaign.Stock.Count(row => row.Quantity == -1000));
        Assert.Equal(66, campaign.Recruits.Count);
        Assert.All(campaign.Recruits, recruit => Assert.Equal("add", recruit.Action));
        Assert.All(campaign.RosterSlots, slot => Assert.Equal(TacticsCampaignState.UnsetSlot, slot.Value));
        Assert.Equal("mission_name_01", Assert.Single(campaign.CurrentLocationIds));
        Assert.Equal(new uint[] { 10, 0, 0, 1, 70, 0, 0, 0, 0, 0, 0, 0 }, campaign.TailValues);
        Assert.Equal(4, campaign.MovieNames.Count);
        Assert.Equal(4, campaign.MoviePaths.Count);
        Assert.Equal("movie\\secondary.bik", campaign.MoviePaths[^1]);
    }

    [Fact]
    public void SnakeSave_VisitedLocationPointsAtTheArchivedSnapshot()
    {
        // ⚑ The one location marked Visited is mission_name_01, and its MissionFile is the archive
        // entry the campaign is currently in — three fields of two different records agreeing.
        var save = RequireMeasuredSave().Save;
        var campaign = save.Campaign;
        Assert.NotNull(campaign);

        var visited = campaign.Locations
            .Where(bag => TacticsCampaignState.Find(bag, "State") is { } state &&
                          TacticsCursor.PropertyText(state) == "Visited")
            .ToList();

        var bagOfVisited = Assert.Single(visited);
        Assert.Equal("mission_name_01",
            TacticsCursor.PropertyText(TacticsCampaignState.Find(bagOfVisited, "Name")!.Value));
        Assert.Equal(
            save.Entries[0].Path,
            TacticsCursor.PropertyText(TacticsCampaignState.Find(bagOfVisited, "MissionFile")!.Value));
        Assert.Equal("Brahmin Wood", save.Header.MissionName);
    }
}
