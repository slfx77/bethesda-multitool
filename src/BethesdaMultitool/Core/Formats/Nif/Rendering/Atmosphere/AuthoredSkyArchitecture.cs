using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;

/// <summary>
///     Game-scoped boundary for authored-sky architectural replacements. Weather parsing, semantic
///     time-band sampling, cloud layers, and telemetry remain independent of this policy.
/// </summary>
internal static class AuthoredSkyArchitecture
{
    internal const string EnvironmentVariableName = EnvironmentVariables.Viewer.AuthoredSky;

    /// <summary>
    ///     Exact tri-state override: <c>1</c> forces the authored path, <c>0</c> forces the
    ///     compatibility path, and unset/anything else uses the game-scoped default. Keeping zero
    ///     distinct from unset provides a real control run after a candidate is promoted.
    /// </summary>
    // Read on demand rather than caching at type initialization so tests and diagnostic sessions
    // can change the override without depending on class-load order.
    internal static bool? ExplicitOverride => EnvironmentVariables.Get(EnvironmentVariableName) switch
    {
        "1" => true,
        "0" => false,
        _ => null
    };

    /// <summary>
    ///     Atmosphere.nif remains an explicit candidate until a game-specific capture matrix proves
    ///     the renderer's blend-weight interpretation. Decoding retail geometry alone is not visual
    ///     parity evidence.
    /// </summary>
    internal static bool ShouldLoadAtmosphereNif(BethesdaGame game, bool? explicitOverride)
    {
        _ = game; // retained at the policy seam for a future evidence-backed per-game promotion
        return explicitOverride == true;
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
        bool? explicitOverride,
        AtmosphereState.ResolvedAmbientCube? directionalAmbient)
    {
        return (explicitOverride ?? (game is BethesdaGame.Skyrim or BethesdaGame.Fallout76))
            ? directionalAmbient
            : null;
    }
}
