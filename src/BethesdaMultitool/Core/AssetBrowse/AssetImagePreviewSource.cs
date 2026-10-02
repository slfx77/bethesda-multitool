namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     Loads a picture node at full size for the preview pane, through the same decode path as the
///     gallery thumbnail (<see cref="AssetImageDecoding" />), so the tile and the preview cannot
///     disagree on palettes or frames.
/// </summary>
internal static class AssetImagePreviewSource
{
    /// <summary>
    ///     The largest frame the pane will hold resident, in pixels: 4096 x 4096, or 64 MiB of RGBA
    ///     before the presenter's own premultiplied copy and bitmap. Larger frames are declined with
    ///     <c>AssetPreview_TooLarge</c> rather than decoded into three copies of a picture the pane
    ///     would downscale anyway.
    /// </summary>
    internal const long MaximumFramePixels = 16_777_216;

    /// <summary>The localization key of the decline a frame above <see cref="MaximumFramePixels" /> earns.</summary>
    internal const string TooLargeReasonKey = "AssetPreview_TooLarge";

    /// <summary>
    ///     Decodes every frame of <paramref name="node" /> at its stored size, or returns null when the
    ///     node is not a picture kind, has no bytes, or will not decode. A decodable asset with a frame
    ///     above <see cref="MaximumFramePixels" /> comes back declined, describing the asset's first frame
    ///     so the message can quote the size.
    /// </summary>
    /// <param name="session">The opened source the node belongs to.</param>
    /// <param name="node">The selected leaf.</param>
    /// <param name="cancellationToken">Checked before and after the decode; the decode itself is not interruptible.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was signaled.</exception>
    internal static AssetImagePreview? TryLoad(AssetBrowseSession session, AssetNode node, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(node);
        cancellationToken.ThrowIfCancellationRequested();
        if (!AssetThumbnailSource.CanRender(node))
        {
            return null;
        }

        var frames = AssetImageDecoding.TryDecodeFrames(session, node);
        cancellationToken.ThrowIfCancellationRequested();
        if (frames is null || frames.Count == 0)
        {
            return null;
        }

        var first = frames[0];
        var info = new AssetImageInfo(first.Width, first.Height, frames.Count);
        foreach (var frame in frames)
        {
            if ((long)frame.Width * frame.Height > MaximumFramePixels)
            {
                return AssetImagePreview.Declined(TooLargeReasonKey, info);
            }
        }

        return new AssetImagePreview(frames, info, null);
    }
}
