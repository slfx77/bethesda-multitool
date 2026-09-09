using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;

/// <summary>
///     Explicit diagnostic admission after the native viewer's ordinary opaque-state gate.
///     The caller captures the request once per render session; the shared PSO cache never owns it.
/// </summary>
internal static class BethesdaViewerClassicSkinDiagnosticPolicy
{
    internal const string EnvironmentVariable = "FALLOUT_VIEWER_NATIVE_SKIN_FACTOR_ONE";

    internal static bool IsRequested(string? value)
    {
        return string.Equals(value, "1", StringComparison.Ordinal);
    }

    internal static bool IsEnabledFor(bool requested, BethesdaGame game, bool isFaceGen)
    {
        return requested && game == BethesdaGame.Oblivion && isFaceGen;
    }
}
