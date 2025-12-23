using Windows.UI;

namespace Xbox360MemoryCarver.App;

/// <summary>
/// Single source of truth for file type colors used across the application.
/// Used by the legend, hex viewer, and minimap to ensure consistency.
/// </summary>
public static class FileTypeColors
{
    /// <summary>
    /// Color used for unknown/untyped regions.
    /// </summary>
    public static readonly Color UnknownColor = FromArgb(0xFF424242);

    // Legend categories with display names and colors
    public static readonly LegendCategory[] LegendCategories =
    [
        new("Texture", 0xFF4CAF50),   // DDX/DDS - Green
        new("PNG", 0xFF81C784),        // PNG - Light Green
        new("Audio", 0xFFE91E63),      // XMA/LIP - Pink
        new("Model", 0xFFFFC107),      // NIF - Yellow
        new("Module", 0xFF9C27B0),     // XEX - Purple
        new("Script", 0xFFFF9800),     // Scripts - Orange
        new("Xbox/XUI", 0xFF3F51B5),   // XDBF/XUI - Indigo
    ];

    // Mapping from normalized type names to colors
    public static Color GetColor(string normalizedTypeName)
    {
        return normalizedTypeName switch
        {
            // Textures (DDX/DDS) - Green
            "dds" or "ddx_3xdo" or "ddx_3xdr" => FromArgb(0xFF4CAF50),

            // PNG - Light Green
            "png" => FromArgb(0xFF81C784),

            // Audio - Pink
            "xma" or "lip" => FromArgb(0xFFE91E63),

            // Models - Yellow
            "nif" => FromArgb(0xFFFFC107),

            // Modules/Executables - Purple
            "xex" or "module" => FromArgb(0xFF9C27B0),

            // Scripts - Orange
            "script_scn" or "obscript" => FromArgb(0xFFFF9800),

            // Xbox Dashboard/XUI - Indigo
            "xdbf" or "xui" => FromArgb(0xFF3F51B5),

            // Game data (ESP) - Amber
            "esp" => FromArgb(0xFFFFB74D),

            // Unknown - Gray
            _ => FromArgb(0xFF646464)
        };
    }

    /// <summary>
    /// Normalize a file type description to a standard key.
    /// </summary>
    public static string NormalizeTypeName(string fileType)
    {
        var lower = fileType.ToLowerInvariant();
        return lower switch
        {
            "xbox 360 ddx texture (3xdo format)" => "ddx_3xdo",
            "xbox 360 ddx texture (3xdr engine-tiled format)" => "ddx_3xdr",
            "directdraw surface texture" => "dds",
            "png image" => "png",
            "xbox media audio (riff/xma)" => "xma",
            "netimmerse/gamebryo 3d model" => "nif",
            "xbox 360 executable" => "module",
            "xex" => "module",
            "xbox dashboard file" => "xdbf",
            "xui scene" => "xui",
            "xui binary" => "xui",
            "elder scrolls plugin" => "esp",
            "lip-sync animation" => "lip",
            "bethesda obscript (scn format)" => "obscript",
            "script_scn" => "obscript",
            _ => FallbackNormalize(lower)
        };
    }

    private static string FallbackNormalize(string lower)
    {
        // Fallback: match by category keywords
        if (lower.Contains("ddx") || lower.Contains("dds") || lower.Contains("texture"))
            return "dds";
        if (lower.Contains("png") || lower.Contains("image"))
            return "png";
        if (lower.Contains("xma") || lower.Contains("audio"))
            return "xma";
        if (lower.Contains("nif") || lower.Contains("model"))
            return "nif";
        if (lower.Contains("xex") || lower.Contains("executable") || lower.Contains("module"))
            return "module";
        if (lower.Contains("script") || lower.Contains("obscript"))
            return "obscript";
        if (lower.Contains("lip"))
            return "lip";
        if (lower.Contains("xdbf") || lower.Contains("dashboard"))
            return "xdbf";
        if (lower.Contains("xui") || lower.Contains("scene") || lower.Contains("xuib") || lower.Contains("xuis"))
            return "xui";
        if (lower.Contains("esp") || lower.Contains("plugin"))
            return "esp";

        return lower.Replace(" ", "_");
    }

    /// <summary>
    /// Get priority for overlap resolution. Lower number = higher priority.
    /// </summary>
    public static int GetPriority(string fileType)
    {
        var lower = fileType.ToLowerInvariant();
        if (lower.Contains("png"))
            return 1;
        if (lower.Contains("ddx") || lower.Contains("dds"))
            return 1;
        if (lower.Contains("xma") || lower.Contains("audio"))
            return 1;
        if (lower.Contains("nif") || lower.Contains("model"))
            return 2;
        if (lower.Contains("script") || lower.Contains("obscript"))
            return 3;
        if (lower.Contains("lip") || lower.Contains("esp") || lower.Contains("xdbf"))
            return 3;
        if (lower.Contains("xui") || lower.Contains("scene") || lower.Contains("xuib") || lower.Contains("xuis"))
            return 3;
        if (lower.Contains("xex") || lower.Contains("module") || lower.Contains("executable"))
            return 4;
        return 5;
    }

    private static Color FromArgb(uint argb)
    {
        return Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF));
    }

    public readonly struct LegendCategory(string name, uint color)
    {
        public string Name { get; } = name;
        public Color Color { get; } = FromArgb(color);
    }
}
