// Tile shape and the device-pixel sizing rule ported from JimmyPCTool / AweMultitool
// (https://github.com/slfx77/JimmyPCTool, MIT licence) — src/AweMultitool/App/Tabs/CanvasGalleryItem.cs.
// Retargeted from that project's CanvasRef to this repo's AssetNode.

using System.ComponentModel;
using System.Runtime.CompilerServices;

using BethesdaMultitool.Core.AssetBrowse;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BethesdaMultitool;

/// <summary>
///     One tile in the asset gallery. <see cref="Thumbnail" /> binds one-way and arrives later, so
///     <see cref="SetThumbnail" /> is UI-thread-only — raising the change from a background
///     continuation throws.
/// </summary>
public sealed class AssetGalleryItem : INotifyPropertyChanged
{
    private bool _broken;
    private ImageSource? _thumbnail;

    /// <summary>The asset this tile stands for.</summary>
    public required AssetNode Node { get; init; }

    /// <summary>File name, shown under the tile.</summary>
    public string DisplayName => Node.Name;

    /// <summary>Path within the source, for the tooltip and the accessible name.</summary>
    public string VirtualPath => Node.VirtualPath;

    /// <summary>Size, and the geometry once decoded.</summary>
    public string Caption { get; private set; } = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            _thumbnail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlaceholderVisibility));
        }
    }

    /// <summary>Shown until a thumbnail arrives, and permanently for a tile that would not decode.</summary>
    public Visibility PlaceholderVisibility => _thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A hollow glyph for "still loading", a filled one for "cannot decode".</summary>
    public string PlaceholderGlyph => _broken ? "" : "";

    /// <summary>
    ///     Size to draw the thumbnail at, in DIPs, so one thumbnail pixel covers exactly one device
    ///     pixel.
    ///     <para>
    ///         Binding these rather than capping the element at a fixed size is what keeps the art
    ///         sharp. <see cref="ThumbnailScaler" /> has already scaled by a WHOLE number; letting
    ///         layout fit the result into a fixed box would scale it again by a fraction and turn
    ///         every 8×8 glyph of Daggerfall's font soft.
    ///     </para>
    /// </summary>
    public double ThumbnailWidth { get; private set; }

    public double ThumbnailHeight { get; private set; }

    /// <summary>
    ///     Whether the gallery currently has this tile realized. The loader reads it to skip work for
    ///     tiles that scrolled away, and re-checks it before applying a result. UI thread only.
    /// </summary>
    internal bool IsRealized { get; set; }

    /// <summary>Whether this tile has already been queued in the current session.</summary>
    internal bool IsRequested { get; set; }

    /// <summary>Attaches a decoded thumbnail. UI thread only.</summary>
    /// <param name="source">The bitmap, or null when the asset could not be decoded.</param>
    /// <param name="deviceScale">Device pixels per DIP, from the tile's <c>XamlRoot</c>.</param>
    internal void SetThumbnail(WriteableBitmap? source, double deviceScale)
    {
        _broken = source is null;

        if (source is not null && deviceScale > 0)
        {
            ThumbnailWidth = source.PixelWidth / deviceScale;
            ThumbnailHeight = source.PixelHeight / deviceScale;
            OnPropertyChanged(nameof(ThumbnailWidth));
            OnPropertyChanged(nameof(ThumbnailHeight));
        }

        Thumbnail = source;
        OnPropertyChanged(nameof(PlaceholderGlyph));
    }

    /// <summary>Sets the caption once the real geometry is known. UI thread only.</summary>
    internal void SetCaption(string caption)
    {
        Caption = caption;
        OnPropertyChanged(nameof(Caption));
    }

    /// <summary>Drops the thumbnail so a new session starts clean.</summary>
    internal void Reset()
    {
        _broken = false;
        IsRequested = false;
        Thumbnail = null;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
