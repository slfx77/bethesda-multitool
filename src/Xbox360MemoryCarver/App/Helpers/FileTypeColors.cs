using Windows.UI;
using Xbox360MemoryCarver.Core;
using Xbox360MemoryCarver.Core.Formats;

namespace Xbox360MemoryCarver;

/// <summary>
///     Provides color mappings for file types in the UI.
///     Wraps the FormatRegistry for WinUI color types.
/// </summary>
public static class FileTypeColors
{
    /// <summary>
    ///     Color used for unknown/untyped regions.
    /// </summary>
    public static readonly Color UnknownColor = FromArgb(FormatRegistry.UnknownColor);

    /// <summary>
    ///     Legend categories for UI display.
    /// </summary>
    public static readonly LegendCategory[] LegendCategories =
    [
        new("Texture", FromArgb(FormatRegistry.CategoryColors[FileCategory.Texture])),
        new("PNG", FromArgb(FormatRegistry.CategoryColors[FileCategory.Image])),
        new("Audio", FromArgb(FormatRegistry.CategoryColors[FileCategory.Audio])),
        new("Model", FromArgb(FormatRegistry.CategoryColors[FileCategory.Model])),
        new("Module", FromArgb(FormatRegistry.CategoryColors[FileCategory.Module])),
        new("Script", FromArgb(FormatRegistry.CategoryColors[FileCategory.Script])),
        new("Xbox/XUI", FromArgb(FormatRegistry.CategoryColors[FileCategory.Xbox])),
        new("Plugin", FromArgb(FormatRegistry.CategoryColors[FileCategory.Plugin])),
        new("Header", FromArgb(FormatRegistry.CategoryColors[FileCategory.Header]))
    ];

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
