using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool;

/// <summary>How a heightmap shade preset turns a normalized height into a color.</summary>
internal enum HeightmapRenderMode
{
    /// <summary>Grayscale height × the preset's tint RGB (the Pip-Boy HUD look).</summary>
    TintMultiply,

    /// <summary>The 9-zone HSL elevation gradient (blue→green→brown→red→white). Tint RGB is ignored.</summary>
    Colorful
}

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
    public static readonly HeightmapColorScheme HudAlt = new("Red", 255, 67, 42);

    /// <summary>9-zone HSL elevation gradient (the CLI's "colorful" map). RGB is unused in this mode.</summary>
    public static readonly HeightmapColorScheme Colorful =
        new("Colorful", 255, 255, 255) { Mode = HeightmapRenderMode.Colorful };

    /// <summary>All available presets.</summary>
    public static readonly HeightmapColorScheme[] Presets = [Amber, Green, White, Blue, HudAlt, Colorful];

    /// <summary>Rendering mode — tint-multiply (default) or the colorful elevation gradient.</summary>
    public HeightmapRenderMode Mode { get; init; } = HeightmapRenderMode.TintMultiply;

    /// <summary>
    ///     Returns the default color scheme for the loaded game: New Vegas uses its amber Pip-Boy HUD,
    ///     every other known game defaults to green. Falls back to the filename heuristic
    ///     (<see cref="DefaultForFile" />) when the game is unknown.
    /// </summary>
    public static HeightmapColorScheme DefaultForGame(BethesdaGame game, string? filePath)
        => game switch
        {
            BethesdaGame.FalloutNewVegas => Amber,
            BethesdaGame.Unknown => DefaultForFile(filePath),
            _ => Green
        };

    /// <summary>
    ///     Returns the default color scheme based on filename (fallback when the game is unknown).
    ///     FalloutNV → Amber, Fallout3/FO3 → Green, else Green.
    /// </summary>
    public static HeightmapColorScheme DefaultForFile(string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return Green;
        }

        var name = Path.GetFileNameWithoutExtension(filePath);
        if (name.Contains("FalloutNV", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("WastelandNV", StringComparison.OrdinalIgnoreCase))
        {
            return Amber;
        }

        return Green;
    }

    public override string ToString() => Name;
}
