using System.Diagnostics;
using System.Globalization;
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

    // Dynamic row height
    private double _rowHeight;

    public HexViewerControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        // Build the legend from the shared color definitions
        BuildLegend();

        // Intercept wheel events on the display area
        HexDisplayArea.AddHandler(
            PointerWheelChangedEvent,
            new PointerEventHandler(HexDisplayArea_PointerWheelChanged),
            true);

        // Handle keyboard navigation
        KeyDown += HexViewerControl_KeyDown;
        PreviewKeyDown += HexViewerControl_PreviewKeyDown;
        IsTabStop = true;

        // Handle copy with Ctrl+C to avoid clipboard COM issues
        HexTextBlock.KeyDown += TextBlock_KeyDown;
        AsciiTextBlock.KeyDown += TextBlock_KeyDown;
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
            var itemPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

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
            CalculateRowHeight();
            UpdateVisibleRowCount();
            if (_analysisResult != null)
            {
                RenderVisibleRows();
                RenderMinimap();
                UpdateMinimapViewport();
            }
        });
    }

    private void CalculateRowHeight()
    {
        var lineHeight = HexTextBlock.FontSize * 1.2;

        // Set line height on all text blocks for consistent alignment
        OffsetTextBlock.LineHeight = lineHeight;
        OffsetTextBlock.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;

        HexTextBlock.LineHeight = lineHeight;
        HexTextBlock.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;

        AsciiTextBlock.LineHeight = lineHeight;
        AsciiTextBlock.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;

        _rowHeight = lineHeight;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();

    private void HexDisplayArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Only re-render if height changed meaningfully
        if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 1)
        {
            UpdateVisibleRowCount();
            if (_analysisResult != null)
            {
                RenderVisibleRows();
            }
        }
    }

    private void UpdateVisibleRowCount()
    {
        // If row height is not yet determined, calculate it based on font size
        if (_rowHeight <= 0)
        {
            CalculateRowHeight();
        }

        // Account for Border padding (4px top + 4px bottom = 8px)
        var displayHeight = HexDisplayArea.ActualHeight - 8;
        _visibleRows = displayHeight > 0 ? (int)Math.Floor(displayHeight / _rowHeight) : 30;

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
        OffsetTextBlock.Text = "";
        HexTextBlock.Inlines.Clear();
        AsciiTextBlock.Text = "";
        MinimapCanvas.Children.Clear();
        ViewportIndicator.Visibility = Visibility.Collapsed;
        VirtualScrollBar.Maximum = 0;
        VirtualScrollBar.Value = 0;
    }

    /// <summary>
    ///     Navigate to a specific byte offset in the file.
    ///     Shows the offset at the top of the view.
    /// </summary>
    public void NavigateToOffset(long offset)
    {
        if (_fileSize == 0 || _totalRows == 0) return;

        // Calculate the row for this offset - show at top of view
        var targetRow = offset / BytesPerRow;
        targetRow = Math.Clamp(targetRow, 0, Math.Max(0, _totalRows - _visibleRows));

        _currentTopRow = targetRow;
        VirtualScrollBar.Value = targetRow;
        RenderVisibleRows();
        UpdateMinimapViewport();
    }

    public void LoadData(string filePath, AnalysisResult analysisResult)
    {
        CleanupMemoryMapping();

        _filePath = filePath;
        _analysisResult = analysisResult;

        _fileSize = new FileInfo(filePath).Length;

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

        _totalRows = (_fileSize + BytesPerRow - 1) / BytesPerRow;
        _currentTopRow = 0;

        if (_rowHeight <= 0)
        {
            CalculateRowHeight();
        }

        // Setup scrollbar - ensure it has a valid range
        UpdateVisibleRowCount();
        var scrollMax = Math.Max(0, _totalRows - _visibleRows);

        // Must set Maximum before Value, and ensure Maximum >= Minimum
        VirtualScrollBar.Minimum = 0;
        VirtualScrollBar.Maximum = Math.Max(0, scrollMax);
        VirtualScrollBar.Value = 0;

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

    private void HexDisplayArea_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var isCtrlPressed = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        // Vertical scrolling
        var delta = e.GetCurrentPoint(HexDisplayArea).Properties.MouseWheelDelta;
        var baseRows = isCtrlPressed ? 10 : 1;
        var rowDelta = delta > 0 ? -baseRows : baseRows;

        ScrollByRows(rowDelta);

        e.Handled = true;
    }

    private void HexViewerControl_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Handle navigation keys before they reach the ScrollViewer
        switch (e.Key)
        {
            case Windows.System.VirtualKey.PageUp:
            case Windows.System.VirtualKey.PageDown:
            case Windows.System.VirtualKey.Home:
            case Windows.System.VirtualKey.End:
            case Windows.System.VirtualKey.Up:
            case Windows.System.VirtualKey.Down:
                HexViewerControl_KeyDown(sender, e);
                break;
        }
    }

    private void HexViewerControl_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.PageUp:
                ScrollByRows(-_visibleRows);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.PageDown:
                ScrollByRows(_visibleRows);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Home:
                ScrollToRow(0);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.End:
                ScrollToRow(Math.Max(0, _totalRows - _visibleRows));
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Up:
                ScrollByRows(-1);
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Down:
                ScrollByRows(1);
                e.Handled = true;
                break;
        }
    }

    private static void CopyTextToClipboardViaInterop(string text)
    {
        try
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    var hGlobal = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(text + '\0');
                    if (SetClipboardData(13, hGlobal) == IntPtr.Zero) // CF_UNICODETEXT = 13
                    {
                        // SetClipboardData failed, free the memory
                        System.Runtime.InteropServices.Marshal.FreeHGlobal(hGlobal);
                        Debug.WriteLine("[HexViewer] SetClipboardData failed");
                    }
                    else
                    {
                        Debug.WriteLine($"[HexViewer] Copied {text.Length} characters to clipboard");
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }
            else
            {
                Debug.WriteLine("[HexViewer] OpenClipboard failed");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HexViewer] Interop copy failed: {ex.Message}");
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
 
    private void CopyHexMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CopySelectedTextToClipboard(HexTextBlock);
    }

    private void CopyAsciiMenuItem_Click(object sender, RoutedEventArgs e)
    {
        CopySelectedTextToClipboard(AsciiTextBlock);
    }

    private void TextBlock_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Handle Ctrl+C for copy
        if (e.Key == Windows.System.VirtualKey.C)
        {
            var isCtrlPressed = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (isCtrlPressed && sender is TextBlock textBlock)
            {
                e.Handled = true;
                CopySelectedTextToClipboard(textBlock);
            }
        }
    }

    private void CopySelectedTextToClipboard(TextBlock textBlock)
    {
        try
        {
            var selectedText = textBlock.SelectedText;
            Debug.WriteLine($"[HexViewer] Attempting copy, SelectedText length: {selectedText?.Length ?? 0}");

            if (string.IsNullOrEmpty(selectedText))
            {
                Debug.WriteLine("[HexViewer] No text selected");
                return;
            }

            // For ASCII column, remove artificial line breaks to get continuous text
            // For Hex column, preserve formatting (spaces between bytes)
            string textToCopy;
            if (textBlock == AsciiTextBlock)
            {
                // Remove newlines to get continuous ASCII string
                textToCopy = selectedText.Replace("\n", "").Replace("\r", "");
            }
            else
            {
                // For hex, keep as-is (user might want the formatting)
                textToCopy = selectedText;
            }

            CopyTextToClipboardViaInterop(textToCopy);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HexViewer] Copy failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ScrollByRows(long rowDelta)
    {
        var newValue = Math.Clamp(_currentTopRow + rowDelta, 0, (long)VirtualScrollBar.Maximum);

        if (newValue != _currentTopRow)
        {
            _currentTopRow = newValue;
            VirtualScrollBar.Value = newValue;
            RenderVisibleRows();
            UpdateMinimapViewport();
            UpdatePositionIndicator();
        }
    }

    private void ScrollToRow(long row)
    {
        var newValue = Math.Clamp(row, 0, (long)VirtualScrollBar.Maximum);

        if (newValue != _currentTopRow)
        {
            _currentTopRow = newValue;
            VirtualScrollBar.Value = newValue;
            RenderVisibleRows();
            UpdateMinimapViewport();
            UpdatePositionIndicator();
        }
    }

    private void RenderVisibleRows()
    {
        if (_fileSize == 0 || _filePath == null) return;

        // Clear all three columns
        OffsetTextBlock.Text = "";
        HexTextBlock.Inlines.Clear();
        AsciiTextBlock.Text = "";

        var startRow = _currentTopRow;
        var endRow = Math.Min(_currentTopRow + _visibleRows, _totalRows);
        var startOffset = startRow * BytesPerRow;
        var endOffset = Math.Min(endRow * BytesPerRow, _fileSize);
        var bytesToRead = (int)(endOffset - startOffset);

        if (bytesToRead <= 0) return;

        var buffer = new byte[bytesToRead];
        ReadBytes(startOffset, buffer);

        var offsetBuilder = new StringBuilder();
        var asciiBuilder = new StringBuilder();

        for (var row = startRow; row < endRow; row++)
        {
            var rowOffset = row * BytesPerRow;
            var bufferOffset = (int)(rowOffset - startOffset);
            var rowBytes = (int)Math.Min(BytesPerRow, _fileSize - rowOffset);
            if (rowBytes <= 0) break;

            // Offset column
            offsetBuilder.Append(CultureInfo.InvariantCulture, $"{rowOffset:X8}");
            if (row < endRow - 1) offsetBuilder.Append('\n');

            // Hex bytes with per-byte coloring
            for (var i = 0; i < BytesPerRow; i++)
            {
                if (i < rowBytes && bufferOffset + i < buffer.Length)
                {
                    var byteOffset = rowOffset + i;
                    var b = buffer[bufferOffset + i];
                    var region = FindRegionForOffset(byteOffset);
                    HexTextBlock.Inlines.Add(new Run
                    {
                        Text = $"{b:X2} ",
                        Foreground = region != null ? new SolidColorBrush(region.Color) : TextBrush
                    });
                }
                else
                {
                    HexTextBlock.Inlines.Add(new Run { Text = "   ", Foreground = TextBrush });
                }
            }

            // ASCII column
            for (var i = 0; i < rowBytes && bufferOffset + i < buffer.Length; i++)
            {
                var b = buffer[bufferOffset + i];
                asciiBuilder.Append(b is >= 32 and < 127 ? (char)b : '.');
            }

            // Newline (except for last row)
            if (row < endRow - 1)
            {
                HexTextBlock.Inlines.Add(new Run { Text = "\n" });
                asciiBuilder.Append('\n');
            }
        }

        OffsetTextBlock.Text = offsetBuilder.ToString();
        AsciiTextBlock.Text = asciiBuilder.ToString();
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
        if (_fileRegions.Count == 0) return null;
        int left = 0, right = _fileRegions.Count - 1;
        while (left <= right)
        {
            var mid = left + (right - left) / 2;
            var region = _fileRegions[mid];
            if (offset >= region.Start && offset < region.End) return region;
            if (region.Start > offset) right = mid - 1;
            else left = mid + 1;
        }
        return null;
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
        if (MinimapCanvas == null || MinimapContainer == null) return;
        MinimapCanvas.Children.Clear();
        MinimapCanvas.Children.Add(ViewportIndicator);

        if (_fileSize == 0)
        {
            ViewportIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        var containerWidth = MinimapContainer.ActualWidth - 8;
        var containerHeight = MinimapContainer.ActualHeight - 8;
        if (containerWidth <= 0 || containerHeight <= 0) return;

        var canvasWidth = Math.Max(20, containerWidth);
        var canvasHeight = Math.Max(containerHeight, containerHeight * _minimapZoom);
        MinimapCanvas.Width = canvasWidth;
        MinimapCanvas.Height = canvasHeight;

        var bg = new Rectangle
        {
            Width = canvasWidth,
            Height = canvasHeight,
            Fill = new SolidColorBrush(Color.FromArgb(255, 30, 30, 30))
        };
        MinimapCanvas.Children.Insert(0, bg);

        Color? currentColor = null;
        double currentStartY = 0;
        var defaultColor = Color.FromArgb(255, 60, 60, 60);

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
                AddMinimapRect(canvasWidth, currentStartY, y - currentStartY, currentColor.Value);
                currentColor = color;
                currentStartY = y;
            }
        }

        if (currentColor != null)
        {
            AddMinimapRect(canvasWidth, currentStartY, canvasHeight - currentStartY, currentColor.Value);
        }

        MinimapCanvas.Children.Remove(ViewportIndicator);
        MinimapCanvas.Children.Add(ViewportIndicator);
    }

    private void AddMinimapRect(double width, double top, double height, Color color)
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

    private void UpdateMinimapViewport()
    {
        if (_fileSize == 0 || _totalRows == 0 || MinimapCanvas == null || ViewportIndicator == null)
        {
            if (ViewportIndicator != null) ViewportIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        var canvasHeight = MinimapCanvas.Height;
        var canvasWidth = MinimapCanvas.Width;
        if (double.IsNaN(canvasHeight) || canvasHeight <= 0) return;

        var viewStartOffset = _currentTopRow * BytesPerRow;
        var viewEndOffset = Math.Min((_currentTopRow + _visibleRows) * BytesPerRow, _fileSize);
        var viewCenterFraction = ((double)viewStartOffset / _fileSize + (double)viewEndOffset / _fileSize) / 2;
        var viewHeightFraction = (double)(viewEndOffset - viewStartOffset) / _fileSize;

        var minimapHeight = Math.Max(12, viewHeightFraction * canvasHeight);
        var minimapTop = viewCenterFraction * canvasHeight - minimapHeight / 2;
        minimapTop = Math.Clamp(minimapTop, 0, canvasHeight - minimapHeight);

        ViewportIndicator.Width = Math.Max(10, canvasWidth - 4);
        ViewportIndicator.Height = minimapHeight;
        Canvas.SetLeft(ViewportIndicator, 2);
        Canvas.SetTop(ViewportIndicator, minimapTop);
        ViewportIndicator.Visibility = Visibility.Visible;

        if (_minimapZoom > 1 && MinimapScrollViewer != null)
        {
            var viewportHeight = MinimapScrollViewer.ViewportHeight;
            if (viewportHeight > 0 && viewportHeight < canvasHeight)
            {
                var targetY = Math.Clamp(minimapTop + minimapHeight / 2 - viewportHeight / 2,
                    0, canvasHeight - viewportHeight);
                MinimapScrollViewer.ChangeView(null, targetY, null, true);
            }
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
        if (_isDraggingMinimap)
        {
            NavigateToMinimapPosition(e.GetCurrentPoint(MinimapCanvas).Position.Y);
        }
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
