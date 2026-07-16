namespace BethesdaMultitool.Core.Games;

/// <summary>Pure per-game screen-space sizing policy for 2D world-map markers.</summary>
internal static class MapMarkerDisplayScale
{
    /// <summary>
    ///     Resolves the multiplier on the marker's target pixel size at <paramref name="zoom" />.
    ///     Profiles with no full-size zoom retain the legacy constant-pixel-size behavior.
    /// </summary>
    public static float Resolve(GameProfile profile, float zoom)
    {
        if (profile.MarkerFullSizeZoom <= 0f) return 1f;

        var minimum = Math.Clamp(profile.MarkerMinScreenScale, 0.1f, 1f);
        var progress = Math.Clamp(
            Math.Max(zoom, 0f) / profile.MarkerFullSizeZoom, 0f, 1f);
        return minimum + ((1f - minimum) * progress);
    }
}
