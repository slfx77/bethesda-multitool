using System.Linq;
using BethesdaMultitool.Core.Formats.Arena;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Arena;

/// <summary>
///     Grammar vectors for <see cref="ArenaTemplateDat" />, shaped after the retail file
///     (395,981 bytes, 811 key lines, 613 distinct keys, max key 1501 — surveyed 2026-09-01).
/// </summary>
public class ArenaTemplateDatTests
{
    [Fact]
    public void ParseText_SplitsOnAmpersandAndDropsTheTrailingSlack()
    {
        var dat = ArenaTemplateDat.ParseText("#0001\r\nfirst&second&trailing junk\r\n");

        var entry = Assert.Single(dat.Entries);
        Assert.Equal(1, entry.Key);
        Assert.Equal(["first", "second"], entry.Values);
    }

    [Fact]
    public void ParseText_CollapsesLineBreaksAndRunsOfSpacesIntoOne()
    {
        var dat = ArenaTemplateDat.ParseText("#0002\r\nYou walk into\r\nthe   room.&\r\n");

        Assert.Equal(["You walk into the room."], Assert.Single(dat.Entries).Values);
    }

    [Fact]
    public void ParseText_LetterSuffix_IsReadAsAVariant()
    {
        var dat = ArenaTemplateDat.ParseText("#0014b\r\nsnowy text&\r\n");

        var entry = Assert.Single(dat.Entries);
        Assert.Equal(14, entry.Key);
        Assert.Equal('b', entry.Letter);
        Assert.True(entry.HasLetter);
        Assert.Equal("#0014b", entry.DisplayKey);
    }

    [Fact]
    public void ParseText_TrailingProseOnAKeyLine_IsNotAVariantLetter()
    {
        // Two retail lines carry authoring prose rather than a suffix: "#0012 No deliver quest
        // unknown" and "#0183 the". A genuine suffix always sits at index 5.
        var dat = ArenaTemplateDat.ParseText("#0012 No deliver quest unknown\r\nbody&\r\n");

        var entry = Assert.Single(dat.Entries);
        Assert.Equal(12, entry.Key);
        Assert.False(entry.HasLetter);
        Assert.Equal("#0012", entry.DisplayKey);
    }

    [Fact]
    public void ParseText_RepeatedKey_BecomesSuccessiveTilesetCopies()
    {
        // Keys #0000-#0004 ship three copies, one per tileset. They must not overwrite each other.
        var dat = ArenaTemplateDat.ParseText(
            "#0000a\r\ntemperate&\r\n#0000a\r\ndesert&\r\n#0000a\r\nsnowy&\r\n");

        Assert.Equal(3, dat.Entries.Count);
        Assert.Equal([0, 1, 2], dat.Entries.Select(e => e.Copy));
        Assert.Equal(["temperate"], dat.Entries[0].Values);
        Assert.Equal(["desert"], dat.Entries[1].Values);
        Assert.Equal(["snowy"], dat.Entries[2].Values);
    }

    [Fact]
    public void ParseText_CommentLine_ClosesTheEntryAndIsNotContent()
    {
        var dat = ArenaTemplateDat.ParseText("#0005\r\nreal text&\r\n; trailing comment\r\n");

        var entry = Assert.Single(dat.Entries);
        Assert.Equal(["real text"], entry.Values);
    }

    [Fact]
    public void ParseText_EntriesAreSortedByKeyThenLetterThenCopy()
    {
        var dat = ArenaTemplateDat.ParseText(
            "#0009\r\nnine&\r\n#0002b\r\ntwo-b&\r\n#0002a\r\ntwo-a&\r\n#0001\r\none&\r\n");

        Assert.Equal([1, 2, 2, 9], dat.Entries.Select(e => e.Key));
        Assert.Equal(['\0', 'a', 'b', '\0'], dat.Entries.Select(e => e.Letter));
    }

    [Fact]
    public void ParseText_ValueWithNoAmpersand_YieldsNoValues()
    {
        // Values are ampersand-terminated; a body without one contributes only slack.
        var dat = ArenaTemplateDat.ParseText("#0003\r\nno terminator here\r\n");

        Assert.Empty(Assert.Single(dat.Entries).Values);
    }

    [Fact]
    public void Find_PrefersTheRequestedVariant_AndFallsBackToTheKey()
    {
        var dat = ArenaTemplateDat.ParseText("#0007a\r\nvariant a&\r\n#0008\r\nplain&\r\n");

        Assert.Equal(["variant a"], dat.Find(7, 'a')!.Values);
        Assert.Equal(["variant a"], dat.Find(7)!.Values);
        Assert.Equal(["plain"], dat.Find(8)!.Values);
        Assert.Null(dat.Find(999));
    }

    [Fact]
    public void Parse_ReadsLatin1Bytes()
    {
        var bytes = System.Text.Encoding.Latin1.GetBytes("#0010\r\nCafé sign&\r\n");

        Assert.Equal(["Café sign"], Assert.Single(ArenaTemplateDat.Parse(bytes).Entries).Values);
    }
}
