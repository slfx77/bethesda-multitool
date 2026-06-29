using BethesdaMultitool.Core.Formats.Esm.Enums;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Map;

/// <summary>
///     Provides embedded map marker icon PNGs keyed by <see cref="MapMarkerType" />.
///     Icons are white silhouettes on transparent background, intended for runtime tinting.
/// </summary>
public static class MapMarkerIconProvider
{
    /// <summary>
    ///     Mapping from <see cref="MapMarkerType" /> to the BSA filename stem used in the embedded resource.
    /// </summary>
    private static readonly Dictionary<MapMarkerType, string> TypeToFileName = new()
    {
        [MapMarkerType.City] = "icon_map_city",
        [MapMarkerType.Settlement] = "icon_map_settlement",
        [MapMarkerType.Encampment] = "icon_map_encampment",
        [MapMarkerType.NaturalLandmark] = "icon_map_natural_landmark",
        [MapMarkerType.Cave] = "icon_map_cave",
        [MapMarkerType.Factory] = "icon_map_factory",
        [MapMarkerType.Monument] = "icon_map_monument",
        [MapMarkerType.Military] = "icon_map_military",
        [MapMarkerType.Office] = "icon_map_office",
        [MapMarkerType.RuinsTown] = "icon_map_ruins_town",
        [MapMarkerType.RuinsUrban] = "icon_map_ruins_urban",
        [MapMarkerType.RuinsSewer] = "icon_map_ruins_sewer",
        [MapMarkerType.Metro] = "icon_map_metro",
        [MapMarkerType.Vault] = "icon_map_vault"
    };

    private static readonly Lazy<Dictionary<MapMarkerType, byte[]>> Cache = new(LoadAll);

    // Per-stem cache for the game-agnostic GetIconPng(iconKey) path (FO3/FNV icon_map_*, Skyrim
    // skyrim_marker_NN, etc.). Resources are matched by name suffix once and cached.
    private static readonly Dictionary<string, byte[]?> StemCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object StemLock = new();

    /// <summary>
    ///     Get the PNG bytes for a marker type, or null if no icon is available.
    /// </summary>
    public static byte[]? GetIconPng(MapMarkerType type)
    {
        return Cache.Value.GetValueOrDefault(type);
    }

    /// <summary>
    ///     Get the embedded PNG bytes for a marker icon by its resource stem (without the <c>.png</c>
    ///     extension), e.g. <c>icon_map_city</c> or <c>skyrim_marker_04</c>; null if not embedded or the
    ///     key is empty. This is the per-game path: a catalog entry's <c>IconKey</c> names the resource.
    /// </summary>
    public static byte[]? GetIconPng(string iconKey)
    {
        if (string.IsNullOrEmpty(iconKey)) return null;
        lock (StemLock)
        {
            if (StemCache.TryGetValue(iconKey, out var cached)) return cached;
            var bytes = LoadByStem(iconKey);
            StemCache[iconKey] = bytes;
            return bytes;
        }
    }

    private static byte[]? LoadByStem(string iconKey)
    {
        var assembly = typeof(MapMarkerIconProvider).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($".{iconKey}.png", StringComparison.OrdinalIgnoreCase));
        if (resourceName is null) return null;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    ///     Check whether an icon is available for the given marker type.
    /// </summary>
    public static bool HasIcon(MapMarkerType? type)
    {
        return type.HasValue && Cache.Value.ContainsKey(type.Value);
    }

    private static Dictionary<MapMarkerType, byte[]> LoadAll()
    {
        var result = new Dictionary<MapMarkerType, byte[]>();
        var assembly = typeof(MapMarkerIconProvider).Assembly;

        foreach (var (type, fileName) in TypeToFileName)
        {
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith($"{fileName}.png", StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
                continue;

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                continue;

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            result[type] = ms.ToArray();
        }

        return result;
    }
}
