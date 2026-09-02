// Ported from OpenTESArena (MIT License), https://github.com/afritz1/OpenTESArena
//   OpenTESArena/src/Assets/TextAssetLibrary.cpp — ArenaTemplateDat::init. License texts are
//   collected centrally in THIRD_PARTY_LICENSES.
//
// One deliberate divergence, measured against the retail TEMPLATE.DAT (395,981 bytes,
// 811 key lines, 2026-09-01): the reference decides "this key line has a letter suffix" by
// reverse-scanning the line for the first alphanumeric character, then reads index 5 regardless.
// Two retail lines carry trailing prose instead of a suffix — "#0012 No deliver quest unknown"
// and "#0183 the" — and the reference assigns them the space at index 5 as their letter. Every
// genuine suffix in the file is a single letter sitting exactly at index 5, so this port tests
// index 5 directly and reads those two lines as plain, unsuffixed keys.

using System.Text;

namespace BethesdaMultitool.Core.Formats.Arena;

/// <summary>
///     Arena's TEMPLATE.DAT — the game's string table, holding conversation lines, quest text,
///     tavern and shop dialogue, and interface messages. Plain text: a <c>#NNNN</c> key line,
///     optionally suffixed with a single letter, followed by the value lines belonging to it, in
///     which <c>&amp;</c> separates the individual strings.
///     <para>
///         The same (key, letter) pair can appear more than once — keys #0000-#0004 ship three
///         copies, one per tileset (temperate, desert, snowy). Those copies are kept in
///         <see cref="ArenaTemplateDatEntry.Copy" /> order rather than overwriting one another.
///     </para>
/// </summary>
internal sealed class ArenaTemplateDat
{
    private readonly List<ArenaTemplateDatEntry> _entries;

    private ArenaTemplateDat(List<ArenaTemplateDatEntry> entries)
    {
        _entries = entries;
    }

    /// <summary>Every entry, ordered by key, then letter, then tileset copy.</summary>
    public IReadOnlyList<ArenaTemplateDatEntry> Entries => _entries;

    /// <summary>Parses TEMPLATE.DAT from its raw bytes (the file is never compressed or encrypted).</summary>
    public static ArenaTemplateDat Parse(ReadOnlySpan<byte> bytes)
    {
        return ParseText(Encoding.Latin1.GetString(bytes));
    }

    /// <summary>Parses TEMPLATE.DAT text.</summary>
    public static ArenaTemplateDat ParseText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var entries = new List<ArenaTemplateDatEntry>();

        // How many entries already carry each (key, letter) pair — this is the tileset copy index.
        var copyCounts = new Dictionary<(int Key, char Letter), int>();

        var value = new StringBuilder();
        var key = ArenaTemplateDatEntry.NoKey;
        var letter = ArenaTemplateDatEntry.NoLetter;
        var open = false;

        foreach (var rawLine in text.Split('\n'))
        {
            // Values are joined with the line's carriage return preserved as a separator, which the
            // whitespace collapse below turns into a single space.
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            switch (line[0])
            {
                case '#':
                    Flush();
                    (key, letter) = ParseKeyLine(line);
                    open = true;
                    continue;

                // The file's single comment line, at the very end, closes the last entry.
                case ';':
                    Flush();
                    open = false;
                    continue;

                default:
                    if (open)
                    {
                        value.Append(line).Append(' ');
                    }

                    continue;
            }
        }

        Flush();

        entries.Sort(static (a, b) =>
        {
            var byKey = a.Key.CompareTo(b.Key);
            if (byKey != 0)
            {
                return byKey;
            }

            var byLetter = a.Letter.CompareTo(b.Letter);
            return byLetter != 0 ? byLetter : a.Copy.CompareTo(b.Copy);
        });

        return new ArenaTemplateDat(entries);

        void Flush()
        {
            if (!open)
            {
                return;
            }

            var identity = (key, letter);
            var copy = copyCounts.GetValueOrDefault(identity);
            copyCounts[identity] = copy + 1;

            entries.Add(new ArenaTemplateDatEntry(key, letter, copy, SplitValues(value.ToString())));

            key = ArenaTemplateDatEntry.NoKey;
            letter = ArenaTemplateDatEntry.NoLetter;
            value.Clear();
            open = false;
        }
    }

    /// <summary>
    ///     Finds the entry for a key, preferring the plain (unsuffixed) variant and the first
    ///     tileset copy. Returns null when the key is absent.
    /// </summary>
    public ArenaTemplateDatEntry? Find(int key, char letter = ArenaTemplateDatEntry.NoLetter)
    {
        return _entries.FirstOrDefault(e => e.Key == key && e.Letter == letter)
               ?? (letter == ArenaTemplateDatEntry.NoLetter
                   ? _entries.FirstOrDefault(e => e.Key == key)
                   : null);
    }

    /// <summary>
    ///     A key line is <c>#</c> followed by exactly four digits, then an optional single-letter
    ///     variant suffix. Anything else on the line is authoring prose and is ignored.
    /// </summary>
    private static (int Key, char Letter) ParseKeyLine(string line)
    {
        const int digitsStart = 1;
        const int digitCount = 4;
        const int letterIndex = digitsStart + digitCount;

        if (line.Length < letterIndex)
        {
            return (ArenaTemplateDatEntry.NoKey, ArenaTemplateDatEntry.NoLetter);
        }

        var key = 0;
        for (var i = digitsStart; i < letterIndex; i++)
        {
            if (!char.IsAsciiDigit(line[i]))
            {
                return (ArenaTemplateDatEntry.NoKey, ArenaTemplateDatEntry.NoLetter);
            }

            key = (key * 10) + (line[i] - '0');
        }

        var letter = line.Length > letterIndex && char.IsAsciiLetter(line[letterIndex])
            ? line[letterIndex]
            : ArenaTemplateDatEntry.NoLetter;

        return (key, letter);
    }

    /// <summary>
    ///     Collapses runs of whitespace to a single space, trims, then splits on '&amp;'. The text
    ///     following the final '&amp;' is authoring slack and is dropped, as in the reference.
    /// </summary>
    private static IReadOnlyList<string> SplitValues(string raw)
    {
        var collapsed = new StringBuilder(raw.Length);
        var previousWasSpace = false;
        foreach (var character in raw)
        {
            var c = char.IsWhiteSpace(character) ? ' ' : character;
            if (c == ' ' && previousWasSpace)
            {
                continue;
            }

            collapsed.Append(c);
            previousWasSpace = c == ' ';
        }

        var parts = collapsed.ToString().Trim().Split('&');
        if (parts.Length <= 1)
        {
            return [];
        }

        var values = new List<string>(parts.Length - 1);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            values.Add(parts[i].Trim());
        }

        return values;
    }
}

/// <summary>
///     One TEMPLATE.DAT entry: a numeric key, an optional single-letter variant, the tileset copy
///     index for keys that ship more than once, and the ampersand-separated strings.
/// </summary>
internal sealed record ArenaTemplateDatEntry(
    int Key,
    char Letter,
    int Copy,
    IReadOnlyList<string> Values)
{
    /// <summary>Sentinel for a line that carried no parsable key.</summary>
    public const int NoKey = -1;

    /// <summary>Sentinel for an entry with no letter variant.</summary>
    public const char NoLetter = '\0';

    /// <summary>Whether this entry carries a letter variant suffix.</summary>
    public bool HasLetter => Letter != NoLetter;

    /// <summary>The authored key as it appears in the file, e.g. <c>#0014b</c>.</summary>
    public string DisplayKey => HasLetter
        ? $"#{Key:D4}{Letter}"
        : $"#{Key:D4}";
}
