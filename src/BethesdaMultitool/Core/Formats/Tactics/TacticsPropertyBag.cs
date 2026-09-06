using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Tactics;

/// <summary>One property of a <see cref="TacticsPropertyBag" />: a name, a type code and its bytes.</summary>
/// <param name="Name">The authored property name, e.g. <c>Display Name</c>.</param>
/// <param name="Type">The type code. ⚠ Deliberately NOT interpreted — see the remarks.</param>
/// <param name="Value">The payload exactly as stored.</param>
internal readonly record struct TacticsProperty(string Name, uint Type, ReadOnlyMemory<byte> Value)
{
    /// <summary>The payload read as ASCII, for the many properties that hold text.</summary>
    public string AsText => Encoding.ASCII.GetString(Value.Span).TrimEnd('\0');
}

/// <summary>
///     A Fallout Tactics <c>.ent</c> / <c>.chr</c> property bag — the entity and character
///     definitions the game is built from. Original RE 2026-09-06; every Tactics reference is GPL,
///     so nothing is ported.
///     <para>
///         The container follows the family's <c>'&lt;tag&gt;' + NUL + ASCII version</c> framing:
///         <c>&lt;entity&gt;</c> version <c>'2'</c> (or <c>&lt;character&gt;</c>), then a
///         u32-length-prefixed type name, then a <c>&lt;esh&gt;</c> block of version <c>'1'</c>
///         holding a u32 property count.
///     </para>
///     <para>
///         ⚑ <b>Every property is SELF-DESCRIBING</b>:
///         <c>u32 nameLength + name + u32 type + u32 size + size bytes</c>. That is the whole format
///         — walking it consumes <b>1,498/1,498</b> <c>.ent</c> and <b>39/39</b> <c>.chr</c> files
///         exactly, 23,740 and 886 top-level properties respectively.
///     </para>
///     <para>
///         ⛔ <b>No type-size table is needed, and building one is wasted work.</b> The backlog
///         called for one; it is unnecessary because <c>size</c> covers every payload including the
///         nested case. Type 11 was believed to be a special "recursive nested ESH" needing its own
///         walk — it does contain a nested block much of the time, but its <c>size</c> already spans
///         it, and special-casing it FAILS: treating type 11 as a literal nested <c>&lt;esh&gt;</c>
///         parses only 107 of the 1,498 files, while the flat reading parses all of them.
///     </para>
///     <para>
///         Type codes are therefore surfaced raw rather than decoded. Names read as authored prose
///         ("XP Reward", "Race Type", "Display Name", "Sprite"), which is the oracle that the walk
///         is aligned.
///     </para>
/// </summary>
internal sealed class TacticsPropertyBag
{
    /// <summary>The nested block tag that holds the properties.</summary>
    public const string BlockTag = "<esh>";

    private TacticsPropertyBag(string name, string kind, IReadOnlyList<TacticsProperty> properties)
    {
        Name = name;
        Kind = kind;
        Properties = properties;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The bag's declared type name, e.g. <c>Actor</c>.</summary>
    public string Kind { get; }

    /// <summary>The properties, in file order.</summary>
    public IReadOnlyList<TacticsProperty> Properties { get; }

    /// <summary>Content probe: the family's tag framing.</summary>
    public static bool IsPropertyBag(ReadOnlySpan<byte> bytes)
    {
        return bytes.StartsWith("<entity>"u8) || bytes.StartsWith("<character>"u8);
    }

    /// <summary>Parses a bag, throwing <see cref="InvalidDataException" /> when it does not tile.</summary>
    public static TacticsPropertyBag Parse(ReadOnlyMemory<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var bag, out var error))
        {
            throw new InvalidDataException(error);
        }

        return bag;
    }

    /// <summary>Parses a bag, reporting why rather than throwing.</summary>
    public static bool TryParse(
        ReadOnlyMemory<byte> bytes,
        string name,
        out TacticsPropertyBag bag,
        out string error)
    {
        bag = null!;
        var span = bytes.Span;
        if (!IsPropertyBag(span))
        {
            error = $"{name}: does not open with an <entity> or <character> tag.";
            return false;
        }

        var block = span.IndexOf("<esh>"u8);
        if (block < 0)
        {
            error = $"{name}: has no {BlockTag} block.";
            return false;
        }

        // The bag's own type name is the length-prefixed string before the block.
        var kind = ReadKind(span, block);

        // Past "<esh>" comes its NUL, an ASCII version character, another NUL, then the count.
        var position = block + 5 + 1 + 1 + 1;
        if (position + 4 > span.Length)
        {
            error = $"{name}: the {BlockTag} block has no property count.";
            return false;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(span[position..]);
        position += 4;

        var properties = new List<TacticsProperty>((int)Math.Min(count, 4096));
        for (var i = 0; i < count; i++)
        {
            if (position + 4 > span.Length)
            {
                error = $"{name}: property {i} has no name length.";
                return false;
            }

            var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(span[position..]);
            position += 4;
            if (nameLength > 256 || position + nameLength > span.Length)
            {
                error = $"{name}: property {i} declares a {nameLength}-byte name that does not fit.";
                return false;
            }

            var propertyName = Encoding.ASCII.GetString(span.Slice(position, (int)nameLength));
            position += (int)nameLength;

            if (position + 8 > span.Length)
            {
                error = $"{name}: property {i} ('{propertyName}') has no type and size.";
                return false;
            }

            var type = BinaryPrimitives.ReadUInt32LittleEndian(span[position..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(span[(position + 4)..]);
            position += 8;
            if (position + (long)size > span.Length)
            {
                error = $"{name}: property {i} ('{propertyName}') runs {size} bytes past the file.";
                return false;
            }

            properties.Add(new TacticsProperty(propertyName, type, bytes.Slice(position, (int)size)));
            position += (int)size;
        }

        if (position != span.Length)
        {
            error = $"{name}: {count} properties end at {position} of {span.Length} bytes.";
            return false;
        }

        bag = new TacticsPropertyBag(name, kind, properties);
        error = string.Empty;
        return true;
    }

    /// <summary>The length-prefixed type name sitting between the outer tag and the block.</summary>
    private static string ReadKind(ReadOnlySpan<byte> span, int block)
    {
        // The outer tag is followed by NUL, a version character, NUL, then u32 length + name, and
        // that name ends exactly where the block begins.
        for (var start = block - 1; start >= 4; start--)
        {
            var length = block - start;
            if (start - 4 < 0)
            {
                break;
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(span[(start - 4)..]) == (uint)length)
            {
                return Encoding.ASCII.GetString(span.Slice(start, length));
            }
        }

        return string.Empty;
    }

    /// <summary>The first property with this name, or null.</summary>
    public TacticsProperty? Find(string propertyName)
    {
        foreach (var property in Properties)
        {
            if (string.Equals(property.Name, propertyName, StringComparison.Ordinal))
            {
                return property;
            }
        }

        return null;
    }
}
