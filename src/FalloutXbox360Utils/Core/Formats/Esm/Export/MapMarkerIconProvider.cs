using System.Reflection;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

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
        [MapMarkerType.Vault] = "icon_map_vault",
    };

    private static readonly Lazy<Dictionary<MapMarkerType, byte[]>> Cache = new(LoadAll);

    /// <summary>
    ///     Get the PNG bytes for a marker type, or null if no icon is available.
    /// </summary>
    public static byte[]? GetIconPng(MapMarkerType type)
    {
        return Cache.Value.GetValueOrDefault(type);
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
