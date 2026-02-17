using System.Collections.Frozen;
using Windows.UI;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Coverage;
using FalloutXbox360Utils.Core.Formats;

namespace FalloutXbox360Utils;

/// <summary>
///     Provides WinUI Color values for memory map visualization.
///     File category colors are sourced from <see cref="FormatRegistry.CategoryColors" /> (single source of truth).
///     Gap classification colors are defined here (GUI-only concern).
/// </summary>
public static class MemoryMapColors
{
    // Gap color (dark gray for unidentified memory regions)
    public static readonly Color GapColor = Color.FromArgb(255, 50, 50, 50);

    // Gap classifications that get distinct rainbow colors (generated independently)
    // AssetManagement uses OKLCH at hue 216° (dark desaturated blue-gray) — sits in
    // the largest gap of the golden-angle category palette, and clearly distinct
    // from file categories via lower lightness (0.60) and chroma (0.06).
    private static readonly FrozenDictionary<GapClassification, Color> GapRainbowColors =
        new Dictionary<GapClassification, Color>
        {
            [GapClassification.RecordSignature] = Color.FromArgb(255, 255, 255, 255), // White
            [GapClassification.AssetManagement] = ArgbToColor(FormatRegistry.OklchToArgb(0.60, 0.06, 216))
        }.ToFrozenDictionary();

    /// <summary>
    ///     Human-readable display names for gap classifications (used by coverage tab).
    /// </summary>
    public static readonly FrozenDictionary<GapClassification, string> GapDisplayNames =
        new Dictionary<GapClassification, string>
        {
            [GapClassification.AsciiText] = "ASCII Text",
            [GapClassification.StringPool] = "String Pool",
            [GapClassification.PointerDense] = "Pointer Dense",
            [GapClassification.AssetManagement] = "Asset Mgmt",
            [GapClassification.RecordSignature] = "Record Signatures",
            [GapClassification.BinaryData] = "Binary Data",
            [GapClassification.ZeroFill] = "Zero Fill"
        }.ToFrozenDictionary();

    /// <summary>
    ///     Legend categories for UI display.
    ///     Order: file categories from FormatRegistry (rainbow), gap rainbow categories, Gap (gray).
    /// </summary>
    public static IReadOnlyList<LegendCategory> LegendCategories { get; } = BuildLegend();

    /// <summary>
    ///     Gets WinUI Color for a file category.
    ///     Sourced from <see cref="FormatRegistry.CategoryColors" />.
    /// </summary>
    public static Color GetColor(FileCategory category)
    {
        var argb = FormatRegistry.CategoryColors.GetValueOrDefault(category, FormatRegistry.UnknownColor);
        return ArgbToColor(argb);
    }

    /// <summary>
    ///     Gets color for a gap classification (memory map view).
    /// </summary>
    public static Color GetGapColor(GapClassification classification)
    {
        return GapRainbowColors.GetValueOrDefault(classification, GapColor);
    }

    private static LegendCategory[] BuildLegend()
    {
        var legend = new List<LegendCategory>();

        // File categories from FormatRegistry (skip Header, show it first as gray)
        legend.Add(new LegendCategory("Header", GetColor(FileCategory.Header)));

        foreach (var category in Enum.GetValues<FileCategory>())
        {
            if (category == FileCategory.Header)
            {
                continue;
            }

            legend.Add(new LegendCategory(FormatCategoryName(category), GetColor(category)));
        }

        // Gap rainbow categories
        foreach (var (classification, name) in GapDisplayNames)
        {
            if (GapRainbowColors.TryGetValue(classification, out var color))
            {
                legend.Add(new LegendCategory(name, color));
            }
        }

        legend.Add(new LegendCategory("Gap", GapColor));

        return legend.ToArray();
    }

    private static string FormatCategoryName(FileCategory category) => category switch
    {
        FileCategory.Image => "PNG",
        FileCategory.EsmData => "ESM Data",
        FileCategory.Xbox => "Xbox/XUI",
        _ => category.ToString()
    };

    private static Color ArgbToColor(uint argb) => Color.FromArgb(
        (byte)(argb >> 24),
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb);

    public readonly struct LegendCategory(string name, Color color)
    {
        public string Name { get; } = name;
        public Color Color { get; } = color;
    }
}
