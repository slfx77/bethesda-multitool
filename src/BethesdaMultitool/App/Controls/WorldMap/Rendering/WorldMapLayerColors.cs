namespace BethesdaMultitool;

/// <summary>
///     Stable FormID → RGB color mapping for the world-map "Terrain regions" layer, extracted
///     from <see cref="WorldMapLayerRenderer" />. Uses golden-angle hue separation so neighboring
///     FormIDs render as visually distinct colors.
/// </summary>
internal static class WorldMapLayerColors
{
    /// <summary>
    ///     Map a FormID to a stable, visually distinct RGB color. Golden-angle hue separation
    ///     keeps neighboring FormIDs from collapsing to similar colors.
    /// </summary>
    internal static (byte R, byte G, byte B) FormIdToColor(uint formId)
    {
        // 137.508° golden angle in hue space, modulo 360
        var hue = (formId * 137u + (formId >> 8) * 23u) % 360u;
        const float saturation = 0.65f;
        const float value = 0.85f;
        return HsvToRgb(hue, saturation, value);
    }

    private static (byte R, byte G, byte B) HsvToRgb(uint h, float s, float v)
    {
        var c = v * s;
        var hp = h / 60f;
        var x = c * (1f - MathF.Abs(hp % 2f - 1f));
        var (r1, g1, b1) = (int)hp switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x)
        };
        var m = v - c;
        return (
            (byte)Math.Clamp((r1 + m) * 255f, 0f, 255f),
            (byte)Math.Clamp((g1 + m) * 255f, 0f, 255f),
            (byte)Math.Clamp((b1 + m) * 255f, 0f, 255f));
    }
}
