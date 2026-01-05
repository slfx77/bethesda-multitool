using Windows.UI;
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
        new("Plugin", FromArgb(FormatRegistry.CategoryColors[FileCategory.Plugin]))
    ];

    /// <summary>
    ///     Get color for a file type by signature ID or description.
    /// </summary>
    public static Color GetColor(string fileType)
    {
        var sigId = FormatRegistry.NormalizeToSignatureId(fileType);
        return FromArgb(FormatRegistry.GetColor(sigId));
    }

    /// <summary>
    ///     Normalize a file type description to a standard signature ID.
    /// </summary>
    public static string NormalizeTypeName(string fileType)
    {
        return FormatRegistry.NormalizeToSignatureId(fileType);
    }

    /// <summary>
    ///     Get priority for overlap resolution. Lower number = higher priority.
    /// </summary>
    public static int GetPriority(string fileType)
    {
        var sigId = FormatRegistry.NormalizeToSignatureId(fileType);
        return FormatRegistry.GetDisplayPriority(sigId);
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
