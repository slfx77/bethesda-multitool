using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;

/// <summary>Explicit stock-head sampling comparison; captured once per native render session.</summary>
internal static class BethesdaViewerClassicSkinAlbedoPolicy
{
    internal const string EnvironmentVariable = "FALLOUT_VIEWER_NATIVE_SKIN_INDEPENDENT_ALBEDO";

    internal static bool IsRequested(string? value)
    {
        return string.Equals(value, "1", StringComparison.Ordinal);
    }

    internal static bool IsEnabledFor(bool requested, BethesdaGame game,
        BethesdaViewerScenePurpose purpose, bool isFaceGen, ClassicSkinAuthoredAlbedo? albedo)
    {
        return requested && game == BethesdaGame.Oblivion &&
               purpose == BethesdaViewerScenePurpose.NpcAppearance && isFaceGen && albedo is not null;
    }
}
