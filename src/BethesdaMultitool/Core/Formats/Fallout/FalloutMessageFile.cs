using System.Globalization;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Fallout;

/// <summary>
///     A Fallout <c>.MSG</c> text file: the game's authored strings, keyed by number.
///     <para>
///         The grammar is three brace-delimited fields per entry — <c>{id}{audio}{text}</c> — with
///         <c>#</c> comment lines between them. No entry's text contains a brace, in either
///         population, so the fields need no escaping rules.
///     </para>
///     <para>
///         ⚠⚠ <b>Entries DO span lines, so the reader must not be line-oriented.</b> None of the
///         13,189 entries in the 27 <c>TEXT\ENGLISH\GAME</c> files does — but
///         <b>
///             3,962 of the
///             23,126 in the 621 <c>TEXT\ENGLISH\DIALOG</c> files do
///         </b>
///         , because spoken lines are
///         wrapped in the source. A line-oriented parse looks perfect on the game text and silently
///         drops or truncates a sixth of the dialogue. This scans brace triples instead, which
///         reproduces both populations exactly.
///     </para>
///     <para>
///         ⚠ <b>Ids are not unique.</b> 14 are duplicated across the corpus, and the copies carry
///         DIFFERENT text, so a policy is required rather than optional.
///         <b>
///             The last occurrence
///             wins
///         </b>
///         , which the one case that touches prototype naming settles: <c>PRO_SCEN.MSG</c>
///         gives 85400 = "Sign" and then 85401 twice — "Maltese Falcon" and "This is a neon sign for
///         the Maltese Falcon." Since a prototype's description is its name's id plus one, 85401 is
///         the description of "Sign", and only the second entry reads as one. Taking the first would
///         put a stray name where the description belongs.
///     </para>
/// </summary>
internal sealed class FalloutMessageFile
{
    private readonly Dictionary<int, string> _messages;

    private FalloutMessageFile(string name, Dictionary<int, string> messages, int duplicates)
    {
        Name = name;
        _messages = messages;
        DuplicateIds = duplicates;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>How many entries the file holds after duplicates are resolved.</summary>
    public int Count => _messages.Count;

    /// <summary>How many ids appeared more than once — 21 across the game text, 18 in dialogue.</summary>
    public int DuplicateIds { get; }

    /// <summary>The message texts, for callers that need the values rather than a lookup.</summary>
    public IReadOnlyCollection<string> Strings => _messages.Values;

    /// <summary>The text for an id, or null when the file has no entry for it.</summary>
    public string? Find(int id)
    {
        return _messages.TryGetValue(id, out var text) ? text : null;
    }

    /// <summary>Parses a message file. Malformed lines are skipped rather than throwing.</summary>
    public static FalloutMessageFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        // DOS-era code page 437; Latin-1 round-trips the byte values and nothing here depends on
        // the glyphs of the handful of accented characters.
        var text = Encoding.Latin1.GetString(bytes);
        var messages = new Dictionary<int, string>();
        var duplicates = 0;

        var position = 0;
        while (TryReadEntry(text, ref position, out var id, out var value))
        {
            if (!messages.TryAdd(id, value))
            {
                duplicates++;
                messages[id] = value; // last wins — see the type remarks
            }
        }

        return new FalloutMessageFile(name, messages, duplicates);
    }

    /// <summary>
    ///     Reads the next <c>{id}{audio}{text}</c> triple from anywhere in the file, advancing
    ///     <paramref name="position" />. Line structure is deliberately ignored — see the type
    ///     remarks on multi-line dialogue.
    /// </summary>
    private static bool TryReadEntry(string text, ref int position, out int id, out string value)
    {
        id = 0;
        value = string.Empty;

        while (position < text.Length)
        {
            var open = text.IndexOf('{', position);
            if (open < 0)
            {
                position = text.Length;
                return false;
            }

            var cursor = open;
            var fields = new string[3];
            var complete = true;
            for (var i = 0; i < 3; i++)
            {
                if (cursor >= text.Length || text[cursor] != '{')
                {
                    complete = false;
                    break;
                }

                var close = text.IndexOf('}', cursor + 1);
                if (close < 0)
                {
                    complete = false;
                    break;
                }

                fields[i] = text[(cursor + 1)..close];
                cursor = close + 1;

                // The three fields may be separated by whitespace, including newlines.
                while (i < 2 && cursor < text.Length && char.IsWhiteSpace(text[cursor]))
                {
                    cursor++;
                }
            }

            if (complete &&
                int.TryParse(fields[0].Trim(), NumberStyles.None,
                    CultureInfo.InvariantCulture, out id))
            {
                value = fields[2].Trim();
                position = cursor;
                return true;
            }

            position = open + 1;
        }

        return false;
    }
}
