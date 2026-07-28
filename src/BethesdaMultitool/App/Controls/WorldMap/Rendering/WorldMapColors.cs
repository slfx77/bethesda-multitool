using System.Collections.Frozen;
using BethesdaMultitool.Core.Formats.Esm.Export.Map;
using BethesdaMultitool.Core.Formats;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models;
using Windows.UI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool;

/// <summary>Color, glyph, and name helpers for placed-object categories and map markers in the world map.</summary>
internal static class WorldMapColors
{
    internal static readonly FrozenDictionary<PlacedObjectCategory, Color> CategoryColors =
        BuildWorldCategoryColors();

    internal static string GetCategoryDisplayName(PlacedObjectCategory category) => category switch
    {
        PlacedObjectCategory.Npc => "NPC",
        PlacedObjectCategory.MapMarker => "Map Marker",
        PlacedObjectCategory.Landscape => "Landscape",
        PlacedObjectCategory.Plants => "Plants",
        PlacedObjectCategory.Effects => "Effects",
        PlacedObjectCategory.Vehicles => "Vehicles",
        PlacedObjectCategory.Traps => "Traps",
        PlacedObjectCategory.Sky => "Sky",
        PlacedObjectCategory.Tree => "Trees",
        _ => category.ToString()
    };

    internal static Color GetCategoryColor(PlacedObjectCategory category) =>
        CategoryColors.GetValueOrDefault(category, Color.FromArgb(255, 80, 80, 80));

    /// <summary>
    ///     Builds perceptually-uniform OKLCH colors for world map categories.
    ///     Pins 3 semantic hues (Creature=red, Plants=green, NPC=blue),
    ///     keeps MapMarker=white and Unknown=dark gray, then distributes the
    ///     remaining 15 categories across 3 hue arcs with lightness cycling.
    ///     Landscape is auto-generated (not pinned) because the default amber
    ///     heightmap tint would make a pinned amber/yellow Landscape blend in.
    /// </summary>
    private static FrozenDictionary<PlacedObjectCategory, Color> BuildWorldCategoryColors()
    {
        ReadOnlySpan<double> lightnessTiers = [0.62, 0.72, 0.78];
        const double chroma = 0.22;
        const double creatureHue = 25.0;
        const double plantsHue = 140.0;
        const double npcHue = 220.0;

        var colors = new Dictionary<PlacedObjectCategory, Color>
        {
            [PlacedObjectCategory.Creature] = ArgbToColor(FormatRegistry.OklchToArgb(0.72, chroma, creatureHue)),
            [PlacedObjectCategory.Plants] = ArgbToColor(FormatRegistry.OklchToArgb(0.72, chroma, plantsHue)),
            [PlacedObjectCategory.Npc] = ArgbToColor(FormatRegistry.OklchToArgb(0.72, chroma, npcHue)),
            [PlacedObjectCategory.MapMarker] = Color.FromArgb(255, 255, 255, 255),
            // Sky-blue for placed sky/glow meshes (DiamondCityGlow). Pinned rather than arc-distributed so
            // the arc slot count stays in lock-step with the `remaining` array below (a mismatch throws).
            [PlacedObjectCategory.Sky] = Color.FromArgb(255, 135, 206, 235),
            // Trees: darker forest green next to Plants' 0.72/0.22/140 so the two vegetation layers
            // read as siblings but stay distinguishable. Pinned for the same arc-lock-step reason.
            [PlacedObjectCategory.Tree] = ArgbToColor(FormatRegistry.OklchToArgb(0.55, 0.18, 145.0)),
            [PlacedObjectCategory.Unknown] = Color.FromArgb(255, 80, 80, 80)
        };

        // 15 remaining categories distributed across 3 hue arcs between pinned hues.
        // Arc 1: 25->140 (115, 5 slots), Arc 2: 140->220 (80, 3 slots),
        // Arc 3: 220->385 (165, 7 slots). ~20 step in each arc.
        // Ordering based on WastelandNV counts: top categories (Landscape 48%,
        // Clutter 12%, Architecture 10%, Static 4%) placed in separate arcs.
        PlacedObjectCategory[] remaining =
        [
            // Arc 1 (25->140): warm orange -> yellow-green
            PlacedObjectCategory.Architecture, PlacedObjectCategory.Effects,
            PlacedObjectCategory.Dungeon, PlacedObjectCategory.Furniture,
            PlacedObjectCategory.Vehicles,
            // Arc 2 (140->220): teal -> blue
            PlacedObjectCategory.Clutter, PlacedObjectCategory.Static,
            PlacedObjectCategory.Sound,
            // Arc 3 (220->385): blue -> purple -> magenta
            PlacedObjectCategory.Landscape, PlacedObjectCategory.Item,
            PlacedObjectCategory.Activator, PlacedObjectCategory.Container,
            PlacedObjectCategory.Door, PlacedObjectCategory.Light,
            PlacedObjectCategory.Traps
        ];

        (double start, double end, int count)[] arcs =
        [
            (creatureHue, plantsHue, 5),
            (plantsHue, npcHue, 3),
            (npcHue, creatureHue + 360, 7)
        ];

        var idx = 0;
        foreach (var (start, end, count) in arcs)
        {
            var step = (end - start) / (count + 1);
            for (var i = 1; i <= count; i++)
            {
                var hue = (start + step * i) % 360.0;
                var lightness = lightnessTiers[idx % lightnessTiers.Length];
                colors[remaining[idx]] = ArgbToColor(FormatRegistry.OklchToArgb(lightness, chroma, hue));
                idx++;
            }
        }

        return colors.ToFrozenDictionary();
    }

    internal static Color ArgbToColor(uint argb) => Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    internal static string GetMarkerGlyph(MapMarkerType? markerType) =>
        MapExportLayoutEngine.GetMarkerGlyph(markerType);

    internal static Color GetMarkerColor(MapMarkerType? markerType)
    {
        var (r, g, b) = MapExportLayoutEngine.GetMarkerColor(markerType);
        return Color.FromArgb(255, r, g, b);
    }

    internal static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    internal static int GetGroupSortOrder(string group) => group switch
    {
        "Unknown" => 2,
        "Interior" => 1,
        _ => 0
    };

    /// <summary>
    ///     Format worldspace name as "Display Name (EditorId)" when both are available
    ///     and different, otherwise just the best available name.
    /// </summary>
    internal static string FormatWorldspaceName(WorldspaceRecord ws)
    {
        var fullName = ws.FullName;
        var editorId = ws.EditorId;

        if (!string.IsNullOrEmpty(fullName) && !string.IsNullOrEmpty(editorId) &&
            !string.Equals(fullName, editorId, StringComparison.OrdinalIgnoreCase))
        {
            return $"{fullName} ({editorId})";
        }

        return fullName ?? editorId ?? $"0x{ws.FormId:X8}";
    }
}

