using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Same-time causal shadow diagnostic. The default preserves all production caster draws;
///     only the exact generated FO4 BNDS wind marker may be omitted by an explicit opt-in.
/// </summary>
internal static class Fo4SplineShadowDiagnosticPolicy
{
    internal const string EnvironmentVariable = "FALLOUT_VIEWER_DIAGNOSTIC_OMIT_FO4_SPLINE_SHADOWS";

    internal static bool IsEnabled(string? value)
    {
        return string.Equals(value, "1", StringComparison.Ordinal);
    }

    internal static bool IsCaster(BethesdaGame game, bool isBendableSplineWind)
    {
        return game == BethesdaGame.Fallout4 && isBendableSplineWind;
    }

    internal static bool ShouldOmit(bool enabled, bool isFo4BendableSpline)
    {
        return enabled && isFo4BendableSpline;
    }
}
