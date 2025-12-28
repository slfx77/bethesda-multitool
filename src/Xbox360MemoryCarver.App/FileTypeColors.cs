using Windows.UI;

namespace Xbox360MemoryCarver.App;

/// <summary>
///     Single source of truth for file type colors used across the application.
///     Used by the legend, hex viewer, and minimap to ensure consistency.
///     Colors are distributed across the hue spectrum for maximum visual distinction.
/// </summary>
public static class FileTypeColors
{
    // Normalized type name constants (S1192)
    private const string TypeModule = "module";
    private const string TypeObscript = "obscript";
    private const string TypeDds = "dds";
    private const string TypePng = "png";
    private const string TypeXma = "xma";
    private const string TypeNif = "nif";
    private const string TypeLip = "lip";
    private const string TypeXdbf = "xdbf";
    private const string TypeXui = "xui";
    private const string TypeEsp = "esp";

    /// <summary>
    ///     Color used for unknown/untyped regions.
    /// </summary>
    public static readonly Color UnknownColor = FromArgb(0xFF3D3D3D);

    // Legend categories with display names and colors
    // Colors distributed across the hue spectrum (0°-360°) for maximum distinction:
    // Red (0°), Orange (30°), Yellow (60°), Green (120°), Cyan (180°), Blue (240°), Purple (270°), Magenta (300°)
    public static readonly LegendCategory[] LegendCategories =
    [
        new("Texture", 0xFF2ECC71),   // Green (120°) - DDX/DDS
        new("PNG", 0xFF1ABC9C),       // Teal/Cyan (170°) - PNG
        new("Audio", 0xFFE74C3C),     // Red (5°) - XMA/LIP
        new("Model", 0xFFF1C40F),     // Yellow (50°) - NIF
        new("Module", 0xFF9B59B6),    // Purple (280°) - XEX
        new("Script", 0xFFE67E22),    // Orange (30°) - Scripts
        new("Xbox/XUI", 0xFF3498DB),  // Blue (210°) - XDBF/XUI
        new("Plugin", 0xFFFF6B9D)     // Pink/Magenta (340°) - ESP
    ];

    // Mapping from normalized type names to colors
    // Using the same hue-distributed palette
    public static Color GetColor(string normalizedTypeName)
    {
        return normalizedTypeName switch
        {
            // Textures (DDX/DDS) - Green
            TypeDds or "ddx_3xdo" or "ddx_3xdr" => FromArgb(0xFF2ECC71),

            // PNG - Teal/Cyan
            TypePng => FromArgb(0xFF1ABC9C),

            // Audio - Red
            TypeXma or TypeLip => FromArgb(0xFFE74C3C),

            // Models - Yellow
            TypeNif => FromArgb(0xFFF1C40F),

            // Modules/Executables - Purple
            "xex" or TypeModule => FromArgb(0xFF9B59B6),

            // Scripts - Orange
            "script_scn" or TypeObscript => FromArgb(0xFFE67E22),

            // Xbox Dashboard/XUI - Blue
            TypeXdbf or TypeXui => FromArgb(0xFF3498DB),

            // Game data (ESP) - Pink/Magenta
            TypeEsp => FromArgb(0xFFFF6B9D),

            // Unknown - Dark Gray
            _ => FromArgb(0xFF555555)
        };
    }

    /// <summary>
    ///     Normalize a file type description to a standard key.
    /// </summary>
    public static string NormalizeTypeName(string fileType)
    {
        ArgumentNullException.ThrowIfNull(fileType);

        var lower = fileType.ToLowerInvariant();
        return lower switch
        {
            "xbox 360 ddx texture (3xdo format)" => "ddx_3xdo",
            "xbox 360 ddx texture (3xdr engine-tiled format)" => "ddx_3xdr",
            "directdraw surface texture" => TypeDds,
            "png image" => TypePng,
            "xbox media audio (riff/xma)" => TypeXma,
            "netimmerse/gamebryo 3d model" => TypeNif,
            "xbox 360 executable" or "xex" or "xbox 360 dll" => TypeModule,
            "xbox 360 module (exe)" or "xbox 360 module (dll)" => TypeModule,
            "xbox dashboard file" => TypeXdbf,
            "xui scene" or "xui binary" => TypeXui,
            "elder scrolls plugin" => TypeEsp,
            "lip-sync animation" => TypeLip,
            "bethesda obscript (scn format)" or "script_scn" => TypeObscript,
            _ => FallbackNormalize(lower)
        };
    }

    private static string FallbackNormalize(string lower)
    {
        return GetFallbackCategory(lower) ?? lower.Replace(" ", "_");
    }

    private static string? GetFallbackCategory(string lower)
    {
        return lower switch
        {
            _ when ContainsAny(lower, "ddx", "dds", "texture") => TypeDds,
            _ when ContainsAny(lower, "png", "image") => TypePng,
            _ when ContainsAny(lower, "xma", "audio") => TypeXma,
            _ when ContainsAny(lower, "nif", "model") => TypeNif,
            _ when ContainsAny(lower, "xex", "executable", "module", "dll") => TypeModule,
            _ when ContainsAny(lower, "script", "obscript") => TypeObscript,
            _ when lower.Contains("lip", StringComparison.Ordinal) => TypeLip,
            _ when ContainsAny(lower, "xdbf", "dashboard") => TypeXdbf,
            _ when ContainsAny(lower, "xui", "scene", "xuib", "xuis") => TypeXui,
            _ when ContainsAny(lower, "esp", "plugin") => TypeEsp,
            _ => null
        };
    }

    private static bool ContainsAny(string text, params string[] terms)
    {
        return terms.Any(term => text.Contains(term, StringComparison.Ordinal));
    }

    /// <summary>
    ///     Get priority for overlap resolution. Lower number = higher priority.
    /// </summary>
    public static int GetPriority(string fileType)
    {
        ArgumentNullException.ThrowIfNull(fileType);

        var lower = fileType.ToLowerInvariant();
        return GetPriorityLevel(lower);
    }

    private static int GetPriorityLevel(string lower)
    {
        // Priority 1: High-value content types
        if (ContainsAny(lower, "png", "ddx", "dds", "xma", "audio")) return 1;

        // Priority 2: Models
        if (ContainsAny(lower, "nif", "model")) return 2;

        // Priority 3: Scripts, data files, UI
        if (ContainsAny(lower, "script", "obscript", "lip", "esp", "xdbf", "xui", "scene", "xuib", "xuis")) return 3;

        // Priority 4: Executables (often false positives in memory dumps)
        if (ContainsAny(lower, "xex", "module", "executable")) return 4;

        // Priority 5: Unknown/default
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
