using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using Slfx77.Multitool.Core.Localization;
using Slfx77.Multitool.Core.Settings;
using Slfx77.Multitool.WinUI.Images;
using Slfx77.Multitool.WinUI.Localization;

namespace BethesdaMultitool;

/// <summary>Composes Shared gallery/search presentation with Bethesda's exact source and export-check ownership.</summary>
public sealed partial class AssetBrowserTab
{
    private readonly Dictionary<AssetNode, AssetGalleryItem> _galleryTiles = new(ReferenceEqualityComparer.Instance);
    private AssetGalleryProjection? _galleryProjection;
    private DisplayLanguage? _galleryLanguage;
    private Task _galleryInitialization = Task.CompletedTask;
    private bool _updatingGallery;
    private bool _gallerySettingsFailed;

    /// <summary>Preserves source and window prerequisites until both scene and thumbnail owners have drained.</summary>
    /// <remarks>A failed loader retains its source, gallery and observation. Native scene proof alone cannot release them.
    /// The native movie preview's flag is false until its own DisposeAsync succeeds (unlike the Shadowkey flag, which
    /// defaults to true), so this must only ever be read after the tab's DisposeAsync.</remarks>
    internal bool WindowResourcesRetired => NativeResourcesRetired && _loader is null && _flic.NativeResourcesRetired;

    /// <summary>Connects retained native controls once using the window's existing localization owner.</summary>
    /// <param name="localization">The borrowed window controller, retained through gallery shutdown.</param>
    private void InitializeGallery(LocalizationController localization)
    {
        _galleryLanguage = localization.Language;
        _galleryLanguage.Changed += GalleryLanguageChanged;
        AssetGalleryView.ItemsView.ItemsSource = _gallery;
        AssetGalleryView.ItemsView.SelectionChanged += GallerySelectionChanged;
        AssetGalleryView.ThumbnailRequested += GalleryThumbnailRequested;
        AssetGalleryFilter.SearchChanged += GallerySearchChanged;
        AssetGalleryFilter.SettingsFailed += GallerySettingsFailed;
        _galleryInitialization = InitializeGallerySettingsAsync(localization);
    }

    /// <summary>Restores only this browser's view preference, preserving Thumbnail mode for a first launch.</summary>
    /// <param name="localization">The borrowed window controller.</param>
    /// <returns>Observed initialization; persistence failure leaves browsing available.</returns>
    private async Task InitializeGallerySettingsAsync(LocalizationController localization)
    {
        BrowserViewSettings? settings = null;
        try
        {
            var preferences = SettingsLocation.ForApplication("BethesdaMultitool");
            var store = new WorkflowSettingsStore(Path.Combine(Path.GetDirectoryName(preferences)!, "workflows.json"));
            settings = new BrowserViewSettings(store, "Assets.Images");
        }
        catch (Exception failure) { GallerySettingsFailed(this, failure); }
        try { await AssetGalleryFilter.InitializeAsync(AssetGalleryView, localization, settings); }
        catch (Exception failure) { GallerySettingsFailed(this, failure); }
    }

    /// <summary>Creates one source projection while retaining the native search text, the type filter and the shared check owner.</summary>
    private void AttachGallerySource()
    {
        _galleryProjection = new AssetGalleryProjection(_selection!, AssetGalleryFilter.Query, _kindFilter);
    }

    /// <summary>Reuses tiles within one folder and changes scope only to original current-source direct children.</summary>
    /// <param name="folder">The exact selected source folder.</param>
    private void ShowGallery(AssetNode folder)
    {
        if (CurrentAssetSelection is null || _galleryProjection is not { } projection) return;
        if (projection.TryShowFolder(folder))
        {
            ReleaseGalleryTiles();
            foreach (var node in projection.Items)
                _galleryTiles.Add(node, new AssetGalleryItem(node, _galleryLanguage!.Catalog, SetGalleryChecked));
        }
        SynchronizeGallery();
        UpdateGalleryStatus();
    }

    /// <summary>Changes visible references without discarding hidden tiles, checks or retained gallery focus.</summary>
    private void SynchronizeGallery()
    {
        if (_galleryProjection is not { } projection) return;
        var visible = projection.VisibleItems.Select(node => _galleryTiles[node]).ToArray();
        _updatingGallery = true;
        try
        {
            if (!_gallery.SequenceEqual(visible))
            {
                _gallery.Clear();
                foreach (var item in visible) _gallery.Add(item);
            }
            AssetGalleryView.ItemsView.SelectedItem = projection.VisibleFocus is { } node ? _galleryTiles[node] : null;
        }
        finally { _updatingGallery = false; }
    }

    /// <summary>Admits search changes without changing actual tree-owned previews or export selection.</summary>
    /// <param name="sender">The retained shared toolbar.</param>
    /// <param name="args">The native query change.</param>
    private void GallerySearchChanged(object? sender, EventArgs args)
    {
        if (CurrentAssetSelection is null || _galleryProjection?.TrySetQuery(AssetGalleryFilter.Query) != true) return;
        SynchronizeGallery();
        UpdateGalleryStatus();
    }

    /// <summary>
    ///     Focuses a clicked or keyboard-selected tile, previews it, and mirrors the selection into the
    ///     tree silently. Filter-induced selection resets are ignored, so gallery focus stays independent
    ///     of the tree while the tree stays in step with what the user chose.
    /// </summary>
    /// <param name="sender">The existing Shared native ItemsView.</param>
    /// <param name="args">The native selection change.</param>
    private void GallerySelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingGallery || CurrentAssetSelection is null || AssetGalleryView.ItemsView.SelectedItem is not AssetGalleryItem item ||
            _galleryProjection?.TryFocus(item.Node) != true) return;
        if (_treeNodes.TryGetValue(item.Node, out var treeNode))
        {
            _mirroringTreeSelection = true;
            try { AssetTreeView.SelectedNode = treeNode; }
            finally { _mirroringTreeSelection = false; }
            UpdateArchiveOpenCommand();
        }

        if (ReferenceEquals(item.Node, _previewNode)) return;
        ShowLevel2D(item.Node);
        ShowPreview(item.Node);
    }

    /// <summary>Forwards checks through exact current-source and current-tile gates before touching the source-wide owner.</summary>
    /// <param name="node">The original occurrence whose shared check control was activated.</param>
    /// <param name="isChecked">The native boolean command.</param>
    private void SetGalleryChecked(AssetNode node, bool isChecked)
    {
        if (IsCurrentAssetNode(node) && _galleryTiles.ContainsKey(node)) _galleryProjection?.TrySetChecked(node, isChecked);
    }

    /// <summary>Queues only exact current-source tile instances after Shared has marked their containers realized.</summary>
    /// <param name="sender">The retained Shared gallery.</param>
    /// <param name="tile">The realized adapter supplied by Shared.</param>
    private void GalleryThumbnailRequested(object? sender, ThumbnailTile tile)
    {
        if (tile is AssetGalleryItem item && IsCurrentAssetNode(item.Node) &&
            _galleryTiles.TryGetValue(item.Node, out var current) && ReferenceEquals(item, current)) _loader?.Request(item);
    }

    /// <summary>Refreshes row metadata in place after a live language change without recreating artwork or selection.</summary>
    /// <param name="sender">The borrowed display language.</param>
    /// <param name="args">The published catalog change.</param>
    private void GalleryLanguageChanged(object? sender, EventArgs args)
    {
        if (_disposed || _galleryLanguage is null) return;
        foreach (var tile in _galleryTiles.Values) tile.Refresh(_galleryLanguage.Catalog);
        UpdateGalleryStatus();
    }

    /// <summary>Reports a recoverable view-setting failure without disabling search or changing the current mode.</summary>
    /// <param name="sender">The shared toolbar or its initialization observer.</param>
    /// <param name="failure">The original persistence failure.</param>
    private void GallerySettingsFailed(object? sender, Exception failure)
    {
        Logger.Instance.Warn("[AssetBrowser] Gallery view setting failed: {0}", failure.Message);
        if (_disposed) return;
        _gallerySettingsFailed = true;
        UpdateGalleryStatus();
    }

    /// <summary>Displays the shown, total and type-filtered direct-child counts; preview messages live in the preview pane.</summary>
    private void UpdateGalleryStatus()
    {
        if (_galleryLanguage is null) return;
        var status = _galleryProjection is { } projection
            ? _galleryLanguage.Format("AssetGallery_Counts", projection.VisibleItems.Count, projection.Items.Count, projection.OtherCount)
            : string.Empty;
        if (_gallerySettingsFailed) status += " " + _galleryLanguage.GetString("AssetGallery_SettingsFailed");
        AssetGalleryStatusText.Text = status.Trim();
    }

    /// <summary>Detaches every folder tile, including filtered rows, without modifying source check values.</summary>
    private void ReleaseGalleryTiles()
    {
        _updatingGallery = true;
        try
        {
            _gallery.Clear();
            foreach (var tile in _galleryTiles.Values) tile.Dispose();
            _galleryTiles.Clear();
        }
        finally { _updatingGallery = false; }
    }

    /// <summary>Cancels and drains the exact loader before source release, retaining a failed drain for every later observer.</summary>
    /// <returns>Completion of the existing source's worker; native preview retirement is an independent prerequisite.</returns>
    private async Task RetireGalleryLoaderAsync()
    {
        var loader = _loader;
        if (loader is null) return;
        await loader.DisposeAsync();
        if (ReferenceEquals(_loader, loader)) _loader = null;
    }

    /// <summary>Clears source references only after the existing source's thumbnail decoder has drained.</summary>
    private void ClearGallerySource()
    {
        ReleaseGalleryTiles();
        _galleryProjection?.Dispose();
        _galleryProjection = null;
    }

    /// <summary>Drains preference initialization and writes before releasing native presentation subscriptions.</summary>
    /// <returns>Complete toolbar shutdown; gallery cleanup runs even if a settings write failed.</returns>
    private async Task DisposeGalleryAsync()
    {
        if (_galleryLanguage is not null) _galleryLanguage.Changed -= GalleryLanguageChanged;
        AssetGalleryView.ThumbnailRequested -= GalleryThumbnailRequested;
        AssetGalleryView.ItemsView.SelectionChanged -= GallerySelectionChanged;
        AssetGalleryFilter.SearchChanged -= GallerySearchChanged;
        AssetGalleryFilter.SettingsFailed -= GallerySettingsFailed;
        try
        {
            await _galleryInitialization;
            await AssetGalleryFilter.DisposeAsync();
        }
        finally
        {
            // Shared's view is retired only after its loader has actually drained. Failure retains
            // the source and visual owners; detaching events alone is not a retirement proof.
            if (_loader is null)
            {
                try
                {
                    ClearGallerySource();
                    AssetGalleryView.Dispose();
                }
                finally { _thumbnailObservation.CompleteBrowserRetirement(); }
            }
            _galleryLanguage = null;
        }
    }
}
