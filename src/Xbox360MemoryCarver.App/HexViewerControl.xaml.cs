using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;
using Windows.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Xbox360MemoryCarver.Core;

namespace Xbox360MemoryCarver.App;

/// <summary>
///     High-performance hex viewer with virtual scrolling.
///     Uses a single TextBlock with Inlines for efficient rendering.
///     Only renders visible rows - true lazy loading.
/// </summary>
public sealed partial class HexViewerControl : UserControl, IDisposable
{
    // Layout constants
    private const int BytesPerRow = 16;
    private const double RowHeight = 16.0; // Approximate height per row

    // Colors are now defined in FileTypeColors.cs - single source of truth

    private static readonly SolidColorBrush OffsetBrush = new(Color.FromArgb(255, 86, 156, 214));
    private static readonly SolidColorBrush TextBrush = new(Color.FromArgb(255, 212, 212, 212));
    private static readonly SolidColorBrush AsciiBrush = new(Color.FromArgb(255, 128, 128, 128));

    // File regions for coloring
    private readonly List<FileRegion> _fileRegions = [];
    private MemoryMappedViewAccessor? _accessor;
    private AnalysisResult? _analysisResult;

    // Scroll state
    private long _currentTopRow;
    private bool _disposed;
    private string? _filePath;
    private long _fileSize;

    // Minimap state
    private bool _isDraggingMinimap;
    private double _lastMinimapContainerHeight;
    private double _minimapZoom = 1.0;
    private MemoryMappedFile? _mmf;
    private long _totalRows;
    private int _visibleRows;

    public HexViewerControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // Build the legend from the shared color definitions
        BuildLegend();

        // Handle mouse wheel on the hex display
        HexDisplayArea.PointerWheelChanged += HexDisplayArea_PointerWheelChanged;
    }

    /// <summary>
    ///     Dispose managed resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        CleanupMemoryMapping();
    }

    private void BuildLegend()
    {
        foreach (var category in FileTypeColors.LegendCategories)
        {
            var itemPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4
            };

            var colorBox = new Border
            {
                Width = 12,
                Height = 12,
                Background = new SolidColorBrush(category.Color),
                CornerRadius = new CornerRadius(2)
            };

            var label = new TextBlock
            {
                Text = category.Name,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 204, 204, 204))
            };

            itemPanel.Children.Add(colorBox);
            itemPanel.Children.Add(label);
            LegendPanel.Children.Add(itemPanel);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Delay initial render to ensure layout is complete
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateVisibleRowCount();
            if (_analysisResult != null)
            {
                RenderVisibleRows();
                RenderMinimap();
                UpdateMinimapViewport();
            }
        });
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }

    private void HexDisplayArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Only re-render if height changed meaningfully
        if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 1)
        {
            UpdateVisibleRowCount();
            if (_analysisResult != null) RenderVisibleRows();
        }
    }

#pragma warning disable S2325, RCS1163 // Event handlers bound in XAML must be instance methods
    private void OnSizeChanged(object _, SizeChangedEventArgs __)
    {
        // Handled by HexDisplayArea_SizeChanged and MinimapContainer_SizeChanged
    }
#pragma warning restore S2325, RCS1163

    private void UpdateVisibleRowCount()
    {
        // Account for Border padding (4px top + 4px bottom = 8px)
        var displayHeight = HexDisplayArea.ActualHeight - 8;
        if (displayHeight > 0)
            // Calculate exact rows that fit, rounding up to fill viewport completely
            _visibleRows = (int)Math.Ceiling(displayHeight / RowHeight);
        else
            _visibleRows = 30; // Default

        // Update slider step frequency for smooth scrolling
        VirtualScrollBar.StepFrequency = 1;
    }

    private void CleanupMemoryMapping()
    {
        _accessor?.Dispose();
        _accessor = null;
        _mmf?.Dispose();
        _mmf = null;
    }

    public void Clear()
    {
        CleanupMemoryMapping();
        _filePath = null;
        _analysisResult = null;
        _fileSize = 0;
        _fileRegions.Clear();
        _currentTopRow = 0;
        _totalRows = 0;
        HexTextBlock.Inlines.Clear();
        MinimapCanvas.Children.Clear();
        ViewportIndicator.Visibility = Visibility.Collapsed;
        VirtualScrollBar.Maximum = 0;
        VirtualScrollBar.Value = 0;
    }

    /// <summary>
    ///     Navigate to a specific byte offset in the file.
    /// </summary>
    public void NavigateToOffset(long offset)
    {
        if (_fileSize == 0 || _totalRows == 0) return;

        // Calculate the row for this offset
        var targetRow = offset / BytesPerRow;

        // Center the view on this row if possible
        targetRow = Math.Max(0, targetRow - _visibleRows / 2);
        targetRow = Math.Clamp(targetRow, 0, Math.Max(0, _totalRows - _visibleRows));

        _currentTopRow = targetRow;
        VirtualScrollBar.Value = targetRow;
        RenderVisibleRows();
        UpdateMinimapViewport();

        // Log for debugging
        var region = FindRegionForOffset(offset);
        Debug.WriteLine(
            $"[Navigate] Offset=0x{offset:X8}, Row={targetRow}, Region={region?.TypeName ?? "none"} (color: {region?.Color})");
    }

    public void LoadData(string filePath, AnalysisResult analysisResult)
    {
        CleanupMemoryMapping();

        _filePath = filePath;
        _analysisResult = analysisResult;

        var fileInfo = new FileInfo(filePath);
        _fileSize = fileInfo.Length;

        Debug.WriteLine($"[HexViewer] Loading {filePath}, size: {_fileSize:N0} bytes");

        BuildFileRegions();

        // Setup memory-mapped file
        try
        {
            _mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            _accessor = _mmf.CreateViewAccessor(0, _fileSize, MemoryMappedFileAccess.Read);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HexViewer] Memory mapping failed: {ex.Message}");
        }

        // Calculate total rows
        _totalRows = (_fileSize + BytesPerRow - 1) / BytesPerRow;
        _currentTopRow = 0;

        // Setup scrollbar - ensure it has a valid range
        UpdateVisibleRowCount();
        var scrollMax = Math.Max(0, _totalRows - _visibleRows);
        VirtualScrollBar.Minimum = 0;
        VirtualScrollBar.Maximum = scrollMax;
        VirtualScrollBar.Value = 0;

        Debug.WriteLine(
            $"[HexViewer] TotalRows={_totalRows}, VisibleRows={_visibleRows}, ScrollMax={scrollMax}");

        RenderVisibleRows();
        RenderMinimap();

        DispatcherQueue.TryEnqueue(UpdateMinimapViewport);
    }

    private void BuildFileRegions()
    {
        _fileRegions.Clear();

        if (_analysisResult == null) return;

        // Sort by offset, then by priority (so higher priority comes first for same offset)
        var sortedFiles = _analysisResult.CarvedFiles
            .Where(f => f.Length > 0)
            .OrderBy(f => f.Offset)
            .ThenBy(f => FileTypeColors.GetPriority(f.FileType))
            .ToList();

        // Build non-overlapping regions, skipping lower-priority overlaps
        var occupiedRanges = new List<(long Start, long End, int Priority)>();
        var skippedOverlaps = 0;

        foreach (var file in sortedFiles)
        {
            var start = file.Offset;
            var end = file.Offset + file.Length;
            var priority = FileTypeColors.GetPriority(file.FileType);

            // Check if this region overlaps with any existing higher-priority region
            var hasHigherPriorityOverlap = occupiedRanges.Any(r =>
                start < r.End && end > r.Start && r.Priority <= priority);

            if (hasHigherPriorityOverlap)
            {
                // Skip regions that overlap with higher or equal priority ones
                skippedOverlaps++;
                Debug.WriteLine(
                    $"[BuildRegions] Skipping {file.FileType} at 0x{start:X} (overlaps with higher priority region)");
                continue;
            }

            var typeName = FileTypeColors.NormalizeTypeName(file.FileType);
            var color = FileTypeColors.GetColor(typeName);

            Debug.WriteLine(
                $"[BuildRegions] Region: {file.FileType} -> {typeName} @ 0x{start:X}-0x{end:X}, Color=#{color.R:X2}{color.G:X2}{color.B:X2}");

            _fileRegions.Add(new FileRegion
            {
                Start = start,
                End = end,
                TypeName = file.FileType,
                Color = color
            });

            occupiedRanges.Add((start, end, priority));
        }

        Debug.WriteLine(
            $"[BuildRegions] Built {_fileRegions.Count} regions, skipped {skippedOverlaps} overlapping regions");
    }

#pragma warning disable RCS1163 // Unused parameter - required for event handler signature
    private void VirtualScrollBar_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _currentTopRow = (long)e.NewValue;
        RenderVisibleRows();
        UpdateMinimapViewport();
        UpdatePositionIndicator();
    }
#pragma warning restore RCS1163

    private void UpdatePositionIndicator()
    {
        var offset = _currentTopRow * BytesPerRow;
        var endOffset = Math.Min(offset + _visibleRows * BytesPerRow, _fileSize);
        PositionIndicator.Text = $"Offset: 0x{offset:X8} - 0x{endOffset:X8} ({offset:N0} - {endOffset:N0})";
    }

#pragma warning disable RCS1163 // Unused parameter - required for event handler signature
    private void HexDisplayArea_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(HexDisplayArea).Properties.MouseWheelDelta;
        var rowDelta = delta > 0 ? -3 : 3; // Scroll 3 rows at a time

        var newValue = Math.Clamp(VirtualScrollBar.Value + rowDelta,
            VirtualScrollBar.Minimum,
            VirtualScrollBar.Maximum);

        if (Math.Abs(newValue - VirtualScrollBar.Value) > 0.0001)
        {
            VirtualScrollBar.Value = newValue;
            _currentTopRow = (long)newValue;
            RenderVisibleRows();
            UpdateMinimapViewport();
        }

        e.Handled = true;
    }
#pragma warning restore RCS1163

    private void RenderVisibleRows()
    {
        if (_fileSize == 0 || _filePath == null) return;

        HexTextBlock.Inlines.Clear();

        // Calculate byte range to read
        var startRow = _currentTopRow;
        var endRow = Math.Min(_currentTopRow + _visibleRows, _totalRows);
        var startOffset = startRow * BytesPerRow;
        var endOffset = Math.Min(endRow * BytesPerRow, _fileSize);
        var bytesToRead = (int)(endOffset - startOffset);

        if (bytesToRead <= 0) return;

        // Read the data
        var buffer = new byte[bytesToRead];
        ReadBytes(startOffset, buffer);

        // Build the display
        for (var row = startRow; row < endRow; row++)
        {
            var rowOffset = row * BytesPerRow;
            var bufferOffset = (int)(rowOffset - startOffset);
            var rowBytes = (int)Math.Min(BytesPerRow, _fileSize - rowOffset);

            if (rowBytes <= 0) break;

            // Offset column
            HexTextBlock.Inlines.Add(new Run
            {
                Text = $"{rowOffset:X8}  ",
                Foreground = OffsetBrush
            });

            // Hex bytes with per-byte coloring
            for (var i = 0; i < BytesPerRow; i++)
                if (i < rowBytes && bufferOffset + i < buffer.Length)
                {
                    var byteOffset = rowOffset + i;
                    var b = buffer[bufferOffset + i];
                    var region = FindRegionForOffset(byteOffset);

                    HexTextBlock.Inlines.Add(new Run
                    {
                        Text = $"{b:X2} ",
                        Foreground = region != null
                            ? new SolidColorBrush(region.Color)
                            : TextBrush
                    });
                }
                else
                {
                    HexTextBlock.Inlines.Add(new Run { Text = "   ", Foreground = TextBrush });
                }

            // Space before ASCII
            HexTextBlock.Inlines.Add(new Run { Text = " ", Foreground = TextBrush });

            // ASCII column
            var asciiBuilder = new StringBuilder(rowBytes);
            for (var i = 0; i < rowBytes && bufferOffset + i < buffer.Length; i++)
            {
                var b = buffer[bufferOffset + i];
                asciiBuilder.Append(b is >= 32 and < 127 ? (char)b : '.');
            }

            HexTextBlock.Inlines.Add(new Run { Text = asciiBuilder.ToString(), Foreground = AsciiBrush });

            // Newline (except for last row)
            if (row < endRow - 1) HexTextBlock.Inlines.Add(new Run { Text = "\n" });
        }
    }

    private void ReadBytes(long offset, byte[] buffer)
    {
        if (_accessor != null)
        {
            _accessor.ReadArray(offset, buffer, 0, buffer.Length);
        }
        else if (_filePath != null)
        {
            using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Seek(offset, SeekOrigin.Begin);
            fs.ReadExactly(buffer);
        }
    }

    private FileRegion? FindRegionForOffset(long offset)
    {
        // Binary search for the region containing offset
        // Regions are sorted by Start offset
        if (_fileRegions.Count == 0) return null;

        var left = 0;
        var right = _fileRegions.Count - 1;
        FileRegion? candidate = null;

        while (left <= right)
        {
            var mid = left + (right - left) / 2;
            var region = _fileRegions[mid];

            if (offset >= region.Start && offset < region.End) return region;

            if (region.Start > offset)
            {
                right = mid - 1;
            }
            else
            {
                // region.End <= offset, so check if this could be a partial match
                // and continue searching to the right
                candidate = region;
                left = mid + 1;
            }
        }

        // Check if candidate contains offset (edge case)
        return candidate != null && offset >= candidate.Start && offset < candidate.End ? candidate : null;
    }

    private sealed class FileRegion
    {
        public long Start { get; init; }
        public long End { get; init; }
        public required string TypeName { get; init; }
        public Color Color { get; init; }
    }

    #region Minimap

#pragma warning disable RCS1163 // Unused parameter - required for event handler signature
    private void MinimapContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Height - _lastMinimapContainerHeight) > 1)
        {
            _lastMinimapContainerHeight = e.NewSize.Height;
            if (_analysisResult != null)
            {
                RenderMinimap();
                UpdateMinimapViewport();
            }
        }
    }

    private void MinimapZoomSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _minimapZoom = e.NewValue;
        if (MinimapScrollViewer != null && _analysisResult != null)
        {
            RenderMinimap();
            UpdateMinimapViewport();
        }
    }
#pragma warning restore RCS1163

    private void RenderMinimap()
    {
        if (!TryInitializeMinimapCanvas(out var canvasWidth, out var canvasHeight)) return;

        AddMinimapBackground(canvasWidth, canvasHeight);
        RenderMinimapRegions(canvasWidth, canvasHeight);
        EnsureViewportIndicatorOnTop();
    }

    private bool TryInitializeMinimapCanvas(out double canvasWidth, out double canvasHeight)
    {
        canvasWidth = 0;
        canvasHeight = 0;

        if (MinimapCanvas == null || MinimapContainer == null) return false;

        MinimapCanvas.Children.Clear();
        MinimapCanvas.Children.Add(ViewportIndicator);

        if (_fileSize == 0)
        {
            ViewportIndicator.Visibility = Visibility.Collapsed;
            return false;
        }

        var containerWidth = MinimapContainer.ActualWidth - 8;
        var containerHeight = MinimapContainer.ActualHeight - 8;

        if (containerWidth <= 0 || containerHeight <= 0) return false;

        canvasWidth = Math.Max(20, containerWidth);
        canvasHeight = Math.Max(containerHeight, containerHeight * _minimapZoom);

        MinimapCanvas.Width = canvasWidth;
        MinimapCanvas.Height = canvasHeight;

        return true;
    }

    private void AddMinimapBackground(double canvasWidth, double canvasHeight)
    {
        var bg = new Rectangle
        {
            Width = canvasWidth,
            Height = canvasHeight,
            Fill = new SolidColorBrush(Color.FromArgb(255, 30, 30, 30))
        };
        MinimapCanvas.Children.Insert(0, bg);
    }

    private void RenderMinimapRegions(double canvasWidth, double canvasHeight)
    {
        // Group consecutive rows with the same color into rectangles for efficiency
        Color? currentColor = null;
        double currentStartY = 0;
        var defaultColor = Color.FromArgb(255, 60, 60, 60); // Dark gray for untyped areas

        for (double y = 0; y < canvasHeight; y++)
        {
            var fileOffset = (long)(y / canvasHeight * _fileSize);
            var region = FindRegionForOffset(fileOffset);
            var color = region?.Color ?? defaultColor;

            if (currentColor == null)
            {
                currentColor = color;
                currentStartY = y;
                continue;
            }

            if (color != currentColor.Value)
            {
                AddMinimapRectangle(canvasWidth, currentStartY, y - currentStartY, currentColor.Value);
                currentColor = color;
                currentStartY = y;
            }
        }

        // Draw the last rectangle
        if (currentColor != null)
            AddMinimapRectangle(canvasWidth, currentStartY, canvasHeight - currentStartY, currentColor.Value);
    }

    private void AddMinimapRectangle(double width, double top, double height, Color color)
    {
        var rect = new Rectangle
        {
            Width = width,
            Height = Math.Max(1, height),
            Fill = new SolidColorBrush(color)
        };
        Canvas.SetTop(rect, top);
        Canvas.SetLeft(rect, 0);
        MinimapCanvas.Children.Insert(MinimapCanvas.Children.Count - 1, rect);
    }

    private void EnsureViewportIndicatorOnTop()
    {
        MinimapCanvas.Children.Remove(ViewportIndicator);
        MinimapCanvas.Children.Add(ViewportIndicator);
    }

    private void UpdateMinimapViewport()
    {
        if (!TryGetMinimapDimensions(out var canvasWidth, out var canvasHeight)) return;

        var (minimapTop, minimapHeight) = CalculateViewportPosition(canvasHeight);
        PositionViewportIndicator(canvasWidth, canvasHeight, minimapTop, minimapHeight);
        AutoScrollMinimapIfZoomed(canvasHeight, minimapTop, minimapHeight);
    }

    private bool TryGetMinimapDimensions(out double canvasWidth, out double canvasHeight)
    {
        canvasWidth = 0;
        canvasHeight = 0;

        if (_fileSize == 0 || _totalRows == 0 || MinimapCanvas == null || ViewportIndicator == null)
        {
            if (ViewportIndicator != null) ViewportIndicator.Visibility = Visibility.Collapsed;
            return false;
        }

        canvasHeight = MinimapCanvas.Height;
        canvasWidth = MinimapCanvas.Width;

        return !double.IsNaN(canvasHeight) && canvasHeight > 0 &&
               !double.IsNaN(canvasWidth) && canvasWidth > 0;
    }

    private (double top, double height) CalculateViewportPosition(double canvasHeight)
    {
        var viewStartOffset = _currentTopRow * BytesPerRow;
        var viewEndOffset = Math.Min((_currentTopRow + _visibleRows) * BytesPerRow, _fileSize);

        var viewStartFraction = (double)viewStartOffset / _fileSize;
        var viewEndFraction = (double)viewEndOffset / _fileSize;
        var viewHeightFraction = viewEndFraction - viewStartFraction;

        var minimapTop = viewStartFraction * canvasHeight;
        var minimapHeight = Math.Max(12, viewHeightFraction * canvasHeight);

        // Clamp top position to keep indicator in bounds
        if (minimapTop + minimapHeight > canvasHeight) minimapTop = canvasHeight - minimapHeight;

        return (Math.Max(0, minimapTop), minimapHeight);
    }

    private void PositionViewportIndicator(double canvasWidth, double canvasHeight, double minimapTop,
        double minimapHeight)
    {
        ViewportIndicator.Width = Math.Max(10, canvasWidth - 4);
        ViewportIndicator.Height = minimapHeight;
        Canvas.SetLeft(ViewportIndicator, 2);
        Canvas.SetTop(ViewportIndicator, minimapTop);
        ViewportIndicator.Visibility = Visibility.Visible;

        // Debug logging
        var viewStartOffset = _currentTopRow * BytesPerRow;
        var region = FindRegionForOffset(viewStartOffset);
        Debug.WriteLine(
            $"[Minimap] Offset=0x{viewStartOffset:X8}, ViewHeight={minimapHeight:F1}px, Top={minimapTop:F1}/{canvasHeight:F1}, Region={region?.TypeName ?? "none"}");
    }

    private void AutoScrollMinimapIfZoomed(double canvasHeight, double minimapTop, double minimapHeight)
    {
        if (_minimapZoom <= 1 || MinimapScrollViewer == null) return;

        var minimapViewportHeight = MinimapScrollViewer.ViewportHeight;
        if (minimapViewportHeight > 0 && minimapViewportHeight < canvasHeight)
        {
            var targetY = minimapTop + minimapHeight / 2 - minimapViewportHeight / 2;
            targetY = Math.Clamp(targetY, 0, canvasHeight - minimapViewportHeight);
            MinimapScrollViewer.ChangeView(null, targetY, null, true);
        }
    }

#pragma warning disable RCS1163 // Unused parameter - required for event handler signature
    private void Minimap_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isDraggingMinimap = true;
        MinimapCanvas.CapturePointer(e.Pointer);
        NavigateToMinimapPosition(e.GetCurrentPoint(MinimapCanvas).Position.Y);
    }

    private void Minimap_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingMinimap) NavigateToMinimapPosition(e.GetCurrentPoint(MinimapCanvas).Position.Y);
    }

    private void Minimap_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isDraggingMinimap = false;
        MinimapCanvas.ReleasePointerCapture(e.Pointer);
    }
#pragma warning restore RCS1163

    private void NavigateToMinimapPosition(double y)
    {
        var canvasHeight = MinimapCanvas.Height;
        if (canvasHeight <= 0 || _totalRows == 0) return;

        // Calculate target row (center viewport on click)
        var fraction = Math.Clamp(y / canvasHeight, 0, 1);
        var targetRow = (long)(fraction * _totalRows) - _visibleRows / 2;
        targetRow = Math.Clamp(targetRow, 0, Math.Max(0, _totalRows - _visibleRows));

        _currentTopRow = targetRow;
        VirtualScrollBar.Value = targetRow;
        RenderVisibleRows();
        UpdateMinimapViewport();
    }

    #endregion
}
