using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using BethesdaMultitool.Core.AssetBrowse;
using Microsoft.UI.Xaml.Media.Imaging;
using Slfx77.Multitool.Core.Localization;
using Slfx77.Multitool.WinUI.Images;

namespace BethesdaMultitool;

/// <summary>
///     Adapts an original Bethesda leaf node of any kind to the shared tile without owning checks,
///     artwork or source lifetime. Picture kinds request a decoded thumbnail; every other kind shows
///     its kind glyph and never asks the worker for artwork.
/// </summary>
internal sealed class AssetGalleryItem : ThumbnailItem<AssetNode>, IDisposable
{
    private IStringCatalog _catalog;
    private Action<AssetNode, bool>? _setChecked;
    private AssetImageInfo? _imageInfo;

    /// <summary>Borrows one exact node and a current-source command gate, observing its existing check mirror.</summary>
    /// <param name="node">The immutable source occurrence; its label is never an identity.</param>
    /// <param name="catalog">The window's current display catalog.</param>
    /// <param name="setChecked">The UI owner's generation-checked command entry point.</param>
    [SetsRequiredMembers]
    internal AssetGalleryItem(AssetNode node, IStringCatalog catalog, Action<AssetNode, bool> setChecked)
    {
        Reference = node;
        DisplayName = node.Name;
        _catalog = catalog;
        _setChecked = setChecked;
        node.PropertyChanged += NodePropertyChanged;
        PropertyChanged += TilePropertyChanged;
        RefreshMetadata(catalog);
    }

    /// <summary>The exact original node retained by both native layouts.</summary>
    internal AssetNode Node => Reference;
    /// <summary>What the asset is, with its full decoded size and frame count once a picture is decoded, then its size on disk.</summary>
    public override string Caption => _imageInfo switch
    {
        null => _catalog.Format("AssetGallery_KindAndBytes", KindLabel, Node.Size),
        { FrameCount: > 1 } info => _catalog.Format("AssetGallery_KindFramesAndBytes", KindLabel, info.Width, info.Height,
            info.FrameCount, Node.Size),
        var info => _catalog.Format("AssetGallery_KindSizeAndBytes", KindLabel, info.Width, info.Height, Node.Size)
    };
    /// <summary>The current-language name of the node's kind, for every kind the tree lists.</summary>
    private string KindLabel => _catalog.GetString(AssetKindLabels.CaptionKey(Node.Kind));
    /// <summary>Only picture kinds ask the worker for artwork; every other kind keeps its glyph without a decode attempt.</summary>
    public override bool CanRequestThumbnail => AssetThumbnailSource.CanRender(Node);
    /// <summary>A Segoe Fluent Icons glyph naming the kind, shown until (or instead of) decoded artwork.</summary>
    public override string NoArtworkGlyph => Node.Kind switch
    {
        AssetNodeKind.Video => "",
        AssetNodeKind.Audio => "",
        AssetNodeKind.Model => "",
        AssetNodeKind.Map => "",
        AssetNodeKind.Archive => "",
        AssetNodeKind.Plugin => "",
        AssetNodeKind.Text => "",
        AssetNodeKind.Save => "",
        AssetNodeKind.Raw => "",
        _ => base.NoArtworkGlyph
    };
    /// <summary>The source-relative path and size shown in the native compact row.</summary>
    public override string ListCaption => Node.VirtualPath + " - " + Caption;
    /// <summary>No provenance badge is fabricated from a display label or path.</summary>
    public override string Badge => string.Empty;
    /// <summary>Export checks are supplied by the existing source selection.</summary>
    public override bool CanCheck => _setChecked is not null;
    /// <summary>The source node's check mirror; commands pass through the browser's current-source gate.</summary>
    public override bool IsChecked
    {
        get => Node.IsChecked == true;
        set
        {
            if (value == IsChecked) return;
            _setChecked?.Invoke(Node, value);
            NotifyCheckStateChanged();
        }
    }
    /// <summary>Full untruncated row metadata exposed by Shared tooltips and accessibility help in both modes.</summary>
    public override string Metadata => _catalog.Format("AssetGallery_Metadata", DisplayName, Node.VirtualPath, KindLabel, Caption);

    /// <summary>Refreshes only display strings, retaining artwork, original identity, checks and focus.</summary>
    /// <param name="catalog">The replacement current-language catalog.</param>
    internal void Refresh(IStringCatalog catalog)
    {
        _catalog = catalog;
        RefreshMetadata(catalog);
    }

    /// <summary>Forwards the source-wide check owner's committed mirror change to both shared native templates.</summary>
    /// <param name="sender">The original source node.</param>
    /// <param name="args">The changed mirror property.</param>
    private void NodePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AssetNode.IsChecked)) NotifyCheckStateChanged();
    }

    /// <summary>Retains what the worker decoded (full size, frames) after recycling without retaining a native bitmap.</summary>
    /// <param name="sender">This adapter's shared visual state.</param>
    /// <param name="args">The changed visual property.</param>
    private void TilePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(Thumbnail) || Thumbnail is not WriteableBitmap || Node.ImageInfo is not { } info) return;
        _imageInfo = info;
        RefreshMetadata();
    }

    /// <summary>Detaches the source mirror and command owner before dropping the realized bitmap.</summary>
    public void Dispose()
    {
        Node.PropertyChanged -= NodePropertyChanged;
        PropertyChanged -= TilePropertyChanged;
        _setChecked = null;
        IsRealized = false;
        Reset();
    }
}
