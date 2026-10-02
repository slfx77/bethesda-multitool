using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Presentation;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Presentation;

/// <summary>
///     <c>show</c> on a TERM record. Before this, a TERM had no typed builder and fell through to the generic
///     identity panel (FormID, type, EditorID, name). The model now carries the record's DNAM fields and links,
///     the terminals whose items open it, and one section per menu item, built from the same
///     <c>TerminalMenuItemDescriber</c> as terminal_report.txt and terminals.csv.
///     <para>
///         The terminals are synthetic models shaped like the retail Dead Money pair (0x0100DAD2 opens
///         0x01011D70). Every expected value is written out literally.
///     </para>
/// </summary>
public sealed class TerminalRecordDetailTests
{
    private const uint ParentFormId = 0x0100DAD2;
    private const uint ChildFormId = 0x01011D70;
    private const uint TerminalRefFormId = 0x0100AAEB;
    private const uint HoloNoteFormId = 0x0100DACD;
    private const uint CodeBoxFormId = 0x0100AE1E;

    private const string YesSource =
        "AddNote NVDLC01VaultTechHoloNote;\r\n" +
        "If VaultMainTerminalREF.iDownloaded == 3\r\n" +
        "  Set VaultCodeBox.bTreasureFound to 1;\r\n" +
        "Endif\r\n" +
        "ForceTerminalBack;";

    private const string YesDecompiled = "AddNote NVDLC01VaultTechHoloNote\r\n; synthetic decompiled body\r\nForceTerminalBack";

    private const string DecompiledLabel =
        "Reconstruction (SCDA)";

    private static readonly FormIdResolver Resolver = new(
        new Dictionary<uint, string>
        {
            [ParentFormId] = "NVDLC01VaultMainInfoDownloadTerminal",
            [ChildFormId] = "NVDLC01VaultMainInfoDownload01Terminal",
            [TerminalRefFormId] = "VaultMainTerminalREF",
            [HoloNoteFormId] = "NVDLC01VaultTechHoloNote",
            [CodeBoxFormId] = "VaultCodeBox"
        },
        []);

    [Fact]
    public void TryBuildForLookup_TerminalReturnsMenuItemSections()
    {
        Assert.True(RecordDetailPresenter.TryBuildForLookup(Records(), Resolver, ChildFormId, null, out var built));
        var model = Assert.IsType<RecordDetailModel>(built);

        Assert.Equal("TERM", model.RecordSignature);
        Assert.Equal("NVDLC01VaultMainInfoDownload01Terminal", model.EditorId);
        Assert.Equal(
            ["Identity", "Header", "Linked From", "Menu Item [1] Yes", "Menu Item [2] No"],
            model.Sections.Select(section => section.Title).ToArray());

        Assert.Equal("Very Easy (0)", Value(model, "Identity", "Difficulty"));
        Assert.Equal("Unlocked (0x0002)", Value(model, "Identity", "Flags"));
        Assert.Equal("8 (-Server 9-)", Value(model, "Identity", "Server Type"));
        Assert.Equal("2", Value(model, "Identity", "Item Count"));
        Assert.Equal("Sierra Madre Control Network\r\n\r\nDownload Hologram Technology Module?",
            Value(model, "Header", "Header Text"));

        var linkedFrom = Assert.Single(ListItems(model, "Linked From", "Linked From"));
        Assert.Equal("NVDLC01VaultMainInfoDownloadTerminal [0x0100DAD2]", linkedFrom.Label);
        Assert.Equal("item [1] \"Retrieve Hologram Technology Data\"", linkedFrom.Value);
        Assert.Equal(ParentFormId, linkedFrom.LinkedFormId);

        const string yes = "Menu Item [1] Yes";
        Assert.Equal("Yes", Value(model, yes, "Item Text"));
        Assert.Equal("Downloading module...", Value(model, yes, "Result Text"));
        Assert.Equal("Add Note, Force Redraw (0x0003)", Value(model, yes, "Flags"));
        var condition = Assert.Single(ListItems(model, yes, "Conditions"));
        Assert.Equal("1", condition.Label);
        Assert.Equal("GetScriptVariable(VaultMainTerminalREF [0x0100AAEB], var 4) != 1 [Run On: Subject]",
            condition.Value);
        Assert.Equal("92 bytes of compiled code (SCDA), 3 references", Value(model, yes, "Result Script"));
        Assert.Equal("plugin-record", Value(model, yes, "Result Script Provenance"));

        var source = Entry(model, yes, "Result Script: Source (SCTX)");
        Assert.Equal(RecordDetailEntryKind.CodeBlock, source.Kind);
        Assert.Equal(YesSource, source.Value);
        var decompiled = Entry(model, yes, $"Result Script: {DecompiledLabel}");
        Assert.Equal(RecordDetailEntryKind.CodeBlock, decompiled.Kind);
        Assert.Equal(YesDecompiled, decompiled.Value);
        Assert.Equal("92 bytes", Value(model, yes, "Result Script Compiled Size"));
        Assert.Equal("Little-Endian", Value(model, yes, "Result Script Bytecode Order"));

        var references = ListItems(model, yes, "Result Script References");
        Assert.Equal(["1", "2", "3"], references.Select(item => item.Label).ToArray());
        Assert.Equal(
            ["NVDLC01VaultTechHoloNote (0x0100DACD)", "VaultMainTerminalREF (0x0100AAEB)", "VaultCodeBox (0x0100AE1E)"],
            references.Select(item => item.Value).ToArray());
        Assert.Equal(HoloNoteFormId, references[0].LinkedFormId);

        const string no = "Menu Item [2] No";
        var note = Entry(model, no, "Display Note");
        Assert.Equal(RecordDetailEntryKind.Link, note.Kind);
        Assert.Equal("NVDLC01VaultTechHoloNote [0x0100DACD]", note.Value);
        Assert.Equal(HoloNoteFormId, note.LinkedFormId);
    }

    [Fact]
    public void TryBuildForLookup_ParentTerminal_LinksEachItemToItsSubMenu_AndEmptyScriptsSayNoCompiledCode()
    {
        Assert.True(RecordDetailPresenter.TryBuildForLookup(
            Records(), Resolver, null, "nvdlc01vaultmaininfodownloadterminal", out var built));
        var model = Assert.IsType<RecordDetailModel>(built);

        Assert.Equal(ParentFormId, model.FormId);
        Assert.DoesNotContain(model.Sections, section => section.Title == "Linked From");

        const string item = "Menu Item [1] Retrieve Hologram Technology Data";
        var subMenu = Entry(model, item, "Sub-menu");
        Assert.Equal(RecordDetailEntryKind.Link, subMenu.Kind);
        Assert.Equal("NVDLC01VaultMainInfoDownload01Terminal [0x01011D70]", subMenu.Value);
        Assert.Equal(ChildFormId, subMenu.LinkedFormId);
        Assert.Equal("Granting database access...", Value(model, item, "Result Text"));
        Assert.Equal("no compiled code (no SCDA or source text in this menu item)", Value(model, item, "Result Script"));
        Assert.DoesNotContain(Section(model, item).Entries,
            entry => entry.Label.StartsWith("Result Script ", StringComparison.Ordinal));
    }

    [Fact]
    public void TryBuildForLookup_DumpInput_DescribesAMissingScriptAsAbsentFromTheCapture()
    {
        var records = new RecordCollection
        {
            Game = BethesdaGame.FalloutNewVegas,
            Terminals =
            [
                new TerminalRecord
                {
                    FormId = ChildFormId,
                    EditorId = "NVDLC01VaultMainInfoDownload01Terminal",
                    MenuItems =
                    [
                        new TerminalMenuItem { Text = "Yes" },
                        new TerminalMenuItem
                        {
                            Text = "No",
                            CompiledData = [0x23, 0x12, 0x00, 0x00],
                            SourceText = "ForceTerminalBack",
                            SourceTextOrigin = ScriptSourceTextOrigin.DecompiledFromBytecode,
                            DecompiledText = "ForceTerminalBack",
                            IsDmpDerived = true
                        }
                    ]
                }
            ]
        };

        Assert.True(RecordDetailPresenter.TryBuildForLookup(
            records, Resolver, ChildFormId, null, out var built, isMemoryDumpInput: true));
        var model = Assert.IsType<RecordDetailModel>(built);

        Assert.Equal(
            "no compiled code recovered: not present in this capture; absence from a partial memory dump is not " +
            "evidence of absence from the build",
            Value(model, "Menu Item [1] Yes", "Result Script"));

        const string no = "Menu Item [2] No";
        Assert.Equal("decompiled-from-bytecode", Value(model, no, "Result Script Provenance"));
        Assert.Equal("ForceTerminalBack", Value(model, no,
            "Result Script: Reconstruction (SCDA)"));
        Assert.DoesNotContain(Section(model, no).Entries,
            entry => entry.Label.Contains("authored", StringComparison.Ordinal));
    }

    private static RecordCollection Records()
    {
        return new RecordCollection
        {
            Game = BethesdaGame.FalloutNewVegas,
            Terminals =
            [
                new TerminalRecord
                {
                    FormId = ParentFormId,
                    EditorId = "NVDLC01VaultMainInfoDownloadTerminal",
                    FullName = "Vault Control Terminal",
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
                        }
                    ]
                },
                new TerminalRecord
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
                            ReferencedObjects = [HoloNoteFormId, TerminalRefFormId, CodeBoxFormId]
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
                }
            ]
        };
    }

    private static DialogueCondition ScriptVariableCondition(uint variableIndex)
    {
        // Type 0x20 = operator 1 (!=). FNV condition function 0x35 = GetScriptVariable.
        return new DialogueCondition
        {
            Type = 0x20,
            ComparisonValue = 1f,
            FunctionIndex = 0x0035,
            Parameter1 = TerminalRefFormId,
            Parameter2 = variableIndex
        };
    }

    private static RecordDetailSection Section(RecordDetailModel model, string title)
    {
        return Assert.Single(model.Sections, section => section.Title == title);
    }

    private static RecordDetailEntry Entry(RecordDetailModel model, string section, string label)
    {
        return Assert.Single(Section(model, section).Entries, entry => entry.Label == label);
    }

    private static string? Value(RecordDetailModel model, string section, string label)
    {
        return Entry(model, section, label).Value;
    }

    private static IReadOnlyList<RecordDetailListItem> ListItems(RecordDetailModel model, string section, string label)
    {
        var entry = Entry(model, section, label);
        Assert.Equal(RecordDetailEntryKind.List, entry.Kind);
        return Assert.IsAssignableFrom<IReadOnlyList<RecordDetailListItem>>(entry.Items);
    }
}
