using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Export.Csv;
using BethesdaMultitool.Core.Formats.Esm.Export.Geck;
using BethesdaMultitool.Core.Formats.Esm.Export.Report;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Export;

/// <summary>
///     terminal_report.txt and terminals.csv for TERM menu items. Before this, the report printed only each
///     item's ITXT ("  - Yes") and the CSV wrote the always-empty legacy ResultScript FormID under the
///     MenuItemResultText header, so a terminal's RNAM text, ANAM flags, sub-menu links, conditions and embedded
///     result script were invisible.
///     <para>
///         The terminals are synthetic models shaped like the retail Dead Money pair the audit named: the
///         NVDLC01VaultMainInfoDownloadTerminal (0x0100DAD2) item that opens NVDLC01VaultMainInfoDownload01Terminal
///         (0x01011D70), whose "Yes" item carries a GetScriptVariable condition and a three-reference script.
///         Every expected line is written out literally: condition text is the shared describer's
///         (<c>EDID [0xFORMID]</c> operands, Run On always stated), flag names are xEdit's FNV/FO3 TERM names.
///     </para>
/// </summary>
public sealed class TerminalMenuReportTests
{
    private const uint ParentFormId = 0x0100DAD2;
    private const uint ChildFormId = 0x01011D70;
    private const uint SiblingFormId = 0x01011D6F;
    private const uint TerminalRefFormId = 0x0100AAEB;
    private const uint HoloNoteFormId = 0x0100DACD;
    private const uint CodeBoxFormId = 0x0100AE1E;

    private const string YesSource =
        "AddNote NVDLC01VaultTechHoloNote;\r\n" +
        "Set VaultMainTerminalREF.bGotHologram to 1;\r\n" +
        "Set VaultMainTerminalREF.iDownloaded to (VaultMainTerminalREF.iDownloaded + 1);\r\n" +
        "If VaultMainTerminalREF.iDownloaded == 3\r\n" +
        "  Set VaultCodeBox.bTreasureFound to 1;\r\n" +
        "  ForceTerminalBack;\r\n" +
        "Endif\r\n" +
        "ForceTerminalBack;";

    private const string YesDecompiled =
        "AddNote NVDLC01VaultTechHoloNote\r\n" +
        "set VaultMainTerminalREF.bGotHologram to 1\r\n" +
        "; synthetic decompiled tail Q7\r\n" +
        "ForceTerminalBack";

    private const string GetScriptVariable4 =
        "GetScriptVariable(VaultMainTerminalREF [0x0100AAEB], var 4) != 1 [Run On: Subject]";

    private const string DecompiledLabel =
        "Reconstruction (SCDA)";

    private const string AbsenceWording =
        "not present in this capture; absence from a partial memory dump is not evidence of absence from the build";

    private static readonly string[] LegacyCsvHeader =
    [
        "RowType", "FormID", "EditorID", "Name", "Difficulty", "DifficultyName", "HeaderText", "Endianness",
        "Offset", "MenuItemText", "MenuItemResultText", "MenuItemSubTerminalFormID"
    ];

    private static readonly string[] AppendedCsvHeader =
    [
        "MenuItemIndex", "MenuItemFlags", "MenuItemFlagNames", "MenuItemDisplayNoteFormID",
        "MenuItemSubTerminalEditorID", "MenuItemConditionCount", "MenuItemConditions", "MenuItemScriptSourceKind",
        "MenuItemScriptSource", "MenuItemScriptDecompiled", "MenuItemScriptReferences", "MenuItemScriptIncomplete"
    ];

    private static readonly Dictionary<uint, string> EditorIds = new()
    {
        [ParentFormId] = "NVDLC01VaultMainInfoDownloadTerminal",
        [ChildFormId] = "NVDLC01VaultMainInfoDownload01Terminal",
        [SiblingFormId] = "NVDLC01VaultMainInfoDownload02Terminal",
        [TerminalRefFormId] = "VaultMainTerminalREF",
        [HoloNoteFormId] = "NVDLC01VaultTechHoloNote",
        [CodeBoxFormId] = "VaultCodeBox"
    };

    private static readonly FormIdResolver Resolver = new(EditorIds, []);

    [Fact]
    public void TerminalReport_PrintsEveryMenuItemFieldWithStableIndex()
    {
        var report = GeckTextContentWriter.GenerateTerminalsReport(
            [Parent(), Child()], Resolver, FnvContext());

        var child = Lines(RecordBlock(report, ChildFormId));

        // Legacy lines keep their relative order.
        AssertInOrder(child,
            "FormID:         0x01011D70",
            "Editor ID:      NVDLC01VaultMainInfoDownload01Terminal",
            "Display Name:   Vault Control Terminal",
            "Difficulty:     Very Easy",
            "Endianness:     Little-Endian (PC)",
            "Offset:         0x00000000",
            "Header:",
            "  Sierra Madre Control Network",
            "  Download Hologram Technology Module?",
            "Menu Items (2):",
            "  - Yes",
            "  - No");

        // Added record-level lines, including the incoming sub-menu edge.
        AssertInOrder(child,
            "Offset:         0x00000000",
            "Flags:          Unlocked (0x0002)",
            "Server Type:    8 (-Server 9-)",
            "Linked From (1):",
            "  NVDLC01VaultMainInfoDownloadTerminal [0x0100DAD2] item [1] \"Retrieve Hologram Technology Data\"",
            "Header:");

        // One full block per menu item, numbered from 1 in record order.
        AssertInOrder(child,
            "  - No",
            "Menu Item [1] Yes",
            "  Result Text:    Downloading module...",
            "  Flags:          Add Note, Force Redraw (0x0003)",
            "  Conditions (1):",
            $"    1: {GetScriptVariable4}",
            "  Result Script:  92 bytes of compiled code (SCDA), 3 references",
            "    Provenance:     plugin-record",
            "    Bytecode Order: Little-Endian",
            "    Source (SCTX):");
        AssertFollowedBy(child, "    Source (SCTX):", IndentedLines(YesSource, 6));
        AssertFollowedBy(child, $"    {DecompiledLabel}:", IndentedLines(YesDecompiled, 6));
        AssertInOrder(child,
            $"    {DecompiledLabel}:",
            "    References (3):",
            "      1: NVDLC01VaultTechHoloNote (0x0100DACD)",
            "      2: VaultMainTerminalREF (0x0100AAEB)",
            "      3: VaultCodeBox (0x0100AE1E)",
            "Menu Item [2] No",
            "  Result Text:    Canceling request...",
            "  Flags:          Add Note, Force Redraw (0x0003)",
            "  Display Note:   NVDLC01VaultTechHoloNote [0x0100DACD]",
            "  Result Script:  4 bytes of compiled code (SCDA)",
            "    Provenance:     plugin-record");

        // A source ending in CRLF adds no blank line: the decompiled heading follows the last source line.
        AssertFollowedBy(child, "    Source (SCTX):",
            ["      AddNote NVDLC01VaultTechHoloNote;"]);
        var noSource = child.LastIndexOf("      ForceTerminalBack;");
        Assert.Equal($"    {DecompiledLabel}:", child[noSource + 1]);

        var parent = Lines(RecordBlock(report, ParentFormId));
        AssertInOrder(parent,
            "Menu Item [1] Retrieve Hologram Technology Data",
            "  Result Text:    Granting database access...",
            "  Flags:          Add Note, Force Redraw (0x0003)",
            "  Sub-menu:       NVDLC01VaultMainInfoDownload01Terminal [0x01011D70]",
            "  Conditions (1):",
            $"    1: {GetScriptVariable4}",
            "  Result Script:  no compiled code (no SCDA or source text in this menu item)",
            "Menu Item [2] Retrieve Vending Replicator Schematics",
            "  Sub-menu:       NVDLC01VaultMainInfoDownload02Terminal [0x01011D6F]",
            "    1: GetScriptVariable(VaultMainTerminalREF [0x0100AAEB], var 2) != 1 [Run On: Subject]");

        // An empty script block is one summary line, never a provenance claim; nothing links into the parent.
        Assert.DoesNotContain(parent, line => line.Contains("Provenance:", StringComparison.Ordinal));
        Assert.DoesNotContain(parent, line => line.StartsWith("Linked From", StringComparison.Ordinal));
        Assert.DoesNotContain("function names assume", report, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalsCsv_MenuItemResultTextIsRnamNotLegacyScriptFormId()
    {
        var rows = ParseCsv(CsvSupplementalWriter.GenerateTerminalsCsv([Parent(), Child()], Resolver, FnvContext()));

        var yes = MenuItemRows(rows, ChildFormId)[0];
        Assert.Equal("MenuItemResultText", rows[0][10]);
        Assert.Equal("Yes", yes[9]);
        Assert.Equal("Downloading module...", yes[10]);
        Assert.DoesNotContain("0x00ABCDEF", yes);
        Assert.Equal("Canceling request...", MenuItemRows(rows, ChildFormId)[1][10]);
    }

    [Fact]
    public void TerminalsCsv_AppendsIndexFlagsConditionsAndFullScriptColumns_LegacyColumnsUnmoved()
    {
        var rows = ParseCsv(CsvSupplementalWriter.GenerateTerminalsCsv([Parent(), Child()], Resolver, FnvContext()));

        Assert.Equal([.. LegacyCsvHeader, .. AppendedCsvHeader], rows[0]);
        Assert.All(rows, row => Assert.Equal(24, row.Length));

        var terminal = Assert.Single(rows, row => row[0] == "TERMINAL" && row[1] == "0x01011D70");
        Assert.Equal("NVDLC01VaultMainInfoDownload01Terminal", terminal[2]);
        Assert.Equal("Very Easy", terminal[5]);
        Assert.All(terminal[12..], cell => Assert.Equal("", cell));

        var childItems = MenuItemRows(rows, ChildFormId);
        Assert.Equal(2, childItems.Count);
        var yes = childItems[0];
        Assert.Equal("1", yes[12]);
        Assert.Equal("0x03", yes[13]);
        Assert.Equal("Add Note, Force Redraw", yes[14]);
        Assert.Equal("", yes[15]);
        Assert.Equal("", yes[16]);
        Assert.Equal("1", yes[17]);
        Assert.Equal(GetScriptVariable4, yes[18]);
        Assert.Equal("plugin-record", yes[19]);
        Assert.Equal(YesSource, yes[20]);
        Assert.Equal(YesDecompiled, yes[21]);
        Assert.Equal(
            "NVDLC01VaultTechHoloNote (0x0100DACD); VaultMainTerminalREF (0x0100AAEB); VaultCodeBox (0x0100AE1E)",
            yes[22]);
        Assert.Equal("No", yes[23]);

        var no = childItems[1];
        Assert.Equal("2", no[12]);
        Assert.Equal("0x0100DACD", no[15]);
        Assert.Equal("0", no[17]);
        Assert.Equal("ForceTerminalBack;\r\n", no[20]);

        var parentItems = MenuItemRows(rows, ParentFormId);
        Assert.Equal("0x01011D70", parentItems[0][11]);
        Assert.Equal("NVDLC01VaultMainInfoDownload01Terminal", parentItems[0][16]);
        Assert.Equal("0x01011D6F", parentItems[1][11]);
        Assert.Equal("NVDLC01VaultMainInfoDownload02Terminal", parentItems[1][16]);
        Assert.Equal("none", parentItems[0][19]);
    }

    [Fact]
    public void TerminalsCsv_MultipleConditionsAreJoinedByLineBreaks()
    {
        var terminal = new TerminalRecord
        {
            FormId = ChildFormId,
            EditorId = "NVDLC01VaultMainInfoDownload01Terminal",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "Yes",
                    Conditions = [ScriptVariableCondition(4, isOr: true), ScriptVariableCondition(2)]
                }
            ]
        };

        var rows = ParseCsv(CsvSupplementalWriter.GenerateTerminalsCsv([terminal], Resolver, FnvContext()));

        var item = Assert.Single(MenuItemRows(rows, ChildFormId));
        Assert.Equal("2", item[17]);
        Assert.Equal(
            "GetScriptVariable(VaultMainTerminalREF [0x0100AAEB], var 4) != 1 [Run On: Subject] OR\n" +
            "GetScriptVariable(VaultMainTerminalREF [0x0100AAEB], var 2) != 1 [Run On: Subject]",
            item[18]);
    }

    [Fact]
    public void TerminalReport_OrConnectorsAndGroupingAreReportedAsGeckConvention()
    {
        var terminal = new TerminalRecord
        {
            FormId = ChildFormId,
            EditorId = "NVDLC01VaultMainInfoDownload01Terminal",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "Yes",
                    Conditions = [ScriptVariableCondition(4, isOr: true), ScriptVariableCondition(2)]
                }
            ]
        };

        var lines = Lines(GeckTextContentWriter.GenerateTerminalsReport([terminal], Resolver, FnvContext()));

        AssertInOrder(lines,
            "  Conditions (2):",
            "    1: GetScriptVariable(VaultMainTerminalREF [0x0100AAEB], var 4) != 1 [Run On: Subject] OR",
            "    2: GetScriptVariable(VaultMainTerminalREF [0x0100AAEB], var 2) != 1 [Run On: Subject]",
            "  Grouping:       1 OR 2 (GECK convention: conditions joined by the OR flag form a group, and " +
            "groups are joined by AND; not verified against the engine)");
    }

    [Theory]
    [InlineData(ScriptSourceTextOrigin.DecompiledFromBytecode,
        "Reconstruction (SCDA):",
        "decompiled-from-bytecode")]
    [InlineData(ScriptSourceTextOrigin.DmpFragment,
        "Recovered source (dump fragment):",
        "dmp-fragment (correspondence: not-recorded)")]
    [InlineData(ScriptSourceTextOrigin.RuntimeSameObject,
        "Recovered source (runtime object):",
        "runtime-same-object (correspondence: not-recorded)")]
    [InlineData(ScriptSourceTextOrigin.None,
        "Recovered source (unattributed):",
        "unattributed-same-dump (correspondence: not-recorded)")]
    public void TerminalReport_LabelsDmpRecoveredAndDecompiledSourceProvenance(
        ScriptSourceTextOrigin origin,
        string expectedHeading,
        string expectedProvenance)
    {
        var terminal = new TerminalRecord
        {
            FormId = ChildFormId,
            EditorId = "NVDLC01VaultMainInfoDownload01Terminal",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "No",
                    CompiledData = [0x23, 0x12, 0x00, 0x00],
                    SourceText = "ForceTerminalBack",
                    SourceTextOrigin = origin,
                    DecompiledText = "ForceTerminalBack",
                    IsDmpDerived = true
                }
            ]
        };

        var lines = Lines(GeckTextContentWriter.GenerateTerminalsReport([terminal], Resolver, FnvContext()));

        AssertInOrder(lines,
            "  Result Script:  4 bytes of compiled code (SCDA)",
            $"    Provenance:     {expectedProvenance}",
            $"    {expectedHeading}",
            "      ForceTerminalBack",
            $"    {DecompiledLabel}:",
            "      ForceTerminalBack");
        Assert.DoesNotContain(lines, line => line.Contains("authored", StringComparison.Ordinal));
    }

    [Fact]
    public void TerminalReport_DumpAbsenceAndIncompleteBundlesNeverClaimTheBuildLackedTheScript()
    {
        var terminal = new TerminalRecord
        {
            FormId = ChildFormId,
            EditorId = "NVDLC01VaultMainInfoDownload01Terminal",
            MenuItems =
            [
                new TerminalMenuItem { Text = "Runtime item with no script" },
                new TerminalMenuItem
                {
                    Text = "Partial bundle",
                    SourceText = "ForceTerminalBack",
                    SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
                    IsDmpDerived = true,
                    IsIncompleteExecutableBundle = true
                }
            ]
        };

        var lines = Lines(GeckTextContentWriter.GenerateTerminalsReport(
            [terminal], Resolver, FnvContext(), isMemoryDumpInput: true));

        AssertInOrder(lines,
            "Menu Item [1] Runtime item with no script",
            $"  Result Script:  no compiled code recovered: {AbsenceWording}",
            "Menu Item [2] Partial bundle",
            "  Result Script:  no compiled code (the menu item holds source text but no SCDA)",
            "    Provenance:     dmp-fragment (correspondence: not-recorded)",
            "    Bundle:         incomplete or unsafe in this capture: the SCHR/SCDA/local/reference bundle is " +
            "structurally inconsistent or failed emission safety validation; this is not a validated, runnable script");
    }

    [Fact]
    public void TerminalReport_WritesLongSourceInFull()
    {
        var longSource = "; " + new string('x', 5_000) + " END-OF-SOURCE-MARKER";
        var terminal = new TerminalRecord
        {
            FormId = ChildFormId,
            EditorId = "NVDLC01VaultMainInfoDownload01Terminal",
            MenuItems = [new TerminalMenuItem { Text = "Yes", CompiledData = [0x1D, 0x00, 0x00, 0x00], SourceText = longSource }]
        };

        var report = GeckTextContentWriter.GenerateTerminalsReport([terminal], Resolver, FnvContext());
        var rows = ParseCsv(CsvSupplementalWriter.GenerateTerminalsCsv([terminal], Resolver, FnvContext()));

        Assert.Contains("      " + longSource, Lines(report));
        Assert.Equal(longSource, Assert.Single(MenuItemRows(rows, ChildFormId))[20]);
    }

    [Fact]
    public void TerminalReport_WithoutAContext_SaysTheGameWasAssumed()
    {
        var report = GeckTextContentWriter.GenerateTerminalsReport([Parent()], Resolver);

        Assert.Contains(
            "Conditions:     function names assume FalloutNewVegas (not detected from the input)",
            Lines(report));
        Assert.Contains($"    1: {GetScriptVariable4}", Lines(report));
    }

    [Fact]
    public void DifficultyName_FiveIsRequiresKey()
    {
        var terminal = new TerminalRecord { FormId = ChildFormId, EditorId = "NVDLC01BunkerTerminal02", Difficulty = 5 };

        Assert.Equal("Requires Key", terminal.DifficultyName);
        Assert.Contains("Difficulty:     Requires Key",
            Lines(GeckTextContentWriter.GenerateTerminalsReport([terminal], Resolver, FnvContext())));
        var row = Assert.Single(ParseCsv(CsvSupplementalWriter.GenerateTerminalsCsv([terminal], Resolver)),
            candidate => candidate[0] == "TERMINAL");
        Assert.Equal("5", row[4]);
        Assert.Equal("Requires Key", row[5]);
    }

    [Fact]
    public void FlagRegistry_TerminalRecordAndMenuItemFlagsMatchXEditSchema()
    {
        Assert.Equal("Hide Welcome Text when displaying Image (0x0008)",
            FlagRegistry.DecodeFlagNamesWithHex(0x08, FlagRegistry.TerminalFlags));
        Assert.Equal("Leveled, Unlocked, Alternate Colors, Hide Welcome Text when displaying Image (0x000F)",
            FlagRegistry.DecodeFlagNamesWithHex(0x0F, FlagRegistry.TerminalFlags));
        Assert.Equal("Add Note, Force Redraw (0x0003)",
            FlagRegistry.DecodeFlagNamesWithHex(0x03, FlagRegistry.TerminalMenuItemFlags));
        Assert.Equal("Force Redraw (0x0002)",
            FlagRegistry.DecodeFlagNamesWithHex(0x02, FlagRegistry.TerminalMenuItemFlags));
    }

    [Fact]
    public void GenerateAllReports_WiresTheResolverAndTheCollectionsGame()
    {
        var records = new RecordCollection
        {
            Game = BethesdaGame.FalloutNewVegas,
            Terminals = [Parent(), Child()],
            FormIdToEditorId = new Dictionary<uint, string>(EditorIds)
        };

        var files = GeckReportGenerator.GenerateAllReports(new ReportDataSources(records));
        var report = Lines(files["terminal_report.txt"]);

        Assert.Contains("Menu Item [1] Yes", report);
        Assert.Contains($"    1: {GetScriptVariable4}", report);
        Assert.Contains("  Sub-menu:       NVDLC01VaultMainInfoDownload01Terminal [0x01011D70]", report);
        Assert.DoesNotContain(report, line => line.Contains("function names assume", StringComparison.Ordinal));
        Assert.Equal([.. LegacyCsvHeader, .. AppendedCsvHeader], ParseCsv(files["terminals.csv"])[0]);

        var combined = Lines(GeckReportGenerator.Generate(records));
        Assert.Contains("  Result Text:    Downloading module...", combined);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerateAllReports_RuntimeInputIdentityControlsAbsenceWording(bool dumpInput)
    {
        var records = new RecordCollection
        {
            Game = BethesdaGame.FalloutNewVegas,
            Scripts = [new() { FormId = 0x00123456, EditorId = "Unattributed", SourceText = "scn Unattributed" }],
            Terminals = [new() { FormId = ChildFormId, MenuItems = [new() { Text = "Empty capture" }] }]
        };
        var sources = new ReportDataSources(records, RuntimeEditorIds: dumpInput
            ? [new RuntimeEditorIdEntry { EditorId = "RuntimeTerminal", FormId = ChildFormId }]
            : null);
        var reports = GeckReportGenerator.GenerateAllReports(sources);
        var report = reports["terminal_report.txt"];
        Assert.Contains(dumpInput
            ? "no compiled code recovered: " + AbsenceWording
            : "no compiled code (no SCDA or source text in this menu item)", report);
        Assert.Equal(dumpInput, report.Contains(AbsenceWording, StringComparison.Ordinal));
        Assert.Equal(dumpInput, reports["script_report.txt"].Contains("unattributed-same-dump", StringComparison.Ordinal));
        Assert.Equal(dumpInput, reports["script_report.txt"].Contains("Source Origin:", StringComparison.Ordinal));
    }

    private static ConditionDisplayContext FnvContext()
    {
        return ConditionDisplayContext.ForResolver(Resolver, BethesdaGame.FalloutNewVegas);
    }

    private static DialogueCondition ScriptVariableCondition(uint variableIndex, bool isOr = false)
    {
        // Type 0x20 = operator 1 (!=); bit 0 = OR. FNV condition function 0x35 = GetScriptVariable.
        return new DialogueCondition
        {
            Type = (byte)(isOr ? 0x21 : 0x20),
            ComparisonValue = 1f,
            FunctionIndex = 0x0035,
            Parameter1 = TerminalRefFormId,
            Parameter2 = variableIndex
        };
    }

    private static TerminalRecord Parent()
    {
        return new TerminalRecord
        {
            FormId = ParentFormId,
            EditorId = "NVDLC01VaultMainInfoDownloadTerminal",
            FullName = "Vault Control Terminal",
            HeaderText = "Sierra Madre Control Network\r\n",
            Flags = 0x02,
            ServerType = 8,
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "Retrieve Hologram Technology Data",
                    ResultText = "Granting database access...",
                    ActionType = 0x03,
                    SubTerminal = ChildFormId,
                    Conditions = [ScriptVariableCondition(4)]
                },
                new TerminalMenuItem
                {
                    Text = "Retrieve Vending Replicator Schematics",
                    ResultText = "Granting database access...",
                    ActionType = 0x03,
                    SubTerminal = SiblingFormId,
                    Conditions = [ScriptVariableCondition(2)]
                }
            ]
        };
    }

    private static TerminalRecord Child()
    {
        return new TerminalRecord
        {
            FormId = ChildFormId,
            EditorId = "NVDLC01VaultMainInfoDownload01Terminal",
            FullName = "Vault Control Terminal",
            HeaderText = "Sierra Madre Control Network\r\n\r\nDownload Hologram Technology Module?",
            Flags = 0x02,
            ServerType = 8,
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "Yes",
                    ResultText = "Downloading module...",
                    ActionType = 0x03,
                    Conditions = [ScriptVariableCondition(4)],
                    CompiledData = new byte[92],
                    SourceText = YesSource,
                    DecompiledText = YesDecompiled,
                    ReferencedObjects = [HoloNoteFormId, TerminalRefFormId, CodeBoxFormId],
                    // The legacy field no TERM subrecord serializes; it must never reach MenuItemResultText.
                    ResultScript = 0x00ABCDEF
                },
                new TerminalMenuItem
                {
                    Text = "No",
                    ResultText = "Canceling request...",
                    ActionType = 0x03,
                    DisplayNoteFormId = HoloNoteFormId,
                    CompiledData = [0x23, 0x12, 0x00, 0x00],
                    SourceText = "ForceTerminalBack;\r\n",
                    DecompiledText = "ForceTerminalBack"
                }
            ]
        };
    }

    private static string RecordBlock(string report, uint formId)
    {
        var start = report.IndexOf($"FormID:         0x{formId:X8}", StringComparison.Ordinal);
        Assert.True(start >= 0, $"no block for 0x{formId:X8}");
        var end = report.IndexOf(new string('=', 80), start, StringComparison.Ordinal);
        return end < 0 ? report[start..] : report[start..end];
    }

    private static List<string> Lines(string text)
    {
        return [.. text.Split(["\r\n", "\n"], StringSplitOptions.None)];
    }

    private static string[] IndentedLines(string text, int indent)
    {
        var pad = new string(' ', indent);
        return text.Split("\r\n").Select(line => pad + line).ToArray();
    }

    /// <summary>Asserts each expected line occurs, in the given order (not necessarily adjacent).</summary>
    private static void AssertInOrder(List<string> lines, params string[] expected)
    {
        var from = 0;
        foreach (var line in expected)
        {
            var index = lines.IndexOf(line, from);
            Assert.True(index >= 0,
                $"expected line not found after line {from}: [{line}]{Environment.NewLine}{string.Join(Environment.NewLine, lines)}");
            from = index + 1;
        }
    }

    /// <summary>Asserts the lines immediately after <paramref name="heading" /> are exactly <paramref name="body" />.</summary>
    private static void AssertFollowedBy(List<string> lines, string heading, IReadOnlyList<string> body)
    {
        var index = lines.IndexOf(heading);
        Assert.True(index >= 0, $"heading not found: [{heading}]");
        Assert.True(index + body.Count < lines.Count, $"too few lines after [{heading}]");
        Assert.Equal(body, lines.Skip(index + 1).Take(body.Count).ToArray());
    }

    private static List<string[]> MenuItemRows(List<string[]> rows, uint formId)
    {
        return rows.Where(row => row[0] == "MENUITEM" && row[1] == $"0x{formId:X8}").ToList();
    }

    /// <summary>
    ///     Minimal RFC 4180 reader: quoted fields may hold commas, doubled quotes and line breaks (kept verbatim);
    ///     an unquoted CR is part of the row terminator. Shared with <see cref="TerminalMenuRetailTests" />.
    /// </summary>
    internal static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c != '"')
                {
                    field.Append(c);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                    fields.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    fields.Add(field.ToString());
                    field.Clear();
                    rows.Add([.. fields]);
                    fields.Clear();
                    break;
                default:
                    field.Append(c);
                    break;
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            rows.Add([.. fields]);
        }

        return rows;
    }
}
