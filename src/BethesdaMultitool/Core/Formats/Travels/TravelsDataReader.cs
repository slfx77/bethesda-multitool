using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Travels;

/// <summary>
///     The one big-endian cursor every Elder Scrolls Travels data table is read through — the
///     J2ME games (Stormhold 2003, Dawnstar 2004) write their tables with
///     <c>java.io.DataOutputStream</c>, so the whole family is big-endian with Java's exact read
///     primitives. Original RE from the bytes (2026-09-05); both games' tables reuse this cursor
///     because the layouts are identical and only the content differs.
///     <para>
///         <c>UTF</c> below means Java's string form: an unsigned 16-bit BYTE length followed by
///         that many bytes — a length in bytes, never in characters. Java writes "modified UTF-8"
///         (a NUL as <c>C0 80</c>, astral characters as CESU-8 surrogate pairs), but neither quirk
///         occurs in either game: all 1,030 retail strings across the two games decode under a
///         STRICT UTF-8 decoder, and the only non-ASCII text is U+2019 (<c>E2 80 99</c>) and
///         U+2026 in Stormhold's <c>npcstrings.dat</c>. So this reads plain UTF-8 and treats a
///         decode failure as file corruption rather than silently substituting U+FFFD.
///     </para>
///     <para>
///         <b>Trap:</b> a whole-file UTF-8 decode of Dawnstar's <c>npcstrings.dat</c> DOES fail
///         (byte 0xB7 at 727, 0x81 at 1019, 11 more) — those bytes are string LENGTH prefixes, not
///         text. Decoding string by string, as here, succeeds on every one of its 167 strings, so
///         the "Dawnstar strings are not valid UTF-8" note in the earlier survey is an artefact of
///         decoding the whole file at once.
///     </para>
///     <para>
///         Every table's identification test is arithmetic, not a magic number: these files have
///         no signature and the game opens them by name, so "the declared structure consumes the
///         payload to its last byte" is all a reader has. <see cref="ExpectEnd" /> is that guard,
///         and every table parser ends with it.
///     </para>
/// </summary>
internal ref struct TravelsDataReader
{
    /// <summary>Bytes in a <c>UTF</c> string's length prefix.</summary>
    public const int UtfLengthPrefixBytes = 2;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly ReadOnlySpan<byte> _bytes;
    private readonly string _name;
    private int _position;

    /// <summary>Wraps one table payload; <paramref name="name" /> appears in every message.</summary>
    public TravelsDataReader(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        _bytes = bytes;
        _name = name;
        _position = 0;
    }

    /// <summary>Byte offset of the next read, from the start of the payload.</summary>
    public readonly int Position => _position;

    /// <summary>Total payload length.</summary>
    public readonly int Length => _bytes.Length;

    /// <summary>Bytes left unread.</summary>
    public readonly int Remaining => _bytes.Length - _position;

    /// <summary>Java <c>readByte</c>: one SIGNED byte.</summary>
    public sbyte ReadInt8()
    {
        Require(1, "a signed byte");
        var value = (sbyte)_bytes[_position];
        _position++;
        return value;
    }

    /// <summary>Java <c>readUnsignedByte</c>: one unsigned byte.</summary>
    public byte ReadUInt8()
    {
        Require(1, "an unsigned byte");
        var value = _bytes[_position];
        _position++;
        return value;
    }

    /// <summary>Java <c>readShort</c>: a signed big-endian 16-bit value.</summary>
    public short ReadInt16()
    {
        Require(2, "a signed 16-bit value");
        var value = BinaryPrimitives.ReadInt16BigEndian(_bytes[_position..]);
        _position += 2;
        return value;
    }

    /// <summary>Java <c>readUnsignedShort</c>: an unsigned big-endian 16-bit value.</summary>
    public ushort ReadUInt16()
    {
        Require(2, "an unsigned 16-bit value");
        var value = BinaryPrimitives.ReadUInt16BigEndian(_bytes[_position..]);
        _position += 2;
        return value;
    }

    /// <summary>Java <c>readInt</c>: a signed big-endian 32-bit value.</summary>
    public int ReadInt32()
    {
        Require(4, "a signed 32-bit value");
        var value = BinaryPrimitives.ReadInt32BigEndian(_bytes[_position..]);
        _position += 4;
        return value;
    }

    /// <summary>
    ///     Java <c>readUTF</c>: an unsigned 16-bit byte length followed by that many UTF-8 bytes.
    ///     Throws when the length or its payload runs past the file, or when the bytes are not
    ///     valid UTF-8 — which on these fixtures only happens once the cursor has desynchronised
    ///     and is reading a length prefix as text.
    /// </summary>
    public string ReadUtf()
    {
        var start = _position;
        int length = ReadUInt16();
        Require(length, $"a {length}-byte string");

        var raw = _bytes.Slice(_position, length);
        _position += length;

        try
        {
            return StrictUtf8.GetString(raw);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(
                $"'{_name}': the {length}-byte string at byte {start} is not valid UTF-8.", ex);
        }
    }

    /// <summary>
    ///     A counted string list: a Java <c>readShort</c> count followed by that many
    ///     <see cref="ReadUtf" /> strings. The count is SIGNED, so a negative one is a violation
    ///     rather than a huge unsigned length.
    /// </summary>
    public ImmutableArray<string> ReadUtfList16()
    {
        var start = _position;
        int count = ReadInt16();
        if (count < 0)
        {
            throw new InvalidDataException(
                $"'{_name}': string list count {count} at byte {start} is negative.");
        }

        return ReadUtfArray(count);
    }

    /// <summary>Reads <paramref name="count" /> back-to-back <see cref="ReadUtf" /> strings.</summary>
    public ImmutableArray<string> ReadUtfArray(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        // A string costs at least its 2-byte prefix, so a count past that bound cannot possibly
        // tile — checking it here keeps a corrupt count from provoking a huge allocation first.
        if ((long)count * UtfLengthPrefixBytes > Remaining)
        {
            throw new InvalidDataException(
                $"'{_name}': {count} strings need at least {(long)count * UtfLengthPrefixBytes} bytes "
                + $"at byte {_position}, past the {_bytes.Length}-byte file.");
        }

        var builder = ImmutableArray.CreateBuilder<string>(count);
        for (var i = 0; i < count; i++)
        {
            builder.Add(ReadUtf());
        }

        return builder.MoveToImmutable();
    }

    /// <summary>
    ///     The tiling guard: the declared structure must have consumed the payload exactly. These
    ///     formats carry no magic, so trailing bytes mean the layout is wrong — not that the file
    ///     has a trailer.
    /// </summary>
    public readonly void ExpectEnd()
    {
        if (_position != _bytes.Length)
        {
            throw new InvalidDataException(
                $"'{_name}': the layout ends at byte {_position} but the file is {_bytes.Length} bytes "
                + $"({Remaining} trailing bytes).");
        }
    }

    private readonly void Require(int count, string what)
    {
        if (count > Remaining)
        {
            throw new InvalidDataException(
                $"'{_name}': {what} needs {count} bytes at byte {_position}, past the {_bytes.Length}-byte file.");
        }
    }
}
