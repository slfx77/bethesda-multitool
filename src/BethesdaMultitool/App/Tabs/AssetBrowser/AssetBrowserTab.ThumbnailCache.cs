using BethesdaMultitool.Core.Concurrency;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Localization;
using Microsoft.UI.Xaml;

namespace BethesdaMultitool;

/// <summary>Provides contextual persistent-thumbnail clearing without replacing the browser source or preview.</summary>
public sealed partial class AssetBrowserTab
{
    private readonly LatestOnlyJob _thumbnailCacheClearJob = new();
    private Task _thumbnailCacheClearWork = Task.CompletedTask;
    private bool _clearingThumbnailCache;

    /// <summary>Publishes the complete operation lifetime before UI callbacks or storage work can begin.</summary>
    /// <param name="sender">The contextual thumbnail-cache button.</param>
    /// <param name="args">The native activation event.</param>
    private void ClearThumbnailCache_Click(object sender, RoutedEventArgs args)
    {
        if (_disposed || _clearingThumbnailCache)
        {
            return;
        }
        _clearingThumbnailCache = true;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thumbnailCacheClearWork = completion.Task;
        _ = ObserveThumbnailCacheClearAsync(completion);
    }

    /// <summary>Retains unexpected UI failures on the task awaited by shutdown, after normal storage errors are reported.</summary>
    /// <param name="completion">The operation task published before callbacks.</param>
    /// <returns>Completion after the storage worker and its UI continuation have settled.</returns>
    private async Task ObserveThumbnailCacheClearAsync(TaskCompletionSource completion)
    {
        try
        {
            await ClearThumbnailCacheAsync();
            completion.TrySetResult();
        }
        catch (Exception failure)
        {
            completion.TrySetException(failure);
        }
    }

    /// <summary>Clears optional disk artwork off-thread, retaining all source, preview and in-memory gallery state.</summary>
    /// <returns>Completion after the owned job settles and its button state is restored.</returns>
    private async Task ClearThumbnailCacheAsync()
    {
        try
        {
            ClearThumbnailCacheButton.IsEnabled = false;
            RuntimeLocalization.SetText(ThumbnailCacheStatusText, "AssetGallery_CacheClearing");
            await _thumbnailCacheClearJob.RunAsync(
                AssetThumbnailLoader.ClearPersistentThumbnailCache,
                cleared =>
                {
                    if (!_disposed)
                    {
                        RuntimeLocalization.SetText(ThumbnailCacheStatusText,
                            cleared ? "AssetGallery_CacheCleared" : "AssetGallery_CacheClearFailed");
                    }
                });
        }
        catch (Exception failure)
        {
            Logger.Instance.Warn("[AssetBrowser] Thumbnail cache clear failed: {0}", failure.Message);
            if (!_disposed)
            {
                RuntimeLocalization.SetText(ThumbnailCacheStatusText, "AssetGallery_CacheClearFailed");
            }
        }
        finally
        {
            _clearingThumbnailCache = false;
            if (!_disposed)
            {
                ClearThumbnailCacheButton.IsEnabled = true;
            }
        }
    }
}
