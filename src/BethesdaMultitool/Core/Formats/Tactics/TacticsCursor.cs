using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Tactics;

/// <summary>
///     A bounds-checked little-endian cursor over a Fallout Tactics buffer, with the family's two
///     string conventions and its <c>'&lt;tag&gt;'</c> framing built in. Every reader that claims
///     to TILE a file (consume it exactly, every declared count satisfied) walks through this so
///     that a misread cannot run silently: an overrun, a string with the wrong flag or a tag that
///     is not where it should be throws <see cref="InvalidDataException" /> naming the offset.
///     Original RE 2026-09-06/07; every Tactics reference is GPL, so nothing is ported.
///     <para>
///         ⚑ <b>Two string encodings share one u32 length prefix, split by bit 31.</b> Bit 31
///         CLEAR is the plain <c>u32 length + ASCII bytes</c> the <c>.ent</c>/<c>.mis</c> files use
///         (<see cref="TacticsPropertyBag" />). Bit 31 SET is <c>u32 (0x80000000 | n)</c> followed by
///         <c>n</c> UTF-16LE code units with no terminator, so <c>0x80000000</c> alone is an EMPTY
///         wide string. Both occur in the SAME file — a save's own fields are wide while the
///         <c>&lt;esh&gt;</c> property bags inside it stay ASCII — which is why
///         <see cref="String" /> dispatches on the flag instead of a caller choosing.
///     </para>
///     <para>
///         ⛔ The first reading of <c>Snake.sav</c>'s header was "u32 1, then a LONE 0x80 byte,
///         then 0x8000000D 'New Save Game'". That was refuted by the SECOND <c>&lt;saveh&gt;</c>
///         embedded in the same file, whose body is <c>00 | 2B 00 00 80 | 'locale/...'</c>: only
///         <c>u8 + wide string</c> tiles both headers. The "lone 0x80" is the high byte of an empty
///         wide string.
///     </para>
/// </summary>
internal sealed class TacticsCursor
{
    /// <summary>Bit 31 of a length prefix: set marks a UTF-16LE string, clear an ASCII one.</summary>
    public const uint WideStringFlag = 0x8000_0000;

    /// <summary>
    ///     Longest string accepted, in code units. The retail briefing text is 1,732 characters and
    ///     the largest shipped speech file 41 KB, so this bounds a misread's allocation without
    ///     touching real data. The fit-inside-the-buffer check is the real guard.
    /// </summary>
    public const int MaximumStringLength = 1 << 20;

    private readonly ReadOnlyMemory<byte> _bytes;

    public TacticsCursor(ReadOnlyMemory<byte> bytes, string name, int position = 0)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, bytes.Length);

        _bytes = bytes;
        Name = name;
        Position = position;
    }

    /// <summary>The source name every error message carries, for readers that raise their own.</summary>
    public string Name { get; }

    /// <summary>Current offset into the buffer.</summary>
    public int Position { get; set; }

    /// <summary>Total buffer length.</summary>
    public int Length => _bytes.Length;

    /// <summary>Bytes left after <see cref="Position" />.</summary>
    public int Remaining => _bytes.Length - Position;

    /// <summary>True exactly when the walk has consumed the buffer.</summary>
    public bool AtEnd => Position == _bytes.Length;

    /// <summary>The whole buffer, for readers that slice it.</summary>
    public ReadOnlyMemory<byte> Memory => _bytes;

    /// <summary>The bytes from <see cref="Position" /> on.</summary>
    public ReadOnlySpan<byte> Ahead => _bytes.Span[Position..];

    public byte U8()
    {
        Need(1, "a byte");
        return _bytes.Span[Position++];
    }

    public ushort U16()
    {
        Need(2, "a u16");
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.Span[Position..]);
        Position += 2;
        return value;
    }

    public uint U32()
    {
        Need(4, "a u32");
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.Span[Position..]);
        Position += 4;
        return value;
    }

    public int I32()
    {
        Need(4, "an i32");
        var value = BinaryPrimitives.ReadInt32LittleEndian(_bytes.Span[Position..]);
        Position += 4;
        return value;
    }

    public float F32()
    {
        Need(4, "a float");
        var value = BinaryPrimitives.ReadSingleLittleEndian(_bytes.Span[Position..]);
        Position += 4;
        return value;
    }

    /// <summary>A u32 read as a count, refused above <paramref name="maximum" />.</summary>
    public int Count(int maximum, string what)
    {
        var at = Position;
        var value = U32();
        if (value > (uint)maximum)
        {
            throw Fail(at, $"{what} count {value} exceeds {maximum}");
        }

        return (int)value;
    }

    /// <summary>Raw bytes, sliced from the buffer without copying.</summary>
    public ReadOnlyMemory<byte> Bytes(int count, string what)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Need(count, what);
        var slice = _bytes.Slice(Position, count);
        Position += count;
        return slice;
    }

    /// <summary>Skips bytes that must all be zero, refusing otherwise.</summary>
    public void Zeros(int count, string what)
    {
        var at = Position;
        var slice = Bytes(count, what).Span;
        if (slice.IndexOfAnyExcept((byte)0) >= 0)
        {
            throw Fail(at, $"{what}: expected {count} zero bytes");
        }
    }

    /// <summary>Requires an exact byte sequence, e.g. a bracketless tag.</summary>
    public void Expect(ReadOnlySpan<byte> literal, string what)
    {
        var at = Position;
        if (!Bytes(literal.Length, what).Span.SequenceEqual(literal))
        {
            throw Fail(at, $"expected {what}");
        }
    }

    /// <summary>A string of either encoding, dispatched on bit 31 of its length prefix.</summary>
    public string String()
    {
        return ReadString(null);
    }

    /// <summary>A string whose prefix MUST carry the wide flag.</summary>
    public string WideString()
    {
        return ReadString(true);
    }

    /// <summary>A string whose prefix MUST NOT carry the wide flag.</summary>
    public string AsciiString()
    {
        return ReadString(false);
    }

    /// <summary>
    ///     Reads the family's <c>'&lt;tag&gt;' NUL version NUL</c> framing and requires the tag.
    ///     The version is returned rather than pinned: a reader that has measured one version
    ///     records it and decides for itself.
    /// </summary>
    public TacticsTagChunk Tag(string expected)
    {
        ArgumentNullException.ThrowIfNull(expected);

        var at = Position;
        if (!TacticsTagChunk.TryRead(Ahead, out var chunk))
        {
            throw Fail(at, $"expected a <{expected}> tag");
        }

        if (!string.Equals(chunk.Tag, expected, StringComparison.Ordinal))
        {
            throw Fail(at, $"expected a <{expected}> tag but found <{chunk.Tag}>");
        }

        Position += chunk.BodyOffset;
        return chunk;
    }

    /// <summary>
    ///     Reads a <c>&lt;esh&gt;</c> v1 property block in place — the same self-describing walk
    ///     <see cref="TacticsPropertyBag" /> performs on a whole <c>.ent</c>, but starting at the
    ///     cursor and stopping after the last property, so blocks embedded in a larger stream (a
    ///     campaign's location list, a save world's entities) can be walked without knowing their
    ///     size in advance. Verified on the 57 campaign bags and the first world entity of the
    ///     retail save, and the 24,626 properties of the shipped <c>.ent</c>/<c>.chr</c> files.
    /// </summary>
    public List<TacticsProperty> Properties()
    {
        Tag("esh");
        var count = Count(4096, "property");
        var properties = new List<TacticsProperty>(count);
        for (var i = 0; i < count; i++)
        {
            var propertyName = AsciiString();
            var type = U32();
            var size = Count(int.MaxValue, $"property '{propertyName}' payload");
            properties.Add(new TacticsProperty(propertyName, type, Bytes(size, $"property '{propertyName}' payload")));
        }

        return properties;
    }

    /// <summary>
    ///     The text of a string-typed <c>&lt;esh&gt;</c> property. Text payloads (types 4, 9 and 25
    ///     in the measured bags) are themselves <c>u32 length + ASCII</c>, so
    ///     <see cref="TacticsProperty.AsText" />'s raw read would carry the four length bytes;
    ///     this strips them when — and only when — the prefix equals the remaining length.
    /// </summary>
    public static string PropertyText(in TacticsProperty property)
    {
        var value = property.Value.Span;
        if (value.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(value) == (uint)(value.Length - 4))
        {
            return Encoding.ASCII.GetString(value[4..]);
        }

        return property.AsText;
    }

    /// <summary>Throws unless the walk landed exactly on the end of the buffer.</summary>
    public void RequireEnd(string what)
    {
        if (!AtEnd)
        {
            throw Fail(Position, $"{what} ends at {Position} of {Length} bytes");
        }
    }

    /// <summary>An <see cref="InvalidDataException" /> naming the file and offset.</summary>
    public InvalidDataException Fail(int at, string message)
    {
        return new InvalidDataException($"{Name} @{at}: {message}.");
    }

    private string ReadString(bool? requireWide)
    {
        var at = Position;
        var prefix = U32();
        var wide = (prefix & WideStringFlag) != 0;
        if (requireWide is { } required && required != wide)
        {
            throw Fail(at, required
                ? $"expected a wide string but the prefix 0x{prefix:X8} has bit 31 clear"
                : $"expected an ASCII string but the prefix 0x{prefix:X8} has bit 31 set");
        }

        var units = prefix & ~WideStringFlag;
        if (units > MaximumStringLength)
        {
            throw Fail(at, $"string length {units} exceeds {MaximumStringLength}");
        }

        var byteLength = (int)units * (wide ? 2 : 1);
        if (byteLength > Remaining)
        {
            // Reported at the string's START, not after its prefix: the offset a reader wants
            // is where the string field began.
            throw Fail(at,
                $"a {(wide ? "wide" : "ASCII")} string of {units} needs {byteLength} bytes but {Remaining} remain");
        }

        var bytes = Bytes(byteLength, $"a {(wide ? "wide" : "ASCII")} string of {units}").Span;
        return wide ? Encoding.Unicode.GetString(bytes) : Encoding.ASCII.GetString(bytes);
    }

    private void Need(int count, string what)
    {
        if (count > Remaining)
        {
            throw Fail(Position, $"{what} needs {count} bytes but {Remaining} remain");
        }
    }
}
