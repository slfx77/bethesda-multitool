using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

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

    internal static bool ShouldLoadAtmosphereNif(bool explicitlyEnabled) => explicitlyEnabled;

    /// <summary>
    ///     Skyrim's DALC cube is scene lighting and is enabled independently of the authored
    ///     Atmosphere.nif replacement. Other modern families retain the existing opt-in boundary until
    ///     their own capture matrices pass. A null result makes shaders consume the flat cube mean.
    /// </summary>
    internal static AtmosphereState.ResolvedAmbientCube? SelectDirectionalAmbientForUpload(
        BethesdaGame game,
        bool explicitlyEnabled,
        AtmosphereState.ResolvedAmbientCube? directionalAmbient) =>
        game == BethesdaGame.Skyrim || explicitlyEnabled ? directionalAmbient : null;
}
