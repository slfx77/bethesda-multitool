using Windows.UI;
using Xbox360MemoryCarver.Core.FileTypes;

namespace Xbox360MemoryCarver.App;

/// <summary>
///     Provides color mappings for file types in the UI.
///     Wraps the FileTypeRegistry for WinUI color types.
/// </summary>
public static class FileTypeColors
{
    /// <summary>
    ///     Color used for unknown/untyped regions.
    /// </summary>
    public static readonly Color UnknownColor = FromArgb(FileTypeRegistry.UnknownColor);

    /// <summary>
    ///     Legend categories for UI display.
    /// </summary>
    public static readonly LegendCategory[] LegendCategories =
    [
        new("Texture", FromArgb(FileTypeRegistry.CategoryColors[FileCategory.Texture])),
        new("PNG", FromArgb(FileTypeRegistry.CategoryColors[FileCategory.Image])),
        new("Audio", FromArgb(FileTypeRegistry.CategoryColors[FileCategory.Audio])),
        new("Model", FromArgb(FileTypeRegistry.CategoryColors[FileCategory.Model])),
        new("Module", FromArgb(FileTypeRegistry.CategoryColors[FileCategory.Module])),
        new("Script", FromArgb(FileTypeRegistry.CategoryColors[FileCategory.Script])),
        new("Xbox/XUI", FromArgb(FileTypeRegistry.CategoryColors[FileCategory.Xbox])),
        new("Plugin", FromArgb(FileTypeRegistry.CategoryColors[FileCategory.Plugin]))
    ];

    /// <summary>
    ///     Get color for a file type by signature ID or description.
    /// </summary>
    public static Color GetColor(string fileType)
    {
        var sigId = FileTypeRegistry.NormalizeToSignatureId(fileType);
        return FromArgb(FileTypeRegistry.GetColor(sigId));
    }

    /// <summary>
    ///     Normalize a file type description to a standard signature ID.
    /// </summary>
    public static string NormalizeTypeName(string fileType)
    {
        return FileTypeRegistry.NormalizeToSignatureId(fileType);
    }

    /// <summary>
    ///     Get priority for overlap resolution. Lower number = higher priority.
    /// </summary>
    public static int GetPriority(string fileType)
    {
        var sigId = FileTypeRegistry.NormalizeToSignatureId(fileType);
        return FileTypeRegistry.GetDisplayPriority(sigId);
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
