using System.Runtime.InteropServices.WindowsRuntime;

using BethesdaMultitool.Core.Imaging;
using BethesdaMultitool.Core.Rendering.Level2D;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BethesdaMultitool;

/// <summary>
///     Shows one <see cref="ILevel2DSource" /> layer at a time — the top-down view of a classic
///     level, for the games whose worlds are authored as grids rather than geometry.
///     <para>
///         Built on <see cref="WriteableBitmap" /> inside a <see cref="ScrollViewer" /> rather than
///         Win2D, which the plan suggested. A <see cref="Level2DRender" /> is already row-major
///         RGBA, so there is nothing to draw — only pixels to hand over — and Win2D would add a
///         second rendering technology to a tab that already uses this exact path for video. The
///         ScrollViewer supplies pan and zoom for free, which matters because these images are
///         large: Daggerfall's WOODS heightmap is 1000x500 before any scale factor.
///     </para>
///     <para>
///         ⚠ <see cref="WriteableBitmap" /> is a CPU surface, so it stops being free as the raster
///         grows. Daggerfall's WOODS at 1000x500 is 2 MB and fine; a 128x128-tile zone rendered at
///         32 px/tile would be 4096x4096 = 64 MB of RGBA, where XAML's decode limits and zoom
///         re-scaling start to matter. The fix when that arrives is to cap pixels-per-tile in the
///         <see cref="ILevel2DSource" />, not to change presenter — the seam hands over pixels
///         either way, so swapping this control costs exactly one file.
///     </para>
///     <para>
///         ⚠ Nothing here runs on a background thread: the decode happens before the control is
///         handed a source, and writing pixels into a WriteableBitmap is UI-thread-only anyway. So
///         this directory does NOT need adding to <c>UiThreadReachableDirectories</c> for a pumping
///         wait — there is no wait to pump.
///     </para>
/// </summary>
internal sealed partial class Level2dMapControl : UserControl
{
    private readonly ComboBox _layerPicker;
    private readonly Image _image;
    private readonly TextBlock _status;

    private ILevel2DSource? _source;

    public Level2dMapControl()
    {
        _image = new Image { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

        var scroller = new ScrollViewer
        {
            Content = _image,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            ZoomMode = ZoomMode.Enabled,
            MinZoomFactor = 0.1f,
            MaxZoomFactor = 8f
        };
        AutomationProperties.SetName(scroller, "Level map");

        _layerPicker = new ComboBox { MinWidth = 140, Margin = new Thickness(0, 0, 8, 0) };
        AutomationProperties.SetName(_layerPicker, "Level layer");
        _layerPicker.SelectionChanged += (_, _) => ShowSelectedLayer();

        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        bar.Children.Add(_layerPicker);
        bar.Children.Add(_status);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(bar, 0);
        Grid.SetRow(scroller, 1);
        root.Children.Add(bar);
        root.Children.Add(scroller);

        Content = root;
    }

    /// <summary>Shows a level, selecting its first layer. Pass null to clear.</summary>
    public void SetSource(ILevel2DSource? source)
    {
        _source = source;
        _layerPicker.Items.Clear();

        if (source is null)
        {
            _image.Source = null;
            _status.Text = string.Empty;
            return;
        }

        foreach (var layer in source.Layers)
        {
            _layerPicker.Items.Add(layer.ToString());
        }

        // Selecting fires SelectionChanged, which paints; an empty layer list would leave the
        // previous image up, so clear it first.
        _image.Source = null;
        if (_layerPicker.Items.Count > 0)
        {
            _layerPicker.SelectedIndex = 0;
        }
        else
        {
            _status.Text = $"{source.DisplayName} — no renderable layers";
        }
    }

    private void ShowSelectedLayer()
    {
        var source = _source;
        var index = _layerPicker.SelectedIndex;
        if (source is null || index < 0 || index >= source.Layers.Count)
        {
            return;
        }

        var layer = source.Layers[index];

        // A null render is "nothing to show" by contract, never an error.
        if (source.Render(layer) is not { } render || render.Rgba.Length == 0)
        {
            _image.Source = null;
            _status.Text = $"{source.DisplayName} — {layer} has nothing to draw";
            return;
        }

        var bitmap = new WriteableBitmap(render.Width, render.Height);
        var bgra = PremultipliedBgra.FromRgba(render.Rgba);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(bgra, 0, bgra.Length);
        }

        bitmap.Invalidate();
        _image.Source = bitmap;

        // Say plainly that the colours are diagnostic; they are not the game's textures.
        _status.Text = $"{source.DisplayName} — {render.Width}x{render.Height}, diagnostic colours";
    }
}
