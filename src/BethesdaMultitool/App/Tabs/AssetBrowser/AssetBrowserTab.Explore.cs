using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Concurrency;
using BethesdaMultitool.Core.Diagnostics;
using Microsoft.UI.Xaml;
using Slfx77.Multitool.Core.Browsing;

namespace BethesdaMultitool;

/// <summary>Shares the workspace source while retaining the existing asset previews.</summary>
public sealed partial class AssetBrowserTab
{
    private BrowserSnapshot? _sourceSnapshot;
    private BrowserLease? _sourceLease;
    private AssetTreeSelection? _selection;
    private FrameworkElement? _workspaceMap;
    private FrameworkElement? _workspaceAssets;

    /// <summary>Mounts the existing map and gallery once, without a duplicate source bar or tab strip.</summary>
    internal void ConfigureWorkspaceHost()
    {
        if (_workspaceAssets is not null) return;
        BrowserHeader.Visibility = Visibility.Collapsed;
        BrowserSourceBar.Visibility = Visibility.Collapsed;
        BrowserLayout.Padding = new Thickness(0);
        _workspaceMap = (FrameworkElement)BrowserMapPane.Content;
        _workspaceAssets = (FrameworkElement)BrowserAssetsPane.Content;
        BrowserMapPane.Content = null;
        BrowserAssetsPane.Content = null;
        WorkspacePanes.Children.Add(_workspaceMap);
        WorkspacePanes.Children.Add(_workspaceAssets);
        PaneTabView.Visibility = Visibility.Collapsed;
        WorkspacePanes.Visibility = Visibility.Visible;
        ShowWorkspaceMaps(false);
    }

    /// <summary>Shows the classic map or gallery while retaining both surfaces in the visual tree.</summary>
    /// <param name="maps">True to show the map; false to show the gallery.</param>
    internal void ShowWorkspaceMaps(bool maps)
    {
        if (_workspaceMap is null || _workspaceAssets is null) return;
        _workspaceMap.Visibility = maps ? Visibility.Visible : Visibility.Collapsed;
        _workspaceAssets.Visibility = maps ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Attaches the workspace's leased source without transferring filesystem ownership.</summary>
    /// <param name="snapshot">The exact shared or independently opened source to retain.</param>
    /// <returns>Completion after the prior audio input is retired and this source is attached.</returns>
    internal async Task AttachWorkspaceSourceAsync(BrowserSnapshot snapshot)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (snapshot.Source is not BethesdaBrowseSource source)
            throw new ArgumentException("The workspace source is not a Bethesda filesystem.", nameof(snapshot));
        var lease = snapshot.AcquireLease();
        try
        {
            var closing = CloseSourceAsync();
            var generation = _sourceGeneration;
            await closing;
            if (_disposed || generation != _sourceGeneration)
            {
                throw new OperationCanceledException("The source attachment was superseded.");
            }
            _sourceSnapshot = snapshot;
            _sourceLease = lease;
            _session = source.Session;
            _selection = new AssetTreeSelection(snapshot);
            SourcePathTextBox.Text = source.Session.SourcePath;
            _loader = new AssetThumbnailLoader(DispatcherQueue, ThumbnailCellPixels,
                XamlRoot?.RasterizationScale ?? 1.0, _thumbnailObservation);
            _loader.BeginSession(_session, snapshot.CancellationToken);
            AttachGallerySource();
            ShowTree(_session);
            ShowGallery(_session.Root);
        }
        catch
        {
            if (ReferenceEquals(_sourceLease, lease))
            {
                await CloseSourceAsync();
            }
            else
            {
                await lease.DisposeAsync();
            }
            throw;
        }
    }

    /// <summary>Keeps an obsolete source alive until a canceled decoder has actually returned.</summary>
    private async Task RunPreviewAsync<T>(LatestOnlyJob job, Func<CancellationToken, T> work,
        Action<T> apply, Action<T>? discard = null)
    {
        var snapshot = _sourceSnapshot;
        await using var lease = snapshot?.AcquireLease();
        try
        {
            await job.RunAsync(work, result =>
            {
                if (snapshot is null || ReferenceEquals(snapshot, _sourceSnapshot) &&
                    !snapshot.CancellationToken.IsCancellationRequested)
                    apply(result);
                else discard?.Invoke(result);
            }, discard);
        }
        catch (Exception exception)
        {
            if (!_disposed && (snapshot is null || ReferenceEquals(snapshot, _sourceSnapshot)))
                Logger.Instance.Warn("[AssetBrowser] Preview failed: {0}", exception.Message);
        }
    }
}
