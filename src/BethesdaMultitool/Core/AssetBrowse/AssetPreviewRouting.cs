using Slfx77.Multitool.Core.Browsing;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     Decides, once per selection and without reading a byte, which preview surface a node goes to.
///     <para>
///         Every predicate consulted here is byte-free. The one thing a name cannot settle is a mesh or
///         level: Daggerfall's ARCH3D records are named by object id with no extension and classify as
///         <see cref="AssetNodeKind.Raw" />, so those fall to <see cref="AssetPreviewSurface.ContentProbe" />
///         and the caller asks the mesh and level probes, which read the file head, last.
///     </para>
/// </summary>
internal static class AssetPreviewRouting
{
    /// <summary>Routes a node by its kind, name and source profile; first match wins.</summary>
    /// <param name="snapshot">The current source opening, or null when no source is attached.</param>
    /// <param name="node">The selected tree node.</param>
    /// <param name="shadowkeyAvailable">Whether a Shadowkey pack presenter is attached to the pane.</param>
    /// <returns>The surface to show; <see cref="AssetPreviewSurface.ContentProbe" /> means "ask the probes".</returns>
    internal static AssetPreviewSurface Select(BrowserSnapshot? snapshot, AssetNode node, bool shadowkeyAvailable)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Kind == AssetNodeKind.Folder)
        {
            return AssetPreviewSurface.None;
        }

        if (shadowkeyAvailable && ShadowkeyPackPreviewSource.IsCandidate(node))
        {
            return AssetPreviewSurface.ShadowkeyPack;
        }

        var video = ClassicVideoRouting.Select(snapshot, node);
        if (video == ClassicVideoRoute.Native)
        {
            return AssetPreviewSurface.NativeVideo;
        }

        if (video == ClassicVideoRoute.Legacy)
        {
            return AssetPreviewSurface.LegacyVideo;
        }

        if (AssetAudioSource.CanPlay(node))
        {
            return AssetPreviewSurface.Audio;
        }

        // Sky before Image: SKY*.DAT classifies as a Sprite, and the backdrop is the better view of it.
        if (ClassicSkySource.IsSky(node))
        {
            return AssetPreviewSurface.Sky;
        }

        if (AssetThumbnailSource.CanRender(node))
        {
            return AssetPreviewSurface.Image;
        }

        return node.Kind switch
        {
            // Archives, plugins, text and saves have no preview; a sound or movie this build does not
            // play is not going to turn out to be a mesh either.
            AssetNodeKind.Archive or AssetNodeKind.Plugin or AssetNodeKind.Text or AssetNodeKind.Save
                or AssetNodeKind.Audio or AssetNodeKind.Video => AssetPreviewSurface.None,
            // Model, Map, Raw (the extension-less Daggerfall meshes) and models.huge without a presenter.
            _ => AssetPreviewSurface.ContentProbe
        };
    }
}
