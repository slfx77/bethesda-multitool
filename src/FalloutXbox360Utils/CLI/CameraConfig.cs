namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Shared camera configuration used by both NIF and NPC render commands.
///     Resolves view mode (iso/side/trimetric) and elevation into concrete render views.
/// </summary>
internal sealed class CameraConfig
{
    private const float TrimetricDefaultElevation = 25.65891f;

    private static readonly (string Suffix, float Azimuth)[] IsoViews =
        [("_ne", 45f), ("_nw", 135f), ("_sw", 225f), ("_se", 315f)];

    private static readonly (string Suffix, float Azimuth)[] SideViews =
        [("_front", 0f), ("_back", 180f), ("_left", 90f), ("_right", 270f)];

    private static readonly (string Suffix, float Azimuth)[] TrimetricViews =
        [("_tri_ne", 30f), ("_tri_nw", 120f), ("_tri_sw", 210f), ("_tri_se", 300f)];

    public bool Isometric { get; init; }
    public float ElevationDeg { get; init; } = 30f;
    public bool ElevationOverridden { get; init; }
    public bool SideProfile { get; init; }
    public bool Trimetric { get; init; }

    /// <summary>
    ///     Whether this config uses multi-view mode (iso/side/trimetric).
    /// </summary>
    internal bool IsMultiView => Isometric || SideProfile || Trimetric;

    /// <summary>
    ///     Resolves the configured view mode into concrete (suffix, azimuth, elevation) tuples.
    ///     For single-view (default), returns one view with the given default azimuth and elevation.
    /// </summary>
    internal (string Suffix, float Azimuth, float Elevation)[] ResolveViews(
        float defaultAzimuth = 0f, float defaultElevation = 0f)
    {
        if (IsMultiView)
        {
            var views = Trimetric ? TrimetricViews : SideProfile ? SideViews : IsoViews;
            var elevation = SideProfile ? 0f
                : Trimetric && !ElevationOverridden ? TrimetricDefaultElevation
                : ElevationDeg;
            return views.Select(v => (v.Suffix, v.Azimuth + defaultAzimuth, elevation)).ToArray();
        }

        // Single view: use explicit elevation if set, otherwise the caller's default
        var elev = ElevationOverridden ? ElevationDeg : defaultElevation;
        return [("", defaultAzimuth, elev)];
    }
}
