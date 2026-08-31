using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;

/// <summary>
///     Opt-in boundary for authored-sky architectural replacements. Weather parsing, semantic
///     time-band sampling, cloud layers, and telemetry remain independent of this switch.
/// </summary>
internal static class AuthoredSkyArchitecture
{
    internal const string EnvironmentVariableName = EnvironmentVariables.Viewer.AuthoredSky;

    // Read on demand rather than caching at type initialization so tests and diagnostic sessions
    // can change the opt-in without depending on class-load order.
    internal static bool Enabled => EnvironmentVariables.IsEnabled(EnvironmentVariableName);

    internal static bool ShouldLoadAtmosphereNif(bool explicitlyEnabled)
    {
        return explicitlyEnabled;
    }

    /// <summary>
    ///     Skyrim and Fallout 76 DALC cubes are scene lighting and are enabled independently of the
    ///     authored Atmosphere.nif replacement. Retail SeventySix.esm authors exactly eight DALC bands
    ///     on all 121 WTHRs, and the shared shaders already consume the six directional faces. Other
    ///     modern families retain the opt-in boundary until their own capture matrices pass. A null
    ///     result makes shaders consume the flat cube mean.
    /// </summary>
    internal static AtmosphereState.ResolvedAmbientCube? SelectDirectionalAmbientForUpload(
        BethesdaGame game,
        bool explicitlyEnabled,
        AtmosphereState.ResolvedAmbientCube? directionalAmbient)
    {
        return game is BethesdaGame.Skyrim or BethesdaGame.Fallout76 || explicitlyEnabled
            ? directionalAmbient
            : null;
    }
}
