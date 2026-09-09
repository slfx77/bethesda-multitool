using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.VanBuren;

/// <summary>
///     The Van Buren prototype's <c>.stf</c> string table (<c>English.stf</c>) — the game's authored
///     text. Original RE 2026-09-06; the only reference is GPL and none of it is ported.
///     <para>
///         Little-endian: three header dwords — <b>3</b>, <b>1</b>, and the <b>string count</b> —
///         then that many 16-byte directory records of
///         <c>(u32 offset, u32 length, u32, u32)</c>, then the string bytes.
///     </para>
///     <para>
///         ⚑ <b>Everything tiles.</b> The directory ends at <c>12 + 16 * count</c> and that is
///         exactly the first string's offset; each string begins where the previous ended; the last
///         ends exactly at EOF. On the shipped <c>English.stf</c> that is <b>3,281 strings</b>
///         spanning 52,508 → 254,846. The length is exact and carries no terminator.
///     </para>
///     <para>
///         ⚑ The strings are ASCII and read as the game's own UI text — "Single Player",
///         "New Game", "Load Game" — which is the oracle that the offsets are right, in a way no
///         structural check could be. ⚠ Note they are ASCII here, NOT the UTF-16 that Brotherhood
///         of Steel's string database uses; the two games are unrelated in this.
///     </para>
///     <para>
///         The third directory field is the FILE LENGTH repeated on every record, and the fourth is
///         always zero. Neither is interpreted — they are pinned as constants rather than named.
///     </para>
/// </summary>
internal sealed class VanBurenStringTable
{
    /// <summary>Bytes of header before the directory.</summary>
    public const int HeaderLength = 12;

    /// <summary>Bytes per directory record.</summary>
    public const int RecordLength = 16;

    /// <summary>The first header dword on the shipped table.</summary>
    public const uint Version = 3;

    private VanBurenStringTable(string name, IReadOnlyList<string> strings)
    {
        Name = name;
        Strings = strings;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The strings, in directory order — the order the game indexes them by.</summary>
    public IReadOnlyList<string> Strings { get; }

    /// <summary>How many strings the table holds.</summary>
    public int Count => Strings.Count;

    /// <summary>Content probe: the two constant header dwords plus a directory that fits.</summary>
    public static bool IsStringTable(ReadOnlySpan<byte> bytes)
    {
        return TryParse(bytes, "probe", out _, out _);
    }

    /// <summary>Parses the table, throwing <see cref="InvalidDataException" /> when it does not tile.</summary>
    public static VanBurenStringTable Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var table, out var error))
        {
            throw new InvalidDataException(error);
        }

        return table;
    }

    /// <summary>Parses the table, reporting why rather than throwing.</summary>
    public static bool TryParse(
        ReadOnlySpan<byte> bytes,
        string name,
        out VanBurenStringTable table,
        out string error)
    {
        table = null!;
        if (bytes.Length < HeaderLength)
        {
            error = $"{name}: {bytes.Length} bytes is shorter than the {HeaderLength}-byte header.";
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Version ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 1)
        {
            error = $"{name}: the header dwords are not the {Version}/1 the shipped table carries.";
            return false;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        if (count > (bytes.Length - HeaderLength) / RecordLength)
        {
            error = $"{name}: {count} records do not fit in {bytes.Length} bytes.";
            return false;
        }

        var cursor = HeaderLength + (int)count * RecordLength;
        var strings = new string[count];
        for (var i = 0; i < count; i++)
        {
            var at = HeaderLength + i * RecordLength;
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 4)..]);

            // The tiling gate: a string must begin exactly where the previous one ended, and the
            // first exactly where the directory does.
            if (offset != cursor || offset + (long)length > bytes.Length)
            {
                error = $"{name}: string {i} starts at {offset} rather than {cursor}, or runs past the file.";
                return false;
            }

            strings[i] = Encoding.ASCII.GetString(bytes.Slice((int)offset, (int)length));
            cursor = (int)(offset + length);
        }

        if (cursor != bytes.Length)
        {
            error = $"{name}: strings end at {cursor} of {bytes.Length} bytes.";
            return false;
        }

        table = new VanBurenStringTable(name, strings);
        error = string.Empty;
        return true;
    }
}
