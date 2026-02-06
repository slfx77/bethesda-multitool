using System.Collections.Frozen;
using Windows.UI;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Coverage;
using FalloutXbox360Utils.Core.Formats;

namespace FalloutXbox360Utils;

/// <summary>
///     Provides color mappings for file types in the UI.
///     Delegates to MemoryMapColors for centralized color management.
/// </summary>
public static class FileTypeColors
{
    /// <summary>
    ///     Color used for unknown/untyped regions.
    /// </summary>
    public static readonly Color UnknownColor = MemoryMapColors.GapColor;

    /// <summary>
    ///     Legend categories for UI display.
    /// </summary>
    public static IReadOnlyList<MemoryMapColors.LegendCategory> LegendCategories
        => MemoryMapColors.LegendCategories;

    /// <summary>
    ///     Human-readable display names for gap classifications (used by coverage tab).
    /// </summary>
    public static FrozenDictionary<GapClassification, string> GapDisplayNames
        => MemoryMapColors.GapDisplayNames;

    /// <summary>
    ///     Get color for a CarvedFileInfo using its Category.
    /// </summary>
    public static Color GetColor(CarvedFileInfo file)
        => MemoryMapColors.GetColor(file.Category);

    /// <summary>
    ///     Get color directly from a category.
    /// </summary>
    public static Color GetColorByCategory(FileCategory category)
        => MemoryMapColors.GetColor(category);

    /// <summary>
    ///     Gets the color for a gap classification in the memory map view.
    /// </summary>
    public static Color GetMemoryMapGapColor(GapClassification classification)
        => MemoryMapColors.GetGapColor(classification);
}
