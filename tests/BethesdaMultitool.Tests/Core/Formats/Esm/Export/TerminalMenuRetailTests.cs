using BethesdaMultitool.Core.Formats.Esm.Export.Csv;
using BethesdaMultitool.Core.Formats.Esm.Export.Geck;
using BethesdaMultitool.Core.Formats.Esm.Presentation;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Export;

/// <summary>
///     The retail Dead Money terminals the tool-feedback audit named, read from the 2022-5-24 Steam build's
///     DeadMoney.esm. Every pinned value was first read straight off the file with a read-only probe (TERM records
///     walked subrecord by subrecord, uncompressed), independently of this tool's parser:
///     <list type="bullet">
///         <item>
///             0x01011D70 NVDLC01VaultMainInfoDownload01Terminal: DNAM 00 02 08 00 (Very Easy, Unlocked, server 8);
///             item 1 ITXT "Yes", RNAM "Downloading module...", ANAM 0x03, SCHR (3 refs, 92 bytes), SCDA 92 bytes,
///             SCTX below, SCRO 0x0100DACD / 0x0100AAEB / 0x0100AE1E, CTDA type 0x20 fn 0x35 p1 0x0100AAEB p2 4
///             comparison 1.0; item 2 ITXT "No", RNAM "Canceling request...", ANAM 0x03, SCDA 23 12 00 00,
///             SCTX "ForceTerminalBack;\r\n".
///         </item>
///         <item>
///             0x0100DAD2 NVDLC01VaultMainInfoDownloadTerminal: three items, each RNAM "Granting database access...",
///             ANAM 0x03, an all-zero SCHR (no SCDA, no SCTX), TNAM 0x01011D70 / 0x01011D6F / 0x01011D6E and CTDA
///             GetScriptVariable(0x0100AAEB, 4 / 2 / 3) != 1. It is the only TERM in the file whose TNAM names
///             0x01011D70.
///         </item>
///         <item>NVDLC01BunkerTerminal02 has DNAM difficulty 5 (xEdit "Requires Key").</item>
///     </list>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", TestCategories.BucketB)]
public sealed class TerminalMenuRetailTests
{
    private const uint ParentFormId = 0x0100DAD2;
    private const uint ChildFormId = 0x01011D70;

    private const string YesSource =
        "AddNote NVDLC01VaultTechHoloNote;\r\n" +
        "Set VaultMainTerminalREF.bGotHologram to 1;\r\n" +
        "Set VaultMainTerminalREF.iDownloaded to (VaultMainTerminalREF.iDownloaded + 1);\r\n" +
        "If VaultMainTerminalREF.iDownloaded == 3\r\n" +
        "  Set VaultCodeBox.bTreasureFound to 1;\r\n" +
        "  ForceTerminalBack;\r\n" +
        "Endif\r\n" +
        "ForceTerminalBack;";

    [Fact]
    public async Task DeadMoneyVaultTerminals_ReportCarriesFullMenuSemantics()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esm = RealAssetPaths.NewVegasBuilds.Steam2022("DeadMoney.esm");
        Assert.SkipUnless(esm is not null, RealAssetPaths.SkipMessage("DeadMoney.esm (2022-5-24 Steam build)"));

        // Never disposed: RealAssetEsmCache owns the result and shares it across this collection.
        var result = await RealAssetEsmCache.LoadAsync(esm, TestContext.Current.CancellationToken);
        var records = result.Records;
        var resolver = records.CreateResolver();
        var conditions = ConditionDisplayContext.From(records, resolver);

        // The parsed model carries every field the probe read.
        var child = Assert.Single(records.Terminals, terminal => terminal.FormId == ChildFormId);
        Assert.Equal("NVDLC01VaultMainInfoDownload01Terminal", child.EditorId);
        Assert.Equal(2, child.MenuItems.Count);
        var yes = child.MenuItems[0];
        Assert.Equal("Yes", yes.Text);
        Assert.Equal("Downloading module...", yes.ResultText);
        Assert.Equal((byte)0x03, yes.ActionType);
        var gate = Assert.Single(yes.Conditions);
        Assert.Equal((ushort)0x0035, gate.FunctionIndex);
        Assert.Equal((byte)0x20, gate.Type);
        Assert.Equal(0x0100AAEBu, gate.Parameter1);
        Assert.Equal(4u, gate.Parameter2);
        Assert.Equal(1f, gate.ComparisonValue);
        Assert.Equal(92, yes.CompiledData?.Length);
        Assert.Equal(YesSource, yes.SourceText);
        Assert.Equal([0x0100DACDu, 0x0100AAEBu, 0x0100AE1Eu], yes.ReferencedObjects);
        var no = child.MenuItems[1];
        Assert.Equal("No", no.Text);
        Assert.Equal("Canceling request...", no.ResultText);
        Assert.Equal([0x23, 0x12, 0x00, 0x00], no.CompiledData);
        Assert.StartsWith("ForceTerminalBack;", no.SourceText, StringComparison.Ordinal);

        var parent = Assert.Single(records.Terminals, terminal => terminal.FormId == ParentFormId);
        Assert.Equal([0x01011D70u, 0x01011D6Fu, 0x01011D6Eu],
            parent.MenuItems.Select(item => item.SubTerminal ?? 0).ToArray());
        Assert.Equal([4u, 2u, 3u], parent.MenuItems.Select(item => Assert.Single(item.Conditions).Parameter2).ToArray());
        Assert.All(parent.MenuItems, item => Assert.Equal("Granting database access...", item.ResultText));

        // terminal_report.txt: the full menu semantics, in the child's own block.
        var report = GeckTextContentWriter.GenerateTerminalsReport(records.Terminals, resolver, conditions);
        var childBlock = RecordBlock(report, ChildFormId);
        Assert.Contains("Flags:          Unlocked (0x0002)", childBlock, StringComparison.Ordinal);
        Assert.Contains("Server Type:    8 (-Server 9-)", childBlock, StringComparison.Ordinal);
        Assert.Contains("Linked From (1):", childBlock, StringComparison.Ordinal);
        Assert.Contains(
            "  NVDLC01VaultMainInfoDownloadTerminal [0x0100DAD2] item [1] \"Retrieve Hologram Technology Data\"",
            childBlock, StringComparison.Ordinal);
        Assert.Contains("Menu Item [1] Yes", childBlock, StringComparison.Ordinal);
        Assert.Contains("  Result Text:    Downloading module...", childBlock, StringComparison.Ordinal);
        Assert.Contains("  Flags:          Add Note, Force Redraw (0x0003)", childBlock, StringComparison.Ordinal);
        Assert.Contains(
            "    1: GetScriptVariable(VaultMainTerminalREF [0x0100AAEB], var 4) != 1 [Run On: Subject]",
            childBlock, StringComparison.Ordinal);
        Assert.Contains("  Result Script:  92 bytes of compiled code (SCDA), 3 references", childBlock,
            StringComparison.Ordinal);
        Assert.Contains("    Source (SCTX):", childBlock, StringComparison.Ordinal);
        Assert.Contains("        Set VaultCodeBox.bTreasureFound to 1;", childBlock, StringComparison.Ordinal);
        Assert.Contains("NVDLC01VaultTechHoloNote (0x0100DACD)", childBlock, StringComparison.Ordinal);
        Assert.Contains("Menu Item [2] No", childBlock, StringComparison.Ordinal);
        Assert.Contains("  Result Text:    Canceling request...", childBlock, StringComparison.Ordinal);

        var parentBlock = RecordBlock(report, ParentFormId);
        Assert.Contains("  Sub-menu:       NVDLC01VaultMainInfoDownload01Terminal [0x01011D70]", parentBlock,
            StringComparison.Ordinal);
        Assert.Contains("  Sub-menu:       NVDLC01VaultMainInfoDownload02Terminal [0x01011D6F]", parentBlock,
            StringComparison.Ordinal);
        Assert.Contains("  Sub-menu:       NVDLC01VaultMainInfoDownload03Terminal [0x01011D6E]", parentBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "    1: GetScriptVariable(VaultMainTerminalREF [0x0100AAEB], var 3) != 1 [Run On: Subject]",
            parentBlock, StringComparison.Ordinal);

        // terminals.csv: column 11 is the RNAM text, and the appended columns carry the item's semantics.
        var rows = TerminalMenuReportTests.ParseCsv(
            CsvSupplementalWriter.GenerateTerminalsCsv(records.Terminals, resolver, conditions));
        var childRows = rows.Where(row => row[0] == "MENUITEM" && row[1] == "0x01011D70").ToList();
        Assert.Equal(2, childRows.Count);
        Assert.Equal("Yes", childRows[0][9]);
        Assert.Equal("Downloading module...", childRows[0][10]);
        Assert.Equal("1", childRows[0][12]);
        Assert.Equal("0x03", childRows[0][13]);
        Assert.Equal("Add Note, Force Redraw", childRows[0][14]);
        Assert.Equal("1", childRows[0][17]);
        Assert.Equal("plugin-record", childRows[0][19]);
        Assert.Equal(YesSource, childRows[0][20]);
        Assert.Equal("Canceling request...", childRows[1][10]);
        Assert.Equal("2", childRows[1][12]);

        var parentRows = rows.Where(row => row[0] == "MENUITEM" && row[1] == "0x0100DAD2").ToList();
        Assert.Equal(["0x01011D70", "0x01011D6F", "0x01011D6E"], parentRows.Select(row => row[11]).ToArray());

        Assert.DoesNotContain(rows, row => row[5] == "Unknown (5)");
        var bunker = Assert.Single(rows, row => row[0] == "TERMINAL" && row[2] == "NVDLC01BunkerTerminal02");
        Assert.Equal("5", bunker[4]);
        Assert.Equal("Requires Key", bunker[5]);

        // show: one section per menu item, the incoming link, and the sub-menu as a navigable link.
        Assert.True(RecordDetailPresenter.TryBuildForLookup(records, resolver, ChildFormId, null, out var childModel));
        var childTitles = Assert.IsType<RecordDetailModel>(childModel).Sections.Select(section => section.Title).ToArray();
        Assert.Contains("Menu Item [1] Yes", childTitles);
        Assert.Contains("Menu Item [2] No", childTitles);
        Assert.Contains("Linked From", childTitles);

        Assert.True(RecordDetailPresenter.TryBuildForLookup(records, resolver, ParentFormId, null, out var parentModel));
        var firstItem = Assert.Single(Assert.IsType<RecordDetailModel>(parentModel).Sections,
            section => section.Title == "Menu Item [1] Retrieve Hologram Technology Data");
        var subMenu = Assert.Single(firstItem.Entries, entry => entry.Label == "Sub-menu");
        Assert.Equal(ChildFormId, subMenu.LinkedFormId);
    }

    private static string RecordBlock(string report, uint formId)
    {
        var start = report.IndexOf($"FormID:         0x{formId:X8}", StringComparison.Ordinal);
        Assert.True(start >= 0, $"no block for 0x{formId:X8}");
        var end = report.IndexOf(new string('=', 80), start, StringComparison.Ordinal);
        return end < 0 ? report[start..] : report[start..end];
    }
}
