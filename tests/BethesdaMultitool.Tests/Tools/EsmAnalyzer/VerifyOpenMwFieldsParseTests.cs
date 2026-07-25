using EsmAnalyzer.Commands;
using Xunit;

namespace BethesdaMultitool.Tests.Tools.EsmAnalyzer;

/// <summary>
///     Pins the text adapter that parses OpenMW <c>esmtool</c>'s dump into per-record field bags
///     (<see cref="VerifyOpenMwFields.ParseRecords" />). The end-to-end harness needs esmtool + a real
///     ESM (not available in CI), so this fixture-based test is the CI-runnable guard against format
///     drift: it exercises the banner skip, the value-less group headers (Attributes/Weather), 4-space
///     nested fields, multi-line quoted values, and the empty-name exterior CELL header.
/// </summary>
public class VerifyOpenMwFieldsParseTests
{
    private static readonly string[] SampleDumpLines =
    [
        "Using default (English) font encoding.",
        "Loading TES3 file: \"x.esm\"",
        "Author: Bethesda Softworks",
        "",
        "Record: GMST \"sMessage5\"",
        "Record flags: [None] (0x00000000)",
        "  variant string: \"Line one ", // trailing space + unclosed quote → continues
        "Line two.\"",
        "",
        "Record: LTEX \"sand\"",
        "Record flags: [None] (0x00000000)",
        "  Id: \"sand\"",
        "  Index: 0",
        "  Texture: Tx_sand_01.tga",
        "",
        "Record: CELL ", // exterior cell: empty name in the header
        "Record flags: [None] (0x00000000)",
        "  Region: \"Bitter Coast Region\"",
        "  Flags: HasWater (0x00000002)",
        "  Coordinates:  (23,7)",
        "",
        "Record: NPC_ \"todd\"",
        "Record flags: [None] (0x00000000)",
        "  Race: \"Dark Elf\"",
        "  Level: 1",
        "  Attributes:",
        "    Strength: 40",
        "    Luck: 33",
        "",
        "Record: REGN \"Bitter Coast Region\"",
        "Record flags: [None] (0x00000000)",
        "  Name: Bitter Coast Region",
        "  Weather:",
        "    Clear: 10",
        "    Blizzard: 1",
        "  Map Color: 16721698",
        "  Sleep List: \"ex_bittercoast_sleep\""
    ];

    private static readonly string[] ExpectedRecordTypes = ["GMST", "LTEX", "CELL", "NPC_", "REGN"];

    private static string SampleDump()
    {
        return string.Join("\n", SampleDumpLines);
    }

    [Fact]
    public void ParseRecords_ReadsAllRecordTypes_AndSkipsBanner()
    {
        var records = VerifyOpenMwFields.ParseRecords(SampleDump());

        Assert.Equal(5, records.Count);
        Assert.Equal(ExpectedRecordTypes, records.Select(r => r.Type));
    }

    [Fact]
    public void ParseRecords_ConcatenatesMultiLineQuotedValue()
    {
        var gmst = VerifyOpenMwFields.ParseRecords(SampleDump()).Single(r => r.Type == "GMST");

        Assert.Equal("sMessage5", gmst.Name);
        // The trailing space on line one is trimmed; the two physical lines join with a newline.
        Assert.Equal("\"Line one\nLine two.\"", gmst.Fields["variant string"]);
    }

    [Fact]
    public void ParseRecords_FlattensNestedGroupsWithDottedKeys()
    {
        var records = VerifyOpenMwFields.ParseRecords(SampleDump());

        var npc = records.Single(r => r.Type == "NPC_");
        Assert.Equal("todd", npc.Name);
        Assert.Equal("Dark Elf", Unquote(npc.Fields["Race"]));
        Assert.Equal("1", npc.Fields["Level"]);
        Assert.Equal("40", npc.Fields["Attributes.Strength"]);
        Assert.Equal("33", npc.Fields["Attributes.Luck"]);

        var regn = records.Single(r => r.Type == "REGN");
        Assert.Equal("10", regn.Fields["Weather.Clear"]);
        Assert.Equal("1", regn.Fields["Weather.Blizzard"]);
        Assert.Equal("16721698", regn.Fields["Map Color"]);
        Assert.Equal("ex_bittercoast_sleep", Unquote(regn.Fields["Sleep List"]));
    }

    [Fact]
    public void ParseRecords_ExteriorCellHasEmptyNameAndCoords()
    {
        var cell = VerifyOpenMwFields.ParseRecords(SampleDump()).Single(r => r.Type == "CELL");

        Assert.Equal("", cell.Name);
        Assert.Equal("Bitter Coast Region", Unquote(cell.Fields["Region"]));
        Assert.Equal("HasWater (0x00000002)", cell.Fields["Flags"]);
        Assert.Equal("(23,7)", cell.Fields["Coordinates"]);
    }

    [Fact]
    public void ParseRecords_LtexExposesIndexAndTexture()
    {
        var ltex = VerifyOpenMwFields.ParseRecords(SampleDump()).Single(r => r.Type == "LTEX");

        Assert.Equal("sand", ltex.Name);
        Assert.Equal("0", ltex.Fields["Index"]);
        Assert.Equal("Tx_sand_01.tga", ltex.Fields["Texture"]);
    }

    private static string Unquote(string s)
    {
        return s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;
    }
}