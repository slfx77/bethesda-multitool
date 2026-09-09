using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BethesdaMultitool.Core.Formats.Redguard;

/// <summary>One world's entry in <c>WORLD.INI</c>: its assets, its lighting, and everything else it declares.</summary>
internal sealed class RedguardWorld
{
    /// <summary>The bracketed index the file keys this world by. Not contiguous — retail skips 9, 10 and 16.</summary>
    public required int Index { get; init; }

    /// <summary>Every <c>key[index]=value</c> pair for this world, in file order, verbatim.</summary>
    public required IReadOnlyDictionary<string, string> Values { get; init; }

    /// <summary>The world's map database (<c>.RGM</c>), or null when it declares none.</summary>
    public string? MapPath => Value("world_map");

    /// <summary>The world's terrain (<c>.WLD</c>). Only the 7 outdoor worlds have one.</summary>
    public string? TerrainPath => Value("world_world");

    /// <summary>The palette (<c>.COL</c>) every texture in this world is resolved through.</summary>
    public string? PalettePath => Value("world_palette");

    /// <summary>The sky image (<c>.GXA</c>).</summary>
    public string? SkyPath => Value("world_sky");

    /// <summary>The Redbook audio track this world plays, or null when it declares none.</summary>
    public int? RedbookTrack => Integer("world_redbook");

    /// <summary>
    ///     The node maps (<c>world_node_map1</c>…) in ascending key order. ⚠ Retail ships NONE of
    ///     these files — see the class remarks.
    /// </summary>
    public IReadOnlyList<string> NodeMaps { get; init; } = [];

    private string? Value(string key)
    {
        return Values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
    }

    private int? Integer(string key)
    {
        return Value(key) is { } raw &&
               int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}

/// <summary>
///     Redguard's <c>WORLD.INI</c> — the plain-text master registry every other file hangs off, and
///     so the natural entry point to the game. One <c>[world]</c> section holds four loose keys plus
///     indexed <c>key[n]=value</c> entries, one group per world.
///     <para>
///         Measured on the retail Steam release (2026-09-04): 18,913 bytes, 672 CRLF lines, pure
///         ASCII, one section, 29 worlds and 70 distinct indexed keys. The indices are NOT
///         contiguous — 9, 10 and 16 are absent and 99 exists — so a reader must key by the bracket
///         value rather than by position.
///     </para>
///     <para>
///         ⚠ Of the 232 file-valued entries, 101 resolve and 131 do not, and the split is exactly by
///         kind: every <c>.GXA</c> (36), <c>.RGM</c> (29), <c>.COL</c> (29) and <c>.WLD</c> (7)
///         reference exists on disk, while every <c>.NOO</c> node map (119) and <c>.BSI</c> sprite
///         (12) is absent — from the install tree and from the <c>.ROB</c> archives alike. Those two
///         classes were never shipped, so a caller must treat a node-map or sky-sprite reference as
///         a name only, never as a file it can open. (<c>.BSI</c> is Battlespire's image extension,
///         which is a nice confirmation of the shared engine lineage.)
///     </para>
/// </summary>
internal sealed partial class RedguardWorldIni
{
    /// <summary>The file name, fixed by the game.</summary>
    public const string FileName = "WORLD.INI";

    private RedguardWorldIni(IReadOnlyDictionary<string, string> settings, IReadOnlyList<RedguardWorld> worlds)
    {
        Settings = settings;
        Worlds = worlds;
    }

    /// <summary>The section's un-indexed keys (<c>start_world</c>, <c>test_map_order</c>, …).</summary>
    public IReadOnlyDictionary<string, string> Settings { get; }

    /// <summary>Every world, in ascending index order.</summary>
    public IReadOnlyList<RedguardWorld> Worlds { get; }

    /// <summary>The world the game starts in, when <c>start_world</c> names a world that exists.</summary>
    public RedguardWorld? StartWorld =>
        Settings.TryGetValue("start_world", out var raw)
        && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            ? Worlds.FirstOrDefault(w => w.Index == index)
            : null;

    /// <summary>Parses the registry. Comment (<c>;</c>) and blank lines are skipped.</summary>
    public static RedguardWorldIni Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byIndex = new SortedDictionary<int, Dictionary<string, string>>();

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == ';' || (trimmed[0] == '[' && trimmed[^1] == ']'))
            {
                continue;
            }

            if (IndexedKey().Match(trimmed) is { Success: true } indexed)
            {
                var index = int.Parse(indexed.Groups[2].Value, CultureInfo.InvariantCulture);
                if (!byIndex.TryGetValue(index, out var values))
                {
                    values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    byIndex[index] = values;
                }

                // First writer wins, matching how the engine reads a registry top-down.
                values.TryAdd(indexed.Groups[1].Value, indexed.Groups[3].Value);
            }
            else if (PlainKey().Match(trimmed) is { Success: true } plain)
            {
                settings.TryAdd(plain.Groups[1].Value, plain.Groups[2].Value);
            }
        }

        var worlds = byIndex
            .Select(entry => new RedguardWorld
            {
                Index = entry.Key,
                Values = entry.Value,
                NodeMaps = NodeMapsOf(entry.Value)
            })
            .ToList();

        return new RedguardWorldIni(settings, worlds);
    }

    /// <summary>Reads the registry from a game root.</summary>
    public static RedguardWorldIni Load(string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);

        var path = Path.Combine(dataRoot, FileName);
        // Latin-1 rather than UTF-8: the file is a DOS-era registry, and although retail is pure
        // ASCII a byte above 0x7F must round-trip rather than become a replacement character.
        return Parse(File.ReadAllText(path, Encoding.Latin1));
    }

    /// <summary>
    ///     The <c>world_node_mapN</c> values in ascending N. Sorted numerically, because the keys
    ///     run past 9 (retail reaches 32) and an ordinal sort would put 10 before 2.
    /// </summary>
    private static IReadOnlyList<string> NodeMapsOf(Dictionary<string, string> values)
    {
        return
        [
            .. values
                .Select(pair => (Match: NodeMapKey().Match(pair.Key), pair.Value))
                .Where(entry => entry.Match.Success)
                .OrderBy(entry => int.Parse(entry.Match.Groups[1].Value, CultureInfo.InvariantCulture))
                .Select(entry => entry.Value)
        ];
    }

    [GeneratedRegex(@"^([A-Za-z_0-9]+)\s*\[\s*(-?\d+)\s*\]\s*=\s*(.*?)\s*$")]
    private static partial Regex IndexedKey();

    [GeneratedRegex(@"^([A-Za-z_0-9]+)\s*=\s*(.*?)\s*$")]
    private static partial Regex PlainKey();

    [GeneratedRegex(@"^world_node_map(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex NodeMapKey();
}
