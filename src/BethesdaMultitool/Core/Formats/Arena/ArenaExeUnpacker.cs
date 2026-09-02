// Ported from OpenTESArena (MIT License), https://github.com/afritz1/OpenTESArena
//   OpenTESArena/src/Assets/ExeUnpacker.cpp / ExeUnpacker.h, which in turn follows the community
//   pklite_specification.md sections 4.3.1 ("Number of bytes") and 4.3.2 ("Offset"). License texts
//   are collected centrally in THIRD_PARTY_LICENSES.
//
// Divergence: the reference stores the two prefix-code tables as arrays of bool and walks them
// with a heap-allocated binary tree. They are written here as bit strings and resolved through a
// (length, code) dictionary — the same codes, in a form that can be read off against the
// specification without decoding 24 bool arrays by eye. Bounds checks are added throughout; the
// reference has none because it only ever opens the shipped executable.

using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Arena;

/// <summary>
///     Unpacks Arena's PKLITE-compressed executable (<c>A.EXE</c>). The game keeps a great deal of
///     content in the executable rather than in data files — item and spell tables, city layouts,
///     creature statistics, the province map — so reaching any of it means undoing this
///     compression first.
///     <para>
///         The stream interleaves two modes selected one bit at a time. A 0 bit means the next
///         byte is a literal, lightly obfuscated by XOR with a key derived from the bit position.
///         A 1 bit means a back-reference, whose length and high offset byte are each read as a
///         variable-length prefix code.
///     </para>
/// </summary>
internal static class ArenaExeUnpacker
{
    /// <summary>Offset of the compressed payload inside the executable.</summary>
    public const int CompressedStart = 752;

    /// <summary>Bytes of trailer after the compressed payload: the decompressed-size pair plus padding.</summary>
    public const int TrailerLength = 8;

    /// <summary>Sentinel for the length code that means "read the count from the next byte instead".</summary>
    private const int EscapeLengthCode = -1;

    /// <summary>Byte value that follows the escape code to mean "skip this bit".</summary>
    private const byte EscapeSkip = 0xFE;

    /// <summary>Byte value that follows the escape code to mean "the stream is finished".</summary>
    private const byte EscapeEnd = 0xFF;

    /// <summary>
    ///     Back-reference length codes (pklite specification 4.3.1). Values 2..24, with one escape
    ///     code whose length is instead carried by the following byte.
    /// </summary>
    private static readonly (string Code, int Value)[] LengthCodes =
    [
        ("10", 2), ("11", 3), ("000", 4), ("0010", 5), ("0011", 6), ("0100", 7),
        ("01010", 8), ("01011", 9), ("01100", 10), ("011010", 11), ("011011", 12),
        ("011100", EscapeLengthCode),
        ("0111010", 13), ("0111011", 14), ("0111100", 15),
        ("01111010", 16), ("01111011", 17), ("01111100", 18),
        ("011111010", 19), ("011111011", 20), ("011111100", 21),
        ("011111101", 22), ("011111110", 23), ("011111111", 24)
    ];

    /// <summary>
    ///     High-offset-byte codes (pklite specification 4.3.2). The decoded value is the most
    ///     significant byte of the back-reference distance.
    /// </summary>
    private static readonly (string Code, int Value)[] OffsetCodes =
    [
        ("1", 0), ("0000", 1), ("0001", 2), ("00100", 3), ("00101", 4), ("00110", 5), ("00111", 6),
        ("010000", 7), ("010001", 8), ("010010", 9), ("010011", 10), ("010100", 11), ("010101", 12),
        ("010110", 13), ("0101110", 14), ("0101111", 15),
        ("0110000", 16), ("0110001", 17), ("0110010", 18), ("0110011", 19),
        ("0110100", 20), ("0110101", 21), ("0110110", 22), ("0110111", 23),
        ("0111000", 24), ("0111001", 25), ("0111010", 26), ("0111011", 27),
        ("0111100", 28), ("0111101", 29), ("0111110", 30), ("0111111", 31)
    ];

    private static readonly Dictionary<(int Length, int Code), int> LengthTable = BuildTable(LengthCodes);
    private static readonly Dictionary<(int Length, int Code), int> OffsetTable = BuildTable(OffsetCodes);

    /// <summary>
    ///     True when the file is long enough to hold a compressed payload and ends with the
    ///     0xFFFF terminator the format requires.
    /// </summary>
    public static bool LooksPacked(ReadOnlySpan<byte> exe)
    {
        return exe.Length > CompressedStart + TrailerLength
               && BinaryPrimitives.ReadUInt16LittleEndian(exe[(exe.Length - TrailerLength - 2)..]) == 0xFFFF;
    }

    /// <summary>Reads the decompressed size the trailer declares, without decompressing.</summary>
    public static int ReadDeclaredSize(ReadOnlySpan<byte> exe)
    {
        var trailer = exe[(exe.Length - TrailerLength)..];
        var segment = BinaryPrimitives.ReadUInt16LittleEndian(trailer);
        var offset = BinaryPrimitives.ReadUInt16LittleEndian(trailer[2..]);

        // A real-mode far address: the size is the segment scaled by the paragraph size, plus the
        // offset within it.
        return (segment * 16) + offset;
    }

    /// <summary>Unpacks the executable, returning its decompressed image.</summary>
    public static byte[] Unpack(ReadOnlySpan<byte> exe, string name)
    {
        if (exe.Length <= CompressedStart + TrailerLength)
        {
            throw new InvalidDataException(
                $"'{name}' is too small to be a packed Arena executable ({exe.Length} bytes).");
        }

        var compressedEnd = exe.Length - TrailerLength;
        var terminator = BinaryPrimitives.ReadUInt16LittleEndian(exe[(compressedEnd - 2)..]);
        if (terminator != 0xFFFF)
        {
            throw new InvalidDataException(
                $"'{name}' does not end with the 0xFFFF terminator (found 0x{terminator:X4}); " +
                "it is probably not PKLITE-packed.");
        }

        var declaredSize = ReadDeclaredSize(exe);
        if (declaredSize <= 0)
        {
            throw new InvalidDataException($"'{name}' declares a decompressed size of {declaredSize}.");
        }

        var output = new byte[declaredSize];
        var written = 0;

        var compressed = exe[CompressedStart..compressedEnd];
        var reader = new BitReader(compressed);

        while (true)
        {
            if (!reader.TryReadBit(out var duplicating))
            {
                throw new InvalidDataException($"'{name}' ends before the compressed stream terminates.");
            }

            if (!duplicating)
            {
                // Literal: XOR with a key derived from how far into the current bit array we are.
                if (!reader.TryReadByte(out var encrypted))
                {
                    throw new InvalidDataException($"'{name}' ends inside a literal byte.");
                }

                var key = (byte)(16 - reader.BitsRead);
                Write(output, ref written, (byte)(encrypted ^ key), name);
                continue;
            }

            var length = ReadCode(ref reader, LengthTable, name, "length");
            if (length == EscapeLengthCode)
            {
                if (!reader.TryReadByte(out var escape))
                {
                    throw new InvalidDataException($"'{name}' ends inside an escape length.");
                }

                if (escape == EscapeSkip)
                {
                    continue;
                }

                if (escape == EscapeEnd)
                {
                    break;
                }

                // Lengths past the table's 24 continue from 25 upward.
                length = escape + 25;
            }

            // A length of 2 always uses a single-byte distance, so its high byte is not coded.
            var high = length != 2 ? ReadCode(ref reader, OffsetTable, name, "offset") : 0;

            if (!reader.TryReadByte(out var low))
            {
                throw new InvalidDataException($"'{name}' ends inside a back-reference distance.");
            }

            var distance = low | (high << 8);
            var source = written - distance;
            if (source < 0)
            {
                throw new InvalidDataException(
                    $"'{name}' has a back-reference {distance} bytes before the start of output.");
            }

            // Copied byte by byte on purpose: a run may overlap its own output, which is how the
            // format encodes repeated sequences.
            for (var i = 0; i < length; i++)
            {
                Write(output, ref written, output[source + i], name);
            }
        }

        if (written < output.Length)
        {
            // The declared size is a paragraph-rounded far address, so a short tail of zero
            // padding is expected and harmless; anything larger means the stream ended early.
            Array.Resize(ref output, written);
        }

        return output;
    }

    private static void Write(byte[] output, ref int written, byte value, string name)
    {
        if (written >= output.Length)
        {
            throw new InvalidDataException(
                $"'{name}' produced more than the {output.Length} bytes its trailer declares.");
        }

        output[written++] = value;
    }

    /// <summary>Reads bits until they spell a code in <paramref name="table" />.</summary>
    private static int ReadCode(
        ref BitReader reader,
        Dictionary<(int Length, int Code), int> table,
        string name,
        string what)
    {
        const int maxCodeLength = 9;

        var code = 0;
        for (var length = 1; length <= maxCodeLength; length++)
        {
            if (!reader.TryReadBit(out var bit))
            {
                throw new InvalidDataException($"'{name}' ends inside a {what} code.");
            }

            code = (code << 1) | (bit ? 1 : 0);
            if (table.TryGetValue((length, code), out var value))
            {
                return value;
            }
        }

        throw new InvalidDataException($"'{name}' contains an unrecognized {what} code.");
    }

    private static Dictionary<(int Length, int Code), int> BuildTable((string Code, int Value)[] codes)
    {
        var table = new Dictionary<(int, int), int>(codes.Length);
        foreach (var (code, value) in codes)
        {
            var bits = 0;
            foreach (var c in code)
            {
                bits = (bits << 1) | (c == '1' ? 1 : 0);
            }

            table[(code.Length, bits)] = value;
        }

        return table;
    }

    /// <summary>
    ///     The format's bit stream: 16-bit little-endian arrays whose bits are consumed from the
    ///     least significant end, with whole bytes read from between the arrays.
    /// </summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private ushort _bitArray;
        private int _byteIndex;

        public BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _bitArray = data.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(data) : (ushort)0;
            _byteIndex = 2;
            BitsRead = 0;
        }

        /// <summary>
        ///     Bits consumed from the current array, 0..15. The literal-decryption key is derived
        ///     from this, so it is part of the format rather than bookkeeping.
        /// </summary>
        public int BitsRead { get; private set; }

        public bool TryReadBit(out bool bit)
        {
            bit = (_bitArray & (1 << BitsRead)) != 0;
            BitsRead++;

            if (BitsRead != 16)
            {
                return true;
            }

            BitsRead = 0;
            if (_byteIndex + 2 > _data.Length)
            {
                // The stream should have terminated before running out; report it rather than
                // silently feeding zero bits like the reference would.
                _bitArray = 0;
                return _byteIndex <= _data.Length;
            }

            _bitArray = (ushort)(_data[_byteIndex] | (_data[_byteIndex + 1] << 8));
            _byteIndex += 2;
            return true;
        }

        public bool TryReadByte(out byte value)
        {
            if (_byteIndex >= _data.Length)
            {
                value = 0;
                return false;
            }

            value = _data[_byteIndex];
            _byteIndex++;
            return true;
        }
    }
}
