using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Map;

/// <summary>
///     Presentation for one map-marker type: its display name, an <see cref="IconKey" /> an icon set
///     may resolve to authentic art, and a glyph/color <see cref="Fallback" /> drawn when no art is
///     available for the type.
/// </summary>
public sealed record MapMarkerEntry(int RawValue, string DisplayName, string IconKey, MapMarkerFallback Fallback);

/// <summary>Segoe MDL2 glyph + RGB used to draw the marker dot when an icon set has no art.</summary>
public readonly record struct MapMarkerFallback(string Glyph, byte R, byte G, byte B);

/// <summary>
///     Resolves a raw <c>TNAM</c> marker value to its per-game presentation. The raw value is parsed
///     identically for every game (a 2-byte <c>itU8 Type + unused</c> struct, see
///     <c>EsmWorldExtractor</c>), but the same number means different things per game — so the FO3/FNV
///     <see cref="MapMarkerType" /> enum cannot be used as a cross-game lookup. Each game owns a dense
///     table (index == raw value); unknown values / games without a table degrade to a deterministic,
///     type-distinct fallback rather than a wrong icon. Pure data — no XAML/GPU dependency, fully unit
///     testable. Authentic per-game icon art is layered on top by <c>IMapMarkerIconSet</c>.
/// </summary>
public static class MapMarkerCatalog
{
    private static readonly IReadOnlyList<MapMarkerEntry> Empty = [];
    private static readonly IReadOnlyList<MapMarkerEntry> FalloutTable = BuildFalloutTable();
    private static readonly IReadOnlyList<MapMarkerEntry> OblivionTable = BuildOblivionTable();
    private static readonly IReadOnlyList<MapMarkerEntry> SkyrimTable = BuildSkyrimTable();

    /// <summary>The full dense marker table for <paramref name="game" /> (index == raw value), or an
    ///     empty list for games whose table isn't wired yet (Skyrim/FO4/FO76 — populated with their
    ///     atlas phase) or that have no markers (Morrowind).</summary>
    public static IReadOnlyList<MapMarkerEntry> For(BethesdaGame game) => game switch
    {
        BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas => FalloutTable,
        BethesdaGame.Oblivion => OblivionTable,
        BethesdaGame.Skyrim => SkyrimTable,
        _ => Empty
    };

    /// <summary>True when a named per-type table exists for <paramref name="game" />.</summary>
    public static bool HasMarkers(BethesdaGame game) => For(game).Count > 0;

    /// <summary>Resolves <paramref name="rawValue" /> for <paramref name="game" />; never null. Out-of-range
    ///     values and games without a table return a deterministic type-distinct fallback entry.</summary>
    public static MapMarkerEntry Resolve(BethesdaGame game, int rawValue)
    {
        var table = For(game);
        return rawValue >= 0 && rawValue < table.Count ? table[rawValue] : UnknownEntry(rawValue);
    }

    // FO3 / FNV (0..14). Reuses the existing glyph/color source so the drawn dot is identical to today.
    private static IReadOnlyList<MapMarkerEntry> BuildFalloutTable()
    {
        (MapMarkerType Type, string Name) [] rows =
        [
            (MapMarkerType.None, "None"),
            (MapMarkerType.City, "City"),
            (MapMarkerType.Settlement, "Settlement"),
            (MapMarkerType.Encampment, "Encampment"),
            (MapMarkerType.NaturalLandmark, "Natural Landmark"),
            (MapMarkerType.Cave, "Cave"),
            (MapMarkerType.Factory, "Factory"),
            (MapMarkerType.Monument, "Monument"),
            (MapMarkerType.Military, "Military"),
            (MapMarkerType.Office, "Office"),
            (MapMarkerType.RuinsTown, "Town Ruins"),
            (MapMarkerType.RuinsUrban, "Urban Ruins"),
            (MapMarkerType.RuinsSewer, "Sewer Ruins"),
            (MapMarkerType.Metro, "Metro"),
            (MapMarkerType.Vault, "Vault")
        ];

        var entries = new MapMarkerEntry[rows.Length];
        for (var raw = 0; raw < rows.Length; raw++)
        {
            var (type, name) = rows[raw];
            var (r, g, b) = MapExportLayoutEngine.GetMarkerColor(type);
            var iconKey = type == MapMarkerType.None ? "" : EmbeddedIconStem(type);
            entries[raw] = new MapMarkerEntry(
                raw, name, iconKey,
                new MapMarkerFallback(MapExportLayoutEngine.GetMarkerGlyph(type), r, g, b));
        }

        return entries;
    }

    // Oblivion (0..12) — values from xEdit wbDefinitionsTES4.pas. Glyph/color are the deferred-art
    // fallback (Oblivion's authentic icons are atlas-packed; see the Oblivion atlas phase).
    private static IReadOnlyList<MapMarkerEntry> BuildOblivionTable()
    {
        (string Name, string IconKey, string Glyph, byte R, byte G, byte B) [] rows =
        [
            ("None",           "",              "", 200, 200, 200),
            ("Camp",           "camp",          "", 180, 140,  60),
            ("Cave",           "cave",          "", 120, 100,  80),
            ("City",           "city",          "", 255, 215,   0),
            ("Elven Ruin",     "elven_ruin",    "", 130, 200, 170),
            ("Fort Ruin",      "fort_ruin",     "", 170, 140, 110),
            ("Mine",           "mine",          "", 150, 140, 120),
            ("Landmark",       "landmark",      "", 160, 180, 120),
            ("Tavern",         "tavern",        "", 210, 160,  90),
            ("Settlement",     "settlement",    "", 200, 170,  80),
            ("Daedric Shrine", "daedric_shrine","", 170,  70, 190),
            ("Oblivion Gate",  "oblivion_gate", "", 220,  70,  40),
            ("Door",           "door",          "", 150, 150, 170)
        ];

        var entries = new MapMarkerEntry[rows.Length];
        for (var raw = 0; raw < rows.Length; raw++)
        {
            var (name, iconKey, glyph, r, g, b) = rows[raw];
            entries[raw] = new MapMarkerEntry(raw, name, iconKey, new MapMarkerFallback(glyph, r, g, b));
        }

        return entries;
    }

    // Skyrim (0..59) — names from xEdit wbMapMarkerEnum (TES5). Icons (1..52) were extracted offline from
    // the discovered *Marker sprites in interface\map.swf and embedded as skyrim_marker_NN.png; the DLC02
    // Solstheim markers (53..59) ship in the Dragonborn archive, so they carry names but no base-game icon
    // (they degrade to a labeled distinct dot). Icons are pre-styled (gray), so this game is NOT tinted.
    private static IReadOnlyList<MapMarkerEntry> BuildSkyrimTable()
    {
        string[] names =
        [
            "None", "City", "Town", "Settlement", "Cave", "Camp", "Fort", "Nordic Ruins", "Dwemer Ruin",
            "Shipwreck", "Grove", "Landmark", "Dragon Lair", "Farm", "Wood Mill", "Mine", "Imperial Camp",
            "Stormcloak Camp", "Doomstone", "Wheat Mill", "Smelter", "Stable", "Imperial Tower", "Clearing",
            "Pass", "Altar", "Rock", "Lighthouse", "Orc Stronghold", "Giant Camp", "Shack", "Nordic Tower",
            "Nordic Dwelling", "Docks", "Shrine", "Riften Castle", "Riften Capitol", "Windhelm Castle",
            "Windhelm Capitol", "Whiterun Castle", "Whiterun Capitol", "Solitude Castle", "Solitude Capitol",
            "Markarth Castle", "Markarth Capitol", "Winterhold Castle", "Winterhold Capitol", "Morthal Castle",
            "Morthal Capitol", "Falkreath Castle", "Falkreath Capitol", "Dawnstar Castle", "Dawnstar Capitol",
            "Temple of Miraak", "Raven Rock", "Beast Stone", "Tel Mithryn", "To Skyrim", "To Solstheim",
            "Castle Karstaag"
        ];

        var entries = new MapMarkerEntry[names.Length];
        for (var raw = 0; raw < names.Length; raw++)
        {
            var iconKey = raw is >= 1 and <= 52 ? $"skyrim_marker_{raw:D2}" : "";
            var (r, g, b) = HsvToRgb((raw * 137.508) % 360.0, 0.45, 0.85);
            entries[raw] = new MapMarkerEntry(raw, names[raw], iconKey, new MapMarkerFallback("", r, g, b));
        }

        return entries;
    }

    private static string EmbeddedIconStem(MapMarkerType type) => type switch
    {
        MapMarkerType.City => "icon_map_city",
        MapMarkerType.Settlement => "icon_map_settlement",
        MapMarkerType.Encampment => "icon_map_encampment",
        MapMarkerType.NaturalLandmark => "icon_map_natural_landmark",
        MapMarkerType.Cave => "icon_map_cave",
        MapMarkerType.Factory => "icon_map_factory",
        MapMarkerType.Monument => "icon_map_monument",
        MapMarkerType.Military => "icon_map_military",
        MapMarkerType.Office => "icon_map_office",
        MapMarkerType.RuinsTown => "icon_map_ruins_town",
        MapMarkerType.RuinsUrban => "icon_map_ruins_urban",
        MapMarkerType.RuinsSewer => "icon_map_ruins_sewer",
        MapMarkerType.Metro => "icon_map_metro",
        MapMarkerType.Vault => "icon_map_vault",
        _ => ""
    };

    // Deterministic, type-distinct dot for raw values with no named entry: golden-angle hue so adjacent
    // types get visibly different colors (used by atlas games until their table/art lands, and for any
    // out-of-range/modded value).
    private static MapMarkerEntry UnknownEntry(int rawValue)
    {
        var hue = (rawValue * 137.508) % 360.0;
        if (hue < 0) hue += 360.0;
        var (r, g, b) = HsvToRgb(hue, 0.55, 0.85);
        return new MapMarkerEntry(rawValue, $"Type {rawValue}", "", new MapMarkerFallback("", r, g, b));
    }

    private static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        var m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
