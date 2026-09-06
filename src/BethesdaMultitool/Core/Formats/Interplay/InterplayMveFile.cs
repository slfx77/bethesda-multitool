using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Interplay;

/// <summary>One chunk of an <see cref="InterplayMveFile" />: a length, a type and its opcodes.</summary>
/// <param name="Type">Chunk type. Retail uses 0-5.</param>
/// <param name="Offset">Byte offset of the chunk header.</param>
/// <param name="Length">Payload length, excluding the 4-byte header.</param>
/// <param name="Opcodes">The opcode bytes found inside, in order.</param>
internal readonly record struct MveChunk(int Type, int Offset, int Length, IReadOnlyList<byte> Opcodes);

/// <summary>
///     An Interplay <c>.MVE</c> movie — <b>IDENTIFY ONLY</b>, used by Fallout, Fallout 2 and the
///     cancelled Van Buren prototype.
///     <para>
///         ⛔ <b>This deliberately does NOT decode video or audio.</b> The only complete decoder is
///         FFmpeg's, which is LGPL, so decoding would need a clean-room effort this does not attempt.
///         The plan scopes this format to identification and metadata, and that is exactly what is
///         here: enough to list a movie, report its geometry and prove the container walks.
///     </para>
///     <para>
///         Layout: a 26-byte signature — the ASCII <c>"Interplay MVE File\x1a\x00"</c> then six more
///         bytes — followed by chunks of <c>u16 length + u16 type</c>, each holding opcodes of
///         <c>u16 length + u8 opcode + u8 version + data</c>. Everything is LITTLE-endian.
///         ⚑ <b>All 13 retail movies tile exactly</b> from +26 to EOF under that reading, which is
///         what makes this a parse rather than a sniff.
///     </para>
///     <para>
///         Opcodes identified: <c>0x0A</c> carries the screen size as two u16 (640x480 on retail),
///         <c>0x05</c> the video buffer in 8-pixel blocks, <c>0x0C</c> a palette (start, count, then
///         count RGB triples), <c>0x03</c> a timer, <c>0x08</c>/<c>0x09</c> audio payloads, and
///         <c>0x01</c> terminates a chunk. The rest are left uninterpreted.
///     </para>
/// </summary>
internal sealed class InterplayMveFile
{
    /// <summary>The ASCII signature, without its trailing control bytes.</summary>
    public const string Signature = "Interplay MVE File";

    /// <summary>Bytes of signature before the first chunk.</summary>
    public const int HeaderLength = 26;

    /// <summary>Opcode carrying the screen dimensions as two little-endian u16.</summary>
    public const byte ScreenSizeOpcode = 0x0A;

    /// <summary>Opcode carrying a palette: start, count, then count RGB triples.</summary>
    public const byte PaletteOpcode = 0x0C;

    /// <summary>Opcode terminating a chunk.</summary>
    public const byte EndOfChunkOpcode = 0x01;

    private InterplayMveFile(string name, int width, int height, IReadOnlyList<MveChunk> chunks)
    {
        Name = name;
        Width = width;
        Height = height;
        Chunks = chunks;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>Screen width from opcode <see cref="ScreenSizeOpcode" />, or 0 when absent.</summary>
    public int Width { get; }

    /// <summary>Screen height, or 0 when absent.</summary>
    public int Height { get; }

    /// <summary>Every chunk, in file order.</summary>
    public IReadOnlyList<MveChunk> Chunks { get; }

    /// <summary>How many chunks carry a palette.</summary>
    public int PaletteChunks => Chunks.Count(c => c.Opcodes.Contains(PaletteOpcode));

    /// <summary>Content probe: the ASCII signature.</summary>
    public static bool IsMveFile(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= HeaderLength && bytes[..Signature.Length].SequenceEqual(Encoding.ASCII.GetBytes(Signature));
    }

    /// <summary>Reads a movie's metadata, throwing when the container does not walk.</summary>
    public static InterplayMveFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var movie, out var error))
        {
            throw new InvalidDataException(error);
        }

        return movie;
    }

    /// <summary>Reads a movie's metadata, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, string name, out InterplayMveFile movie, out string error)
    {
        movie = null!;
        if (!IsMveFile(bytes))
        {
            error = $"{name}: does not open with the '{Signature}' signature.";
            return false;
        }

        var chunks = new List<MveChunk>();
        var width = 0;
        var height = 0;
        var position = HeaderLength;

        while (position + 4 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes[position..]);
            var type = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(position + 2)..]);
            var end = position + 4 + length;
            if (end > bytes.Length)
            {
                error = $"{name}: chunk at {position} declares {length} bytes and runs past the file.";
                return false;
            }

            var opcodes = new List<byte>();
            var cursor = position + 4;
            while (cursor + 4 <= end)
            {
                var opcodeLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[cursor..]);
                var opcode = bytes[cursor + 2];
                if (cursor + 4 + opcodeLength > end)
                {
                    break;
                }

                opcodes.Add(opcode);
                if (opcode == ScreenSizeOpcode && opcodeLength >= 4 && width == 0)
                {
                    width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(cursor + 4)..]);
                    height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(cursor + 6)..]);
                }

                cursor += 4 + opcodeLength;
            }

            chunks.Add(new MveChunk(type, position, length, opcodes));
            position = end;
        }

        if (position != bytes.Length)
        {
            error = $"{name}: chunks end at {position} of {bytes.Length} bytes.";
            return false;
        }

        movie = new InterplayMveFile(name, width, height, chunks);
        error = string.Empty;
        return true;
    }
}
