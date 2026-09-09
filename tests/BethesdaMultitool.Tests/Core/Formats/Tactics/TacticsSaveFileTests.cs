using System.Text;
using BethesdaMultitool.Core.Formats.Tactics;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Tactics;

/// <summary>
///     Vectors for the archive a Fallout Tactics save really is — a <c>&lt;saveh&gt;</c> header
///     followed by a <c>&lt;campaign_save&gt;</c> v1 directory of whole files — shaped after
///     <c>Snake.sav</c> measured 2026-09-07 (two entries, <c>user/$$current$$/mission01.sav</c> and
///     <c>user/$$current$$/save.cam</c>, summing to EOF).
///     <para>
///         ⚑ The claim under test is that the directory ACCOUNTS FOR EVERY BYTE: that is what makes
///         the file an archive rather than a header followed by data nobody has explained. So every
///         refusal here is a byte too many or a byte too few.
///     </para>
/// </summary>
public sealed class TacticsSaveFileTests
{
    private static readonly string[] Strings =
        ["", "New Save Game", "Snake", "Brahmin Wood", "Jan 1 2197.  06:29"];

    private static readonly float[] Floats = [30f, 30f, 36f, 0f, 0f, 0f];

    private static byte[] Archive(string version, params (string Path, byte[] Bytes)[] entries)
    {
        var parts = new List<byte[]>
        {
            TacticsSyntheticBytes.SaveHeader(1, Strings, Floats),
            TacticsSyntheticBytes.Tag("campaign_save", version),
            TacticsSyntheticBytes.U32((uint)entries.Length)
        };

        foreach (var (path, bytes) in entries)
        {
            parts.Add(TacticsSyntheticBytes.Wide(path));
            parts.Add(TacticsSyntheticBytes.U32((uint)bytes.Length));
            parts.Add(bytes);
        }

        return TacticsSyntheticBytes.Concat([.. parts]);
    }

    [Fact]
    public void Parse_ReadsAHeaderAndAnArchiveDirectoryOfTwoEntries()
    {
        byte[] first = [1, 2, 3, 4, 5];
        byte[] second = [0xAA, 0xBB];
        var bytes = Archive("1", ("user/$$current$$/mission01.sav", first), ("user/$$current$$/save.cam", second));

        // 319 (header) + 18 ('<campaign_save>' NUL '1' NUL) + 4 (count)
        //   + (4 + 60 + 4 + 5) + (4 + 50 + 4 + 2) = 474.
        Assert.Equal(474, bytes.Length);

        var save = TacticsSaveFile.Parse(bytes, "vector.sav");

        Assert.Equal("Snake", save.Header.SaveName);
        Assert.Equal(2, save.Entries.Count);
        Assert.Equal("user/$$current$$/mission01.sav", save.Entries[0].Path);
        Assert.Equal("user/$$current$$/save.cam", save.Entries[1].Path);
        Assert.Equal(first, save.Entries[0].Bytes.ToArray());
        Assert.Equal(second, save.Entries[1].Bytes.ToArray());

        // Neither payload carries a known framing, so both readings report why rather than pretend.
        Assert.Null(save.Snapshot);
        Assert.Null(save.Campaign);
        Assert.NotEmpty(save.SnapshotError);
        Assert.NotEmpty(save.CampaignError);
    }

    [Fact]
    public void Parse_RefusesBytesLeftOverAfterTheLastEntry()
    {
        var bytes = TacticsSyntheticBytes.Concat(Archive("1", ("a", [1, 2, 3])), [0x00]);

        var error = Assert.Throws<InvalidDataException>(() => TacticsSaveFile.Parse(bytes, "vector.sav"));

        Assert.Contains("campaign_save", error.Message, StringComparison.Ordinal);
        Assert.Contains("ends at", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesAnEntryThatRunsPastTheEndOfTheFile()
    {
        var bytes = Archive("1", ("a", [1, 2, 3]));

        // The last entry's length is the last u32 before its payload: overstate it by one.
        var lengthAt = bytes.Length - 3 - 4;
        bytes[lengthAt] = 4;

        var error = Assert.Throws<InvalidDataException>(() => TacticsSaveFile.Parse(bytes, "vector.sav"));

        Assert.Contains("remain", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesADirectoryVersionOtherThanTheMeasuredOne()
    {
        var bytes = Archive("2", ("a", [1]));

        var error = Assert.Throws<InvalidDataException>(() => TacticsSaveFile.Parse(bytes, "vector.sav"));

        Assert.Contains("'2'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_ReportsTheFileNameAndOffsetInsteadOfThrowing()
    {
        var bytes = TacticsSyntheticBytes.Concat(Archive("1", ("a", [1])), [0x00, 0x00]);

        Assert.False(TacticsSaveFile.TryParse(bytes, "Snake.sav", out _, out var error));
        Assert.Contains("Snake.sav", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RoutesTheSnapshotAndTheCampaignByContentNotByPosition()
    {
        // ⚠ The campaign is written FIRST here and the snapshot second — the reverse of the retail
        // file — because the two kinds are recognised by their tags, not by their index.
        var world = TacticsSyntheticBytes.Concat(
            TacticsSyntheticBytes.WorldHead("campaigns/missions/core/mission01.mis", "At ease, Initiate.",
                ["BaseAI", "Entity"]),
            TacticsSyntheticBytes.TextBag(("Weapon Item Type", "SMG")),
            TacticsSyntheticBytes.Team("BOS", 1));

        var snapshot = TacticsSyntheticBytes.Concat(
            TacticsSyntheticBytes.SaveHeader(0, ["locale/missions/mission01/MIS_01_Speech.txt", "", "", "", ""],
                [0f, 0f, 0f, 0f, 0f, 0f]),
            TacticsSyntheticBytes.WorldContainer(world));

        var bytes = Archive(
            "1",
            ("user/$$current$$/save.cam", TacticsCampaignStateTests.MinimalCampaign()),
            ("user/$$current$$/mission01.sav", snapshot));

        var save = TacticsSaveFile.Parse(bytes, "vector.sav");

        Assert.Empty(save.SnapshotError);
        Assert.Empty(save.CampaignError);
        Assert.NotNull(save.Snapshot);
        Assert.NotNull(save.Campaign);
        Assert.Equal("campaigns/missions/core/mission01.mis", save.Snapshot!.MissionPath);
        Assert.Equal("locale/missions/mission01/MIS_01_Speech.txt", save.Snapshot.Header.SpeechTextPath);
        Assert.Equal("campaigns/bos.cam", save.Campaign!.SourceCampaign);
    }

    [Fact]
    public void IsSave_MatchesTheOuterHeaderTagOnly()
    {
        Assert.True(TacticsSaveFile.IsSave(TacticsSyntheticBytes.SaveHeader(1, Strings, Floats)));
        Assert.False(TacticsSaveFile.IsSave(Encoding.ASCII.GetBytes("<campaign>\0" + "21\0")));
        Assert.False(TacticsSaveFile.IsSave([1, 2, 3]));
    }
}