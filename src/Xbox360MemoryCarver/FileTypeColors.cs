using Windows.UI;
using Xbox360MemoryCarver.Core.Models;

namespace Xbox360MemoryCarver.App;

/// <summary>
///     Single source of truth for file type colors used across the application.
///     Uses FileTypeMetadata categories from Core and maps them to colors.
/// </summary>
public static class FileTypeColors
{
    /// <summary>
    ///     Color used for unknown/untyped regions.
    /// </summary>
    public static readonly Color UnknownColor = FromArgb(0xFF3D3D3D);

    /// <summary>
    ///     Colors for each category, distributed across the hue spectrum for visual distinction.
    /// </summary>
    private static readonly Dictionary<FileTypeMetadata.Category, Color> CategoryColors = new()
    {
        [FileTypeMetadata.Category.Texture] = FromArgb(0xFF2ECC71),  // Green (120°)
        [FileTypeMetadata.Category.Image] = FromArgb(0xFF1ABC9C),    // Teal/Cyan (170°)
        [FileTypeMetadata.Category.Audio] = FromArgb(0xFFE74C3C),    // Red (5°)
        [FileTypeMetadata.Category.Model] = FromArgb(0xFFF1C40F),    // Yellow (50°)
        [FileTypeMetadata.Category.Module] = FromArgb(0xFF9B59B6),   // Purple (280°)
        [FileTypeMetadata.Category.Script] = FromArgb(0xFFE67E22),   // Orange (30°)
        [FileTypeMetadata.Category.Xbox] = FromArgb(0xFF3498DB),     // Blue (210°)
        [FileTypeMetadata.Category.Plugin] = FromArgb(0xFFFF6B9D)    // Pink/Magenta (340°)
    };

    /// <summary>
    ///     Legend categories for UI display.
    /// </summary>
    public static readonly LegendCategory[] LegendCategories =
    [
        new("Texture", CategoryColors[FileTypeMetadata.Category.Texture]),
        new("PNG", CategoryColors[FileTypeMetadata.Category.Image]),
        new("Audio", CategoryColors[FileTypeMetadata.Category.Audio]),
        new("Model", CategoryColors[FileTypeMetadata.Category.Model]),
        new("Module", CategoryColors[FileTypeMetadata.Category.Module]),
        new("Script", CategoryColors[FileTypeMetadata.Category.Script]),
        new("Xbox/XUI", CategoryColors[FileTypeMetadata.Category.Xbox]),
        new("Plugin", CategoryColors[FileTypeMetadata.Category.Plugin])
    ];

    /// <summary>
    ///     Get color for a file type by signature key or description.
    /// </summary>
    public static Color GetColor(string fileType)
    {
        var sigKey = FileTypeMetadata.NormalizeToSignatureKey(fileType);
        var category = FileTypeMetadata.GetCategory(sigKey);
        return CategoryColors.TryGetValue(category, out var color) ? color : FromArgb(0xFF555555);
    }

    /// <summary>
    ///     Normalize a file type description to a standard key.
    /// </summary>
    public static string NormalizeTypeName(string fileType)
    {
        return FileTypeMetadata.NormalizeToSignatureKey(fileType);
    }

    /// <summary>
    ///     Get priority for overlap resolution. Lower number = higher priority.
    /// </summary>
    public static int GetPriority(string fileType)
    {
        var category = FileTypeMetadata.GetCategory(FileTypeMetadata.NormalizeToSignatureKey(fileType));
        return category switch
        {
            FileTypeMetadata.Category.Texture or FileTypeMetadata.Category.Image or FileTypeMetadata.Category.Audio => 1,
            FileTypeMetadata.Category.Model => 2,
            FileTypeMetadata.Category.Script or FileTypeMetadata.Category.Plugin or FileTypeMetadata.Category.Xbox => 3,
            FileTypeMetadata.Category.Module => 4,
            _ => 5
        };
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
