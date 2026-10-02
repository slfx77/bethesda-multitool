using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Analysis;

public sealed class EsmScriptDiagnosticsAnalyzerTests
{
    private const uint TerminalId = 0x01011D70;
    private const uint SubTerminalId = 0x01011D6F;
    private const uint TerminalRefId = 0x0100AAEB;
    private const uint TerminalNoteId = 0x0100D001;

    /// <summary>
    ///     A menu-item script longer than the 180-character preview, with CRLF line endings and a tab, whose tail
    ///     marker only the full text can carry (the shape of Dead Money 0x01011D70's 291-byte "Yes" script).
    /// </summary>
    private const string TerminalSource =
        "; Synthetic TERM menu-item script (shape of Dead Money 0x01011D70)\r\n" +
        "If VaultMainTerminalREF.iDownloaded == 3\r\n" +
        "\tSet VaultCodeBox.bTreasureFound to 1;\r\n" +
        "\tForceTerminalBack;\r\n" +
        "Endif\r\n" +
        "; filler that carries the text past the 180-character preview\r\n" +
        "ForceTerminalBack;\r\n" +
        "; TAIL-MARKER-7F3A";

    private const string YesCondition =
        "GetScriptVariable(VaultMainTerminalREF [0x0100AAEB], var 4) != 1 [Run On: Subject]";

    private const string NoFirstCondition =
        "GetScriptVariable(VaultMainTerminalREF [0x0100AAEB], var 2) != 1 [Run On: Subject] OR";

    private const string NoSecondCondition =
        "GetScriptVariable(VaultMainTerminalREF [0x0100AAEB], var 3) != 1 [Run On: Subject]";

    [Fact]
    public void AnalyzeRecords_TerminalItemsJoinConditionsBlocksAndFullSource()
    {
        var result = AnalyzeSyntheticTerminal();

        var items = result.TerminalItems.Where(row => row.FormId == TerminalId).ToArray();
        Assert.Equal(2, items.Length);

        var yes = items[0];
        Assert.Equal("explicit", yes.Target);
        Assert.Equal("explicit-record", yes.Relation);
        Assert.Equal("VaultRetrieveTerminal", yes.EditorId);
        Assert.Equal(1, yes.ItemIndex);
        Assert.Equal("Yes", yes.ItemText);
        Assert.Equal("Downloading module...", yes.ResultText);
        Assert.Equal((byte)0x03, yes.FlagsRaw);
        Assert.Equal("Add Note, Force Redraw", yes.FlagNames);
        Assert.Null(yes.DisplayNoteFormId);
        Assert.Null(yes.SubTerminalFormId);
        Assert.Equal([1], yes.ConditionIndexes);
        Assert.Equal([YesCondition], yes.ConditionExpressions);
        Assert.Equal([1], yes.ScriptBlockIndexes);
        Assert.Equal(TerminalSource.Length, yes.SourceTextLength);

        var no = items[1];
        Assert.Equal(2, no.ItemIndex);
        Assert.Equal("No", no.ItemText);
        Assert.Equal("Canceling request...", no.ResultText);
        Assert.Equal((byte)0x01, no.FlagsRaw);
        Assert.Equal("Add Note", no.FlagNames);
        Assert.Equal(TerminalNoteId, no.DisplayNoteFormId);
        Assert.Equal("VaultPasswordNote", no.DisplayNoteLabel);
        Assert.Equal(SubTerminalId, no.SubTerminalFormId);
        Assert.Equal("VaultSubTerminal", no.SubTerminalLabel);
        Assert.Equal([2, 3], no.ConditionIndexes);
        Assert.Equal([NoFirstCondition, NoSecondCondition], no.ConditionExpressions);
        Assert.Equal([2], no.ScriptBlockIndexes);
        Assert.Null(no.SourceTextLength);

        // The join keys are the numbers target_conditions.csv and target_result_scripts.csv print.
        Assert.Equal(
            [1, 2, 3],
            result.Conditions.Where(row => row.FormId == TerminalId).Select(row => row.ConditionIndex).ToArray());

        var blocks = result.ScriptBlocks.Where(row => row.FormId == TerminalId).ToArray();
        Assert.Equal(2, blocks.Length);
        Assert.Equal(1, blocks[0].BlockIndex);
        Assert.Equal(TerminalSource, blocks[0].SourceText);
        Assert.Equal(180, blocks[0].SourceTextPreview.Length);
        Assert.EndsWith("...", blocks[0].SourceTextPreview, StringComparison.Ordinal);
        Assert.DoesNotContain("TAIL-MARKER-7F3A", blocks[0].SourceTextPreview, StringComparison.Ordinal);
        Assert.Equal(1, blocks[0].OwnerMenuItemIndex);
        Assert.Equal("Yes", blocks[0].OwnerMenuItemText);
        Assert.Equal(2, blocks[1].BlockIndex);
        Assert.Equal(string.Empty, blocks[1].SourceText);
        Assert.Equal(2, blocks[1].OwnerMenuItemIndex);
        Assert.Equal("No", blocks[1].OwnerMenuItemText);

        // The stored SCTX is the fixture's Latin-1 bytes plus one NUL; the row keeps the bytes minus that NUL.
        Assert.Equal(Encoding.Latin1.GetBytes(TerminalSource), blocks[0].SourceTextBytes);
        Assert.Null(blocks[1].SourceTextBytes);

        var blockCsv = ParseCsv(EsmScriptDiagnosticsCsvWriter.BuildScriptBlocksCsv(result));
        Assert.Equal("source_text_preview", blockCsv[0][17]);
        Assert.Equal(["source_text_length", "owner_item_index", "owner_item_text", "source_text", "source_text_file"],
            blockCsv[0][18..]);
        var yesBlock = blockCsv.Skip(1).Single(fields => fields[3] == "0x01011D70" && fields[5] == "1");
        Assert.Equal(TerminalSource.Length.ToString(CultureInfo.InvariantCulture), yesBlock[18]);
        Assert.Equal("1", yesBlock[19]);
        Assert.Equal("Yes", yesBlock[20]);
        Assert.Equal(TerminalSource, yesBlock[21]);
        Assert.Equal("scripts/TERM_01011D70_block01.gek", yesBlock[22]);
        var noBlock = blockCsv.Skip(1).Single(fields => fields[3] == "0x01011D70" && fields[5] == "2");
        Assert.Equal(string.Empty, noBlock[22]);

        var itemCsv = ParseCsv(EsmScriptDiagnosticsCsvWriter.BuildTerminalItemsCsv(result));
        Assert.Equal(3, itemCsv.Count);
        Assert.Equal(
            [
                "target", "relation", "form_id", "editor_id", "item_index", "item_text", "result_text", "flags_raw",
                "flag_names", "display_note_form_id", "display_note_label", "sub_terminal_form_id",
                "sub_terminal_label", "condition_indexes", "condition_expressions", "script_block_index",
                "source_text_length"
            ],
            itemCsv[0]);
        Assert.Equal(
            [
                "explicit", "explicit-record", "0x01011D70", "VaultRetrieveTerminal", "1", "Yes",
                "Downloading module...", "0x03", "Add Note, Force Redraw", "", "", "", "", "1", YesCondition, "1",
                TerminalSource.Length.ToString(CultureInfo.InvariantCulture)
            ],
            itemCsv[1]);
        Assert.Equal(
            [
                "explicit", "explicit-record", "0x01011D70", "VaultRetrieveTerminal", "2", "No",
                "Canceling request...", "0x01", "Add Note", "0x0100D001", "VaultPasswordNote", "0x01011D6F",
                "VaultSubTerminal", "2|3", NoFirstCondition + " | " + NoSecondCondition, "2", ""
            ],
            itemCsv[2]);
    }

    [Fact]
    public void TerminalScriptBlockSubrecordOrder_StopsAtNextMenuItem()
    {
        var terminal = AnalyzeSyntheticTerminal();
        var blocks = terminal.ScriptBlocks.Where(row => row.FormId == TerminalId).ToArray();

        // Before ITXT was a block boundary, block 1 ran on into item 2 as
        // SCHR>SCDA>SCTX>SCRO>CTDA>ITXT>RNAM>ANAM>INAM>TNAM.
        Assert.Equal("SCHR>SCDA>SCTX>SCRO>CTDA", blocks[0].SubrecordOrder);
        Assert.Equal("SCHR>CTDA>CTDA", blocks[1].SubrecordOrder);

        // INFO result-script blocks still end at NEXT and at the next SCHR.
        const uint infoId = 0xFE000500;
        var info = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp",
            [
                Record("INFO", infoId,
                    ScriptHeader(1, 4),
                    Sub("SCDA", 0xFF, 0xFF, 0x00, 0x00),
                    StringSub("SCTX", "set SomeGlobal to 1"),
                    FormIdSubrecord("SCRO", 0xFE000501),
                    Sub("NEXT"),
                    ScriptHeader(0, 0))
            ],
            [],
            BethesdaGame.FalloutNewVegas,
            new HashSet<uint> { infoId });
        var infoBlocks = info.ScriptBlocks.Where(row => row.FormId == infoId).ToArray();
        Assert.Equal(2, infoBlocks.Length);
        Assert.Equal("SCHR>SCDA>SCTX>SCRO>NEXT", infoBlocks[0].SubrecordOrder);
        Assert.Equal("SCHR", infoBlocks[1].SubrecordOrder);
        Assert.Null(infoBlocks[0].OwnerMenuItemIndex);
        Assert.Equal(string.Empty, infoBlocks[0].OwnerMenuItemText);
        Assert.Empty(info.TerminalItems);
    }

    [Fact]
    public void WriteReport_WritesTerminalItemsCsv_AndKeepsTheConditionsHeader()
    {
        var result = AnalyzeSyntheticTerminal();
        var directory = Path.Combine(Path.GetTempPath(), $"scriptdiag_term_{Guid.NewGuid():N}");
        try
        {
            EsmScriptDiagnosticsAnalyzer.WriteReport(result, directory);

            var itemLines = File.ReadAllLines(Path.Combine(directory, "target_terminal_items.csv"));
            Assert.Equal(3, itemLines.Length);
            Assert.StartsWith("target,relation,form_id,editor_id,item_index,item_text,result_text,",
                itemLines[0], StringComparison.Ordinal);

            Assert.Equal(
                "target,relation,record_type,form_id,editor_id,condition_index,function_name,function_index,type," +
                "comparison_value,parameter1,parameter1_label,parameter2,parameter2_label,run_on,reference_storage," +
                "semantic_reference_label,raw_bytes,comparison_kind,comparison_raw_bits,comparison_global_form_id," +
                "comparison_global_label,parameter3,reference_storage_is_semantic,semantic_reference_form_id," +
                "ctda_body_length,layout_status",
                File.ReadLines(Path.Combine(directory, "target_conditions.csv")).First());
            Assert.Contains("TAIL-MARKER-7F3A",
                File.ReadAllText(Path.Combine(directory, "target_result_scripts.csv")), StringComparison.Ordinal);
            Assert.Contains("- Terminal menu items: 2",
                File.ReadAllText(Path.Combine(directory, "summary.md")), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void WriteReport_WritesVerbatimFullSourceSidecarGek()
    {
        // 0x92 is Windows-1252's right single quote: a UTF-8 writer emits E2 80 99 for it and a Latin-1
        // round trip C2 92, so only an untouched copy of the stored bytes reproduces it.
        byte[] yesSource =
        [
            .. Encoding.ASCII.GetBytes("; Caesar"), 0x92,
            .. Encoding.ASCII.GetBytes(
                "s Legion terminal\r\n" +
                "If VaultMainTerminalREF.iDownloaded == 3\r\n" +
                "\tSet VaultCodeBox.bTreasureFound to 1;\r\n" +
                "Endif\r\n" +
                "ForceTerminalBack;")
        ];
        byte[] yesSctx = [.. yesSource, 0x00];

        // Two stored NULs: exactly one is dropped. A copy rebuilt from the decoded text (which trims every
        // trailing NUL) would lose both.
        byte[] maybeSctx = [.. Encoding.ASCII.GetBytes("ForceTerminalBack;"), 0x00, 0x00];
        byte[] maybeFile = [.. Encoding.ASCII.GetBytes("ForceTerminalBack;"), 0x00];

        // Two label targets reach the same TERM, so every block has a row under each target.
        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "DeadMoney.esm",
            [
                Record("TERM", TerminalId,
                    StringSub("EDID", "VaultRetrieveTerminal"),
                    StringSub("ITXT", "Yes"),
                    ScriptHeader(0, 4),
                    Sub("SCDA", 0x23, 0x12, 0x00, 0x00),
                    Sub("SCTX", yesSctx),
                    StringSub("ITXT", "No"),
                    ScriptHeader(0, 0),
                    StringSub("ITXT", "Maybe"),
                    ScriptHeader(0, 4),
                    Sub("SCDA", 0x23, 0x12, 0x00, 0x00),
                    Sub("SCTX", maybeSctx))
            ],
            ["VaultRetrieve", "RetrieveTerminal"],
            BethesdaGame.FalloutNewVegas);
        Assert.Equal(6, result.ScriptBlocks.Count(row => row.FormId == TerminalId));

        var directory = Path.Combine(Path.GetTempPath(), $"scriptdiag_gek_{Guid.NewGuid():N}");
        try
        {
            EsmScriptDiagnosticsAnalyzer.WriteReport(result, directory);

            var scriptsDirectory = Path.Combine(directory, "scripts");
            Assert.Equal(
                ["TERM_01011D70_block01.gek", "TERM_01011D70_block03.gek"],
                Directory.GetFiles(scriptsDirectory)
                    .Select(path => Path.GetFileName(path))
                    .Order(StringComparer.Ordinal)
                    .ToArray());

            // Byte for byte: the 0x92 unchanged, CRLF kept, no BOM, no header, nothing appended.
            Assert.Equal(yesSource, File.ReadAllBytes(Path.Combine(scriptsDirectory, "TERM_01011D70_block01.gek")));
            Assert.Equal(maybeFile, File.ReadAllBytes(Path.Combine(scriptsDirectory, "TERM_01011D70_block03.gek")));

            var blockCsv = ParseCsv(File.ReadAllText(Path.Combine(directory, "target_result_scripts.csv")));
            Assert.Equal("source_text_file", blockCsv[0][^1]);
            var fileByTargetAndBlock = blockCsv.Skip(1)
                .Where(fields => fields[3] == "0x01011D70")
                .ToDictionary(fields => (fields[0], fields[5]), fields => fields[^1]);
            Assert.Equal(6, fileByTargetAndBlock.Count);
            foreach (var target in new[] { "VaultRetrieve", "RetrieveTerminal" })
            {
                Assert.Equal("scripts/TERM_01011D70_block01.gek", fileByTargetAndBlock[(target, "1")]);
                Assert.Equal(string.Empty, fileByTargetAndBlock[(target, "2")]);
                Assert.Equal("scripts/TERM_01011D70_block03.gek", fileByTargetAndBlock[(target, "3")]);
            }

            Assert.True(File.Exists(Path.Combine(directory, "target_terminal_items.csv")));
            Assert.Equal(
                "target,relation,record_type,form_id,editor_id,condition_index,function_name,function_index,type," +
                "comparison_value,parameter1,parameter1_label,parameter2,parameter2_label,run_on,reference_storage," +
                "semantic_reference_label,raw_bytes,comparison_kind,comparison_raw_bits,comparison_global_form_id," +
                "comparison_global_label,parameter3,reference_storage_is_semantic,semantic_reference_form_id," +
                "ctda_body_length,layout_status",
                File.ReadLines(Path.Combine(directory, "target_conditions.csv")).First());
            Assert.Contains("- Script source files: 2 in scripts/",
                File.ReadAllText(Path.Combine(directory, "summary.md")), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    /// <summary>
    ///     The sidecar extension follows the game (<c>.gek</c> only for the GECK-script games). The record's block
    ///     has no SCHR, so its SCTX is found after the SCDA that opens the block.
    /// </summary>
    [Theory]
    [InlineData(BethesdaGame.FalloutNewVegas, "SCPT_00ABC001_block01.gek")]
    [InlineData(BethesdaGame.Fallout3, "SCPT_00ABC001_block01.gek")]
    [InlineData(BethesdaGame.Oblivion, "SCPT_00ABC001_block01.txt")]
    [InlineData(BethesdaGame.Unknown, "SCPT_00ABC001_block01.txt")]
    public void WriteReport_SourceSidecarExtensionFollowsTheGame(BethesdaGame game, string expectedFileName)
    {
        const uint scriptId = 0x00ABC001;
        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "Plugin.esp",
            [
                Record("SCPT", scriptId,
                    StringSub("EDID", "SidecarScript"),
                    Sub("SCDA", 0x1D, 0x00, 0x00, 0x00),
                    StringSub("SCTX", "scn SidecarScript\r\n\r\nBegin GameMode\r\nEnd"))
            ],
            [],
            game,
            new HashSet<uint> { scriptId });

        var directory = Path.Combine(Path.GetTempPath(), $"scriptdiag_ext_{Guid.NewGuid():N}");
        try
        {
            EsmScriptDiagnosticsAnalyzer.WriteReport(result, directory);

            var file = Assert.Single(Directory.GetFiles(Path.Combine(directory, "scripts")));
            Assert.Equal(expectedFileName, Path.GetFileName(file));
            Assert.Equal(
                Encoding.ASCII.GetBytes("scn SidecarScript\r\n\r\nBegin GameMode\r\nEnd"),
                File.ReadAllBytes(file));
            var row = ParseCsv(File.ReadAllText(Path.Combine(directory, "target_result_scripts.csv")))[1];
            Assert.Equal("scripts/" + expectedFileName, row[^1]);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void WriteReport_WritesNoScriptsDirectoryWhenNoBlockHasSource()
    {
        const uint infoId = 0xFE000600;
        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp",
            [
                Record("INFO", infoId,
                    ScriptHeader(0, 4),
                    Sub("SCDA", 0x23, 0x12, 0x00, 0x00),
                    Sub("SCTX", 0x00),
                    Sub("NEXT"),
                    ScriptHeader(0, 0))
            ],
            [],
            BethesdaGame.FalloutNewVegas,
            new HashSet<uint> { infoId });

        var directory = Path.Combine(Path.GetTempPath(), $"scriptdiag_nosrc_{Guid.NewGuid():N}");
        try
        {
            EsmScriptDiagnosticsAnalyzer.WriteReport(result, directory);

            // Block 1's SCTX holds only its NUL (present but empty); block 2 has no SCTX at all.
            var blocks = result.ScriptBlocks.Where(row => row.FormId == infoId).ToArray();
            Assert.Equal(2, blocks.Length);
            var emptySource = blocks[0].SourceTextBytes;
            Assert.NotNull(emptySource);
            Assert.Empty(emptySource);
            Assert.Null(blocks[1].SourceTextBytes);
            Assert.False(Directory.Exists(Path.Combine(directory, "scripts")));
            Assert.All(
                ParseCsv(File.ReadAllText(Path.Combine(directory, "target_result_scripts.csv"))).Skip(1),
                fields => Assert.Equal(string.Empty, fields[^1]));
            Assert.DoesNotContain("Script source files",
                File.ReadAllText(Path.Combine(directory, "summary.md")), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    [Fact]
    public void AnalyzeRecords_Fallout4TerminalItem_KeepsRawAnamAndDoesNotInterpretAClassicWidthCtda()
    {
        // Fallout 4 stores a menu-item type enum in ANAM, and its CTDA is 32 bytes: a 28-byte CTDA
        // there is listed raw, the same fail-closed rule target_conditions.csv applies.
        const uint fo4TerminalId = 0x00A00001;
        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "Fallout4.esm",
            [
                Record("TERM", fo4TerminalId,
                    StringSub("EDID", "Fo4Terminal"),
                    StringSub("ITXT", "Open"),
                    Sub("ANAM", 0x04),
                    Ctda(0x35, TerminalRefId, 4, 0x20))
            ],
            [],
            BethesdaGame.Fallout4,
            new HashSet<uint> { fo4TerminalId });

        var item = Assert.Single(result.TerminalItems);
        Assert.Equal("Open", item.ItemText);
        Assert.Null(item.ResultText);
        Assert.Equal((byte)0x04, item.FlagsRaw);
        Assert.Equal(string.Empty, item.FlagNames);
        Assert.Equal([1], item.ConditionIndexes);
        Assert.Equal(
            ["CTDA not interpreted (game_width_mismatch): 200000000000803F35000000EBAA0001040000000000000000000000"],
            item.ConditionExpressions);
        Assert.Empty(item.ScriptBlockIndexes);
        Assert.Null(item.SourceTextLength);
    }

    [Theory]
    [InlineData(BethesdaGame.FalloutNewVegas, 0x00, "None")]
    [InlineData(BethesdaGame.FalloutNewVegas, 0x02, "Force Redraw")]
    [InlineData(BethesdaGame.FalloutNewVegas, 0x07, "Add Note, Force Redraw, unknown bits 0x04")]
    [InlineData(BethesdaGame.Fallout3, 0x01, "Add Note")]
    [InlineData(BethesdaGame.Fallout4, 0x03, "")]
    public void FormatMenuItemFlags_NamesFo3FnvBitsAndKeepsUnknownOnes(BethesdaGame game, int flags, string expected)
    {
        Assert.Equal(expected, EsmScriptTerminalItemRowBuilder.FormatMenuItemFlags((byte)flags, game));
    }

    [Fact]
    public void AnalyzeFile_ThreadsDetectedGameIntoDiagnostics()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"scriptdiag_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "FalloutNV.esm");
        File.WriteAllBytes(path, BuildTes4Plugin(1.34f));
        try
        {
            var result = EsmScriptDiagnosticsAnalyzer.AnalyzeFile(path, []);

            Assert.Equal(BethesdaGame.FalloutNewVegas, result.Game);
            Assert.Empty(result.Conditions);
            Assert.Contains("- Game: FalloutNewVegas",
                EsmScriptDiagnosticsCsvWriter.BuildSummary(result), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void AnalyzeRecords_DumpsTargetDialogueScriptRefsAndFollowUpFields()
    {
        const uint ulysses = 0x00112233;
        const uint infoId = 0xFE000100;
        const uint topicId = 0xFE000200;
        const uint questId = 0xFE000300;
        const uint validRef = 0xFE000400;

        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp",
            [
                Record("NPC_", ulysses,
                    StringSub("EDID", "UlyssesNPC"),
                    StringSub("FULL", "Ulysses")),
                Record("DIAL", topicId,
                    StringSub("EDID", "VFreeformUlyssesRoot"),
                    FormIdSubrecord("TNAM", ulysses)),
                Record("QUST", questId, StringSub("EDID", "VFreeformUlysses")),
                Record("GLOB", validRef, StringSub("EDID", "UlyssesFollowerState")),
                Record("INFO", infoId,
                    FormIdSubrecord("TPIC", topicId),
                    FormIdSubrecord("QSTI", questId),
                    FormIdSubrecord("PNAM", 0xFE000099),
                    FormIdSubrecord("TCLT", topicId),
                    FormIdSubrecord("TCFU", 0xFE000101),
                    CtdaGetIsId(ulysses),
                    ScriptHeader(1, 4),
                    Sub("SCDA", 0xFF, 0xFF, 0x00, 0x00),
                    StringSub("SCTX", "set UlyssesFollowerState to 1"),
                    FormIdSubrecord("SCRO", validRef),
                    Sub("NEXT"),
                    Sub("TRDT", new byte[24]),
                    StringSub("NAM1", "Travel with me."))
            ],
            ["Ulysses"],
            BethesdaGame.FalloutNewVegas);

        Assert.Contains(result.TargetMatches, row => row.FormId == ulysses && row.MatchReason == "actor-label");
        var dialogue = Assert.Single(result.Dialogue, row => row.InfoFormId == infoId);
        Assert.Equal(topicId, dialogue.TopicFormId);
        Assert.Equal(questId, dialogue.QuestFormId);
        Assert.Equal(ulysses, dialogue.SpeakerFormId);
        Assert.Contains("0xFE000101", dialogue.FollowUpInfos, StringComparison.Ordinal);
        Assert.True(dialogue.HasResultScript);
        var audit = Assert.Single(result.DialogueAudit, row => row.InfoFormId == infoId);
        Assert.Equal("InternalLinkedTopic", audit.RootClassification);
        Assert.True(audit.HasIncomingTopicEdge);
        Assert.Contains("000200FE", audit.RawTcltBytes, StringComparison.OrdinalIgnoreCase);

        var script = Assert.Single(result.ScriptBlocks, row => row.FormId == infoId);
        Assert.True(script.CompiledSizeMatches);
        Assert.True(script.RefCountMatches);
        Assert.True(script.WalkedToEnd);
        Assert.Equal("canonical", script.OrderStatus);
        Assert.Contains("UlyssesFollowerState", script.SourceTextPreview, StringComparison.Ordinal);

        var reference = Assert.Single(result.ScriptReferences, row => row.ParentFormId == infoId);
        Assert.Equal("SCRO", reference.ReferenceKind);
        Assert.Equal("Resolved", reference.Status);
        Assert.Equal(validRef, reference.ResolvedFormId);
        Assert.Equal("UlyssesFollowerState", reference.ResolvedEditorId);
    }

    [Fact]
    public void AnalyzeRecords_FlagsNullAndMissingScroRefs()
    {
        const uint actorId = 0x00112233;
        const uint infoId = 0xFE000100;

        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp",
            [
                Record("NPC_", actorId,
                    StringSub("EDID", "UlyssesNPC"),
                    StringSub("FULL", "Ulysses")),
                Record("INFO", infoId,
                    CtdaGetIsId(actorId),
                    ScriptHeader(2, 4),
                    Sub("SCDA", 0xFF, 0xFF, 0x00, 0x00),
                    FormIdSubrecord("SCRO", 0),
                    FormIdSubrecord("SCRO", 0xFE00DEAD))
            ],
            ["Ulysses"],
            BethesdaGame.FalloutNewVegas);

        Assert.Contains(result.ScriptReferences, row => row.Status == "Null");
        Assert.Contains(result.ScriptReferences, row => row.Status == "Missing");
    }

    [Fact]
    public void AnalyzeRecords_ReportsActorPackagesAndPackageConditions()
    {
        const uint chomps = 0x00100020;
        const uint pack = 0xFE000220;
        const uint targetRef = 0xFE000330;

        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp",
            [
                Record("NPC_", chomps,
                    StringSub("EDID", "ChompsLewis"),
                    StringSub("FULL", "Chomps Lewis"),
                    FormIdSubrecord("PKID", pack)),
                Record("REFR", targetRef,
                    StringSub("EDID", "ChompsMarker")),
                Record("PACK", pack,
                    StringSub("EDID", "ChompsLewisFollowPackage"),
                    CtdaGetIsId(chomps),
                    // PLDT is 12 bytes in every retail FNV PACK: type 0 (Near Reference) + 3 pad
                    // bytes, the reference at +4, the radius at +8.
                    NearReferencePldt(targetRef, 256),
                    ScriptHeader(1, 4),
                    Sub("SCDA", 0xFF, 0xFF, 0x00, 0x00),
                    FormIdSubrecord("SCRO", targetRef))
            ],
            ["Chomps Lewis"],
            BethesdaGame.FalloutNewVegas);

        var packageRecord = Assert.Single(result.Records, row => row.RecordType == "PACK");
        Assert.Contains("actor-package", packageRecord.Relation, StringComparison.Ordinal);
        Assert.Contains("CTDA", packageRecord.InterestingSubrecords, StringComparison.Ordinal);
        Assert.Contains("PLDT(type=0:NearReference,ref=0xFE000330 (ChompsMarker),radius=256)",
            packageRecord.InterestingSubrecords, StringComparison.Ordinal);

        var packageScript = Assert.Single(result.ScriptBlocks, row => row.RecordType == "PACK");
        Assert.True(packageScript.CompiledSizeMatches);
        var packageRef = Assert.Single(result.ScriptReferences, row => row.ParentRecordType == "PACK");
        Assert.Equal("Resolved", packageRef.Status);
        Assert.Equal("ChompsMarker", packageRef.ResolvedEditorId);
    }

    [Fact]
    public void AnalyzeRecords_AuditsSexConditionsWithoutTreatingEnumAsFormId()
    {
        const uint actorId = 0x00112233;
        const uint infoId = 0xFE000100;

        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp",
            [
                Record("NPC_", actorId,
                    StringSub("EDID", "UlyssesNPC"),
                    StringSub("FULL", "Ulysses")),
                Record("INFO", infoId,
                    CtdaGetIsId(actorId),
                    CtdaGetPCIsSex(0),
                    Sub("TRDT", new byte[24]),
                    StringSub("NAM1", "You are a man."))
            ],
            ["Ulysses"],
            BethesdaGame.FalloutNewVegas);

        var sexCondition = Assert.Single(result.Conditions, row => row.FunctionName == "GetPCIsSex");
        Assert.Equal(0x0083, sexCondition.FunctionIndex);
        Assert.Equal(0u, sexCondition.Parameter1);
        Assert.Equal("Male", sexCondition.Parameter1Label);
        Assert.StartsWith("00", sexCondition.RawBytes, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeRecords_FnvActorValueKeepsEnumDisplayWithoutCreatingActorReference()
    {
        const uint actorId = 5;
        const uint infoId = 0xFE000100;
        var records = new[]
        {
            Record("NPC_", actorId, StringSub("EDID", "ActorFive")),
            Record("INFO", infoId, Ctda(0x00E, actorId))
        };
        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp",
            records,
            ["ActorFive"],
            BethesdaGame.FalloutNewVegas,
            new HashSet<uint> { infoId });

        var info = Assert.Single(result.Records, row => row.FormId == infoId);
        Assert.DoesNotContain("actor-dialogue", info.Relation, StringComparison.Ordinal);
        Assert.DoesNotContain("ActorFive", info.InterestingSubrecords, StringComparison.Ordinal);
        var condition = Assert.Single(result.Conditions, row => row.FormId == infoId);
        Assert.Equal("GetActorValue", condition.FunctionName);
        Assert.Equal("Strength", condition.Parameter1Label);

        var provenance = EsmScriptProvenanceAnalyzer.AnalyzeRecords(records, result, null, null);
        Assert.DoesNotContain(provenance.StateTrace,
            row => row.FormId == infoId && row.Category == "condition-parameter");
        Assert.Contains(provenance.StateTrace,
            row => row.FormId == infoId
                   && row.Category == "condition-raw"
                   && row.LinkedFormId == 0
                   && row.Detail.Contains("p1=0x00000005", StringComparison.Ordinal));
    }

    [Fact]
    public void SkyrimGetVatsValue_DiagnosticsUseParam1ToClassifyParam2()
    {
        Assert.True(EsmScriptDiagnosticsResolvers.IsFormIdConditionParameter(
            BethesdaGame.Skyrim,
            407,
            1,
            0,
            0,
            0));
        Assert.False(EsmScriptDiagnosticsResolvers.IsFormIdConditionParameter(
            BethesdaGame.Skyrim,
            407,
            1,
            0,
            0,
            5));
        Assert.False(EsmScriptDiagnosticsResolvers.IsFormIdConditionParameter(
            BethesdaGame.Skyrim,
            407,
            1,
            0,
            0));
    }

    [Fact]
    public void AnalyzeRecords_ConditionRelationsUseBothTypedSlotsAndFo4ContextOverrides()
    {
        const uint actorId = 5;
        const uint fnvInfoId = 0xFE000101;
        const uint fo4InfoId = 0xFE000102;
        var records = new[]
        {
            Record("NPC_", actorId, StringSub("EDID", "ActorFive")),
            Record("INFO", fnvInfoId, Ctda(0x03C, 0, actorId)),
            Record("INFO", fo4InfoId, Ctda(0x0001, actorId, type: 0x02, length: 32))
        };

        var fnv = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp", records, ["ActorFive"], BethesdaGame.FalloutNewVegas,
            new HashSet<uint> { fnvInfoId });
        var fnvInfo = Assert.Single(fnv.Records, row => row.FormId == fnvInfoId);
        Assert.Contains("actor-dialogue", fnvInfo.Relation, StringComparison.Ordinal);
        Assert.Contains("p2=0x00000005 (ActorFive)", fnvInfo.InterestingSubrecords, StringComparison.Ordinal);
        var fnvProvenance = EsmScriptProvenanceAnalyzer.AnalyzeRecords(records, fnv, null, null);
        var parameter2Link = Assert.Single(fnvProvenance.StateTrace,
            row => row.FormId == fnvInfoId && row.Category == "condition-parameter");
        Assert.Equal(actorId, parameter2Link.LinkedFormId);
        Assert.Contains("linked_slot=p2", parameter2Link.Detail, StringComparison.Ordinal);

        var fo4 = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp", records, ["ActorFive"], BethesdaGame.Fallout4,
            new HashSet<uint> { fo4InfoId });
        var fo4Info = Assert.Single(fo4.Records, row => row.FormId == fo4InfoId);
        Assert.DoesNotContain("actor-dialogue", fo4Info.Relation, StringComparison.Ordinal);
        Assert.DoesNotContain("ActorFive", fo4Info.InterestingSubrecords, StringComparison.Ordinal);
        Assert.Equal("5", Assert.Single(fo4.Conditions, row => row.FormId == fo4InfoId).Parameter1Label);
        var fo4Provenance = EsmScriptProvenanceAnalyzer.AnalyzeRecords(records, fo4, null, null);
        Assert.DoesNotContain(fo4Provenance.StateTrace,
            row => row.FormId == fo4InfoId && row.Category == "condition-parameter");
    }

    [Fact]
    public void AnalyzeRecords_PackageRelationIgnoresNumericConditionValues()
    {
        const uint actorId = 5;
        const uint numericPackId = 0xFE000201;
        const uint formPackId = 0xFE000202;
        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp",
            [
                Record("NPC_", actorId, StringSub("EDID", "ActorFive")),
                Record("PACK", numericPackId, Ctda(0x00E, actorId)),
                Record("PACK", formPackId, CtdaGetIsId(actorId))
            ],
            ["ActorFive"],
            BethesdaGame.FalloutNewVegas);

        Assert.DoesNotContain(result.Records, row => row.FormId == numericPackId);
        Assert.Contains(result.Records,
            row => row.FormId == formPackId
                   && row.Relation.Contains("target-ref-pack", StringComparison.Ordinal));
    }

    [Fact]
    public void AnalyzeRecords_DoesNotInventSpeakerFromAmbiguousOrUnsupportedGetIsId()
    {
        const uint actor1 = 0x00110001;
        const uint actor2 = 0x00110002;
        const uint ambiguousInfoId = 0xFE000301;
        const uint unsupportedInfoId = 0xFE000302;
        const uint negativeInfoId = 0xFE000303;
        var records = new[]
        {
            Record("NPC_", actor1, StringSub("EDID", "ActorOne")),
            Record("NPC_", actor2, StringSub("EDID", "ActorTwo")),
            Record("INFO", ambiguousInfoId, CtdaGetIsId(actor1), CtdaGetIsId(actor2)),
            Record("INFO", unsupportedInfoId, CtdaGetIsId(actor1)),
            Record("INFO", negativeInfoId, Ctda(0x0048, actor1, type: 0x20))
        };

        var fnv = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp", records, ["ActorOne"], BethesdaGame.FalloutNewVegas,
            new HashSet<uint> { ambiguousInfoId, negativeInfoId });
        Assert.Equal(0u, Assert.Single(fnv.Dialogue, row => row.InfoFormId == ambiguousInfoId).SpeakerFormId);
        Assert.Equal(0u, Assert.Single(fnv.Dialogue, row => row.InfoFormId == negativeInfoId).SpeakerFormId);

        var unsupported = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp", records, ["fixture"], BethesdaGame.Unknown,
            new HashSet<uint> { unsupportedInfoId });
        Assert.Equal(0u,
            Assert.Single(unsupported.Dialogue, row => row.InfoFormId == unsupportedInfoId).SpeakerFormId);
    }

    [Theory]
    [InlineData(BethesdaGame.FalloutNewVegas, "GetItemCount", "QuestTarget")]
    [InlineData(BethesdaGame.Fallout4, "GetItemCount", "QuestTarget")]
    [InlineData(BethesdaGame.Fallout76, "GetItemCount", "QuestTarget")]
    [InlineData(BethesdaGame.Starfield, "GetItemCount", "QuestTarget")]
    [InlineData(BethesdaGame.Unknown, "Func0x002F", "")]
    public void AnalyzeRecords_UsesGameKeyedConditionNamesAndKinds(
        BethesdaGame game,
        string expectedName,
        string expectedParameterLabel)
    {
        const uint infoId = 0xFE000100;
        const uint targetId = 0xFE000200;
        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp",
            [
                Record("QUST", targetId, StringSub("EDID", "QuestTarget")),
                Record("INFO", infoId, Ctda(0x02F, targetId,
                    length: game is BethesdaGame.Fallout4 or BethesdaGame.Fallout76 or
                        BethesdaGame.Starfield
                        ? 32
                        : 28))
            ],
            ["fixture"],
            game,
            new HashSet<uint> { infoId });

        var condition = Assert.Single(result.Conditions, row => row.FormId == infoId);
        Assert.Equal(expectedName, condition.FunctionName);
        Assert.Equal(expectedParameterLabel, condition.Parameter1Label);
    }

    [Fact]
    public void AnalyzeRecords_Fallout76HighConditionIndicesUseExactRawMetadata()
    {
        const uint rawInfoId = 0xFE000100;
        const uint questInfoId = 0xFE000101;
        const uint questId = 0xFE000200;
        var records = new[]
        {
            Record("QUST", questId, StringSub("EDID", "QuestTarget")),
            Record("INFO", rawInfoId, Ctda(5000, questId, length: 32)),
            Record("INFO", questInfoId, Ctda(5004, questId, length: 32))
        };
        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp",
            records,
            ["fixture"],
            BethesdaGame.Fallout76,
            new HashSet<uint> { rawInfoId, questInfoId });

        var raw = Assert.Single(result.Conditions, row => row.FormId == rawInfoId);
        Assert.Equal("IsInAirOrFloating", raw.FunctionName);
        Assert.Equal(string.Empty, raw.Parameter1Label);
        var quest = Assert.Single(result.Conditions, row => row.FormId == questInfoId);
        Assert.Equal("PlayerHasQuest", quest.FunctionName);
        Assert.Equal("QuestTarget", quest.Parameter1Label);

        var provenance = EsmScriptProvenanceAnalyzer.AnalyzeRecords(records, result, null, null);
        Assert.DoesNotContain(provenance.StateTrace,
            row => row.FormId == rawInfoId && row.Category == "condition-parameter");
        Assert.Contains(provenance.StateTrace,
            row => row.FormId == rawInfoId && row.Category == "condition-raw" && row.LinkedFormId == 0);
        Assert.Contains(provenance.StateTrace,
            row => row.FormId == questInfoId && row.Category == "condition-parameter"
                                             && row.LinkedFormId == questId);
    }

    [Fact]
    public void AnalyzeRecords_StarfieldUsesItsConditionOnlyUnionKinds()
    {
        const uint infoId = 0xFE000100;
        const uint actorValueId = 0xFE000200;
        var records = new[]
        {
            Record("AVIF", actorValueId, StringSub("EDID", "Health")),
            Record("INFO", infoId, Ctda(14, actorValueId, length: 32))
        };
        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp",
            records,
            ["fixture"],
            BethesdaGame.Starfield,
            new HashSet<uint> { infoId });

        var condition = Assert.Single(result.Conditions, row => row.FormId == infoId);
        Assert.Equal("GetValue", condition.FunctionName);
        Assert.Equal("Health", condition.Parameter1Label);

        var provenance = EsmScriptProvenanceAnalyzer.AnalyzeRecords(records, result, null, null);
        Assert.Contains(provenance.StateTrace,
            row => row.FormId == infoId && row.Category == "condition-parameter"
                                        && row.LinkedFormId == actorValueId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnalyzeRecords_ConditionCsv_DistinguishesNumericAndGlobalComparisonUnion(bool bigEndian)
    {
        const uint infoId = 0xFE000100;
        const uint globalFormId = 0x7FC12345;
        const uint numericBits = 0x3FA00000; // 1.25f
        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp",
            [
                Record("GLOB", globalFormId, StringSub("EDID", "UseGlobalFixture")),
                Record("INFO", infoId,
                    CtdaComparison(0x00, numericBits, bigEndian),
                    CtdaComparison(0x04, globalFormId, bigEndian))
            ],
            ["fixture"],
            BethesdaGame.FalloutNewVegas,
            new HashSet<uint> { infoId });

        var conditions = result.Conditions.Where(row => row.FormId == infoId).ToArray();
        Assert.Equal(2, conditions.Length);

        var numeric = conditions[0];
        Assert.False(numeric.UsesGlobalComparison);
        Assert.Equal(numericBits, numeric.ComparisonRawBits);
        Assert.Equal(1.25f, numeric.NumericComparisonValue);
        Assert.Null(numeric.ComparisonGlobalFormId);
        Assert.Equal(string.Empty, numeric.ComparisonGlobalLabel);

        var global = conditions[1];
        Assert.True(global.UsesGlobalComparison);
        Assert.Equal(globalFormId, global.ComparisonRawBits);
        Assert.Null(global.NumericComparisonValue);
        Assert.Equal(globalFormId, global.ComparisonGlobalFormId);
        Assert.Equal("UseGlobalFixture", global.ComparisonGlobalLabel);

        var csvLines = EsmScriptDiagnosticsCsvWriter.BuildConditionsCsv(result)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, csvLines.Length);
        var headers = csvLines[0].Split(',');
        Assert.Equal("comparison_value", headers[9]);
        Assert.Equal("parameter1", headers[10]);
        Assert.Equal("raw_bytes", headers[17]);
        Assert.Equal(
            [
                "comparison_kind", "comparison_raw_bits", "comparison_global_form_id", "comparison_global_label",
                "parameter3", "reference_storage_is_semantic", "semantic_reference_form_id", "ctda_body_length",
                "layout_status"
            ],
            headers[18..]);

        var numericFields = csvLines[1].Split(',');
        Assert.Equal("1.25", numericFields[9]);
        Assert.Equal("numeric", numericFields[18]);
        Assert.Equal("0x3FA00000", numericFields[19]);
        Assert.Equal(string.Empty, numericFields[20]);
        Assert.Equal(string.Empty, numericFields[21]);

        var globalFields = csvLines[2].Split(',');
        Assert.Equal(string.Empty, globalFields[9]);
        Assert.Equal("global_form_id", globalFields[18]);
        Assert.Equal("0x7FC12345", globalFields[19]);
        Assert.Equal("0x7FC12345", globalFields[20]);
        Assert.Equal("UseGlobalFixture", globalFields[21]);
    }

    [Fact]
    public void AnalyzeRecords_TargetNormalizationFindsChompsLewisTravelPackage()
    {
        const uint chomps = 0x00100020;
        const uint travelPackage = 0xFE000220;

        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp",
            [
                Record("NPC_", chomps,
                    StringSub("EDID", "QJChompsLewis"),
                    StringSub("FULL", "Chomps Lewis")),
                Record("PACK", travelPackage,
                    StringSub("EDID", "QJChompsLewisTravelPackage"))
            ],
            ["Chomps Lewis"],
            BethesdaGame.FalloutNewVegas);

        Assert.Contains(result.Records,
            row => row.RecordType == "PACK" &&
                   row.FormId == travelPackage &&
                   row.Relation.Contains("target-ref-pack", StringComparison.Ordinal));
    }

    [Fact]
    public void Provenance_FlagsNonzeroSourceScroEmittedAsNull()
    {
        const uint scriptFormId = 0xFE000100;
        const uint sourceRef = 0x00123456;

        var generated = GeneratedUlyssesScriptRecords(scriptFormId, FormIdSubrecord("SCRO", 0));
        var diagnostics = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp", generated, ["Ulysses"], BethesdaGame.FalloutNewVegas);
        var provenance = EsmScriptProvenanceAnalyzer.AnalyzeRecords(
            generated,
            diagnostics,
            new RecordCollection
            {
                Scripts =
                [
                    new ScriptRecord
                    {
                        FormId = 0x00133FD7,
                        EditorId = "UlyssesScript",
                        ReferencedObjects = [sourceRef]
                    }
                ],
                FormIdToEditorId = new Dictionary<uint, string> { [sourceRef] = "FollowerSwitchAggressive" }
            },
            null);

        var row = Assert.Single(provenance.SourceVsEmittedRefs);
        Assert.Equal("ConverterNulledNonZeroSource", row.Classification);
        Assert.Equal(sourceRef, row.SourceRawValue);
        Assert.Equal(0u, row.EmittedRawValue);
    }

    [Fact]
    public void Provenance_ClassifiesTrueSourceNullScro()
    {
        const uint scriptFormId = 0xFE000100;

        var generated = GeneratedUlyssesScriptRecords(scriptFormId, FormIdSubrecord("SCRO", 0));
        var diagnostics = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp", generated, ["Ulysses"], BethesdaGame.FalloutNewVegas);
        var provenance = EsmScriptProvenanceAnalyzer.AnalyzeRecords(
            generated,
            diagnostics,
            new RecordCollection
            {
                Scripts =
                [
                    new ScriptRecord
                    {
                        FormId = 0x00133FD7,
                        EditorId = "UlyssesScript",
                        ReferencedObjects = [0]
                    }
                ]
            },
            null);

        var row = Assert.Single(provenance.SourceVsEmittedRefs);
        Assert.Equal("SourceNull", row.Classification);
    }

    [Fact]
    public void Provenance_FlagsSourceScrvEmittedAsScro()
    {
        const uint scriptFormId = 0xFE000100;

        var generated = GeneratedUlyssesScriptRecords(scriptFormId, FormIdSubrecord("SCRO", 2));
        var diagnostics = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp", generated, ["Ulysses"], BethesdaGame.FalloutNewVegas);
        var provenance = EsmScriptProvenanceAnalyzer.AnalyzeRecords(
            generated,
            diagnostics,
            new RecordCollection
            {
                Scripts =
                [
                    new ScriptRecord
                    {
                        FormId = 0x00133FD7,
                        EditorId = "UlyssesScript",
                        ReferencedObjects = [0x80000002]
                    }
                ]
            },
            null);

        var row = Assert.Single(provenance.SourceVsEmittedRefs);
        Assert.Equal("SourceScrvEmittedAsScro", row.Classification);
    }

    [Fact]
    public void Provenance_FlagsSourceResultScriptEmittedAsPlaceholderOnly()
    {
        const uint actorId = 0x00112233;
        const uint infoId = 0xFE000100;

        var generated =
            new[]
            {
                Record("NPC_", actorId,
                    StringSub("EDID", "Ulysses"),
                    StringSub("FULL", "Ulysses")),
                Record("INFO", infoId,
                    CtdaGetIsId(actorId),
                    ScriptHeader(0, 0),
                    Sub("NEXT"),
                    ScriptHeader(0, 0),
                    Sub("TRDT", new byte[24]),
                    StringSub("NAM1", "Travel with me."))
            };

        var diagnostics = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp", generated, ["Ulysses"], BethesdaGame.FalloutNewVegas);
        var provenance = EsmScriptProvenanceAnalyzer.AnalyzeRecords(
            generated,
            diagnostics,
            new RecordCollection
            {
                Dialogues =
                [
                    new DialogueRecord
                    {
                        FormId = 0x0010ABCD,
                        SpeakerFormId = actorId,
                        Responses = [new DialogueResponse { Text = "Travel with me." }],
                        ResultScripts =
                        [
                            new DialogueResultScript
                            {
                                CompiledData = [0xFF, 0xFF, 0x00, 0x00],
                                ReferencedObjects = [0x000B16D0]
                            }
                        ],
                        HasResultScript = true
                    }
                ]
            },
            null);

        var row = Assert.Single(provenance.ResultScripts);
        Assert.Equal("EmittedPlaceholderOnly", row.Classification);
        Assert.Equal(0x0010ABCDu, row.SourceInfoFormId);
    }

    [Fact]
    public void Provenance_MatchesInfoByQuestTopicSpeakerAndResponseBeforeLooseResponseText()
    {
        const uint actorId = 0x00112233;
        const uint infoId = 0xFE000100;
        const uint topicId = 0xFE000200;
        const uint questId = 0xFE000300;
        byte[] bytecode = [0xFF, 0xFF, 0x00, 0x00];

        var generated =
            new[]
            {
                Record("NPC_", actorId,
                    StringSub("EDID", "Ulysses"),
                    StringSub("FULL", "Ulysses")),
                Record("INFO", infoId,
                    FormIdSubrecord("TPIC", topicId),
                    FormIdSubrecord("QSTI", questId),
                    CtdaGetIsId(actorId),
                    ScriptHeader(0, 0),
                    Sub("NEXT"),
                    Sub("TRDT", new byte[24]),
                    StringSub("NAM1", "Travel with me."))
            };

        var diagnostics = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp", generated, ["Ulysses"], BethesdaGame.FalloutNewVegas);
        var provenance = EsmScriptProvenanceAnalyzer.AnalyzeRecords(
            generated,
            diagnostics,
            new RecordCollection
            {
                Dialogues =
                [
                    new DialogueRecord
                    {
                        FormId = 0x00100001,
                        TopicFormId = 0x00ABCDEF,
                        QuestFormId = questId,
                        SpeakerFormId = actorId,
                        Responses = [new DialogueResponse { Text = "Travel with me." }],
                        ResultScripts =
                        [
                            new DialogueResultScript
                            {
                                CompiledData = bytecode
                            }
                        ],
                        HasResultScript = true
                    },
                    new DialogueRecord
                    {
                        FormId = 0x00100002,
                        TopicFormId = topicId,
                        QuestFormId = questId,
                        SpeakerFormId = actorId,
                        Responses = [new DialogueResponse { Text = "Travel with me." }],
                        ResultScripts =
                        [
                            new DialogueResultScript
                            {
                                CompiledData = bytecode
                            }
                        ],
                        HasResultScript = true
                    }
                ]
            },
            null);

        var row = Assert.Single(provenance.ResultScripts);
        Assert.Equal("info-quest-topic-speaker-response", row.MatchStrategy);
        Assert.Equal(0x00100002u, row.SourceInfoFormId);
    }

    [Fact]
    public void Provenance_TreatsCanonicalEmptyInfoEndBlockAsPreserved()
    {
        const uint actorId = 0x00112233;
        const uint infoId = 0xFE000100;
        const uint sourceInfoId = 0x0010ABCD;
        const uint scriptRef = 0x000B16D0;
        byte[] bytecode = [0xFF, 0xFF, 0x00, 0x00];

        var generated =
            new[]
            {
                Record("NPC_", actorId,
                    StringSub("EDID", "Ulysses"),
                    StringSub("FULL", "Ulysses")),
                Record("INFO", infoId,
                    CtdaGetIsId(actorId),
                    ScriptHeader(1, (uint)bytecode.Length),
                    Sub("SCDA", bytecode),
                    FormIdSubrecord("SCRO", scriptRef),
                    Sub("NEXT"),
                    ScriptHeader(0, 0),
                    Sub("TRDT", new byte[24]),
                    StringSub("NAM1", "Travel with me."))
            };

        var diagnostics = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp", generated, ["Ulysses"], BethesdaGame.FalloutNewVegas);
        var provenance = EsmScriptProvenanceAnalyzer.AnalyzeRecords(
            generated,
            diagnostics,
            new RecordCollection
            {
                Dialogues =
                [
                    new DialogueRecord
                    {
                        FormId = sourceInfoId,
                        SpeakerFormId = actorId,
                        Responses = [new DialogueResponse { Text = "Travel with me." }],
                        ResultScripts =
                        [
                            new DialogueResultScript
                            {
                                CompiledData = bytecode,
                                ReferencedObjects = [scriptRef]
                            }
                        ],
                        HasResultScript = true
                    }
                ]
            },
            null);

        var row = Assert.Single(provenance.ResultScripts);
        Assert.Equal("Preserved", row.Classification);
        Assert.All(provenance.SourceVsEmittedRefs, reference => Assert.Equal("Resolved", reference.Classification));
    }

    [Fact]
    public void Provenance_EndianProbeClassifiesBigEndianBytecodeNeedingSwap()
    {
        const uint actorId = 0x00112233;
        const uint infoId = 0xFE000100;

        var generated =
            new[]
            {
                Record("NPC_", actorId,
                    StringSub("EDID", "Ulysses"),
                    StringSub("FULL", "Ulysses")),
                Record("INFO", infoId,
                    CtdaGetIsId(actorId),
                    ScriptHeader(0, 15),
                    Sub("SCDA",
                        0x00, 0x15, 0x00, 0x0B,
                        0x66,
                        0x00, 0x00,
                        0x00, 0x06,
                        0x20, 0x6E, 0x00, 0x00, 0x00, 0x01),
                    Sub("NEXT"),
                    Sub("TRDT", new byte[24]),
                    StringSub("NAM1", "Travel with me."))
            };

        var diagnostics = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp", generated, ["Ulysses"], BethesdaGame.FalloutNewVegas);
        var provenance = EsmScriptProvenanceAnalyzer.AnalyzeRecords(generated, diagnostics, null, null);

        var row = Assert.Single(provenance.BytecodeEndianProbes, r => r.Origin == "Emitted");
        Assert.Equal("WouldSwapFix", row.Classification);
        Assert.Equal("0x1500", row.LittleEndianOpcode);
        Assert.Equal("0x0015", row.BigEndianOpcode);
    }

    [Fact]
    public void Diagnostics_And_Provenance_Ignore_Nonsemantic_Fnv_Reference_Storage()
    {
        const uint actorId = 0x00112233;
        const uint markerId = 0x00114455;
        const uint infoId = 0xFE000100;
        var generated = new[]
        {
            Record("NPC_", actorId, StringSub("EDID", "Ulysses"), StringSub("FULL", "Ulysses")),
            Record("REFR", markerId, StringSub("EDID", "SemanticMarker")),
            Record("INFO", infoId,
                CtdaReference(0x0001, 4, actorId),
                CtdaReference(0x006A, 2, actorId),
                CtdaReference(0x0001, 2, markerId),
                CtdaReference(0x0001, 2, 0))
        };

        var diagnostics = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp",
            generated,
            ["Ulysses"],
            BethesdaGame.FalloutNewVegas,
            new HashSet<uint> { infoId });

        var info = Assert.Single(diagnostics.Records, row => row.FormId == infoId);
        Assert.DoesNotContain("actor-dialogue", info.Relation, StringComparison.Ordinal);
        var conditions = diagnostics.Conditions.Where(row => row.FormId == infoId).ToArray();
        Assert.Equal(4, conditions.Length);
        Assert.Equal(actorId, conditions[0].ReferenceStorage);
        Assert.Equal(string.Empty, conditions[0].SemanticReferenceLabel);
        Assert.Equal(actorId, conditions[1].ReferenceStorage);
        Assert.Equal(string.Empty, conditions[1].SemanticReferenceLabel);
        Assert.Equal("SemanticMarker", conditions[2].SemanticReferenceLabel);
        Assert.False(conditions[0].ReferenceStorageIsSemantic);
        Assert.False(conditions[1].ReferenceStorageIsSemantic);
        Assert.True(conditions[2].ReferenceStorageIsSemantic);
        Assert.Equal(markerId, conditions[2].SemanticReferenceFormId);
        Assert.True(conditions[3].ReferenceStorageIsSemantic);
        Assert.Equal<uint?>(0, conditions[3].ReferenceStorage);
        Assert.Null(conditions[3].SemanticReferenceFormId);

        var provenance = EsmScriptProvenanceAnalyzer.AnalyzeRecords(generated, diagnostics, null, null);
        var linkedReference = Assert.Single(
            provenance.StateTrace,
            row => row.Category == "condition-reference");
        Assert.Equal(markerId, linkedReference.LinkedFormId);
        Assert.Contains(
            provenance.StateTrace,
            row => row.Detail.Contains($"reference_storage=0x{actorId:X8}", StringComparison.Ordinal));
        Assert.Contains(
            provenance.StateTrace,
            row => row.Category == "condition-raw" &&
                   row.Detail.Contains("ref=0x00000000", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(BethesdaGame.FalloutNewVegas, false)]
    [InlineData(BethesdaGame.Fallout3, true)]
    [InlineData(BethesdaGame.Unknown, false)]
    public void Diagnostics_And_Provenance_Use_Explicit_Game_For_Fnv_Reference_Exceptions(
        BethesdaGame game,
        bool expectedSemanticReference)
    {
        const uint actorId = 0x00112233;
        const uint infoId = 0xFE000100;
        var generated = new[]
        {
            Record("NPC_", actorId, StringSub("EDID", "Ulysses"), StringSub("FULL", "Ulysses")),
            Record("INFO", infoId, CtdaReference(0x006A, 2, actorId))
        };

        var diagnostics = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "generated.esp",
            generated,
            ["Ulysses"],
            game,
            new HashSet<uint> { infoId });

        Assert.Equal(game, diagnostics.Game);
        var info = Assert.Single(diagnostics.Records, row => row.FormId == infoId);
        Assert.Equal(expectedSemanticReference,
            info.Relation.Contains("actor-dialogue", StringComparison.Ordinal));

        var condition = Assert.Single(diagnostics.Conditions, row => row.FormId == infoId);
        Assert.Equal(actorId, condition.ReferenceStorage);
        Assert.Equal(game is BethesdaGame.Unknown ? "unsupported_game" : "valid", condition.LayoutStatus);
        Assert.Equal(expectedSemanticReference, condition.ReferenceStorageIsSemantic);
        Assert.Equal(expectedSemanticReference ? actorId : null, condition.SemanticReferenceFormId);
        Assert.Equal(expectedSemanticReference ? "Ulysses" : string.Empty,
            condition.SemanticReferenceLabel);

        var provenance = EsmScriptProvenanceAnalyzer.AnalyzeRecords(generated, diagnostics, null, null);
        var referenceTraces = provenance.StateTrace
            .Where(row => row.Category == "condition-reference")
            .ToArray();
        Assert.Equal(expectedSemanticReference ? 1 : 0, referenceTraces.Length);
        if (expectedSemanticReference)
        {
            Assert.Equal(actorId, referenceTraces[0].LinkedFormId);
        }

        var parameterTrace = Assert.Single(provenance.StateTrace,
            row => row.FormId == infoId &&
                   row.Category == (game == BethesdaGame.Unknown ? "condition-invalid" : "condition-raw"));
        if (game != BethesdaGame.Unknown)
        {
            Assert.Equal(!expectedSemanticReference,
                parameterTrace.Detail.Contains("reference_storage=", StringComparison.Ordinal));
        }

        var csv = EsmScriptDiagnosticsCsvWriter.BuildConditionsCsv(diagnostics);
        Assert.StartsWith(
            "target,relation,record_type,form_id,editor_id,condition_index,function_name,function_index,type,comparison_value,parameter1,parameter1_label,parameter2,parameter2_label,run_on,reference_storage,semantic_reference_label,raw_bytes",
            csv,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Diagnostics_AcceptOnlyExactCtdaWidths_AndPreserveAbsentTailWords(bool bigEndian)
    {
        const uint infoId = 0x00123456;
        ParsedSubrecord[] conditions =
        [
            CtdaWithWidth(20, bigEndian),
            CtdaWithWidth(21, bigEndian),
            CtdaWithWidth(24, bigEndian),
            CtdaWithWidth(29, bigEndian),
            CtdaWithWidth(28, bigEndian),
            CtdaWithWidth(31, bigEndian),
            CtdaWithWidth(32, bigEndian)
        ];
        var generated = new[]
        {
            Record("INFO", infoId, [StringSub("EDID", "WidthProbe"), .. conditions])
        };

        var diagnostics = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "synthetic.esm",
            generated,
            [],
            BethesdaGame.FalloutNewVegas,
            new HashSet<uint> { infoId });

        var rows = diagnostics.Conditions.Where(row => row.FormId == infoId).ToArray();
        Assert.Equal(Enumerable.Range(1, 7), rows.Select(row => row.ConditionIndex));
        Assert.Equal(
            [
                "valid", "unsupported_length", "valid", "unsupported_length", "valid",
                "unsupported_length", "game_width_mismatch"
            ],
            rows.Select(row => row.LayoutStatus));
        Assert.Null(rows[0].RunOn);
        Assert.Null(rows[0].ReferenceStorage);
        Assert.Equal<uint?>(0, rows[2].RunOn);
        Assert.Null(rows[2].ReferenceStorage);
        Assert.Equal<uint?>(0, rows[4].RunOn);
        Assert.Equal<uint?>(0, rows[4].ReferenceStorage);
        Assert.Null(rows[0].Parameter3);
        Assert.Equal(0, rows[6].Parameter3);

        var csvLines = EsmScriptDiagnosticsCsvWriter.BuildConditionsCsv(diagnostics)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var twentyByteColumns = csvLines[1].Split(',');
        var twentyFourByteColumns = csvLines[3].Split(',');
        var twentyEightByteColumns = csvLines[5].Split(',');
        Assert.Equal(string.Empty, twentyByteColumns[14]);
        Assert.Equal(string.Empty, twentyByteColumns[15]);
        Assert.Equal("0", twentyFourByteColumns[14]);
        Assert.Equal(string.Empty, twentyFourByteColumns[15]);
        Assert.Equal("0", twentyEightByteColumns[14]);
        Assert.Equal("0x00000000", twentyEightByteColumns[15]);

        var provenance = EsmScriptProvenanceAnalyzer.AnalyzeRecords(generated, diagnostics, null, null);
        var rawRows = provenance.StateTrace
            .Where(row => row.FormId == infoId && row.Category == "condition-raw")
            .ToArray();
        Assert.Equal(3, rawRows.Length);
        Assert.Contains("runOn=absent reference_storage=absent", rawRows[0].Detail, StringComparison.Ordinal);
        Assert.Contains("runOn=0 reference_storage=absent", rawRows[1].Detail, StringComparison.Ordinal);
        Assert.Contains("runOn=0 reference_storage=0x00000000", rawRows[2].Detail, StringComparison.Ordinal);
        Assert.Equal(4, provenance.StateTrace.Count(row =>
            row.FormId == infoId && row.Category == "condition-invalid"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Diagnostics_ModernCtdaPreservesSignedParameter3(bool bigEndian)
    {
        const uint infoId = 0x00123457;
        var generated = new[]
        {
            Record("INFO", infoId,
                CtdaWithWidth(32, bigEndian),
                CtdaWithWidth(32, bigEndian, -42))
        };

        var diagnostics = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "synthetic.esm", generated, [], BethesdaGame.Skyrim,
            new HashSet<uint> { infoId });

        var rows = diagnostics.Conditions.Where(row => row.FormId == infoId).ToArray();
        Assert.Equal([0, -42], rows.Select(row => row.Parameter3));
        Assert.All(rows, row => Assert.Equal("valid", row.LayoutStatus));

        var csv = EsmScriptDiagnosticsCsvWriter.BuildConditionsCsv(diagnostics);
        Assert.Contains(
            ",parameter3,reference_storage_is_semantic,semantic_reference_form_id,ctda_body_length,layout_status",
            csv.Split('\n')[0], StringComparison.Ordinal);
        var provenance = EsmScriptProvenanceAnalyzer.AnalyzeRecords(generated, diagnostics, null, null);
        Assert.Contains(provenance.StateTrace,
            row => row.Category == "condition-raw" && row.Detail.Contains("p3=0", StringComparison.Ordinal));
        Assert.Contains(provenance.StateTrace,
            row => row.Category == "condition-raw" && row.Detail.Contains("p3=-42", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A TERM in the subrecord order retail Dead Money stores (0x01011D70 / 0x0100DAD2): item 1 "Yes" carries
    ///     RNAM, ANAM 0x03, a script (SCHR, SCDA, SCTX, SCRO) and one CTDA; item 2 "No" carries a display note, a
    ///     sub-terminal, an empty SCHR and an OR-joined pair of CTDAs. Analyzed as an explicit record only.
    /// </summary>
    private static EsmScriptDiagnosticsResult AnalyzeSyntheticTerminal()
    {
        Assert.True(TerminalSource.Length > 180, "The fixture source must exceed the 180-character preview.");
        return EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "DeadMoney.esm",
            [
                Record("REFR", TerminalRefId, StringSub("EDID", "VaultMainTerminalREF")),
                Record("NOTE", TerminalNoteId, StringSub("EDID", "VaultPasswordNote")),
                Record("TERM", SubTerminalId, StringSub("EDID", "VaultSubTerminal")),
                Record("TERM", TerminalId,
                    StringSub("EDID", "VaultRetrieveTerminal"),
                    Sub("DNAM", 0x02, 0x00, 0x00, 0x00),
                    StringSub("ITXT", "Yes"),
                    StringSub("RNAM", "Downloading module..."),
                    Sub("ANAM", 0x03),
                    ScriptHeader(1, 4),
                    Sub("SCDA", 0x23, 0x12, 0x00, 0x00),
                    StringSub("SCTX", TerminalSource),
                    FormIdSubrecord("SCRO", TerminalRefId),
                    Ctda(0x35, TerminalRefId, 4, 0x20),
                    StringSub("ITXT", "No"),
                    StringSub("RNAM", "Canceling request..."),
                    Sub("ANAM", 0x01),
                    FormIdSubrecord("INAM", TerminalNoteId),
                    FormIdSubrecord("TNAM", SubTerminalId),
                    ScriptHeader(0, 0),
                    Ctda(0x35, TerminalRefId, 2, 0x21),
                    Ctda(0x35, TerminalRefId, 3, 0x20))
            ],
            [],
            BethesdaGame.FalloutNewVegas,
            new HashSet<uint> { TerminalId });
    }

    /// <summary>
    ///     Minimal RFC 4180 reader for the diagnostics CSVs: quoted fields may hold commas, doubled quotes and line
    ///     breaks (kept verbatim); an unquoted CR is part of the row terminator.
    /// </summary>
    private static List<string[]> ParseCsv(string text)
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

    private static ParsedMainRecord Record(string signature, uint formId, params ParsedSubrecord[] subrecords)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = signature,
                FormId = formId,
                Version = 0x000F
            },
            Subrecords = [.. subrecords]
        };
    }

    private static ParsedSubrecord Sub(string signature, params byte[] data)
    {
        return new ParsedSubrecord
        {
            Signature = signature,
            Data = data
        };
    }

    private static ParsedSubrecord StringSub(string signature, string value)
    {
        var data = new byte[value.Length + 1];
        Encoding.Latin1.GetBytes(value, data);
        return Sub(signature, data);
    }

    private static ParsedSubrecord FormIdSubrecord(string signature, uint formId)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, formId);
        return Sub(signature, data);
    }

    private static ParsedSubrecord NearReferencePldt(uint referenceFormId, int radius)
    {
        var data = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), referenceFormId);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), radius);
        return Sub("PLDT", data);
    }

    private static ParsedSubrecord ScriptHeader(uint refCount, uint compiledSize)
    {
        var data = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), refCount);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), compiledSize);
        data[18] = 1;
        return Sub("SCHR", data);
    }

    private static ParsedSubrecord CtdaGetIsId(uint actorFormId)
    {
        var data = new byte[28];
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), 1.0f);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), 0x48);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), actorFormId);
        return Sub("CTDA", data);
    }

    private static ParsedSubrecord CtdaGetPCIsSex(uint sex)
    {
        var data = new byte[28];
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), 1.0f);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), 0x83);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), sex);
        return Sub("CTDA", data);
    }

    private static ParsedSubrecord Ctda(
        ushort functionIndex,
        uint parameter1,
        uint parameter2 = 0,
        byte type = 0,
        uint runOn = 0,
        int length = 28)
    {
        var data = new byte[length];
        data[0] = type;
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), 1.0f);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), functionIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), parameter1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), parameter2);
        if (length >= 24)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), runOn);
        }

        return Sub("CTDA", data);
    }

    private static ParsedSubrecord CtdaComparison(byte type, uint comparisonRawBits, bool bigEndian)
    {
        var data = new byte[28];
        data[0] = type;
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), comparisonRawBits);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8), 1);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), comparisonRawBits);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), 1);
        }

        return new ParsedSubrecord
        {
            Signature = "CTDA",
            Data = data,
            BigEndian = bigEndian
        };
    }

    private static ParsedSubrecord CtdaReference(ushort functionIndex, uint runOn, uint reference)
    {
        var data = new byte[28];
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), 1.0f);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), functionIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), runOn);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), reference);
        return Sub("CTDA", data);
    }

    private static ParsedSubrecord CtdaWithWidth(int length, bool bigEndian, int parameter3 = 0)
    {
        var data = new byte[length];
        if (length >= 8)
        {
            if (bigEndian)
            {
                BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(4, 4), 1f);
            }
            else
            {
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4, 4), 1f);
            }
        }

        if (length >= 10)
        {
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8, 2), 1);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8, 2), 1);
            }
        }

        if (length >= 32)
        {
            if (bigEndian)
            {
                BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(28, 4), parameter3);
            }
            else
            {
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28, 4), parameter3);
            }
        }

        return new ParsedSubrecord
        {
            Signature = "CTDA",
            Data = data,
            BigEndian = bigEndian
        };
    }

    private static ParsedMainRecord[] GeneratedUlyssesScriptRecords(uint scriptFormId, params ParsedSubrecord[] refs)
    {
        var subs = new List<ParsedSubrecord>
        {
            StringSub("EDID", "UlyssesScript"),
            ScriptHeader((uint)refs.Length, 0)
        };
        subs.AddRange(refs);

        return
        [
            Record("NPC_", 0xFE000010,
                StringSub("EDID", "Ulysses"),
                StringSub("FULL", "Ulysses"),
                FormIdSubrecord("SCRI", scriptFormId)),
            Record("SCPT", scriptFormId, [.. subs])
        ];
    }

    private static byte[] BuildTes4Plugin(float version)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write("TES4"u8.ToArray());
        writer.Write(18u); // HEDR subrecord header (6) + body (12)
        writer.Write(0u); // flags
        writer.Write(0u); // form ID
        writer.Write(0u); // version-control info 1
        writer.Write((ushort)0); // form version
        writer.Write((ushort)0); // version-control info 2
        writer.Write("HEDR"u8.ToArray());
        writer.Write((ushort)12);
        writer.Write(version);
        writer.Write(0u); // record count
        writer.Write(0x800u); // next object ID
        writer.Flush();
        return stream.ToArray();
    }
}