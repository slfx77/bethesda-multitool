using BethesdaMultitool.CLI.Formatters;
using BethesdaMultitool.Core.Games;
using Spectre.Console;
using Xunit;
using static BethesdaMultitool.Tests.CLI.SemdiffTestRecords;

namespace BethesdaMultitool.Tests.CLI;

/// <summary>
///     Tests for the semdiff table renderer, <see cref="SemdiffFieldFormatter" />, captured through a
///     StringWriter-backed console. The console is wide so no cell wraps and a phrase can be matched
///     whole.
/// </summary>
public sealed class SemdiffDisplayTests
{
    private static readonly SemdiffTypes.SemdiffCompareOptions NewVegas = new()
    {
        GameA = BethesdaGame.FalloutNewVegas,
        GameB = BethesdaGame.FalloutNewVegas
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisplayRecordDiff_HeaderOnlyDifference_NeverPrintsRecordsAreIdentical(bool showAll)
    {
        var a = Record("ACHR", 0x000E739E, AchrPayload()) with
        {
            Flags = 0x00000400, VersionControl1 = 0x00195609, VersionControl2 = 3
        };
        var b = Record("ACHR", 0x000E739E, AchrPayload()) with
        {
            Flags = 0x00000C00, VersionControl1 = 0x0006060B, VersionControl2 = 4
        };
        var diff = Assert.Single(SemdiffComparer.Compare([a], [b], NewVegas with { ShowAll = showAll }).Records);

        var output = Render(console => SemdiffFieldFormatter.DisplayRecordDiff(console, diff, "File A", "File B",
            showAll));

        Assert.Contains("0x00000400 (Persistent)", output);
        Assert.Contains("0x00000C00 (Persistent, Initially Disabled)", output);
        Assert.Contains("+ bit 11 (0x00000800) Initially Disabled", output);
        Assert.Contains("Subrecords identical; record header differs", output);
        Assert.DoesNotContain("Records are identical", output);
        if (showAll)
        {
            Assert.Contains("IGNORED (version control)", output);
            Assert.Contains("0x0006060B", output);
        }
        else
        {
            Assert.DoesNotContain("IGNORED", output);
            Assert.DoesNotContain("0x0006060B", output);
        }
    }

    [Fact]
    public void DisplayRecordDiff_SignatureMismatch_ShowsBothSignaturesAndEditorIds()
    {
        var diff = Assert.Single(
            SemdiffComparer.Compare([OwbPrototypeQuest()], [OwbRetailReference()], NewVegas).Records);
        diff = diff with
        {
            EditorIdHint = SemdiffComparer.DescribeEditorIdLookup("File B", "QUST", "NVDLC03X13VR", [])
        };

        var output = Render(console => SemdiffFieldFormatter.DisplayRecordDiff(console, diff));

        Assert.Contains("0x01011E59", output);
        Assert.Contains("QUST NVDLC03X13VR", output);
        Assert.Contains("REFR (no EDID)", output);
        Assert.Contains("typed field comparison refused", output);
        Assert.Contains("EDID (13 B), DATA (8 B)", output);
        Assert.Contains("NAME (4 B), DATA (24 B), XSCL (4 B)", output);
        Assert.Contains("File B has no QUST with EditorID NVDLC03X13VR", output);
        Assert.Contains("Use --match editorid or --map A=B", output);

        // Nothing of B is decoded under A's schema: no QUST field names, no REFR coordinates.
        Assert.DoesNotContain("QuestDelay", output);
        Assert.DoesNotContain("-1309.1", output);
        Assert.DoesNotContain("Records are identical", output);
    }

    [Fact]
    public void DisplayResult_ShowAll_IdenticalRecord_IsNotCountedAsADifference()
    {
        var a = Record("ACHR", 0x000E739E, AchrPayload());
        var result = SemdiffComparer.Compare([a], [a], NewVegas with { ShowAll = true });

        var output = Render(console =>
            SemdiffFieldFormatter.DisplayResult(console, result, "File A", "File B", 10, true));

        Assert.Contains("Showing 1 record(s) matching the filter; 0 with differences", output);
        Assert.Contains("Records are identical (header and subrecords)", output);
        Assert.DoesNotContain("Found 1 record(s) with differences", output);
    }

    [Fact]
    public void DisplayResult_VersionControlOnly_IsNotADifferenceButIsMentioned()
    {
        var a = Record("ACHR", 0x000E739E, AchrPayload()) with { VersionControl1 = 0x00195609, VersionControl2 = 3 };
        var b = a with { VersionControl1 = 0x0006060B, VersionControl2 = 4 };

        var hidden = Render(console => SemdiffFieldFormatter.DisplayResult(console,
            SemdiffComparer.Compare([a], [b], NewVegas), "File A", "File B", 10, false));
        Assert.Contains("No differences found.", hidden);
        Assert.Contains("1 more record(s) differ only in version-control bookkeeping", hidden);

        var shown = Render(console => SemdiffFieldFormatter.DisplayResult(console,
            SemdiffComparer.Compare([a], [b], NewVegas with { ShowAll = true }), "File A", "File B", 10, true));
        Assert.Contains("Showing 1 record(s) matching the filter; 0 with differences", shown);
        Assert.Contains("only version-control bookkeeping differs: VCI1 0x00195609 -> 0x0006060B, VCI2 3 -> 4",
            shown);
        Assert.DoesNotContain("Records are identical", shown);
    }

    [Fact]
    public void DisplayResult_FormVersionOnly_IsNotADifferenceButIsCountedInItsOwnLine()
    {
        var a = Record("DIAL", 0x00001234, Edid("GREETING")) with { FormVersion = 14 };
        var b = Record("DIAL", 0x00001234, Edid("GREETING")) with { FormVersion = 15 };

        var hidden = Render(console => SemdiffFieldFormatter.DisplayResult(console,
            SemdiffComparer.Compare([a], [b], NewVegas), "File A", "File B", 10, false));
        Assert.Contains("No differences found.", hidden);
        Assert.Contains("1 record(s) differ only in form version (--all lists them)", hidden);
        Assert.DoesNotContain("with differences", hidden);

        var shown = Render(console => SemdiffFieldFormatter.DisplayResult(console,
            SemdiffComparer.Compare([a], [b], NewVegas with { ShowAll = true }), "File A", "File B", 10, true));
        Assert.Contains("Showing 1 record(s) matching the filter; 0 with differences", shown);
        Assert.Contains("only the form version differs: 14 -> 15", shown);
        Assert.Contains("Form Version", shown);
        Assert.DoesNotContain("differ only in form version (--all lists them)", shown);
        Assert.DoesNotContain("Records are identical", shown);
        Assert.DoesNotContain("record header differs", shown);
    }

    [Fact]
    public void DisplayRecordDiff_FormVersionAndSubrecordChange_KeepsTheFormVersionRow()
    {
        var a = Record("ALCH", 0x00002000, Sub("DATA", U32(1))) with { FormVersion = 14 };
        var b = Record("ALCH", 0x00002000, Sub("DATA", U32(2))) with { FormVersion = 15 };
        var diff = Assert.Single(SemdiffComparer.Compare([a], [b], NewVegas).Records);

        var output = Render(console => SemdiffFieldFormatter.DisplayRecordDiff(console, diff));

        Assert.Equal(SemdiffTypes.DiffType.Different, diff.DiffType);
        Assert.Contains("Form Version", output);
        Assert.Contains("Record header", output);
        Assert.DoesNotContain("IGNORED", output);
    }

    [Fact]
    public void DisplayRecordDiff_OrderOnlyDifference_PrintsTheOrderNoteNotIdentical()
    {
        var a = Record("CELL", 0x000845F4, Edid("Cell"), Sub("XCLR", U32(0x00012345)), Sub("XCAS", U32(0x00054321)));
        var b = Record("CELL", 0x000845F4, Edid("Cell"), Sub("XCAS", U32(0x00054321)), Sub("XCLR", U32(0x00012345)));
        var result = SemdiffComparer.Compare([a], [b], NewVegas);

        var output = Render(console =>
            SemdiffFieldFormatter.DisplayResult(console, result, "File A", "File B", 10, false));

        Assert.Contains("Found 1 record(s) with differences", output);
        Assert.Contains("Same subrecords per signature, in a different order", output);
        Assert.Contains("where File A has XCLR and File B has XCAS", output);
        Assert.DoesNotContain("Records are identical", output);
        Assert.DoesNotContain("Subrecords identical", output);
    }

    [Fact]
    public void DisplayResult_RepeatedWarnings_AreSummarizedPerFile()
    {
        var records = Enumerable.Range(0, 5)
            .SelectMany(i => new[]
            {
                Record("INFO", 0x00100000u + (uint)i) with { Offset = i * 100 },
                Record("INFO", 0x00100000u + (uint)i) with { Offset = i * 100 + 50 }
            })
            .ToList();
        // The command hands the comparer the same labels it renders with; the warning text comes from them.
        var result = SemdiffComparer.Compare(records, [], NewVegas with { LabelA = "July", LabelB = "Retail" });

        var output = Render(console => SemdiffFieldFormatter.DisplayResult(console, result, "July", "Retail", 1, false));

        Assert.Equal(5, result.Warnings.Count);
        Assert.Contains("FormID 0x00100000 occurs 2 times in July", output);
        Assert.DoesNotContain("FormID 0x00100004 occurs", output);
        Assert.Contains("... and 2 more duplicate-formid warning(s) for July", output);
    }

    [Fact]
    public void DisplayRecordDiff_EditorIdWithMarkupCharacters_IsEscaped()
    {
        var a = Record("MISC", 0x00001234, Edid("Odd[red]Name"), Sub("DATA", U32(1)));
        var b = Record("MISC", 0x00001234, Edid("Odd[red]Name"), Sub("DATA", U32(2)));
        var diff = Assert.Single(SemdiffComparer.Compare([a], [b], NewVegas).Records);

        var output = Render(console => SemdiffFieldFormatter.DisplayRecordDiff(console, diff, "A [x]", "B"));

        Assert.Contains("Odd[red]Name", output);
        Assert.Contains("A [x]", output);
    }

    private static string Render(Action<IAnsiConsole> render)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No
        });
        console.Profile.Width = 240;
        render(console);
        return writer.ToString();
    }
}
