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
    ///     Returns the lossless DALC cube only for the opt-in architecture. A null result makes the
    ///     shaders consume Resolved.AmbientColor, which already carries the cube mean as the flat
    ///     compatibility fallback; parsing and telemetry retain the original six faces either way.
    /// </summary>
    internal static AtmosphereState.ResolvedAmbientCube? SelectDirectionalAmbientForUpload(
        bool explicitlyEnabled,
        AtmosphereState.ResolvedAmbientCube? directionalAmbient) =>
        explicitlyEnabled ? directionalAmbient : null;
}
