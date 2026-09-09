using BethesdaMultitool.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>BOKnnnnn.TXT parsing on synthetic books: header, pages, clipping and naming.</summary>
public class DaggerfallBookFileTests
{
    [Fact]
    public void Parse_ReadsHeaderAndPages()
    {
        var bytes = DaggerfallTextFixture.Book("The First Scroll of Baan Dar", "Arkan", "", 1500, 3,
            DaggerfallTextFixture.Page("Page one."),
            [0x00, 0xF9, 0x02, 0xFD, .. "Page two"u8, 0x00, .. " continues."u8, 0xF6]);

        var book = DaggerfallBookFile.Parse(bytes, "bok00000.txt");

        Assert.Equal("BOK00000.TXT", book.Name);
        Assert.Equal(0, book.Number);
        Assert.Equal("The First Scroll of Baan Dar", book.Title);
        Assert.Equal("Arkan", book.Author);
        Assert.False(book.IsNaughty);
        Assert.Equal(1500u, book.Price);
        Assert.Equal(3, book.Unknown1);
        Assert.Equal(1234, book.Unknown2);
        Assert.Equal(2345, book.Unknown3);
        Assert.Equal(2, book.Pages.Count);
        Assert.Equal(0, book.UnterminatedPageCount);

        Assert.Equal("Page one.", book.PageTexts[0]);
        Assert.Equal("Page two\ncontinues.", book.PageTexts[1]);

        // Raw pages exclude the end-of-page byte.
        Assert.Equal(9, book.Pages[0].Length);
    }

    [Fact]
    public void Parse_NaughtyFlag_IsComparedTrimmed()
    {
        // BOK00088's flag reads "naughty " with a trailing space; BOK10000's carries a stray name.
        var naughty =
            DaggerfallBookFile.Parse(
                DaggerfallTextFixture.Book("A", "B", "naughty ", 1, 1, DaggerfallTextFixture.Page("x")),
                "BOK00088.TXT");
        var stray = DaggerfallBookFile.Parse(
            DaggerfallTextFixture.Book("A", "B", "Arkay", 1, 1, DaggerfallTextFixture.Page("x")), "BOK10000.TXT");

        Assert.True(naughty.IsNaughty);
        Assert.False(stray.IsNaughty);
        Assert.Equal(10000, stray.Number);
    }

    [Fact]
    public void Parse_PageWithoutEndMarker_IsClippedAtTheNextOffset_OrTheFileEnd()
    {
        var bytes = DaggerfallTextFixture.Book("T", "A", "", 1, 1,
            DaggerfallTextFixture.Bytes("first page has no marker"),
            DaggerfallTextFixture.Page("second page"),
            DaggerfallTextFixture.Bytes("last page runs to the end"));

        var book = DaggerfallBookFile.Parse(bytes, "BOK00031.TXT");

        Assert.Equal(["first page has no marker", "second page", "last page runs to the end"], book.PageTexts);
        Assert.Equal(2, book.UnterminatedPageCount);
    }

    [Theory]
    [InlineData("BOK00000.TXT", true)]
    [InlineData("bok10000.txt", true)]
    [InlineData("BOK0001.TXT", false)]
    [InlineData("BOK00001.TX", false)]
    [InlineData("BOKA0001.TXT", false)]
    [InlineData("MAPS.BSA", false)]
    [InlineData("", false)]
    public void IsBookFileName_RequiresBokFiveDigitsTxt(string name, bool expected)
    {
        Assert.Equal(expected, DaggerfallBookFile.IsBookFileName(name));
    }

    [Fact]
    public void BookNumber_ParsesTheDigits_AndRejectsOtherNames()
    {
        Assert.Equal(12, DaggerfallBookFile.BookNumber("BOK00012.TXT"));
        Assert.Equal(10000, DaggerfallBookFile.BookNumber("bok10000.txt"));
        Assert.Throws<ArgumentException>(() => DaggerfallBookFile.BookNumber("TEXT.RSC"));
    }

    [Fact]
    public void Parse_RejectsShortHeaders_AndOffsetsInsideTheTable()
    {
        Assert.Throws<InvalidDataException>(() => DaggerfallBookFile.Parse(new byte[100], "BOK00001.TXT"));

        // Page count says one page, but the file ends before the offset table.
        var truncated = DaggerfallTextFixture.Book("T", "A", "", 1, 1, DaggerfallTextFixture.Page("x"));
        Assert.Throws<InvalidDataException>(() => DaggerfallBookFile.Parse(truncated[..236], "BOK00001.TXT"));

        // An offset pointing back into the header.
        var inside = DaggerfallTextFixture.Book("T", "A", "", 1, 1, DaggerfallTextFixture.Page("x"));
        inside[236] = 0x10;
        inside[237] = 0x00;
        Assert.Throws<InvalidDataException>(() => DaggerfallBookFile.Parse(inside, "BOK00001.TXT"));
    }
}