using System.Globalization;

namespace BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;

/// <summary>One printed slot of a <see cref="BosNfoFile" />.</summary>
/// <param name="Slot">The slot number the dump prints before the colon.</param>
/// <param name="Hash">The stored key.</param>
/// <param name="Offset">The string's byte offset in the database the dump describes.</param>
/// <param name="Value">The string.</param>
/// <param name="IsName">
///     True when <c>BosNameHash.Compute(Value) == Hash</c> — i.e. this string is the identifier
///     that PRODUCES the key, rather than a display string filed under someone else's key.
/// </param>
internal readonly record struct BosNfoEntry(int Slot, uint Hash, int Offset, string Value, bool IsName);

/// <summary>
///     A <c>.NFO</c> file from Fallout: Brotherhood of Steel — the build tool's plain-text dump of
///     a <see cref="BosStringDatabase" />, shipped beside it on both discs (53 on the Xbox tree, 2
///     on the PS2's). Reading it is trivial; what it is FOR is the point.
///     <para>
///         Layout: a title line, a <c>Header Information</c> block of <c>key:\tvalue</c> pairs
///         (<c>hashtableoffset</c>, <c>magic</c>, <c>numstrings</c>, <c>hashtablesize</c> — the
///         field names <see cref="BosStringDatabase" /> takes its own from), then a
///         <c>String Entries</c> block of one line per slot:
///         <c>NN:\thash: 0xXXXXXXXX\toffset: 0xNNN NNNN\t&lt;the string&gt;</c>, with empty slots
///         printed as just <c>NN:</c>. ⚠ Line endings are mixed inside a single file (LF early, CRLF
///         late), so the trailing <c>\r</c> must be trimmed or it lands in the string.
///     </para>
///     <para>
///         ⚑ <b>These files are the ORACLE for <see cref="BosNameHash" />.</b> Every line prints a
///         key beside its string, so the hash can be checked against the shipping build without
///         guessing: 5,215 of the Xbox disc's 5,589 pairs and 119 of the PS2 disc's 131 reproduce
///         exactly. Nine rival hashes score zero on the same corpus.
///     </para>
///     <para>
///         ⚑ <b>The misses are the format's real lesson, not noise.</b> Every one of them is a
///         DISPLAY string ("Freezer Chest", "Save Game Console", "Footlocker") filed under the key
///         of the internal name it decorates — 69 distinct labels, repeated across levels. The
///         control is the UNDERSCORE: not one of the 374 contains one, while identifiers here are
///         underscore-separated and only 13 of the 5,215 proven names are Title-Case without one.
///         ⛔ NOT "they all contain a space" — 81 are single words (Footlocker 34, Locker 13,
///         Switch 11). A database therefore holds two kinds of row, and
///         <see cref="BosNfoEntry.IsName" /> separates them — which is exactly how a record gets a
///         proven name and a display name from the same table.
///     </para>
///     <para>
///         ⚑ <c>numstrings</c> equals the number of populated lines on <b>55 of 55</b> files, so
///         the parse is self-checking.
///     </para>
///     <para>
///         ⛔ <b>Do NOT use a dump's hashes or offsets against the shipped database beside it.</b>
///         They describe an EARLIER BUILD: the key sets differ on 53 of 53 files, and 14 of the 65
///         names one dump lists do not exist in its database at all. Use it for the hash oracle and
///         for field names — never for content.
///     </para>
/// </summary>
internal sealed class BosNfoFile
{
    private BosNfoFile(string name, string subject, int declaredCount, int hashTableOffset, int hashTableSize,
        uint magic, IReadOnlyList<BosNfoEntry> entries)
    {
        Name = name;
        Subject = subject;
        DeclaredCount = declaredCount;
        HashTableOffset = hashTableOffset;
        HashTableSize = hashTableSize;
        Magic = magic;
        Entries = entries;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The database the dump names on its first line, e.g. <c>c1/BAR/BAR.sdb</c>.</summary>
    public string Subject { get; }

    /// <summary>The dump's <c>numstrings</c>.</summary>
    public int DeclaredCount { get; }

    /// <summary>The dump's <c>hashtableoffset</c>.</summary>
    public int HashTableOffset { get; }

    /// <summary>The dump's <c>hashtablesize</c> — the slot count, always a power of two.</summary>
    public int HashTableSize { get; }

    /// <summary>The dump's <c>magic</c>, 1499 on every file (see <see cref="BosStringDatabase.Magic" />).</summary>
    public uint Magic { get; }

    /// <summary>The printed slots, in slot order.</summary>
    public IReadOnlyList<BosNfoEntry> Entries { get; }

    /// <summary>Entries whose string reproduces the stored key through <see cref="BosNameHash" />.</summary>
    public int NameCount
    {
        get
        {
            var count = 0;
            foreach (var entry in Entries)
            {
                if (entry.IsName)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Content probe: the dump's first line.</summary>
    public static bool IsNfo(string text)
    {
        return text is not null && text.StartsWith("SDB Information for", StringComparison.Ordinal);
    }

    /// <summary>Parses the dump, throwing <see cref="InvalidDataException" /> when it does not fit.</summary>
    public static BosNfoFile Parse(string text, string name)
    {
        if (!TryParse(text, name, out var file, out var error))
        {
            throw new InvalidDataException(error);
        }

        return file;
    }

    /// <summary>Parses the dump, reporting why rather than throwing.</summary>
    public static bool TryParse(string text, string name, out BosNfoFile file, out string error)
    {
        file = null!;
        if (!IsNfo(text))
        {
            error = $"{name}: the first line is not 'SDB Information for …'.";
            return false;
        }

        var subject = string.Empty;
        var declared = -1;
        var tableOffset = -1;
        var tableSize = -1;
        var magic = 0u;
        var entries = new List<BosNfoEntry>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("SDB Information for", StringComparison.Ordinal))
            {
                subject = line["SDB Information for".Length..].Trim();
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var rest = line[(colon + 1)..].Trim();

            switch (key)
            {
                case "hashtableoffset":
                    tableOffset = ParseInt(rest);
                    continue;
                case "magic":
                    magic = (uint)ParseInt(rest);
                    continue;
                case "numstrings":
                    declared = ParseInt(rest);
                    continue;
                case "hashtablesize":
                    tableSize = ParseInt(rest);
                    continue;
            }

            // A slot line: "NN:" alone for an empty slot, otherwise three tab-separated fields.
            if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var slot) || rest.Length == 0)
            {
                continue;
            }

            var fields = rest.Split('\t');
            if (fields.Length < 3 || !fields[0].StartsWith("hash: 0x", StringComparison.Ordinal))
            {
                continue;
            }

            if (!uint.TryParse(fields[0]["hash: 0x".Length..], NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var hash))
            {
                continue;
            }

            var offset = 0;
            if (fields[1].StartsWith("offset: 0x", StringComparison.Ordinal))
            {
                var hex = fields[1]["offset: 0x".Length..].Split(' ')[0];
                if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
                {
                    offset = parsed;
                }
            }

            var value = string.Join('\t', fields[2..]);
            entries.Add(new BosNfoEntry(slot, hash, offset, value, BosNameHash.Names(value, hash)));
        }

        if (declared < 0)
        {
            error = $"{name}: the header block has no 'numstrings'.";
            return false;
        }

        // ⚑ The count is the self-check, and it holds on 55 of 55 shipped dumps.
        if (entries.Count != declared)
        {
            error = $"{name}: {entries.Count} slot lines carry a string but the header declares {declared}.";
            return false;
        }

        file = new BosNfoFile(name, subject, declared, tableOffset, tableSize, magic, entries);
        error = string.Empty;
        return true;
    }

    private static int ParseInt(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : -1;
    }
}
