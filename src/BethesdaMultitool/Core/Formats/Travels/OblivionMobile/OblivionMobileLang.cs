using System.Globalization;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Travels.OblivionMobile;

/// <summary>
///     An Oblivion mobile <c>lang_N.txt</c> text table — every line of authored text in the game.
///     Original RE (2026-09-05) over all 13 retail files.
///     <para>
///         Format: records <c>"&lt;id&gt; &lt;text&gt;|"</c> joined by CRLF with NO trailing CRLF,
///         so the file ends on a <c>|</c>. The id is decimal ASCII, one space separates it from the
///         text, and the text never contains <c>|</c>, CR or LF. Ids ascend and are unique within a
///         file. There is no header, no BOM and no record count.
///     </para>
///     <para>
///         Trap — the encoding is ISO-8859-1, not UTF-8. The engine maps each byte to one char with
///         no multi-byte decoding. Every byte of all 13 retail files is below 0x80 so the shipped
///         set is plain ASCII and the two decoders agree today; they stop agreeing the moment a
///         localised build carries a high byte, where UTF-8 would either mangle a pair of Latin-1
///         characters into one or throw. Latin-1 is what the engine does, so Latin-1 is what this
///         reads.
///     </para>
///     <para>
///         What the 13 files are: NOT languages. <c>lang_0</c> is the base table (UI, items,
///         spells, help — 305 records, ids 1..574) and <c>lang_1</c>..<c>lang_12</c> are per-chapter
///         English dialogue overlays loaded by a script's LOADLANG. None of the 12 translates
///         anything: they share only 9 ids with lang_0 and all 9 are byte-identical duplicates
///         ("To Bruma" x5, "Take Artifact" x3, "You recieve the artifact" x1 — the misspelling is
///         retail). Lookup consults lang_0 first, then whichever overlay is in force; an overlay
///         stays loaded across a LOADSCR, which is how the sub-scripts that carry no LOADLANG of
///         their own resolve their ids.
///     </para>
///     <para>
///         Retail census: 546 records over 13 files — lang_0 305 (ids 1..574), then 34, 46, 9, 16,
///         6, 42, 20, 16, 30, 9, 7, 6 for overlays 1..12.
///     </para>
/// </summary>
internal sealed class OblivionMobileLang
{
    /// <summary>Ends a record's text.</summary>
    public const byte RecordTerminator = (byte)'|';

    /// <summary>Separates the decimal id from the text.</summary>
    public const byte IdSeparator = (byte)' ';

    private readonly Dictionary<int, string> _byId;

    private OblivionMobileLang(string name, int index, IReadOnlyList<OblivionMobileLangString> strings)
    {
        Name = name;
        Index = index;
        Strings = strings;

        // The engine indexes a plain array by id, so a repeated id would leave the LAST text in
        // place. No retail file repeats one; this keeps the same answer if one ever does.
        _byId = new Dictionary<int, string>(strings.Count);
        foreach (var entry in strings)
        {
            _byId[entry.Id] = entry.Text;
        }
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>
    ///     The <c>N</c> of <c>lang_N.txt</c> — 0 for the base table, 1..12 for a chapter overlay,
    ///     or -1 when the name does not carry one (this is a naming convention, not a field in the
    ///     file, so a renamed fixture reports -1 rather than a wrong index).
    /// </summary>
    public int Index { get; }

    /// <summary>Records in file order, which is ascending id order on every retail file.</summary>
    public IReadOnlyList<OblivionMobileLangString> Strings { get; }

    /// <summary>The highest id present, or -1 when the table is empty.</summary>
    public int MaxId => Strings.Count == 0 ? -1 : Strings.Max(s => s.Id);

    /// <summary>
    ///     Reads a table. Throws <see cref="InvalidDataException" /> naming the file and the byte
    ///     position for a record with no <c>|</c> terminator, a record whose id is not decimal
    ///     ASCII, a separator between records that is not exactly CRLF, or a trailing CRLF (the
    ///     file must end on the terminator).
    /// </summary>
    public static OblivionMobileLang Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var strings = new List<OblivionMobileLangString>();
        var position = 0;
        while (position < bytes.Length)
        {
            var recordStart = position;
            var terminator = bytes[position..].IndexOf(RecordTerminator);
            if (terminator < 0)
            {
                throw new InvalidDataException(
                    $"'{name}': the record starting at byte {recordStart} has no '|' terminator before the end of the {bytes.Length}-byte file.");
            }

            terminator += position;
            strings.Add(ReadRecord(bytes[recordStart..terminator], name, recordStart));
            position = terminator + 1;
            if (position >= bytes.Length)
            {
                break;
            }

            RequireLineBreak(bytes, name, ref position);
        }

        return new OblivionMobileLang(name, IndexFromName(name), strings);
    }

    /// <summary>
    ///     Reads <c>start.txt</c>, the pre-table boot strings: <c>|</c>-terminated entries with no
    ///     line breaks at all, indexed by ordinal. Retail carries 5 ("Loading", "Press any key",
    ///     "Resume game?", "Yes", "Exit"). Same throws as <see cref="Parse" /> for a missing
    ///     terminator; entries here have no id, so nothing is parsed as a number.
    /// </summary>
    public static IReadOnlyList<string> ParseStartText(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var entries = new List<string>();
        var position = 0;
        while (position < bytes.Length)
        {
            var terminator = bytes[position..].IndexOf(RecordTerminator);
            if (terminator < 0)
            {
                throw new InvalidDataException(
                    $"'{name}': the entry starting at byte {position} has no '|' terminator before the end of the {bytes.Length}-byte file.");
            }

            terminator += position;
            entries.Add(Encoding.Latin1.GetString(bytes[position..terminator]));
            position = terminator + 1;
        }

        return entries;
    }

    /// <summary>The text for <paramref name="id" />, or null when this table does not carry it.</summary>
    public string? Find(int id)
    {
        return _byId.GetValueOrDefault(id);
    }

    /// <summary>
    ///     True when this table carries <paramref name="id" />. On a miss <paramref name="text" />
    ///     is the empty string, which is what the engine's array-of-strings lookup yields.
    /// </summary>
    public bool TryGetText(int id, out string text)
    {
        if (_byId.TryGetValue(id, out var found))
        {
            text = found;
            return true;
        }

        text = string.Empty;
        return false;
    }

    private static OblivionMobileLangString ReadRecord(ReadOnlySpan<byte> record, string name, int recordStart)
    {
        var digits = 0;
        while (digits < record.Length && record[digits] is >= (byte)'0' and <= (byte)'9')
        {
            digits++;
        }

        if (digits == 0)
        {
            throw new InvalidDataException(
                $"'{name}': the record at byte {recordStart} does not start with a decimal id.");
        }

        var id = int.Parse(Encoding.Latin1.GetString(record[..digits]), CultureInfo.InvariantCulture);
        var textStart = digits;
        if (textStart < record.Length && record[textStart] == IdSeparator)
        {
            textStart++;
        }
        else if (textStart < record.Length)
        {
            throw new InvalidDataException(
                $"'{name}': the record at byte {recordStart} has '{(char)record[textStart]}' after its id where a single space belongs.");
        }

        var text = record[textStart..];
        var breakAt = text.IndexOfAny((byte)'\r', (byte)'\n');
        if (breakAt >= 0)
        {
            throw new InvalidDataException(
                $"'{name}': the record at byte {recordStart} contains a line break at byte {recordStart + textStart + breakAt}, before its '|'.");
        }

        return new OblivionMobileLangString(id, Encoding.Latin1.GetString(text), recordStart + textStart);
    }

    private static void RequireLineBreak(ReadOnlySpan<byte> bytes, string name, ref int position)
    {
        if (position + 2 > bytes.Length || bytes[position] != (byte)'\r' || bytes[position + 1] != (byte)'\n')
        {
            throw new InvalidDataException(
                $"'{name}': records are joined by CRLF, but byte {position} starts {Describe(bytes, position)}.");
        }

        position += 2;
        if (position >= bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}': the file ends with a trailing CRLF at byte {position - 2}; it must end on the last record's '|'.");
        }
    }

    private static string Describe(ReadOnlySpan<byte> bytes, int position)
    {
        return position + 1 < bytes.Length
            ? $"{bytes[position]:X2} {bytes[position + 1]:X2}"
            : $"{bytes[position]:X2} at the end of the file";
    }

    private static int IndexFromName(string name)
    {
        var fileName = Path.GetFileNameWithoutExtension(name);
        const string prefix = "lang_";
        return fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               && int.TryParse(fileName.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture,
                   out var index)
            ? index
            : -1;
    }
}

/// <summary>One text record.</summary>
/// <param name="Id">The decimal id the scripts reference. A single global numbering across all 13 files.</param>
/// <param name="Text">The text, decoded as ISO-8859-1.</param>
/// <param name="Offset">Byte offset of the first character of the text (past the id and its space).</param>
internal sealed record OblivionMobileLangString(int Id, string Text, int Offset);
