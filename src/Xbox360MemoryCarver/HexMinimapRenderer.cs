using Windows.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Xbox360MemoryCarver.App.HexViewer;

namespace Xbox360MemoryCarver.App;

/// <summary>
///     Minimap rendering and interaction logic for HexViewerControl.
///     Extracted to reduce complexity of the main control.
/// </summary>
internal sealed class HexMinimapRenderer
{
    private readonly Canvas _canvas;
    private readonly Func<long, FileRegion?> _findRegion;
    private readonly ScrollViewer _scrollViewer;
    private readonly Border _viewportIndicator;

    public HexMinimapRenderer(
        Canvas canvas,
        Border viewportIndicator,
        ScrollViewer scrollViewer,
        Func<long, FileRegion?> findRegion)
    {
        _canvas = canvas;
        _viewportIndicator = viewportIndicator;
        _scrollViewer = scrollViewer;
        _findRegion = findRegion;
    }

    public double Zoom { get; set; } = 1.0;

    public bool IsDragging { get; set; }

    public void Render(long fileSize, double containerWidth, double containerHeight)
    {
        _canvas.Children.Clear();
        _canvas.Children.Add(_viewportIndicator);

        if (fileSize == 0)
        {
            _viewportIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        if (containerWidth <= 0 || containerHeight <= 0)
        {
            return;
        }

        var canvasWidth = Math.Max(20, containerWidth);
        var canvasHeight = Math.Max(containerHeight, containerHeight * Zoom);
        _canvas.Width = canvasWidth;
        _canvas.Height = canvasHeight;

        var bg = new Rectangle
        {
            Width = canvasWidth,
            Height = canvasHeight,
            Fill = new SolidColorBrush(Color.FromArgb(255, 30, 30, 30))
        };
        _canvas.Children.Insert(0, bg);

        Color? currentColor = null;
        double currentStartY = 0;
        var defaultColor = Color.FromArgb(255, 60, 60, 60);

        for (double y = 0; y < canvasHeight; y++)
        {
            var fileOffset = (long)(y / canvasHeight * fileSize);
            var region = _findRegion(fileOffset);
            var color = region?.Color ?? defaultColor;

            if (currentColor == null)
            {
                currentColor = color;
                currentStartY = y;
                continue;
            }

            if (color != currentColor.Value)
            {
                AddRect(canvasWidth, currentStartY, y - currentStartY, currentColor.Value);
                currentColor = color;
                currentStartY = y;
            }
        }

        if (currentColor != null)
        {
            AddRect(canvasWidth, currentStartY, canvasHeight - currentStartY, currentColor.Value);
        }

        _canvas.Children.Remove(_viewportIndicator);
        _canvas.Children.Add(_viewportIndicator);
    }

    private void AddRect(double width, double top, double height, Color color)
    {
        var rect = new Rectangle
        {
            Width = width,
            Height = Math.Max(1, height),
            Fill = new SolidColorBrush(color)
        };
        Canvas.SetTop(rect, top);
        Canvas.SetLeft(rect, 0);
        _canvas.Children.Insert(_canvas.Children.Count - 1, rect);
    }

    public void UpdateViewport(
        long fileSize,
        long totalRows,
        long currentTopRow,
        int visibleRows,
        int bytesPerRow)
    {
        if (fileSize == 0 || totalRows == 0)
        {
            _viewportIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        var canvasHeight = _canvas.Height;
        var canvasWidth = _canvas.Width;
        if (double.IsNaN(canvasHeight) || canvasHeight <= 0)
        {
            return;
        }

        var viewStartOffset = currentTopRow * bytesPerRow;
        var viewEndOffset = Math.Min((currentTopRow + visibleRows) * bytesPerRow, fileSize);
        var viewCenterFraction = ((double)viewStartOffset / fileSize + (double)viewEndOffset / fileSize) / 2;
        var viewHeightFraction = (double)(viewEndOffset - viewStartOffset) / fileSize;

        var minimapHeight = Math.Max(12, viewHeightFraction * canvasHeight);
        var minimapTop = viewCenterFraction * canvasHeight - minimapHeight / 2;
        minimapTop = Math.Clamp(minimapTop, 0, canvasHeight - minimapHeight);

        _viewportIndicator.Width = Math.Max(10, canvasWidth - 4);
        _viewportIndicator.Height = minimapHeight;
        Canvas.SetLeft(_viewportIndicator, 2);
        Canvas.SetTop(_viewportIndicator, minimapTop);
        _viewportIndicator.Visibility = Visibility.Visible;

        if (Zoom > 1)
        {
            var viewportHeight = _scrollViewer.ViewportHeight;
            if (viewportHeight > 0 && viewportHeight < canvasHeight)
            {
                var targetY = Math.Clamp(
                    minimapTop + minimapHeight / 2 - viewportHeight / 2,
                    0,
                    canvasHeight - viewportHeight);
                _scrollViewer.ChangeView(null, targetY, null, true);
            }
        }
    }

    public long? GetRowFromPosition(double y, long totalRows, int visibleRows)
    {
        var canvasHeight = _canvas.Height;
        if (canvasHeight <= 0 || totalRows == 0)
        {
            return null;
        }

        var fraction = Math.Clamp(y / canvasHeight, 0, 1);
        var targetRow = (long)(fraction * totalRows) - visibleRows / 2;
        return Math.Clamp(targetRow, 0, Math.Max(0, totalRows - visibleRows));
    }

    public void HandlePointerPressed(PointerRoutedEventArgs e)
    {
        IsDragging = true;
        _canvas.CapturePointer(e.Pointer);
    }

    public void HandlePointerReleased(PointerRoutedEventArgs e)
    {
        IsDragging = false;
        _canvas.ReleasePointerCapture(e.Pointer);
    }
}
