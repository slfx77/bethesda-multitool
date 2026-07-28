using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Walk;

/// <summary>
///     Pure helpers for the walk-camera's vertical (jump/gravity) integration. Kept out of the
///     <c>WINDOWS_GUI</c>-gated <c>FlythroughCameraController</c> so the math is unit-testable
///     in the CLI/test build that lacks the GUI target framework.
/// </summary>
internal static class WalkVerticalMath
{
    /// <summary>
    ///     Clamps a jumping camera's next eye Z so it can't pass a ceiling. Returns <paramref name="newZ" />
    ///     unchanged when there is no ceiling or the eye stays below it; otherwise the capped Z
    ///     (<paramref name="ceiling" /> − <paramref name="headroom" />). The caller treats a returned
    ///     value below <paramref name="newZ" /> as ceiling contact (and zeroes vertical velocity).
    /// </summary>
    public static float ClampToCeiling(float newZ, float? ceiling, float headroom)
    {
        if (ceiling is not float c) return newZ;
        var cap = c - headroom;
        return newZ > cap ? cap : newZ;
    }
}
