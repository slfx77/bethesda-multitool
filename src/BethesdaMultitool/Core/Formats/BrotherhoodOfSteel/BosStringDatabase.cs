using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;

/// <summary>One entry of a <see cref="BosStringDatabase" />: a hash and the string it names.</summary>
/// <param name="Slot">Position in the hash table, which is the entry's only stable identity.</param>
/// <param name="Hash">
///     The stored hash. ⚑ The function IS established (2026-09-08): <see cref="BosNameHash" />,
///     <c>default.xbe</c> <c>0x00014BE0</c>. When <c>BosNameHash.Compute(Value) == Hash</c> the
///     entry is the record's own NAME; otherwise it is a display string filed under that name's
///     key. 8,758 of the Xbox tree's 14,829 entries are names.
/// </param>
/// <param name="Offset">Byte offset of the string.</param>
/// <param name="Value">The string itself.</param>
internal readonly record struct BosStringEntry(int Slot, uint Hash, int Offset, string Value);

/// <summary>
///     The <c>.SDB</c> string database from Fallout: Brotherhood of Steel (2004, PS2). Original RE
///     2026-09-06 against the shipped disc; the Snowblind references are GPL or unlicensed and, per
///     the plan, describe a different container family anyway.
///     <para>
///         Little-endian: <c>+0</c> u32 <b>magic 1499</b>, <c>+4</c> u32 <b>hash-table offset</b>,
///         <c>+8</c> u32 <b>string count</b>, then NUL-terminated <b>UTF-16LE</b> strings from
///         <c>+12</c>. At the table offset sit 8-byte slots of <c>(u32 hash, u32 stringOffset)</c>,
///         with unused slots left as <c>(0, 0)</c>.
///     </para>
///     <para>
///         ⚑ <b>The count is the proof</b>, and it holds on <b>56/56</b> shipped databases: the
///         slots resolve to exactly the <c>numstrings</c> the header declares. The 8-byte stride is
///         measured rather than assumed — a 16-byte stride resolves only 40 of BAR.SDB's 85.
///     </para>
///     <para>
///         ⚠ <b>The table is terminated, not merely bounded by the file.</b> 34 databases carry a
///         <see cref="TerminatorHash" />/<see cref="TerminatorOffset" /> slot after a power-of-two
///         number of entries, with unrelated data beyond it; the other 22 run to EOF. Walking to EOF
///         regardless looked correct on BAR.SDB and on 50 others, and produced exactly ONE spurious
///         extra entry in five — enough to break the count check, not enough to look wrong. Stop at
///         the terminator.
///     </para>
///     <para>
///         ⛔
///         <b>
///             The field names came from the disc's own <c>.NFO</c> debug dumps, and NOTHING
///             else should.
///         </b>
///         Those files list a header and every string with hash, offset and name,
///         which makes them look like a perfect oracle. They are not: measured against the shipped
///         database, <b>14 of the 65 names an NFO lists do not exist in it at all</b> and the
///         offsets have no constant delta. They describe a DIFFERENT BUILD. Use them for field
///         names and structure; never for offsets, counts or content.
///     </para>
/// </summary>
internal sealed class BosStringDatabase
{
    /// <summary>The magic every database declares, named by the disc's own debug dumps.</summary>
    public const uint Magic = 1499;

    /// <summary>Bytes of header before the string area.</summary>
    public const int HeaderLength = 12;

    /// <summary>Bytes per hash-table slot: a hash and a string offset.</summary>
    public const int SlotLength = 8;

    /// <summary>
    ///     The hash half of the slot that TERMINATES the table. 34 of the 56 shipped databases carry
    ///     it, always immediately after a power-of-two number of slots (64, 128, 256, 512, 2048 —
    ///     i.e. the <c>hashtablesize</c> the disc's debug dumps name), with trailing data beyond it.
    ///     The other 22 have no trailing data and simply run to EOF.
    /// </summary>
    public const uint TerminatorHash = 0x002F0178;

    /// <summary>
    ///     The offset half of the terminator. It is a fixed marker rather than a real offset — no
    ///     database is anywhere near 3 MB, which is why it never resolves to a string.
    /// </summary>
    public const uint TerminatorOffset = 3_080_568;

    private BosStringDatabase(string name, IReadOnlyList<BosStringEntry> entries, int unresolved, int tableOffset, bool terminated)
    {
        Name = name;
        Entries = entries;
        UnresolvedSlots = unresolved;
        HashTableOffset = tableOffset;
        Terminated = terminated;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The strings, in slot order.</summary>
    public IReadOnlyList<BosStringEntry> Entries { get; }

    /// <summary>
    ///     Slots that are populated but whose offset does not resolve to a string. Recorded rather
    ///     than skipped silently so it cannot be mistaken for a clean read.
    ///     <para>
    ///         ⚑ NON-ZERO IS NORMAL, and only for a database with no terminator: measured over the
    ///         PS2 disc's 56 databases on 2026-09-09, the 34 that end on the terminator slot yield
    ///         ZERO between them, while the 22 that do not yield 940 across 21 of them (BAR.SDB is
    ///         terminated and yields 0 — an earlier version of this comment said it yielded one).
    ///         A table's real length is its power-of-two slot count, which an unterminated file
    ///         does not state, so the walk runs to EOF and counts trailing bytes as populated
    ///         slots that resolve to nothing. Every string the header declares is still found:
    ///         <see cref="TryParse" /> refuses the file otherwise. Pair any assertion about this
    ///         with <see cref="Terminated" />; a bare "expect 0" is wrong for 22 of the 56.
    ///     </para>
    /// </summary>
    public int UnresolvedSlots { get; }

    /// <summary>
    ///     Whether the walk stopped on the explicit terminator slot rather than running to the end
    ///     of the file. False means the table's end is unstated, which is what makes
    ///     <see cref="UnresolvedSlots" /> non-zero.
    /// </summary>
    public bool Terminated { get; }

    /// <summary>Where the hash table begins.</summary>
    public int HashTableOffset { get; }

    /// <summary>Content probe: the magic dword.</summary>
    public static bool IsStringDatabase(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= HeaderLength && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == Magic;
    }

    /// <summary>Parses the database, throwing <see cref="InvalidDataException" /> when it does not fit.</summary>
    public static BosStringDatabase Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var database, out var error))
        {
            throw new InvalidDataException(error);
        }

        return database;
    }

    /// <summary>Parses the database, reporting why rather than throwing.</summary>
    public static bool TryParse(
        ReadOnlySpan<byte> bytes,
        string name,
        out BosStringDatabase database,
        out string error)
    {
        database = null!;
        if (!IsStringDatabase(bytes))
        {
            error = $"{name}: the magic dword is not {Magic}.";
            return false;
        }

        var tableOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        if (tableOffset < HeaderLength || tableOffset > bytes.Length)
        {
            error = $"{name}: the hash table offset {tableOffset} lies outside the {bytes.Length}-byte file.";
            return false;
        }

        var slots = (bytes.Length - tableOffset) / SlotLength;
        var entries = new List<BosStringEntry>((int)Math.Min(declared, 4096));
        var unresolved = 0;
        var terminated = false;

        for (var slot = 0; slot < slots; slot++)
        {
            var at = tableOffset + slot * SlotLength;
            var hash = BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 4)..]);

            // ⚠ The table ENDS here, and what follows is not slots. Reading on treats trailing
            // bytes as entries, and in five of the shipped databases that produced exactly one
            // extra "resolvable" entry — enough to break the count check but not to look wrong.
            if (hash == TerminatorHash && offset == TerminatorOffset)
            {
                terminated = true;
                break;
            }

            if (hash == 0 && offset == 0)
            {
                continue;
            }

            if (offset < HeaderLength || offset >= tableOffset || !TryReadString(bytes, (int)offset, out var value))
            {
                unresolved++;
                continue;
            }

            entries.Add(new BosStringEntry(slot, hash, (int)offset, value));
        }

        if (entries.Count != declared)
        {
            error = $"{name}: {entries.Count} slots resolve to strings but the header declares {declared}.";
            return false;
        }

        database = new BosStringDatabase(name, entries, unresolved, tableOffset, terminated);
        error = string.Empty;
        return true;
    }

    /// <summary>Reads a NUL-terminated UTF-16LE string.</summary>
    private static bool TryReadString(ReadOnlySpan<byte> bytes, int offset, out string value)
    {
        value = string.Empty;
        var end = offset;
        while (end + 1 < bytes.Length && (bytes[end] != 0 || bytes[end + 1] != 0))
        {
            end += 2;
        }

        if (end <= offset || end + 1 >= bytes.Length)
        {
            return false;
        }

        value = Encoding.Unicode.GetString(bytes[offset..end]);
        return true;
    }

    /// <summary>The string a hash names, or null when the database has no such entry.</summary>
    public string? Find(uint hash)
    {
        foreach (var entry in Entries)
        {
            if (entry.Hash == hash)
            {
                return entry.Value;
            }
        }

        return null;
    }
}
