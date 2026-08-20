namespace BethesdaMultitool.Core.Games;

/// <summary>
///     Resolved screen-space metrics for one 2D world-map marker. The same policy feeds the live draw,
///     hit testing, export layout, and accessibility bounds so a per-game visual adjustment cannot leave
///     any of those paths using a stale constant.
/// </summary>
internal readonly record struct MapMarkerMetrics(
    float ScreenScale,
    float VisualDiameterPixels,
    float IconScale,
    float IconHeightPixels,
    float HitRadiusPixels)
{
    internal const float DefaultVisualDiameterPixels = 16f;
    internal const float LegacyHitRadiusPixels = 20f;
    internal const float MinimumUsableHitRadiusPixels = 12f;

    /// <summary>
    ///     Resolves all marker metrics at the supplied screen-pixels-per-world-unit value. Passing a
    ///     different base diameter lets export retain its proportional long-edge sizing while applying
    ///     exactly the same per-game zoom policy as the live map.
    /// </summary>
    internal static MapMarkerMetrics Resolve(
        GameProfile profile,
        float screenPixelsPerWorldUnit,
        float baseVisualDiameterPixels = DefaultVisualDiameterPixels)
    {
        var screenScale = ResolveScreenScale(profile, screenPixelsPerWorldUnit);
        var baseDiameter = float.IsFinite(baseVisualDiameterPixels)
            ? Math.Max(0f, baseVisualDiameterPixels)
            : DefaultVisualDiameterPixels;
        var iconScale = float.IsFinite(profile.MarkerIconScale)
            ? Math.Max(0f, profile.MarkerIconScale)
            : 1f;
        var visualDiameter = baseDiameter * screenScale;
        var iconHeight = visualDiameter * iconScale;

        // Constant-size profiles retain the historical 20-screen-pixel target. Zoom-aware profiles
        // shrink their interaction footprint with their art, but never below a usable 12-pixel radius.
        // If a future profile has still-larger art, cover that art as well.
        var scaledHitRadius = profile.MarkerFullSizeZoom > 0f
            ? LegacyHitRadiusPixels * screenScale
            : LegacyHitRadiusPixels;
        var hitRadius = Math.Max(
            MinimumUsableHitRadiusPixels,
            Math.Max(scaledHitRadius, Math.Max(visualDiameter, iconHeight) * 0.5f));

        return new MapMarkerMetrics(
            screenScale,
            visualDiameter,
            iconScale,
            iconHeight,
            hitRadius);
    }

    private static float ResolveScreenScale(GameProfile profile, float screenPixelsPerWorldUnit)
    {
        if (profile.MarkerFullSizeZoom <= 0f)
        {
            return 1f;
        }

        var minimum = Math.Clamp(profile.MarkerMinScreenScale, 0.1f, 1f);
        var zoom = float.IsFinite(screenPixelsPerWorldUnit)
            ? Math.Max(screenPixelsPerWorldUnit, 0f)
            : 0f;
        var progress = Math.Clamp(
            zoom / profile.MarkerFullSizeZoom, 0f, 1f);
        return minimum + (1f - minimum) * progress;
    }
}

/// <summary>
///     Compatibility facade for the initial visual-only implementation. New consumers should resolve
///     <see cref="MapMarkerMetrics" /> so drawing and interaction share the complete metric set.
/// </summary>
internal static class MapMarkerDisplayScale
{
    public static float Resolve(GameProfile profile, float zoom)
    {
        return MapMarkerMetrics.Resolve(profile, zoom).ScreenScale;
    }
}
