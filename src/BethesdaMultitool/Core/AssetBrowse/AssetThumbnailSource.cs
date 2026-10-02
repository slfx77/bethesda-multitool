namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     Turns an <see cref="AssetNode" /> into a gallery-sized RGBA thumbnail.
///     <para>
///         Decoding is <see cref="AssetImageDecoding" />'s, shared with the full-size preview, so the
///         tile and the preview pane cannot disagree on palettes or frames; this class only scales.
///     </para>
///     <para>
///         Multi-frame sources (a Daggerfall TEXTURE set, a six-direction FRM) thumbnail as their
///         FIRST frame. The gallery shows one cell per file, not per frame; the preview pane is
///         where every frame is reachable.
///     </para>
/// </summary>
internal static class AssetThumbnailSource
{
    /// <summary>Kinds this class can produce a picture for. Anything else gets an icon, not a thumbnail.</summary>
    public static bool CanRender(AssetNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Kind is AssetNodeKind.Texture or AssetNodeKind.Sprite;
    }

    /// <summary>
    ///     Decodes and scales one node to fit <paramref name="cellPixels" />, or returns null when the
    ///     node has no picture in it.
    ///     <para>
    ///         Returns null rather than throwing for a file this build cannot decode yet. A gallery
    ///         over a retail install will always contain formats a given milestone has not reached,
    ///         and one undecodable file must not take out the whole view.
    ///     </para>
    /// </summary>
    public static RgbaThumbnail? TryRender(
        AssetBrowseSession session, AssetNode node, int cellPixels, DdsThumbnailProducer? persistentDds = null,
        double deviceScale = 1, CancellationToken cancellationToken = default) =>
        TryRender(session, node, cellPixels, persistentDds, deviceScale, cancellationToken, out _);

    /// <summary>
    ///     Decodes and scales one node exactly as the shorter overload does, and also reports what was
    ///     decoded: the full first-frame size and the frame count, for a caption that describes the
    ///     asset rather than its thumbnail. <paramref name="imageInfo" /> is null whenever the result is.
    /// </summary>
    public static RgbaThumbnail? TryRender(
        AssetBrowseSession session, AssetNode node, int cellPixels, DdsThumbnailProducer? persistentDds,
        double deviceScale, CancellationToken cancellationToken, out AssetImageInfo? imageInfo)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellPixels);
        imageInfo = null;

        if (!CanRender(node))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (persistentDds is not null && node.Kind == AssetNodeKind.Texture && AssetImageDecoding.IsDdsName(node.Name))
        {
            return persistentDds.TryRender(session, node, cellPixels, deviceScale, cancellationToken, out _, out imageInfo);
        }

        var frames = AssetImageDecoding.TryDecodeFrames(session, node);
        if (frames is null || frames.Count == 0)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var first = frames[0];
        imageInfo = new AssetImageInfo(first.Width, first.Height, frames.Count);
        return ThumbnailScaler.Fit(new RgbaThumbnail(first.Width, first.Height, first.Rgba), cellPixels);
    }
}
