using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Travels.OblivionMobile;

/// <summary>
///     The Oblivion mobile (2006, J2ME, Vir2L) <c>.scr</c> script — the game's entire scripted
///     content: level set-up, dialogue, item and spell tables, sprite choreography. Original RE
///     (2026-09-05); big-endian throughout, in the J2ME <c>DataInputStream</c> house style.
///     <para>
///         Layout. A u8 label count <c>N</c>; then <c>N</c> table entries of
///         <c>
///             {u8 id, u16
///             offset}
///         </c>
///         where the offset is ABSOLUTE into the file; then a definition section of
///         zero or more blocks, each framed <c>0x1E kind ... 0x1F</c>; then the code, one chunk per
///         label, each preceded by the 3-byte marker <c>00 &lt;labelId&gt; 01</c>. A chunk's byte
///         range is <c>[offset, nextOffset - 3)</c>, the last running to EOF, so a file tiles as
///         <c>1 + 3N + sum(blocks) + sum(3 + chunk)</c> == its size, with nothing left over.
///     </para>
///     <para>
///         The table is written in DESCENDING id order and offsets increase with id, so label 1 is
///         both the lowest code address and the entry point (starting a script pushes label 1).
///         That gives a second, independent pin on where the code starts: label 1's offset must
///         equal the end of the last definition block plus the 3 marker bytes — true on 32/32
///         retail files, and the check this parser makes rather than trusting the block walk alone.
///     </para>
///     <para>
///         Measured over the 32 retail scripts (36..3,174 bytes): 394 labels, 570 definition
///         blocks, 3,844 instructions, 395 RET, 179 CALLs that all resolve to a label in their own
///         file, 30 distinct <c>.scr</c> names loaded, 36 PNGs, 331 distinct text ids. Every byte
///         of every file is accounted for.
///     </para>
///     <para>
///         Trap — the text reference has TWO widths. In code an operand text reference is a
///         big-endian u16: <c>0xFxxx</c> means lang id <c>xxx</c>, any other value is an inline
///         string LENGTH and that many bytes follow (a zero word is "no text"). Inside a definition
///         block the same idea is one byte wide: a leading high nibble of <c>0xF</c> starts a u16
///         lang reference, anything else is a u8 length followed by its bytes. Reading the code
///         form as one byte desyncs 17 of the 32 files within a few instructions, which is exactly
///         how that difference was found. The block form's inline case never occurs in retail (all
///         104 block references are lang ids), so only its lang branch is measured.
///     </para>
///     <para>
///         Trap — one retail file has dead bytes. <c>l04_4b.scr</c> carries 5 orphaned bytes
///         (<c>00 07 01 28 02</c>, a duplicate copy of label 7's chunk) between label 6's RET and
///         the real label-7 marker, and its table skips id 8 accordingly. They are unreachable, and
///         they still decode cleanly, so they are reported as
///         <see cref="OblivionMobileScriptChunk.DeadByteCount" /> on label 6 rather than thrown on:
///         a parser that rejected them would reject a shipped file. This is the sole opcode-0 byte
///         in the corpus.
///     </para>
/// </summary>
internal sealed class OblivionMobileScript
{
    /// <summary>Opens a definition block; the run of blocks ends at the first byte that is not this.</summary>
    public const byte BlockOpen = 0x1E;

    /// <summary>Closes a definition block.</summary>
    public const byte BlockClose = 0x1F;

    /// <summary>Bytes in a label table entry: the id and its big-endian absolute offset.</summary>
    public const int LabelEntryLength = 3;

    /// <summary>Bytes in a label's code marker, <c>00 &lt;labelId&gt; 01</c>.</summary>
    public const int MarkerLength = 3;

    /// <summary>First marker byte. Also the interpreter's "invalid opcode" log case.</summary>
    public const byte MarkerLead = 0x00;

    /// <summary>Third marker byte.</summary>
    public const byte MarkerTail = 0x01;

    /// <summary>
    ///     Entries in a kind-7 block's flat list. The engine copies bytes in PAIRS until it sees
    ///     the terminator, so a <c>0x1F</c> at an odd position would be swallowed as data; all 13
    ///     retail payloads are even (50 bytes x10, 52 x3) and terminate cleanly.
    /// </summary>
    public const int RawListCapacity = 100;

    private OblivionMobileScript(
        string name,
        int size,
        IReadOnlyList<OblivionMobileScriptLabel> labels,
        IReadOnlyList<OblivionMobileScriptBlock> blocks,
        IReadOnlyList<OblivionMobileScriptChunk> chunks,
        int codeStart,
        IReadOnlyDictionary<byte, int> opcodeCensus,
        IReadOnlyDictionary<OblivionMobileBlockKind, int> blockCensus,
        IReadOnlyList<string> loadedScripts,
        IReadOnlyList<OblivionMobileMapReference> maps,
        IReadOnlyList<string> images,
        IReadOnlyList<ushort> langIds,
        int? overlayIndex)
    {
        Name = name;
        Size = size;
        Labels = labels;
        Blocks = blocks;
        Chunks = chunks;
        CodeStart = codeStart;
        OpcodeCensus = opcodeCensus;
        BlockCensus = blockCensus;
        LoadedScripts = loadedScripts;
        Maps = maps;
        Images = images;
        LangIds = langIds;
        OverlayIndex = overlayIndex;
    }

    /// <summary>Source file name, for messages and for the LOADSCR graph.</summary>
    public string Name { get; }

    /// <summary>File length in bytes; every one of them is covered by the members below.</summary>
    public int Size { get; }

    /// <summary>The label table in FILE order, which is descending id order.</summary>
    public IReadOnlyList<OblivionMobileScriptLabel> Labels { get; }

    /// <summary>The definition blocks, in file order.</summary>
    public IReadOnlyList<OblivionMobileScriptBlock> Blocks { get; }

    /// <summary>The code chunks in ascending offset order — that is, ascending label id.</summary>
    public IReadOnlyList<OblivionMobileScriptChunk> Chunks { get; }

    /// <summary>Offset of the first instruction: label 1's offset, one marker past the blocks.</summary>
    public int CodeStart { get; }

    /// <summary>Instructions decoded across every chunk, including the unreachable ones.</summary>
    public int InstructionCount => Chunks.Sum(c => c.Instructions.Count);

    /// <summary>
    ///     Bytes that decode but sit after a chunk's terminating RET. 5 on <c>l04_4b.scr</c>,
    ///     0 on the other 31 retail files.
    /// </summary>
    public int DeadByteCount => Chunks.Sum(c => c.DeadByteCount);

    /// <summary>How many times each opcode occurs, over every chunk of this file.</summary>
    public IReadOnlyDictionary<byte, int> OpcodeCensus { get; }

    /// <summary>How many definition blocks of each kind this file carries.</summary>
    public IReadOnlyDictionary<OblivionMobileBlockKind, int> BlockCensus { get; }

    /// <summary>Distinct <c>.scr</c> names this script hands control to (opcode 29), in first-use order.</summary>
    public IReadOnlyList<string> LoadedScripts { get; }

    /// <summary>Distinct map loads (opcode 8): a <c>.jtm</c> tile map and its <c>.cml</c> sprite set.</summary>
    public IReadOnlyList<OblivionMobileMapReference> Maps { get; }

    /// <summary>Distinct PNG names this script preloads (opcode 72), in first-use order.</summary>
    public IReadOnlyList<string> Images { get; }

    /// <summary>Every distinct lang id referenced, from both code operands and block fields, ascending.</summary>
    public IReadOnlyList<ushort> LangIds { get; }

    /// <summary>
    ///     The <c>lang_N.txt</c> overlay this script selects (opcode 56), or null when it selects
    ///     none and inherits whatever the script that loaded it left in force. Exactly 20 of the 32
    ///     retail scripts carry one, and never more than one each.
    /// </summary>
    public int? OverlayIndex { get; }

    /// <summary>
    ///     Decodes a whole script. Throws <see cref="InvalidDataException" /> naming the file and
    ///     the byte position on a table that overruns the file, an unknown block kind, a block key
    ///     past the end of its record, an unknown opcode, an operand that runs off the end, a chunk
    ///     that does not tile up to the next marker, or a marker that is not
    ///     <c>00 &lt;id&gt; 01</c>.
    /// </summary>
    public static OblivionMobileScript Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (bytes.Length < 1 + LabelEntryLength + MarkerLength)
        {
            throw new InvalidDataException(
                $"'{name}': {bytes.Length} bytes is too short for a label count, one table entry and a marker.");
        }

        int labelCount = bytes[0];
        if (labelCount == 0)
        {
            throw new InvalidDataException(
                $"'{name}': the label count at byte 0 is zero; every script has at least label 1.");
        }

        var position = 1;
        var labels = ReadLabelTable(bytes, name, labelCount, ref position);
        var blocks = ReadBlocks(bytes, name, ref position);
        var chunks = ReadChunks(bytes, name, labels, position);

        var census = new Dictionary<byte, int>();
        var blockCensus = new Dictionary<OblivionMobileBlockKind, int>();
        var scripts = new List<string>();
        var maps = new List<OblivionMobileMapReference>();
        var images = new List<string>();
        var langIds = new SortedSet<ushort>();
        int? overlay = null;

        foreach (var block in blocks)
        {
            blockCensus[block.Kind] = blockCensus.GetValueOrDefault(block.Kind) + 1;
            foreach (var field in block.Fields)
            {
                if (field.LangId is { } id)
                {
                    langIds.Add(id);
                }
            }
        }

        foreach (var chunk in chunks)
        {
            foreach (var instruction in chunk.Instructions)
            {
                census[instruction.Opcode] = census.GetValueOrDefault(instruction.Opcode) + 1;
                CollectResources(instruction, scripts, maps, images, langIds, ref overlay);
            }
        }

        return new OblivionMobileScript(
            name,
            bytes.Length,
            labels,
            blocks,
            chunks,
            labels.Min(l => (int)l.Offset),
            census,
            blockCensus,
            scripts,
            maps,
            images,
            [.. langIds],
            overlay);
    }

    /// <summary>The chunk for a label id, or null when the table has no such label.</summary>
    public OblivionMobileScriptChunk? FindChunk(byte labelId)
    {
        return Chunks.FirstOrDefault(c => c.Label == labelId);
    }

    private static void CollectResources(
        OblivionMobileScriptInstruction instruction,
        List<string> scripts,
        List<OblivionMobileMapReference> maps,
        List<string> images,
        SortedSet<ushort> langIds,
        ref int? overlay)
    {
        foreach (var operand in instruction.Operands)
        {
            if (operand.Kind == OblivionMobileOperandKind.LangId)
            {
                langIds.Add((ushort)operand.Value);
            }
        }

        switch (instruction.Opcode)
        {
            case OblivionMobileScriptOpcodes.LoadScript:
                AddDistinct(scripts, instruction.Operands[0].Text);
                break;
            case OblivionMobileScriptOpcodes.LoadImage:
                AddDistinct(images, instruction.Operands[0].Text);
                break;
            case OblivionMobileScriptOpcodes.LoadMap:
                var reference = new OblivionMobileMapReference(
                    instruction.Operands[0].Text ?? string.Empty,
                    instruction.Operands[1].Text ?? string.Empty);
                if (!maps.Contains(reference))
                {
                    maps.Add(reference);
                }

                break;
            case OblivionMobileScriptOpcodes.LoadLang:
                overlay = instruction.Operands[1].Value;
                break;
        }
    }

    private static void AddDistinct(List<string> into, string? value)
    {
        if (value is not null && !into.Contains(value, StringComparer.Ordinal))
        {
            into.Add(value);
        }
    }

    private static List<OblivionMobileScriptLabel> ReadLabelTable(
        ReadOnlySpan<byte> bytes,
        string name,
        int labelCount,
        ref int position)
    {
        var labels = new List<OblivionMobileScriptLabel>(labelCount);
        for (var i = 0; i < labelCount; i++)
        {
            if (position + LabelEntryLength > bytes.Length)
            {
                throw new InvalidDataException(
                    $"'{name}': label table entry {i} of {labelCount} starts at byte {position}, past the {bytes.Length}-byte file.");
            }

            var id = bytes[position];
            var offset = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(position + 1, 2));
            labels.Add(new OblivionMobileScriptLabel(id, offset, position));
            position += LabelEntryLength;
        }

        return labels;
    }

    private static List<OblivionMobileScriptBlock> ReadBlocks(ReadOnlySpan<byte> bytes, string name, ref int position)
    {
        var blocks = new List<OblivionMobileScriptBlock>();
        while (position < bytes.Length && bytes[position] == BlockOpen)
        {
            var start = position;
            position++;
            if (position >= bytes.Length)
            {
                throw new InvalidDataException(
                    $"'{name}': a definition block opens at byte {start} with no kind byte before the end of the file.");
            }

            var kindByte = bytes[position];
            position++;
            if (!OblivionMobileScriptBlock.IsKnownKind(kindByte))
            {
                throw new InvalidDataException(
                    $"'{name}': unknown definition block kind {kindByte} at byte {position - 1}; the engine has no reader for it and would stop parsing here.");
            }

            var kind = (OblivionMobileBlockKind)kindByte;
            var fields = kind == OblivionMobileBlockKind.RawByteList
                ? ReadRawBlock(bytes, name, start, ref position)
                : ReadKeyedBlock(bytes, name, kind, ref position);

            blocks.Add(new OblivionMobileScriptBlock(kind, start, position - start, SlotOf(fields), fields));
        }

        return blocks;
    }

    private static byte SlotOf(List<OblivionMobileScriptField> fields)
    {
        // Key 0 is the record's 1-based slot index and is always written first. A kind-7 block has
        // no keys at all — its entries are raw bytes that all report key 0 — so it reports slot 0.
        return fields.Count > 0
               && fields[0].Key == 0
               && fields[0].ValueKind != OblivionMobileValueKind.RawByte
            ? (byte)fields[0].Value
            : (byte)0;
    }

    private static List<OblivionMobileScriptField> ReadRawBlock(
        ReadOnlySpan<byte> bytes,
        string name,
        int start,
        ref int position)
    {
        var fields = new List<OblivionMobileScriptField>();
        while (true)
        {
            if (position >= bytes.Length)
            {
                throw new InvalidDataException(
                    $"'{name}': the raw-list block at byte {start} runs to the end of the {bytes.Length}-byte file without a 0x1F terminator.");
            }

            if (bytes[position] == BlockClose)
            {
                position++;
                return fields;
            }

            if (position + 2 > bytes.Length)
            {
                throw new InvalidDataException(
                    $"'{name}': the raw-list block at byte {start} needs a byte pair at {position}, past the {bytes.Length}-byte file.");
            }

            if (fields.Count + 2 > RawListCapacity)
            {
                throw new InvalidDataException(
                    $"'{name}': the raw-list block at byte {start} exceeds the engine's {RawListCapacity}-entry list at byte {position}.");
            }

            fields.Add(new OblivionMobileScriptField(0, OblivionMobileValueKind.RawByte, bytes[position], null, null));
            fields.Add(new OblivionMobileScriptField(0, OblivionMobileValueKind.RawByte, bytes[position + 1], null,
                null));
            position += 2;
        }
    }

    private static List<OblivionMobileScriptField> ReadKeyedBlock(
        ReadOnlySpan<byte> bytes,
        string name,
        OblivionMobileBlockKind kind,
        ref int position)
    {
        var fields = new List<OblivionMobileScriptField>();
        var slots = OblivionMobileScriptBlock.SlotCount(kind);
        while (true)
        {
            if (position >= bytes.Length)
            {
                throw new InvalidDataException(
                    $"'{name}': the {kind} block runs to the end of the {bytes.Length}-byte file without a 0x1F terminator.");
            }

            if (bytes[position] == BlockClose)
            {
                position++;
                return fields;
            }

            var key = bytes[position];
            position++;
            if (key >= slots)
            {
                throw new InvalidDataException(
                    $"'{name}': key {key} at byte {position - 1} is past the {slots} slots of a {kind} record.");
            }

            fields.Add(ReadField(bytes, name, kind, key, ref position));
        }
    }

    private static OblivionMobileScriptField ReadField(
        ReadOnlySpan<byte> bytes,
        string name,
        OblivionMobileBlockKind kind,
        byte key,
        ref int position)
    {
        var valueKind = OblivionMobileScriptBlock.ValueKindOf(kind, key);
        switch (valueKind)
        {
            case OblivionMobileValueKind.Flag:
                return new OblivionMobileScriptField(key, valueKind, 1, null, null);
            case OblivionMobileValueKind.Unsigned:
            case OblivionMobileValueKind.ListItem:
                RequireBytes(bytes, name, position, 1, $"the value of key {key}");
                return new OblivionMobileScriptField(key, valueKind, bytes[position++], null, null);
            case OblivionMobileValueKind.Signed:
                RequireBytes(bytes, name, position, 1, $"the value of key {key}");
                return new OblivionMobileScriptField(key, valueKind, (sbyte)bytes[position++], null, null);
            case OblivionMobileValueKind.UInt16:
                RequireBytes(bytes, name, position, 2, $"the value of key {key}");
                var word = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(position, 2));
                position += 2;
                return new OblivionMobileScriptField(key, valueKind, word, null, null);
            case OblivionMobileValueKind.UInt24:
                RequireBytes(bytes, name, position, 3, $"the value of key {key}");
                var value = (bytes[position] << 16) | (bytes[position + 1] << 8) | bytes[position + 2];
                position += 3;
                return new OblivionMobileScriptField(key, valueKind, value, null, null);
            case OblivionMobileValueKind.Text:
                var text = ReadText8(bytes, name, ref position, $"the string of key {key}");
                return new OblivionMobileScriptField(key, valueKind, text.Length, text, null);
            default:
                return ReadBlockTextRef(bytes, name, key, ref position);
        }
    }

    private static OblivionMobileScriptField ReadBlockTextRef(
        ReadOnlySpan<byte> bytes,
        string name,
        byte key,
        ref int position)
    {
        // Block form: ONE byte decides. A high nibble of 0xF starts a two-byte lang reference,
        // anything else is a u8 string length. All 104 retail block references take the lang
        // branch, so the inline branch is the engine's reader rather than a measured layout.
        RequireBytes(bytes, name, position, 1, $"the text reference of key {key}");
        if ((bytes[position] & 0xF0) == 0xF0)
        {
            RequireBytes(bytes, name, position, 2, $"the lang reference of key {key}");
            var id = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(position, 2)) & 0x0FFF);
            position += 2;
            return new OblivionMobileScriptField(key, OblivionMobileValueKind.LangId, id, null, id);
        }

        var inline = ReadText8(bytes, name, ref position, $"the inline text of key {key}");
        return new OblivionMobileScriptField(key, OblivionMobileValueKind.InlineText, inline.Length, inline, null);
    }

    private static List<OblivionMobileScriptChunk> ReadChunks(
        ReadOnlySpan<byte> bytes,
        string name,
        List<OblivionMobileScriptLabel> labels,
        int blocksEnd)
    {
        var ordered = labels.OrderBy(l => l.Offset).ToList();
        var first = ordered[0];
        if (first.Offset != blocksEnd + MarkerLength)
        {
            throw new InvalidDataException(
                $"'{name}': the definition section ends at byte {blocksEnd}, so the first chunk should start at "
                + $"{blocksEnd + MarkerLength}, but label {first.Id} claims byte {first.Offset}.");
        }

        var chunks = new List<OblivionMobileScriptChunk>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var label = ordered[i];
            var end = i + 1 < ordered.Count ? ordered[i + 1].Offset - MarkerLength : bytes.Length;
            chunks.Add(ReadChunk(bytes, name, label, end));
        }

        return chunks;
    }

    private static OblivionMobileScriptChunk ReadChunk(
        ReadOnlySpan<byte> bytes,
        string name,
        OblivionMobileScriptLabel label,
        int end)
    {
        var marker = label.Offset - MarkerLength;
        if (marker < 0 || end > bytes.Length || end < label.Offset)
        {
            throw new InvalidDataException(
                $"'{name}': label {label.Id} claims code at byte {label.Offset} running to {end}, outside the {bytes.Length}-byte file.");
        }

        if (bytes[marker] != MarkerLead || bytes[marker + 1] != label.Id || bytes[marker + 2] != MarkerTail)
        {
            throw new InvalidDataException(
                $"'{name}': label {label.Id} should be introduced by 00 {label.Id:X2} 01 at byte {marker}, found "
                + $"{bytes[marker]:X2} {bytes[marker + 1]:X2} {bytes[marker + 2]:X2}.");
        }

        var instructions = new List<OblivionMobileScriptInstruction>();
        int position = label.Offset;
        var afterFirstReturn = -1;
        while (position < end)
        {
            var instruction = ReadInstruction(bytes, name, ref position);
            instructions.Add(instruction);
            if (instruction.Opcode == OblivionMobileScriptOpcodes.Return && afterFirstReturn < 0)
            {
                afterFirstReturn = position;
            }
        }

        if (position != end)
        {
            throw new InvalidDataException(
                $"'{name}': label {label.Id}'s code ends at byte {position}, {position - end} past the next marker at {end}; "
                + "an operand width in this chunk is wrong.");
        }

        var dead = afterFirstReturn >= 0 && afterFirstReturn < end ? end - afterFirstReturn : 0;
        return new OblivionMobileScriptChunk(label.Id, label.Offset, end - label.Offset, instructions, dead);
    }

    private static OblivionMobileScriptInstruction ReadInstruction(ReadOnlySpan<byte> bytes, string name,
        ref int position)
    {
        var start = position;
        var opcode = bytes[position];
        position++;
        var info = OblivionMobileScriptOpcodes.Find(opcode);
        if (info is null)
        {
            throw new InvalidDataException(
                $"'{name}': unknown opcode {opcode} at byte {start}. The engine ignores opcodes above "
                + $"{OblivionMobileScriptOpcodes.MaxOpcode}, but a decoder cannot tell one from a lost sync.");
        }

        var operands = new List<OblivionMobileScriptOperand>(info.Operands.Count);
        foreach (var form in info.Operands)
        {
            operands.Add(ReadOperand(bytes, name, info, form, ref position));
        }

        return new OblivionMobileScriptInstruction(start, position - start, opcode, info.Mnemonic, operands);
    }

    private static OblivionMobileScriptOperand ReadOperand(
        ReadOnlySpan<byte> bytes,
        string name,
        OblivionMobileOpcodeInfo info,
        OblivionMobileOperandForm form,
        ref int position)
    {
        var what = $"an operand of {info.Mnemonic}";
        switch (form)
        {
            case OblivionMobileOperandForm.UInt8:
            case OblivionMobileOperandForm.LabelId:
            case OblivionMobileOperandForm.ObjectSlot:
                RequireBytes(bytes, name, position, 1, what);
                var kind = form switch
                {
                    OblivionMobileOperandForm.LabelId => OblivionMobileOperandKind.Label,
                    OblivionMobileOperandForm.ObjectSlot => OblivionMobileOperandKind.ObjectSlot,
                    _ => OblivionMobileOperandKind.UInt8
                };
                return new OblivionMobileScriptOperand(kind, bytes[position++]);
            case OblivionMobileOperandForm.UInt16:
                RequireBytes(bytes, name, position, 2, what);
                var word = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(position, 2));
                position += 2;
                return new OblivionMobileScriptOperand(OblivionMobileOperandKind.UInt16, word);
            case OblivionMobileOperandForm.UInt24:
                RequireBytes(bytes, name, position, 3, what);
                var triple = (bytes[position] << 16) | (bytes[position + 1] << 8) | bytes[position + 2];
                position += 3;
                return new OblivionMobileScriptOperand(OblivionMobileOperandKind.UInt24, triple);
            case OblivionMobileOperandForm.Text8:
                var text8 = ReadText8(bytes, name, ref position, what);
                return new OblivionMobileScriptOperand(OblivionMobileOperandKind.InlineText, text8.Length, text8);
            case OblivionMobileOperandForm.Text16:
                return ReadText16(bytes, name, what, ref position);
            case OblivionMobileOperandForm.TextRef:
            case OblivionMobileOperandForm.OptionalTextRef:
                return ReadCodeTextRef(bytes, name, what, ref position);
            case OblivionMobileOperandForm.ByteList:
                return ReadByteList(bytes, name, what, ref position);
            default:
                return ReadSubCommand(bytes, name, what, ref position);
        }
    }

    private static OblivionMobileScriptOperand ReadText16(
        ReadOnlySpan<byte> bytes,
        string name,
        string what,
        ref int position)
    {
        RequireBytes(bytes, name, position, 2, what);
        var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(position, 2));
        position += 2;
        RequireBytes(bytes, name, position, length, what);
        var text = Encoding.Latin1.GetString(bytes.Slice(position, length));
        position += length;
        return new OblivionMobileScriptOperand(OblivionMobileOperandKind.InlineText, length, text);
    }

    private static OblivionMobileScriptOperand ReadCodeTextRef(
        ReadOnlySpan<byte> bytes,
        string name,
        string what,
        ref int position)
    {
        // Code form: always a big-endian u16 first. 0xFxxx is lang id xxx; 0 is "no text"; any
        // other value is an inline string length. Reading this as one byte is what desyncs a
        // decoder that assumes the definition-block form.
        RequireBytes(bytes, name, position, 2, what);
        var word = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(position, 2));
        position += 2;
        if ((word & 0xF000) == 0xF000)
        {
            return new OblivionMobileScriptOperand(OblivionMobileOperandKind.LangId, word & 0x0FFF);
        }

        if (word == 0)
        {
            return new OblivionMobileScriptOperand(OblivionMobileOperandKind.NoText, 0);
        }

        RequireBytes(bytes, name, position, word, what);
        var text = Encoding.Latin1.GetString(bytes.Slice(position, word));
        position += word;
        return new OblivionMobileScriptOperand(OblivionMobileOperandKind.InlineText, word, text);
    }

    private static OblivionMobileScriptOperand ReadByteList(
        ReadOnlySpan<byte> bytes,
        string name,
        string what,
        ref int position)
    {
        RequireBytes(bytes, name, position, 1, what);
        int count = bytes[position];
        position++;
        RequireBytes(bytes, name, position, count, what);
        var items = bytes.Slice(position, count).ToArray();
        position += count;
        return new OblivionMobileScriptOperand(OblivionMobileOperandKind.ByteList, count, null, items);
    }

    private static OblivionMobileScriptOperand ReadSubCommand(
        ReadOnlySpan<byte> bytes,
        string name,
        string what,
        ref int position)
    {
        RequireBytes(bytes, name, position, 1, what);
        var command = bytes[position];
        position++;
        var width = OblivionMobileScriptOpcodes.SubCommandOperandWidth(command);
        if (width == 0)
        {
            return new OblivionMobileScriptOperand(OblivionMobileOperandKind.SubCommand, command);
        }

        RequireBytes(bytes, name, position, width, $"{what} sub-command {command}");
        int argument = width == 1
            ? bytes[position]
            : BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(position, 2));
        position += width;
        return new OblivionMobileScriptOperand(OblivionMobileOperandKind.SubCommand, command, null, null, argument);
    }

    private static string ReadText8(ReadOnlySpan<byte> bytes, string name, ref int position, string what)
    {
        RequireBytes(bytes, name, position, 1, what);
        int length = bytes[position];
        position++;
        RequireBytes(bytes, name, position, length, what);
        var text = Encoding.Latin1.GetString(bytes.Slice(position, length));
        position += length;
        return text;
    }

    private static void RequireBytes(ReadOnlySpan<byte> bytes, string name, int position, int count, string what)
    {
        if (position + count > bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}': {what} at byte {position} needs {count} bytes, past the {bytes.Length}-byte file.");
        }
    }
}

/// <summary>One label table entry: an id and the ABSOLUTE offset of its first instruction.</summary>
/// <param name="Id">Label id. Unique per file; 31 of 32 retail files number them 1..N.</param>
/// <param name="Offset">Absolute byte offset of the first instruction, one marker past its own marker.</param>
/// <param name="TableOffset">Where this entry sits in the table, for messages.</param>
internal sealed record OblivionMobileScriptLabel(byte Id, ushort Offset, int TableOffset);

/// <summary>
///     One definition block, <c>0x1E kind ... 0x1F</c>. Every kind but 7 is a key/value list whose
///     value width depends on <c>(kind, key)</c>; key 0 is always present, always first, and is the
///     record's 1-based slot index.
/// </summary>
/// <param name="Kind">Which record table this row belongs to.</param>
/// <param name="Offset">Byte offset of the opening 0x1E.</param>
/// <param name="Length">Bytes from the 0x1E through the closing 0x1F.</param>
/// <param name="Slot">The key-0 value: the row's 1-based slot in its table. 0 for a raw-list block.</param>
/// <param name="Fields">Fields in file order; a repeated key is a repeated entry, which is how list values work.</param>
internal sealed record OblivionMobileScriptBlock(
    OblivionMobileBlockKind Kind,
    int Offset,
    int Length,
    byte Slot,
    IReadOnlyList<OblivionMobileScriptField> Fields)
{
    /// <summary>
    ///     Slots in a record of this kind, i.e. one past the highest legal key. These are the
    ///     consumer array sizes; a key at or above the bound would write past the record, so it is
    ///     rejected rather than guessed at. Kind 3 has no reader in the engine and never occurs.
    /// </summary>
    public static int SlotCount(OblivionMobileBlockKind kind)
    {
        return kind switch
        {
            OblivionMobileBlockKind.ActorTemplate => 21,
            OblivionMobileBlockKind.Armor => 10,
            OblivionMobileBlockKind.Potion => 14,
            OblivionMobileBlockKind.Weapon => 8,
            OblivionMobileBlockKind.PlayerClass => 15,
            OblivionMobileBlockKind.TileAttribute => 7,
            OblivionMobileBlockKind.RawByteList => OblivionMobileScript.RawListCapacity,
            OblivionMobileBlockKind.Spell => 15,
            OblivionMobileBlockKind.LevelParameters => 21,
            OblivionMobileBlockKind.LootEntry => 4,
            _ => 0
        };
    }

    /// <summary>True for the ten kinds the engine reads. Kind 3, and 11 and up, are not among them.</summary>
    public static bool IsKnownKind(byte kind)
    {
        return kind <= 10 && kind != 3;
    }

    /// <summary>
    ///     How the value after <paramref name="key" /> is encoded in a block of this kind. Untyped
    ///     keys take the kind's default width of one byte, read signed for the four item tables
    ///     (armor, weapon, class, spell — spell 8 "Restore Health" carries -50/-75/-100 where every
    ///     other spell's matching fields are positive) and unsigned for the rest. Signedness is the
    ///     one part of this table the bytes cannot settle on their own.
    /// </summary>
    public static OblivionMobileValueKind ValueKindOf(OblivionMobileBlockKind kind, byte key)
    {
        var typed = TypedValueKind(kind, key);
        if (typed is { } known)
        {
            return known;
        }

        return kind is OblivionMobileBlockKind.Armor
            or OblivionMobileBlockKind.Weapon
            or OblivionMobileBlockKind.PlayerClass
            or OblivionMobileBlockKind.Spell
            ? OblivionMobileValueKind.Signed
            : OblivionMobileValueKind.Unsigned;
    }

    private static OblivionMobileValueKind? TypedValueKind(OblivionMobileBlockKind kind, byte key)
    {
        return kind switch
        {
            // Actor template: key 1 is a plain sprite-set file name, not a text reference.
            OblivionMobileBlockKind.ActorTemplate => key switch
            {
                1 => OblivionMobileValueKind.Text,
                7 or 14 or 15 => OblivionMobileValueKind.UInt16,
                _ => null
            },
            OblivionMobileBlockKind.Armor => key switch
            {
                1 => OblivionMobileValueKind.TextRef,
                5 => OblivionMobileValueKind.UInt24,
                6 => OblivionMobileValueKind.Flag,
                9 => OblivionMobileValueKind.UInt16,
                _ => null
            },
            OblivionMobileBlockKind.Potion => key switch
            {
                1 => OblivionMobileValueKind.TextRef,
                4 => OblivionMobileValueKind.Flag,
                5 => OblivionMobileValueKind.UInt24,
                13 => OblivionMobileValueKind.UInt16,
                _ => null
            },
            OblivionMobileBlockKind.Weapon => key switch
            {
                1 => OblivionMobileValueKind.TextRef,
                7 => OblivionMobileValueKind.UInt16,
                _ => null
            },
            OblivionMobileBlockKind.PlayerClass => key switch
            {
                1 => OblivionMobileValueKind.TextRef,
                2 or 3 => OblivionMobileValueKind.ListItem,
                6 or 13 or 14 => OblivionMobileValueKind.UInt16,
                _ => null
            },
            OblivionMobileBlockKind.TileAttribute => key == 2 ? OblivionMobileValueKind.UInt16 : null,
            OblivionMobileBlockKind.Spell => key switch
            {
                1 => OblivionMobileValueKind.TextRef,
                6 => OblivionMobileValueKind.UInt24,
                14 => OblivionMobileValueKind.UInt16,
                _ => null
            },
            OblivionMobileBlockKind.LevelParameters => key switch
            {
                1 or 2 => OblivionMobileValueKind.UInt16,
                20 => OblivionMobileValueKind.ListItem,
                _ => null
            },
            _ => null
        };
    }
}

/// <summary>One key/value pair inside a definition block.</summary>
/// <param name="Key">The field key, below its kind's slot count. Always 0 for a raw-list byte.</param>
/// <param name="ValueKind">How the value was encoded.</param>
/// <param name="Value">The numeric value; a string's length; 1 for a flag; the lang id for a reference.</param>
/// <param name="Text">The decoded ISO-8859-1 string, for the two text encodings only.</param>
/// <param name="LangId">The referenced <c>lang_N.txt</c> id, when this field is a lang reference.</param>
internal sealed record OblivionMobileScriptField(
    byte Key,
    OblivionMobileValueKind ValueKind,
    int Value,
    string? Text,
    ushort? LangId);

/// <summary>One label's code: its marker is at <c>Offset - 3</c> and its bytes run to the next marker.</summary>
/// <param name="Label">The label id this chunk belongs to.</param>
/// <param name="Offset">First instruction byte.</param>
/// <param name="Length">Bytes of code, marker excluded.</param>
/// <param name="Instructions">Decoded instructions, including any that follow the terminating RET.</param>
/// <param name="DeadByteCount">
///     Bytes after the chunk's first RET. Non-zero only for label 6 of <c>l04_4b.scr</c> (5 bytes,
///     an orphaned duplicate of label 7's chunk that the authoring tool left behind).
/// </param>
internal sealed record OblivionMobileScriptChunk(
    byte Label,
    int Offset,
    int Length,
    IReadOnlyList<OblivionMobileScriptInstruction> Instructions,
    int DeadByteCount);

/// <summary>One decoded instruction.</summary>
/// <param name="Offset">Byte offset of the opcode.</param>
/// <param name="Length">Opcode plus operands.</param>
/// <param name="Opcode">The opcode byte.</param>
/// <param name="Mnemonic">This decoder's name for it.</param>
/// <param name="Operands">Operands in file order.</param>
internal sealed record OblivionMobileScriptInstruction(
    int Offset,
    int Length,
    byte Opcode,
    string Mnemonic,
    IReadOnlyList<OblivionMobileScriptOperand> Operands);

/// <summary>One operand.</summary>
/// <param name="Kind">What the operand is.</param>
/// <param name="Value">
///     The number, the string's length, the lang id, the list count, or opcode 34's sub-command.
/// </param>
/// <param name="Text">The decoded string, for an inline text operand.</param>
/// <param name="Bytes">The list items, for a byte-list operand.</param>
/// <param name="SubArgument">
///     Opcode 34's sub-command argument — one byte, one big-endian u16, or nothing at all,
///     depending on the sub-command.
/// </param>
internal sealed record OblivionMobileScriptOperand(
    OblivionMobileOperandKind Kind,
    int Value,
    string? Text = null,
    IReadOnlyList<byte>? Bytes = null,
    int? SubArgument = null);

/// <summary>A map load: the tile map and the sprite set that opcode 8 pulls in together.</summary>
/// <param name="TileMap">The <c>.jtm</c> name, as written (leading slash included).</param>
/// <param name="SpriteSet">The <c>.cml</c> name, as written.</param>
internal sealed record OblivionMobileMapReference(string TileMap, string SpriteSet);

/// <summary>
///     The ten definition block kinds. The names come from the data each table resolves to — the
///     armor table's key 1 references "Leather Bracer".."Robe of Magicka", the spell table's the
///     nine spells, and so on. Kind 3 is absent: the engine has no reader for it.
/// </summary>
internal enum OblivionMobileBlockKind
{
    /// <summary>Actor template; 20 rows, all in <c>startup.scr</c>, each naming an <c>/oh_*.cml</c>.</summary>
    ActorTemplate = 0,

    /// <summary>Armor or worn item; 41 rows.</summary>
    Armor = 1,

    /// <summary>Potion or consumable; 10 rows.</summary>
    Potion = 2,

    /// <summary>Weapon; 36 rows.</summary>
    Weapon = 4,

    /// <summary>Player class; the 8 selectable classes.</summary>
    PlayerClass = 5,

    /// <summary>Tile attribute; 248 rows across 21 level scripts, slots 1..15.</summary>
    TileAttribute = 6,

    /// <summary>A flat byte list copied in pairs; 13 blocks, 50 or 52 bytes each. Role unknown.</summary>
    RawByteList = 7,

    /// <summary>Spell; the 9 spells.</summary>
    Spell = 8,

    /// <summary>Level parameters; 13 rows across 7 level scripts, slots 1..3.</summary>
    LevelParameters = 9,

    /// <summary>Loot table entry; 172 rows across 21 level scripts, slots 1..10.</summary>
    LootEntry = 10
}

/// <summary>How a definition-block field's value was encoded.</summary>
internal enum OblivionMobileValueKind
{
    /// <summary>One byte, unsigned.</summary>
    Unsigned,

    /// <summary>One byte, sign-extended by the consumer.</summary>
    Signed,

    /// <summary>Two bytes, big-endian.</summary>
    UInt16,

    /// <summary>Three bytes, big-endian — an RGB colour everywhere it occurs.</summary>
    UInt24,

    /// <summary>A u8-length string; only the actor template's sprite-set name.</summary>
    Text,

    /// <summary>A <c>lang_N.txt</c> id, from the <c>0xF</c> branch of a block text reference.</summary>
    LangId,

    /// <summary>An inline string, from the other branch. Never occurs in retail blocks.</summary>
    InlineText,

    /// <summary>The key alone, with no value; the consumer stores 1.</summary>
    Flag,

    /// <summary>One byte appended to a per-block list, so the key may repeat.</summary>
    ListItem,

    /// <summary>One byte of a kind-7 raw list, which has no keys at all.</summary>
    RawByte,

    /// <summary>Placeholder for the text reference before its branch is known.</summary>
    TextRef
}

/// <summary>What one decoded instruction operand is.</summary>
internal enum OblivionMobileOperandKind
{
    /// <summary>One unsigned byte.</summary>
    UInt8,

    /// <summary>A big-endian u16.</summary>
    UInt16,

    /// <summary>A big-endian u24; an RGB colour wherever it occurs.</summary>
    UInt24,

    /// <summary>A label id in this script's own table.</summary>
    Label,

    /// <summary>A live object/actor slot.</summary>
    ObjectSlot,

    /// <summary>A string carried in the script itself.</summary>
    InlineText,

    /// <summary>A <c>lang_N.txt</c> id.</summary>
    LangId,

    /// <summary>An optional text reference that carries none (a zero word).</summary>
    NoText,

    /// <summary>A counted byte list.</summary>
    ByteList,

    /// <summary>Opcode 34's sub-command, with its argument in <c>SubArgument</c>.</summary>
    SubCommand
}
