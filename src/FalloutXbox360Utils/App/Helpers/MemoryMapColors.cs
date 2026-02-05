using Windows.UI;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats;

namespace FalloutXbox360Utils;

/// <summary>
///     Generates and manages colors for memory map visualization.
///     Hardcoded colors for neutral categories (Header, Gap).
///     Dynamic rainbow spectrum for all other categories.
/// </summary>
public static class MemoryMapColors
{
    // Hardcoded neutral colors
    public static readonly Color HeaderColor = Color.FromArgb(255, 112, 128, 144); // Steel gray
    public static readonly Color GapColor = Color.FromArgb(255, 50, 50, 50); // Dark gray (identified gaps)

    // Dynamic categories in display order (will be assigned rainbow colors)
    private static readonly string[] DynamicCategoryNames =
    [
        "Module", "Texture", "PNG", "Audio", "Model",
        "Script", "ESM Data", "Xbox/XUI", "ESM-like", "Asset Mgmt"
    ];

    // Map from GapClassification to dynamic category index (or -1 for Gap)
    private static readonly Dictionary<GapClassification, int> GapCategoryIndex = new()
    {
        [GapClassification.EsmLike] = 8,
        [GapClassification.AssetManagement] = 9
        // All others map to GapColor
    };

    /// <summary>
    ///     Human-readable display names for gap classifications (used by coverage tab).
    /// </summary>
    public static readonly Dictionary<GapClassification, string> GapDisplayNames = new()
    {
        [GapClassification.AsciiText] = "ASCII Text",
        [GapClassification.StringPool] = "String Pool",
        [GapClassification.PointerDense] = "Pointer Dense",
        [GapClassification.AssetManagement] = "Asset Mgmt",
        [GapClassification.EsmLike] = "ESM-like",
        [GapClassification.BinaryData] = "Binary Data",
        [GapClassification.ZeroFill] = "Zero Fill"
    };

    // Lazy-initialized color cache
    private static Color[]? _dynamicColors;

    /// <summary>
    ///     Legend categories for UI display.
    ///     Order: Header (gray), dynamic categories (rainbow), Gap (gray).
    /// </summary>
    public static IReadOnlyList<LegendCategory> LegendCategories { get; } = BuildLegend();

    /// <summary>
    ///     Gets color for a file category.
    ///     Uses exhaustive switch - compiler errors if FileCategory has unhandled value.
    /// </summary>
    public static Color GetColor(FileCategory category)
    {
        return category switch
        {
            FileCategory.Header => HeaderColor,
            FileCategory.Module => GetDynamicColor(0),
            FileCategory.Texture => GetDynamicColor(1),
            FileCategory.Image => GetDynamicColor(2),
            FileCategory.Audio => GetDynamicColor(3),
            FileCategory.Model => GetDynamicColor(4),
            FileCategory.Script => GetDynamicColor(5),
            FileCategory.EsmData => GetDynamicColor(6),
            FileCategory.Xbox => GetDynamicColor(7),
            FileCategory.Video => GapColor // Rarely used (BIK format), not assigned distinct rainbow color
        };
    }

    /// <summary>
    ///     Gets color for a gap classification (memory map view).
    /// </summary>
    public static Color GetGapColor(GapClassification classification)
    {
        if (GapCategoryIndex.TryGetValue(classification, out var index))
        {
            return GetDynamicColor(index);
        }

        return GapColor; // ASCII Text, String Pool, Pointer Dense, Binary Data, Zero Fill → Gap
    }

    private static Color GetDynamicColor(int index)
    {
        _dynamicColors ??= GenerateDynamicColors();
        return _dynamicColors[index];
    }

    private static Color[] GenerateDynamicColors()
    {
        var count = DynamicCategoryNames.Length;
        var colors = new Color[count];
        var hueStep = 360.0 / count;

        for (var i = 0; i < count; i++)
        {
            var hue = i * hueStep;
            colors[i] = HsvToRgb(hue, 0.70, 0.88);
        }

        return colors;
    }

    private static LegendCategory[] BuildLegend()
    {
        var legend = new List<LegendCategory>
        {
            new("Header", HeaderColor)
        };

        // Add dynamic categories
        for (var i = 0; i < DynamicCategoryNames.Length; i++)
        {
            legend.Add(new LegendCategory(DynamicCategoryNames[i], GetDynamicColor(i)));
        }

        legend.Add(new LegendCategory("Gap", GapColor));

        return legend.ToArray();
    }

    /// <summary>
    ///     Converts HSV to RGB color.
    /// </summary>
    private static Color HsvToRgb(double hue, double saturation, double value)
    {
        var h = hue / 60.0;
        var i = (int)Math.Floor(h);
        var f = h - i;
        var p = value * (1 - saturation);
        var q = value * (1 - saturation * f);
        var t = value * (1 - saturation * (1 - f));

        double r, g, b;
        switch (i % 6)
        {
            case 0:
                r = value;
                g = t;
                b = p;
                break;
            case 1:
                r = q;
                g = value;
                b = p;
                break;
            case 2:
                r = p;
                g = value;
                b = t;
                break;
            case 3:
                r = p;
                g = q;
                b = value;
                break;
            case 4:
                r = t;
                g = p;
                b = value;
                break;
            default:
                r = value;
                g = p;
                b = q;
                break;
        }

        return Color.FromArgb(255, (byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    public readonly struct LegendCategory(string name, Color color)
    {
        public string Name { get; } = name;
        public Color Color { get; } = color;
    }
}
