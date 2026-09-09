namespace BethesdaMultitool.Core.Formats.Battlespire;

/// <summary>One magical-item record: its keys in file order, plus the parsed conveniences.</summary>
internal sealed record BattlespireItemEntry(IReadOnlyDictionary<string, string> Fields)
{
    /// <summary>The item's display name, or empty when the record carries none.</summary>
    public string Name => Fields.TryGetValue("Name", out var v) ? v : string.Empty;

    /// <summary>
    ///     The record's id, or null. ⚠ 13 of the 457 retail records carry NO <c>ID#</c>, so this is
    ///     genuinely optional and must never be used as the record key.
    /// </summary>
    public int? Id => Fields.TryGetValue("ID#", out var v) && int.TryParse(v, out var n) ? n : null;

    /// <summary>A comma-separated field split into its parts, empty when the key is absent.</summary>
    public IReadOnlyList<string> List(string key)
    {
        if (!Fields.TryGetValue(key, out var value) || value.Length == 0)
        {
            return [];
        }

        var parts = value.Split(',');
        var result = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            result.Add(part.Trim());
        }

        return result;
    }
}

/// <summary>
///     Parses Battlespire's tab-keyed tables out of <c>TXT.BSA</c> — the magical-item definitions.
///     <para>
///         ⚑ <b>Measured 2026-09-06, and the job is far smaller than the archive suggests.</b> Of
///         the 253 <c>.TXT</c> entries only <b>22 are tab-keyed at all and 231 are plain prose</b>;
///         the item table lives in just TWO files, <c>MG2_SPC.TXT</c> (232 records) and
///         <c>MG0_GEN.TXT</c> (212), for <b>444 items</b>.
///     </para>
///     <para>
///         The grammar is <c>key TAB value</c> with blank lines separating records. Seven keys occur
///         457 times each — <c>Name</c>, <c>Item</c>, <c>Spell</c>, <c>App</c>, <c>Adv</c>,
///         <c>Uses</c>, <c>Level</c> — and values are scalars or comma-separated lists such as
///         <c>Spell  -1, 16, -1, 4, 0</c>.
///     </para>
///     <para>
///         ⚠ <b><c>ID#</c> appears only 444 times against the others' 457</b>, so 13 records have no
///         id. Keying records by id silently drops them.
///     </para>
///     <para>
///         ⛔ <b>Not every tab-keyed file is a table.</b> Nine begin with <c>DATE</c> and are dated
///         DEVELOPER LOGS whose keys are dates (<c>8/21</c>, <c>9/29</c>); the rest are one-off
///         notes. Parsing every tab-keyed entry would emit changelog lines as game data, which is
///         why <see cref="LooksLikeItemTable" /> gates on the item keys rather than on tabs.
///     </para>
/// </summary>
internal static class BattlespireItemTable
{
    /// <summary>Keys every item record carries; the gate that separates tables from dev logs.</summary>
    public static readonly string[] RequiredKeys = ["Name", "Item", "Spell", "App", "Adv", "Uses", "Level"];

    /// <summary>
    ///     Whether the text is an item table rather than prose or a developer log. Requires the
    ///     record keys to be present, not merely that the file contains tabs.
    /// </summary>
    public static bool LooksLikeItemTable(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (var key in RequiredKeys)
        {
            if (!text.Contains(key + "\t", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Parses every record. A record begins at each <c>Name</c> key and ends at the next one, so
    ///     blank-line spacing (which varies between one and three lines in retail) does not matter.
    /// </summary>
    public static IReadOnlyList<BattlespireItemEntry> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var entries = new List<BattlespireItemEntry>();
        Dictionary<string, string>? current = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab <= 0)
            {
                continue;
            }

            var key = line[..tab].Trim();
            var value = line[(tab + 1)..].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            if (key == "Name")
            {
                if (current is not null)
                {
                    entries.Add(new BattlespireItemEntry(current));
                }

                current = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            // A key before the first Name belongs to no record and is dropped.
            current?[key] = value;
        }

        if (current is not null)
        {
            entries.Add(new BattlespireItemEntry(current));
        }

        return entries;
    }
}
