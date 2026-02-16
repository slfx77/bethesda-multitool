namespace FalloutXbox360Utils;

/// <summary>
///     Named color scheme preset for heightmap tinting, matching in-game Pip-Boy HUD colors.
/// </summary>
internal sealed record HeightmapColorScheme(string Name, byte R, byte G, byte B)
{
    /// <summary>FNV engine default (uHUDColor = 4290134783 = 0xFFB642FF).</summary>
    public static readonly HeightmapColorScheme Amber = new("Amber", 255, 182, 66);

    /// <summary>Fallout_default.ini iSystemColorHUDMain (FO3 default).</summary>
    public static readonly HeightmapColorScheme Green = new("Green", 26, 255, 128);

    /// <summary>Common user preference — no tint.</summary>
    public static readonly HeightmapColorScheme White = new("White", 255, 255, 255);

    /// <summary>Common user preference.</summary>
    public static readonly HeightmapColorScheme Blue = new("Blue", 100, 180, 255);

    /// <summary>Fallout_default.ini iSystemColorHUDAlt (power armor HUD).</summary>
    public static readonly HeightmapColorScheme HudAlt = new("HUD Alt", 255, 67, 42);

    /// <summary>All available presets.</summary>
    public static readonly HeightmapColorScheme[] Presets = [Amber, Green, White, Blue, HudAlt];

    /// <summary>
    ///     Returns the default color scheme based on filename.
    ///     FalloutNV → Amber, Fallout3/FO3 → Green, else Amber.
    /// </summary>
    public static HeightmapColorScheme DefaultForFile(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return Amber;
        }

        var name = Path.GetFileNameWithoutExtension(filePath);
        if (name.Contains("Fallout3", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("FO3", StringComparison.OrdinalIgnoreCase))
        {
            return Green;
        }

        return Amber;
    }

    public override string ToString() => Name;
}
