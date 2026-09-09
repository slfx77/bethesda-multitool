using BethesdaMultitool.Core.Formats.Battlespire;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Battlespire;

/// <summary>
///     Vectors for Battlespire's tab-keyed magical-item table, shaped after the 444 records in
///     <c>MG2_SPC.TXT</c> and <c>MG0_GEN.TXT</c> (measured 2026-09-06).
/// </summary>
public sealed class BattlespireItemTableTests
{
    private const string TwoRecords =
        "Name\tof the Typos Sophia\n" +
        "ID#\t1010\n" +
        "Item\t1\n" +
        "Spell\t-1, 16, -1, 4, 0\n" +
        "App\t100\n" +
        "Adv\t36, 37\n" +
        "Uses\t30\n" +
        "Level\t1\n" +
        "\n\t\n" +
        "Name\tof Lord Methats\n" +
        "ID#\t1011\n" +
        "Item\t0\n" +
        "Spell\t-1, -1, -1, -1, -1\n" +
        "App\t4, 15, 100\n" +
        "Adv\t-1\n" +
        "Uses\t10\n" +
        "Level\t0\n";

    [Fact]
    public void Parse_SplitsRecordsOnTheNameKey()
    {
        // ⚠ Records are delimited by the Name key, NOT by blank lines: retail spaces them with one
        // to three blank lines and one of those "blank" lines is a lone tab.
        var entries = BattlespireItemTable.Parse(TwoRecords);

        Assert.Equal(2, entries.Count);
        Assert.Equal("of the Typos Sophia", entries[0].Name);
        Assert.Equal("of Lord Methats", entries[1].Name);
    }

    [Fact]
    public void Parse_ReadsScalarAndCommaListValues()
    {
        var first = BattlespireItemTable.Parse(TwoRecords)[0];

        Assert.Equal(1010, first.Id);
        Assert.Equal("1", first.Fields["Item"]);
        Assert.Equal<string[]>(["-1", "16", "-1", "4", "0"], [.. first.List("Spell")]);
        Assert.Equal<string[]>(["36", "37"], [.. first.List("Adv")]);
    }

    [Fact]
    public void Parse_TreatsTheIdAsOptional()
    {
        // ⚠ 13 of the 457 retail records carry no ID#. Keying on it silently drops them.
        var entries = BattlespireItemTable.Parse("Name\tNameless\nItem\t2\n");

        Assert.Null(Assert.Single(entries).Id);
        Assert.Equal("Nameless", entries[0].Name);
    }

    [Fact]
    public void Parse_DropsKeysThatPrecedeTheFirstRecord()
    {
        var entries = BattlespireItemTable.Parse("Header\tignored\nName\tReal\nItem\t1\n");

        Assert.Equal("Real", Assert.Single(entries).Name);
        Assert.DoesNotContain("Header", entries[0].Fields.Keys);
    }

    [Fact]
    public void LooksLikeItemTable_RejectsADeveloperLogEvenThoughItIsTabKeyed()
    {
        // ⛔ THE gate. Nine TXT.BSA entries are dated dev logs whose keys are dates. They are
        // tab-keyed like a table, so gating on tabs alone would emit changelog lines as game data.
        const string devLog = "DATE\t8/21\n8/21\tfixed the thing\n8/22\tfixed another\n";

        Assert.False(BattlespireItemTable.LooksLikeItemTable(devLog));
        Assert.True(BattlespireItemTable.LooksLikeItemTable(TwoRecords));
    }

    [Fact]
    public void LooksLikeItemTable_RejectsPlainProse()
    {
        // 231 of the 253 retail .TXT entries are prose with no tabs at all.
        Assert.False(BattlespireItemTable.LooksLikeItemTable("The Battlespire is a proving ground."));
    }

    [Fact]
    public void List_ReturnsEmptyForAnAbsentKey()
    {
        Assert.Empty(BattlespireItemTable.Parse("Name\tX\n")[0].List("Spell"));
    }
}