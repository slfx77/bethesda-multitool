using BethesdaMultitool.Core.AssetBrowse;
using Microsoft.UI.Dispatching;
using Slfx77.Multitool.Core.Caching;
using Slfx77.Multitool.Core.Documents;
using Slfx77.Multitool.Core.Media;
using Slfx77.Multitool.WinUI.Images;

namespace BethesdaMultitool;

/// <summary>Connects Bethesda's existing image and palette decoder to Shared's bounded thumbnail worker and cache.</summary>
/// <remarks>The browser must await disposal before releasing its source lease. Each attachment creates one loader;
/// source replacement never changes the decoder captured by an in-flight request.</remarks>
internal sealed class AssetThumbnailLoader : IAsyncDisposable
{
    // ApplicationThumbnailCache construction retains configuration only; opening occurs on the worker.
    private static readonly ApplicationThumbnailCache PersistentThumbnails = new("BethesdaMultitool");
    private readonly ThumbnailLoader<AssetNode> _loader;
    private readonly DdsThumbnailProducer _persistentDds;
    private readonly double _deviceScale;
    private readonly ThumbnailCacheObservation _observation;
    private bool _started;

    /// <summary>Clears stored thumbnails through this application's existing shared owner, leaving previews and memory caches intact.</summary>
    /// <param name="cancellationToken">Cancels admission and clearing between storage operations.</param>
    /// <returns>Whether clearing completed; ongoing producers may subsequently store new thumbnails.</returns>
    internal static bool ClearPersistentThumbnailCache(CancellationToken cancellationToken) =>
        PersistentThumbnails.Clear(cancellationToken);

    /// <summary>Registers a new shared loader before any decoder work can start, preserving BMT's memory limits.</summary>
    /// <param name="dispatcher">The owning native presentation dispatcher.</param>
    /// <param name="cellPixels">The existing maximum decoded edge in device pixels.</param>
    /// <param name="deviceScale">The current display scale used for device-correct image layout.</param>
    /// <param name="observation">The browser-lifetime resource observer; it owns no second image cache.</param>
    internal AssetThumbnailLoader(DispatcherQueue dispatcher, int cellPixels,
        double deviceScale, ThumbnailCacheObservation observation)
    {
        _loader = new ThumbnailLoader<AssetNode>(dispatcher, cellPixels, deviceScale,
            maximumCacheEntries: 2048, maximumCacheBytes: 64L * 1024 * 1024);
        _persistentDds = new DdsThumbnailProducer(PersistentThumbnails);
        _deviceScale = deviceScale;
        _observation = observation;
        _observation.Attach(this, () => _loader.CacheStatistics, _loader.TrimCacheToBytes);
    }

    /// <summary>Captures an exact source only after the browser owns this loader and its retirement path.</summary>
    /// <param name="source">The source retained by the browser's existing lease until this loader drains.</param>
    /// <param name="cancellationToken">The exact source snapshot's retirement token.</param>
    internal void BeginSession(AssetBrowseSession source, CancellationToken cancellationToken)
    {
        if (_started) throw new InvalidOperationException("A thumbnail attachment cannot substitute its source.");
        _started = true;
        _loader.BeginContentSession((node, pixels, token) =>
        {
            var thumbnail = AssetThumbnailSource.TryRender(source, node, pixels, _persistentDds, _deviceScale, token,
                out var imageInfo);
            // Published before the artwork crosses to the dispatcher, so the tile's caption can read it on arrival.
            node.ImageInfo = imageInfo;
            return thumbnail is { } image
                ? new ThumbnailContent(new DecodedImage(image.Width, image.Height, image.Rgba))
                : new ThumbnailContent(null, [DocumentText.Resource("AssetGallery_NoPreview")]);
        }, cancellationToken);
    }

    /// <summary>Queues one admitted realized occurrence; Shared owns recycling and late-publication checks.</summary>
    /// <param name="item">The exact current-source tile admitted by the UI owner.</param>
    internal void Request(AssetGalleryItem item) => _loader.Request(item);

    /// <summary>Returns the retained asynchronous drain, including failure, before the borrowed source can close.</summary>
    /// <returns>Completion of all detached worker activity and cache retirement.</returns>
    public async ValueTask DisposeAsync()
    {
        await _loader.DisposeAsync();
        _observation.CompleteRetirement(this);
    }
}
