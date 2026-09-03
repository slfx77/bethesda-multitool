using System;
using System.IO;
using System.Linq;
using BethesdaMultitool.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>TEXT.RSC parsing on synthetic tables: entries, aliases, variants and rejection.</summary>
public class DaggerfallTextFileTests
{
    [Fact]
    public void Parse_ReadsRecordsInTableOrder_WithIdsAndRenderedText()
    {
        var file = DaggerfallTextFile.Parse(DaggerfallTextFixture.TextRsc(
            (0, [.. "STRENGTH"u8, 0xFD, .. " Strength governs"u8, 0xFC]),
            (303, DaggerfallTextFixture.Bytes("Reputation balance must equal zero.")),
            (9999, [])));

        Assert.Equal(3, file.Records.Count);
        Assert.Equal([0, 303, 9999], file.Records.Select(r => r.Id));
        Assert.Equal("STRENGTH\nStrength governs", file.Records[0].Text);
        Assert.Equal("Reputation balance must equal zero.", file.FindById(303)!.Text);
        Assert.Equal(string.Empty, file.FindById(9999)!.Text);
        Assert.Null(file.FindById(4));

        // Raw excludes the terminator and starts where the table says.
        Assert.Equal(2 + 4 * 6, file.Records[0].Offset);
        Assert.Equal(27, file.Records[0].Raw.Length);
    }

    [Fact]
    public void Parse_AliasedIds_ShareOneRecord()
    {
        var file = DaggerfallTextFile.Parse(DaggerfallTextFixture.TextRsc(
            (1200, DaggerfallTextFixture.Bytes("shared")),
            (1201, null),
            (1202, null),
            (1203, DaggerfallTextFixture.Bytes("own"))));

        Assert.Equal(4, file.Records.Count);
        Assert.Equal("shared", file.FindById(1201)!.Text);
        Assert.Equal(file.FindById(1200)!.Offset, file.FindById(1202)!.Offset);
        Assert.Equal("own", file.FindById(1203)!.Text);
        Assert.Equal(2, file.Records.Select(r => r.Offset).Distinct().Count());
    }

    [Fact]
    public void Parse_SplitsSubrecordVariants()
    {
        var file = DaggerfallTextFile.Parse(DaggerfallTextFixture.TextRsc(
            (7, [.. "Greetings."u8, 0xFF, .. "Hail, %pcn."u8, 0xFF, .. "Well met."u8])));

        var record = file.Records[0];
        Assert.Equal(["Greetings.", "Hail, %pcn.", "Well met."], record.Subrecords);
        Assert.Equal("Greetings.", record.Text);
    }

    [Fact]
    public void Parse_RejectsMalformedTables()
    {
        // Header length not a multiple of six.
        Assert.Throws<InvalidDataException>(() => DaggerfallTextFile.Parse([0x07, 0x00, 0, 0, 0, 0, 0, 0, 0]));

        // Header longer than the file.
        Assert.Throws<InvalidDataException>(() => DaggerfallTextFile.Parse([0x0C, 0x00, 0, 0]));

        // An offset past the end of the file.
        var pastEnd = DaggerfallTextFixture.TextRsc((1, DaggerfallTextFixture.Bytes("x")));
        pastEnd[4] = 0xFF;
        pastEnd[5] = 0xFF;
        Assert.Throws<InvalidDataException>(() => DaggerfallTextFile.Parse(pastEnd));

        // A record with no end-of-record byte.
        var unterminated = DaggerfallTextFixture.TextRsc((1, DaggerfallTextFixture.Bytes("x")));
        unterminated[^1] = 0x20;
        Assert.Throws<InvalidDataException>(() => DaggerfallTextFile.Parse(unterminated));

        Assert.Throws<ArgumentNullException>(() => DaggerfallTextFile.Parse(null!));
    }
}
