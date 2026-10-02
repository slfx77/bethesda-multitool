using Slfx77.Multitool.Core.Browsing;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>The preview a selected browser node takes for movie playback.</summary>
internal enum ClassicVideoRoute
{
    /// <summary>Not a movie: every video surface is hidden.</summary>
    None,

    /// <summary>An admitted Arena-style FLC/CEL leaf, played through the retained decoded session and the native player.</summary>
    Native,

    /// <summary>Every other movie this build plays, through the existing frame-timer preview and its own transport.</summary>
    Legacy
}

/// <summary>Chooses, once per selection, which video preview a node goes to.</summary>
/// <remarks>This is the only routing decision the browser makes for movies. Native admission is exactly
/// <see cref="FlicPreviewPreparation.IsCandidate" />: an FLC/CEL leaf in an Arena or unclassified source. A strict
/// decline inside that route hands the bytes it already read to the same detached frames the legacy route decodes,
/// so a node routed natively is never read twice. Routing itself reads nothing.</remarks>
internal static class ClassicVideoRouting
{
    /// <summary>Routes a node by its source profile and extension without reading any bytes.</summary>
    /// <param name="snapshot">The current source opening, or null when no source is attached.</param>
    /// <param name="node">The selected tree node.</param>
    /// <returns>Native for an admitted FLC/CEL leaf; Legacy for any other playable movie; otherwise None.</returns>
    internal static ClassicVideoRoute Select(BrowserSnapshot? snapshot, AssetNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (snapshot is not null && FlicPreviewPreparation.IsCandidate(snapshot, node))
        {
            return ClassicVideoRoute.Native;
        }

        return ClassicVideoClip.CanOpenAnyVideo(node) ? ClassicVideoRoute.Legacy : ClassicVideoRoute.None;
    }
}
