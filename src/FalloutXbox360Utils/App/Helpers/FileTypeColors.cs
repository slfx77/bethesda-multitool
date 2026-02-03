using Windows.UI;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats;

namespace FalloutXbox360Utils;

/// <summary>
///     Provides color mappings for file types in the UI.
///     Wraps the FormatRegistry for WinUI color types.
///     All categories (file types + gap classifications) share a unified
///     hue spectrum (S=70%, V=88%) for maximum visual distinction.
/// </summary>
public static class FileTypeColors
{
    /// <summary>
    ///     Color used for unknown/untyped regions.
    /// </summary>
    public static readonly Color UnknownColor = FromArgb(FormatRegistry.UnknownColor);

    /// <summary>
    ///     Legend categories for UI display.
    ///     Order: Header, Module, Texture, PNG, Audio, Model, Script, ESM Data, Xbox/XUI.
    /// </summary>
    public static readonly LegendCategory[] LegendCategories =
    [
        new("Header", FromArgb(FormatRegistry.CategoryColors[FileCategory.Header])),
        new("Module", FromArgb(FormatRegistry.CategoryColors[FileCategory.Module])),
        new("Texture", FromArgb(FormatRegistry.CategoryColors[FileCategory.Texture])),
        new("PNG", FromArgb(FormatRegistry.CategoryColors[FileCategory.Image])),
        new("Audio", FromArgb(FormatRegistry.CategoryColors[FileCategory.Audio])),
        new("Model", FromArgb(FormatRegistry.CategoryColors[FileCategory.Model])),
        new("Script", FromArgb(FormatRegistry.CategoryColors[FileCategory.Script])),
        new("ESM Data", FromArgb(FormatRegistry.CategoryColors[FileCategory.EsmData])),
        new("Xbox/XUI", FromArgb(FormatRegistry.CategoryColors[FileCategory.Xbox]))
    ];

    /// <summary>
    ///     Gap classification colors for coverage analysis.
    ///     Continues the hue spectrum from file categories (216°–312°).
    ///     Ordered: ASCII Text, String Pool, Pointer Dense, Asset Mgmt, Binary Data, Zero Fill.
    /// </summary>
    public static readonly Dictionary<GapClassification, Color> GapColors = new()
    {
        [GapClassification.AsciiText] = FromArgb(0xFF4382E0), // Hue 216° Steel blue
        [GapClassification.StringPool] = FromArgb(0xFF4343E0), // Hue 240° Blue
        [GapClassification.PointerDense] = FromArgb(0xFF8243E0), // Hue 264° Violet
        [GapClassification.AssetManagement] = FromArgb(0xFFC043E0), // Hue 288° Purple
        [GapClassification.EsmLike] = FromArgb(0xFFE043C0), // Hue 312° Magenta
        [GapClassification.BinaryData] = FromArgb(0xFFE04382), // Hue 336° Rose
        [GapClassification.ZeroFill] = FromArgb(0xFF2A2A2A) // Dark (unchanged)
    };

    /// <summary>
    ///     Human-readable display names for gap classifications.
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

    /// <summary>
    ///     Get color for a CarvedFileInfo using its Category.
    /// </summary>
    public static Color GetColor(CarvedFileInfo file)
    {
        return GetColorByCategory(file.Category);
    }

    /// <summary>
    ///     Get color directly from a category (most efficient).
    /// </summary>
    public static Color GetColorByCategory(FileCategory category)
    {
        return FromArgb(FormatRegistry.CategoryColors.TryGetValue(category, out var color)
            ? color
            : FormatRegistry.UnknownColor);
    }

    private static Color FromArgb(uint argb)
    {
        return Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF));
    }

    public readonly struct LegendCategory(string name, Color color)
    {
        public string Name { get; } = name;
        public Color Color { get; } = color;
    }
}
