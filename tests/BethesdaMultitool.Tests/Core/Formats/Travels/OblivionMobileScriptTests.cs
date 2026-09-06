using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BethesdaMultitool.Core.Formats.Travels.OblivionMobile;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     Synthetic vectors for <see cref="OblivionMobileScript" /> and
///     <see cref="OblivionMobileLang" />, shaped after the 32 retail <c>.scr</c> and 13
///     <c>lang_N.txt</c> files surveyed 2026-09-05.
///     <para>
///         Three things carry the weight here and none of them shows up in a "does it parse" check.
///         (1) A text reference is TWO bytes wide in code and ONE byte wide inside a definition
///         block — both branches of both are exercised below, because reading the code form as one
///         byte still decodes the first few instructions before it desyncs. (2) Opcode 34's width
///         depends on its sub-command byte, so one fixture puts all three width classes in a single
///         chunk, where a wrong width shows up as a chunk that does not tile. (3) A chunk may carry
///         decodable bytes AFTER its RET — retail does, once — and those are counted, not thrown on.
///     </para>
/// </summary>
public sealed class OblivionMobileScriptTests
{
    private static (byte Id, byte[] Code) Chunk(byte id, params byte[] code)
    {
        return (id, code);
    }

    /// <summary>
    ///     Assembles a script exactly as the format tiles it: the u8 label count, the label table in
    ///     DESCENDING id order with absolute offsets, the definition bytes, then each chunk behind
    ///     its <c>00 &lt;id&gt; 01</c> marker. Chunks are given in ascending id order, which is the
    ///     order their code appears in.
    /// </summary>
    private static byte[] BuildScript(IReadOnlyList<(byte Id, byte[] Code)> chunks, params byte[] definitions)
    {
        var headerLength = 1 + (OblivionMobileScript.LabelEntryLength * chunks.Count);
        var offsets = new int[chunks.Count];
        var cursor = headerLength + definitions.Length;
        for (var i = 0; i < chunks.Count; i++)
        {
            cursor += OblivionMobileScript.MarkerLength;
            offsets[i] = cursor;
            cursor += chunks[i].Code.Length;
        }

        var bytes = new List<byte>(cursor) { (byte)chunks.Count };
        for (var i = chunks.Count - 1; i >= 0; i--)
        {
            bytes.Add(chunks[i].Id);
            bytes.Add((byte)(offsets[i] >> 8));
            bytes.Add((byte)offsets[i]);
        }

        bytes.AddRange(definitions);
        for (var i = 0; i < chunks.Count; i++)
        {
            bytes.Add(OblivionMobileScript.MarkerLead);
            bytes.Add(chunks[i].Id);
            bytes.Add(OblivionMobileScript.MarkerTail);
            bytes.AddRange(chunks[i].Code);
        }

        return bytes.ToArray();
    }

    /// <summary>A one-label script: a tile-attribute block, a WAIT, a SETCOLOR and the RET.</summary>
    private static byte[] BuildMinimalScript()
    {
        return BuildScript(
            new[] { Chunk(1, 0x0B, 0x00, 0xC8, 0x40, 0x12, 0x34, 0x56, 0x02) },
            0x1E, 0x06, 0x00, 0x01, 0x02, 0x00, 0x20, 0x1F);
    }

    /// <summary>
    ///     The happy path: one label, one block, three instructions, and every byte of the file
    ///     accounted for by header + table + block + marker + code.
    /// </summary>
    [Fact]
    public void MinimalScript_DecodesItsLabelBlockAndInstructions()
    {
        var bytes = BuildMinimalScript();

        var script = OblivionMobileScript.Parse(bytes, "minimal.scr");

        Assert.Equal(bytes.Length, script.Size);
        var label = Assert.Single(script.Labels);
        Assert.Equal(1, (int)label.Id);
        Assert.Equal(script.CodeStart, (int)label.Offset);

        var block = Assert.Single(script.Blocks);
        Assert.Equal(OblivionMobileBlockKind.TileAttribute, block.Kind);
        Assert.Equal(1, (int)block.Slot);
        Assert.Equal(8, block.Length);
        Assert.Equal(2, block.Fields.Count);
        Assert.Equal(OblivionMobileValueKind.Unsigned, block.Fields[0].ValueKind);
        Assert.Equal(OblivionMobileValueKind.UInt16, block.Fields[1].ValueKind);
        Assert.Equal(0x20, block.Fields[1].Value);

        var chunk = Assert.Single(script.Chunks);
        Assert.Equal(0, chunk.DeadByteCount);
        Assert.Equal(3, chunk.Instructions.Count);
        Assert.Equal("WAIT", chunk.Instructions[0].Mnemonic);
        Assert.Equal(200, chunk.Instructions[0].Operands[0].Value);
        Assert.Equal("SETCOLOR", chunk.Instructions[1].Mnemonic);
        Assert.Equal(0x123456, chunk.Instructions[1].Operands[0].Value);
        Assert.Equal(OblivionMobileScriptOpcodes.Return, chunk.Instructions[2].Opcode);

        // Marker plus code plus everything before it is the whole file: the format tiles exactly.
        Assert.Equal(bytes.Length, chunk.Offset + chunk.Length);
    }

    /// <summary>
    ///     The table is written id N first and label 1 last, while the chunks run the other way. A
    ///     parser that trusted table order for code order would mis-slice every multi-label file.
    /// </summary>
    [Fact]
    public void LabelTable_IsDescendingWhileChunksAscend()
    {
        var bytes = BuildScript(new[] { Chunk(1, 0x28, 0x02), Chunk(2, 0x02) });

        var script = OblivionMobileScript.Parse(bytes, "two.scr");

        Assert.Equal(new[] { 2, 1 }, script.Labels.Select(l => (int)l.Id));
        Assert.Equal(new[] { 1, 2 }, script.Chunks.Select(c => (int)c.Label));
        Assert.True(script.Chunks[0].Offset < script.Chunks[1].Offset);
        Assert.Equal(script.Chunks[0].Offset, script.CodeStart);
        Assert.Equal(2, script.FindChunk(2)!.Label);
        Assert.Null(script.FindChunk(3));
    }

    /// <summary>
    ///     A code text reference is a big-endian u16: <c>0xFxxx</c> is a lang id, any other value is
    ///     an inline LENGTH. Both MSGs here are the same opcode, and the second's four text bytes
    ///     only land right if the length was read as a word.
    /// </summary>
    [Fact]
    public void CodeTextRef_ReadsBothTheLangIdAndTheInlineString()
    {
        var bytes = BuildScript(new[]
        {
            Chunk(
                1,
                0x27, 0xF1, 0x9E, 0x3C, 0x02, 0x00,
                0x27, 0x00, 0x04, (byte)'J', (byte)'u', (byte)'m', (byte)'p', 0x3C, 0x02, 0x00,
                0x02),
        });

        var script = OblivionMobileScript.Parse(bytes, "msg.scr");
        var instructions = script.Chunks[0].Instructions;

        Assert.Equal(3, instructions.Count);
        Assert.Equal(OblivionMobileOperandKind.LangId, instructions[0].Operands[0].Kind);
        Assert.Equal(414, instructions[0].Operands[0].Value);
        Assert.Equal(OblivionMobileOperandKind.InlineText, instructions[1].Operands[0].Kind);
        Assert.Equal("Jump", instructions[1].Operands[0].Text);
        Assert.Equal(414, Assert.Single(script.LangIds));
    }

    /// <summary>
    ///     A zero word in DIALOG's optional slot means "no text and no bytes" — 157 of the 233
    ///     retail DIALOGs take that branch, so it is the common case rather than an edge one.
    /// </summary>
    [Fact]
    public void OptionalCodeTextRef_ZeroWordCarriesNoText()
    {
        var bytes = BuildScript(new[] { Chunk(1, 0x0F, 0x00, 0x00, 0x01, 0x0A, 0x03, 0x71, 0x03, 0x90, 0x02) });

        var script = OblivionMobileScript.Parse(bytes, "dialog.scr");
        var dialog = script.Chunks[0].Instructions[0];

        Assert.Equal("DIALOG", dialog.Mnemonic);
        Assert.Equal(OblivionMobileOperandKind.NoText, dialog.Operands[0].Kind);
        Assert.Equal(5, dialog.Operands.Count);
        Assert.Equal(0x0371, dialog.Operands[3].Value);
        Assert.Equal(0x0390, dialog.Operands[4].Value);
        Assert.Empty(script.LangIds);
    }

    /// <summary>
    ///     Inside a definition block the same idea is ONE byte wide: a leading <c>0xF</c> nibble
    ///     opens a two-byte lang reference, anything else is a u8 length. Retail only ever takes the
    ///     lang branch, so both are pinned here.
    /// </summary>
    [Fact]
    public void BlockTextRef_ReadsBothBranchesAtByteWidth()
    {
        var bytes = BuildScript(
            new[] { Chunk(1, 0x02) },
            0x1E, 0x01, 0x00, 0x01, 0x01, 0xF1, 0x6C, 0x05, 0x11, 0x22, 0x33, 0x06, 0x09, 0x00, 0x32, 0x1F,
            0x1E, 0x04, 0x00, 0x02, 0x01, 0x03, (byte)'A', (byte)'x', (byte)'e', 0x1F);

        var script = OblivionMobileScript.Parse(bytes, "items.scr");

        Assert.Equal(2, script.Blocks.Count);
        var name = script.Blocks[0].Fields.Single(f => f.Key == 1);
        Assert.Equal(OblivionMobileValueKind.LangId, name.ValueKind);
        Assert.Equal((ushort)364, name.LangId);
        Assert.Equal(0x112233, script.Blocks[0].Fields.Single(f => f.Key == 5).Value);
        Assert.Equal(OblivionMobileValueKind.Flag, script.Blocks[0].Fields.Single(f => f.Key == 6).ValueKind);
        Assert.Equal(50, script.Blocks[0].Fields.Single(f => f.Key == 9).Value);

        var inline = script.Blocks[1].Fields.Single(f => f.Key == 1);
        Assert.Equal(OblivionMobileValueKind.InlineText, inline.ValueKind);
        Assert.Equal("Axe", inline.Text);
        Assert.Equal(2, (int)script.Blocks[1].Slot);
        Assert.Equal(364, Assert.Single(script.LangIds));
    }

    /// <summary>
    ///     An untyped key in one of the four item tables is read sign-extended — the only way spell
    ///     fields like -50 make sense — while the same byte in a loot row is unsigned.
    /// </summary>
    [Fact]
    public void UntypedBlockBytes_AreSignedForItemTablesAndUnsignedElsewhere()
    {
        var bytes = BuildScript(
            new[] { Chunk(1, 0x02) },
            0x1E, 0x08, 0x00, 0x01, 0x03, 0xCE, 0x1F,
            0x1E, 0x0A, 0x00, 0x01, 0x02, 0xCE, 0x1F);

        var script = OblivionMobileScript.Parse(bytes, "signs.scr");

        Assert.Equal(-50, script.Blocks[0].Fields.Single(f => f.Key == 3).Value);
        Assert.Equal(206, script.Blocks[1].Fields.Single(f => f.Key == 2).Value);
    }

    /// <summary>
    ///     A kind-7 block has no keys: bytes are copied in PAIRS until the terminator, and the
    ///     block's slot stays 0 rather than being read out of the first data byte.
    /// </summary>
    [Fact]
    public void RawByteListBlock_CopiesPairsUntilTheTerminator()
    {
        var bytes = BuildScript(new[] { Chunk(1, 0x02) }, 0x1E, 0x07, 0x01, 0x02, 0x03, 0x04, 0x1F);

        var script = OblivionMobileScript.Parse(bytes, "raw.scr");

        var block = Assert.Single(script.Blocks);
        Assert.Equal(OblivionMobileBlockKind.RawByteList, block.Kind);
        Assert.Equal(0, (int)block.Slot);
        Assert.Equal(new[] { 1, 2, 3, 4 }, block.Fields.Select(f => f.Value));
        Assert.All(block.Fields, f => Assert.Equal(OblivionMobileValueKind.RawByte, f.ValueKind));
    }

    /// <summary>
    ///     Opcode 34's sub-command byte picks the width that follows it — one byte for command 3, a
    ///     big-endian u16 for command 7, and nothing at all for command 1. All three sit in one
    ///     chunk, so a wrong width leaves the chunk not tiling to the file end.
    /// </summary>
    [Fact]
    public void SpriteCommand_WidthFollowsItsSubCommandByte()
    {
        var bytes = BuildScript(new[]
        {
            Chunk(1, 0x22, 0x05, 0x03, 0x09, 0x22, 0x05, 0x07, 0x01, 0x2C, 0x22, 0x05, 0x01, 0x02),
        });

        var script = OblivionMobileScript.Parse(bytes, "sprcmd.scr");
        var instructions = script.Chunks[0].Instructions;

        Assert.Equal(4, instructions.Count);
        Assert.Equal(4, instructions[0].Length);
        Assert.Equal(3, instructions[0].Operands[1].Value);
        Assert.Equal(9, instructions[0].Operands[1].SubArgument);
        Assert.Equal(5, instructions[1].Length);
        Assert.Equal(300, instructions[1].Operands[1].SubArgument);
        Assert.Equal(3, instructions[2].Length);
        Assert.Null(instructions[2].Operands[1].SubArgument);
    }

    /// <summary>
    ///     The <c>l04_4b.scr</c> shape: a chunk whose RET is followed by five more decodable bytes
    ///     the authoring tool orphaned. They are unreachable, they still tile, and rejecting them
    ///     would mean rejecting a shipped file — so they are reported as a count.
    /// </summary>
    [Fact]
    public void BytesAfterTheReturn_AreCountedAsDeadRatherThanThrown()
    {
        var bytes = BuildScript(new[] { Chunk(1, 0x28, 0x02, 0x00, 0x07, 0x01, 0x28, 0x02) });

        var script = OblivionMobileScript.Parse(bytes, "dead.scr");
        var chunk = Assert.Single(script.Chunks);

        Assert.Equal(5, chunk.DeadByteCount);
        Assert.Equal(5, script.DeadByteCount);
        Assert.Equal(6, chunk.Instructions.Count);
        Assert.Equal(0, (int)chunk.Instructions[2].Opcode);
        Assert.Equal(2, script.OpcodeCensus[OblivionMobileScriptOpcodes.Return]);
    }

    /// <summary>The census counts every decoded instruction, dead ones included.</summary>
    [Fact]
    public void OpcodeCensus_CountsEveryDecodedInstruction()
    {
        var bytes = BuildScript(new[] { Chunk(1, 0x28, 0x28, 0x0B, 0x00, 0x01, 0x02) });

        var script = OblivionMobileScript.Parse(bytes, "census.scr");

        Assert.Equal(2, script.OpcodeCensus[40]);
        Assert.Equal(1, script.OpcodeCensus[11]);
        Assert.Equal(4, script.InstructionCount);
        Assert.Empty(script.BlockCensus);
        Assert.Null(script.OverlayIndex);
    }

    /// <summary>
    ///     Resource names are collected from the three loaders, de-duplicated in first-use order.
    ///     LOADMAP's pair travels together because the sprite set is only meaningful with its map.
    /// </summary>
    [Fact]
    public void ResourceNames_AreCollectedFromTheLoaders()
    {
        var bytes = BuildScript(new[]
        {
            Chunk(
                1,
                0x08,
                0x08, (byte)'/', (byte)'l', (byte)'0', (byte)'1', (byte)'.', (byte)'j', (byte)'t', (byte)'m',
                0x08, (byte)'/', (byte)'l', (byte)'0', (byte)'1', (byte)'.', (byte)'c', (byte)'m', (byte)'l',
                0x48, 0x00, 0x06, (byte)'/', (byte)'1', (byte)'.', (byte)'p', (byte)'n', (byte)'g',
                0x48, 0x00, 0x06, (byte)'/', (byte)'1', (byte)'.', (byte)'p', (byte)'n', (byte)'g',
                0x1D, 0x06, (byte)'/', (byte)'n', (byte)'.', (byte)'s', (byte)'c', (byte)'r',
                0x38, 0x01, (byte)'x', 0x06,
                0x02),
        });

        var script = OblivionMobileScript.Parse(bytes, "res.scr");

        Assert.Equal(new OblivionMobileMapReference("/l01.jtm", "/l01.cml"), Assert.Single(script.Maps));
        Assert.Equal("/1.png", Assert.Single(script.Images));
        Assert.Equal("/n.scr", Assert.Single(script.LoadedScripts));
        Assert.Equal(6, script.OverlayIndex);
    }

    /// <summary>
    ///     Every opcode 0..78 has a width, so a script from another build that uses one of the 18
    ///     the retail corpus never reaches still decodes instead of desyncing.
    /// </summary>
    [Fact]
    public void OpcodeTable_CoversEveryInterpreterCaseAndStopsAtSeventyNine()
    {
        for (byte code = 0; code <= OblivionMobileScriptOpcodes.MaxOpcode; code++)
        {
            Assert.NotNull(OblivionMobileScriptOpcodes.Find(code));
        }

        Assert.Null(OblivionMobileScriptOpcodes.Find(79));
        Assert.Null(OblivionMobileScriptOpcodes.Find(255));

        var observed = Enumerable.Range(0, OblivionMobileScriptOpcodes.MaxOpcode + 1)
            .Count(c => OblivionMobileScriptOpcodes.Find((byte)c)!.Observed);
        Assert.Equal(61, observed);
        Assert.Equal(18, OblivionMobileScriptOpcodes.MaxOpcode + 1 - observed);
    }

    /// <summary>Sub-command widths are 1 for the common set, 2 for 7/14/15, and 0 for the rest.</summary>
    [Fact]
    public void SubCommandWidths_AreOneTwoOrNothing()
    {
        Assert.Equal(1, OblivionMobileScriptOpcodes.SubCommandOperandWidth(3));
        Assert.Equal(1, OblivionMobileScriptOpcodes.SubCommandOperandWidth(20));
        Assert.Equal(2, OblivionMobileScriptOpcodes.SubCommandOperandWidth(7));
        Assert.Equal(2, OblivionMobileScriptOpcodes.SubCommandOperandWidth(14));
        Assert.Equal(2, OblivionMobileScriptOpcodes.SubCommandOperandWidth(15));
        Assert.Equal(0, OblivionMobileScriptOpcodes.SubCommandOperandWidth(0));
        Assert.Equal(0, OblivionMobileScriptOpcodes.SubCommandOperandWidth(16));
        Assert.Equal(0, OblivionMobileScriptOpcodes.SubCommandOperandWidth(255));
    }

    /// <summary>A label table that runs off the end names the entry and the byte it starts at.</summary>
    [Fact]
    public void LabelTableThatOverrunsTheFile_Throws()
    {
        var bytes = new byte[16];
        bytes[0] = 200;

        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileScript.Parse(bytes, "short.scr"));

        Assert.Contains("short.scr", error.Message, StringComparison.Ordinal);
        Assert.Contains("label table entry", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Kind 3 has no reader in the engine, so a decoder must stop rather than guess a width.</summary>
    [Fact]
    public void UnknownBlockKind_Throws()
    {
        var bytes = BuildScript(new[] { Chunk(1, 0x02) }, 0x1E, 0x03, 0x00, 0x01, 0x1F);

        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileScript.Parse(bytes, "kind3.scr"));

        Assert.Contains("unknown definition block kind 3", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A key at or past a record's slot count would write outside the record.</summary>
    [Fact]
    public void BlockKeyPastTheRecordWidth_Throws()
    {
        var bytes = BuildScript(new[] { Chunk(1, 0x02) }, 0x1E, 0x06, 0x00, 0x01, 0x09, 0x00, 0x1F);

        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileScript.Parse(bytes, "wide.scr"));

        Assert.Contains("key 9", error.Message, StringComparison.Ordinal);
        Assert.Contains("7 slots", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Opcode 79 and up are ignored by the engine but rejected here: a decoder cannot tell a
    ///     future opcode from a sync it lost three instructions ago.
    /// </summary>
    [Fact]
    public void UnknownOpcode_Throws()
    {
        var bytes = BuildScript(new[] { Chunk(1, 0x4F, 0x02) });

        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileScript.Parse(bytes, "op79.scr"));

        Assert.Contains("unknown opcode 79", error.Message, StringComparison.Ordinal);
    }

    /// <summary>An operand that needs bytes the file does not have names the width it wanted.</summary>
    [Fact]
    public void OperandThatRunsPastTheFile_Throws()
    {
        var bytes = BuildScript(new[] { Chunk(1, 0x40, 0x12, 0x34) });

        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileScript.Parse(bytes, "cut.scr"));

        Assert.Contains("SETCOLOR", error.Message, StringComparison.Ordinal);
        Assert.Contains("needs 3 bytes", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The tiling check is the whole proof that the operand widths are right, so a chunk that
    ///     overshoots the next marker must be an error rather than a silent re-sync.
    /// </summary>
    [Fact]
    public void ChunkThatDoesNotTileToTheNextMarker_Throws()
    {
        var bytes = BuildScript(new[] { Chunk(1, 0x40, 0x12), Chunk(2, 0x02) });

        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileScript.Parse(bytes, "overshoot.scr"));

        Assert.Contains("past the next marker", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Every chunk is introduced by <c>00 &lt;id&gt; 01</c>; the id byte is checked too.</summary>
    [Fact]
    public void MarkerThatDoesNotNameItsLabel_Throws()
    {
        var bytes = BuildMinimalScript();
        var script = OblivionMobileScript.Parse(bytes, "minimal.scr");
        bytes[script.CodeStart - 2] = 0x09;

        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileScript.Parse(bytes, "marker.scr"));

        Assert.Contains("should be introduced by 00 01 01", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Label 1's offset is an independent pin on where the code starts. When it disagrees with
    ///     the block walk one of the two is wrong, and neither can be preferred silently.
    /// </summary>
    [Fact]
    public void CodeStartThatDisagreesWithTheBlockWalk_Throws()
    {
        var bytes = BuildMinimalScript();
        bytes[3]++;

        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileScript.Parse(bytes, "pin.scr"));

        Assert.Contains("the definition section ends at byte", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A file with no labels at all has no entry point.</summary>
    [Fact]
    public void ZeroLabels_Throws()
    {
        var error = Assert.Throws<InvalidDataException>(
            () => OblivionMobileScript.Parse(new byte[12], "empty.scr"));

        Assert.Contains("label count at byte 0 is zero", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A lang table is CRLF-joined <c>"id text|"</c> records with no trailing CRLF, and the
    ///     recorded offset points at the text rather than at the id.
    /// </summary>
    [Fact]
    public void LangTable_ParsesCrlfFramedRecords()
    {
        var bytes = Encoding.Latin1.GetBytes("1 Health|\r\n43 Take Artifact|\r\n574 Exit|");

        var table = OblivionMobileLang.Parse(bytes, "lang_0.txt");

        Assert.Equal(0, table.Index);
        Assert.Equal(3, table.Strings.Count);
        Assert.Equal(new[] { 1, 43, 574 }, table.Strings.Select(s => s.Id));
        Assert.Equal("Take Artifact", table.Find(43));
        Assert.Null(table.Find(44));
        Assert.Equal(574, table.MaxId);
        Assert.Equal("Health", Encoding.Latin1.GetString(bytes, table.Strings[0].Offset, 6));
        Assert.True(table.TryGetText(574, out var exit));
        Assert.Equal("Exit", exit);
        Assert.False(table.TryGetText(99, out var missing));
        Assert.Equal(string.Empty, missing);
    }

    /// <summary>
    ///     The decoder is ISO-8859-1, not UTF-8. Retail is all-ASCII so the two agree there; a lone
    ///     0xE9 is where they part, and Latin-1 is what the engine does with it.
    /// </summary>
    [Fact]
    public void LangTable_DecodesHighBytesAsLatin1()
    {
        byte[] bytes = [(byte)'7', (byte)' ', (byte)'C', (byte)'a', (byte)'f', 0xE9, (byte)'|'];

        var table = OblivionMobileLang.Parse(bytes, "lang_3.txt");

        Assert.Equal(3, table.Index);
        Assert.Equal("Café", table.Find(7));
        Assert.Equal(4, table.Find(7)!.Length);
    }

    /// <summary>A record with no terminator is the one thing that cannot be recovered from.</summary>
    [Fact]
    public void LangRecordWithoutTerminator_Throws()
    {
        var bytes = Encoding.Latin1.GetBytes("1 Health|\r\n2 Magicka");

        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileLang.Parse(bytes, "lang_0.txt"));

        Assert.Contains("no '|' terminator", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The id is decimal ASCII; anything else means the framing is not what we think.</summary>
    [Fact]
    public void LangRecordWithNonNumericId_Throws()
    {
        var bytes = Encoding.Latin1.GetBytes("one Health|");

        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileLang.Parse(bytes, "lang_0.txt"));

        Assert.Contains("does not start with a decimal id", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The file ends on the last record's terminator; a trailing CRLF is not the format.</summary>
    [Fact]
    public void LangTableWithTrailingCrlf_Throws()
    {
        var bytes = Encoding.Latin1.GetBytes("1 Health|\r\n");

        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileLang.Parse(bytes, "lang_0.txt"));

        Assert.Contains("trailing CRLF", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A bare LF between records is rejected: the separator is exactly CRLF.</summary>
    [Fact]
    public void LangRecordsJoinedByBareLf_Throw()
    {
        var bytes = Encoding.Latin1.GetBytes("1 Health|\n2 Magicka|");

        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileLang.Parse(bytes, "lang_0.txt"));

        Assert.Contains("joined by CRLF", error.Message, StringComparison.Ordinal);
    }

    /// <summary><c>start.txt</c> has no ids and no line breaks at all — just terminated entries.</summary>
    [Fact]
    public void StartText_ParsesTerminatedEntriesByOrdinal()
    {
        var bytes = Encoding.Latin1.GetBytes("Loading|Press any key|Resume game?|Yes|Exit|");

        var entries = OblivionMobileLang.ParseStartText(bytes, "start.txt");

        Assert.Equal(new[] { "Loading", "Press any key", "Resume game?", "Yes", "Exit" }, entries);
    }

    /// <summary>An unterminated trailing entry in start.txt is an error, not a silent extra.</summary>
    [Fact]
    public void StartTextWithoutTerminator_Throws()
    {
        var bytes = Encoding.Latin1.GetBytes("Loading|Press any key");

        var error = Assert.Throws<InvalidDataException>(() => OblivionMobileLang.ParseStartText(bytes, "start.txt"));

        Assert.Contains("no '|' terminator", error.Message, StringComparison.Ordinal);
    }
}
