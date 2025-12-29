using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Xbox360MemoryCarver.App.HexViewer;
using Xbox360MemoryCarver.Core;

namespace Xbox360MemoryCarver.App;

/// <summary>
///     High-performance hex viewer with virtual scrolling.
///     Only renders visible rows - true lazy loading.
/// </summary>
public sealed partial class HexViewerControl : UserControl, IDisposable
{
    private const int BytesPerRow = 16;

    private readonly List<FileRegion> _fileRegions = [];
    private MemoryMappedViewAccessor? _accessor;
    private AnalysisResult? _analysisResult;
    private long _currentTopRow;
    private bool _disposed;
    private string? _filePath;
    private long _fileSize;
    private double _lastMinimapContainerHeight;
    private HexMinimapRenderer? _minimapRenderer;
    private MemoryMappedFile? _mmf;
    private double _rowHeight;
    private HexRowRenderer? _rowRenderer;
    private long _totalRows;
    private int _visibleRows;

    public HexViewerControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        BuildLegend();

        HexDisplayArea.AddHandler(PointerWheelChangedEvent,
            new PointerEventHandler(HexDisplayArea_PointerWheelChanged), true);

        KeyDown += HexViewerControl_KeyDown;
        PreviewKeyDown += HexViewerControl_PreviewKeyDown;
        IsTabStop = true;
        HexTextBlock.KeyDown += TextBlock_KeyDown;
        AsciiTextBlock.KeyDown += TextBlock_KeyDown;
    }

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
            itemPanel.Children.Add(new Border
            {
                Width = 12, Height = 12,
                Background = new SolidColorBrush(category.Color),
                CornerRadius = new CornerRadius(2)
            });
            itemPanel.Children.Add(new TextBlock
            {
                Text = category.Name, FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 204, 204, 204))
            });
            LegendPanel.Children.Add(itemPanel);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _minimapRenderer =
            new HexMinimapRenderer(MinimapCanvas, ViewportIndicator, MinimapScrollViewer, FindRegionForOffset);
        _rowRenderer = new HexRowRenderer(OffsetTextBlock, HexTextBlock, AsciiTextBlock, FindRegionForOffset);

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
        OffsetTextBlock.LineHeight = HexTextBlock.LineHeight = AsciiTextBlock.LineHeight = lineHeight;
        OffsetTextBlock.LineStackingStrategy = HexTextBlock.LineStackingStrategy =
            AsciiTextBlock.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _rowHeight = lineHeight;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();

    private void HexDisplayArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 1)
        {
            UpdateVisibleRowCount();
            if (_analysisResult != null) RenderVisibleRows();
        }
    }

    private void UpdateVisibleRowCount()
    {
        if (_rowHeight <= 0) CalculateRowHeight();
        _visibleRows = HexDisplayArea.ActualHeight - 8 > 0
            ? (int)Math.Floor((HexDisplayArea.ActualHeight - 8) / _rowHeight)
            : 30;
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
        _rowRenderer?.Clear();
        MinimapCanvas.Children.Clear();
        ViewportIndicator.Visibility = Visibility.Collapsed;
        VirtualScrollBar.Maximum = VirtualScrollBar.Value = 0;
    }

    public void NavigateToOffset(long offset)
    {
        if (_fileSize == 0 || _totalRows == 0) return;
        var targetRow = Math.Clamp(offset / BytesPerRow, 0, Math.Max(0, _totalRows - _visibleRows));
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
        if (_rowHeight <= 0) CalculateRowHeight();
        UpdateVisibleRowCount();
        VirtualScrollBar.Minimum = 0;
        VirtualScrollBar.Maximum = Math.Max(0, _totalRows - _visibleRows);
        VirtualScrollBar.Value = 0;
        RenderVisibleRows();
        RenderMinimap();
        DispatcherQueue.TryEnqueue(UpdateMinimapViewport);
    }

    private void BuildFileRegions()
    {
        _fileRegions.Clear();
        if (_analysisResult == null) return;

        var sortedFiles = _analysisResult.CarvedFiles.Where(f => f.Length > 0)
            .OrderBy(f => f.Offset).ThenBy(f => FileTypeColors.GetPriority(f.FileType)).ToList();

        var occupiedRanges = new List<(long Start, long End, int Priority)>();
        foreach (var file in sortedFiles)
        {
            var start = file.Offset;
            var end = file.Offset + file.Length;
            var priority = FileTypeColors.GetPriority(file.FileType);

            if (occupiedRanges.Any(r => start < r.End && end > r.Start && r.Priority <= priority))
            {
                continue;
            }

            _fileRegions.Add(new FileRegion
            {
                Start = start,
                End = end,
                TypeName = file.FileType,
                Color = FileTypeColors.GetColor(file.FileType)
            });
            occupiedRanges.Add((start, end, priority));
        }

        // Sort regions by start offset for binary search
        _fileRegions.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    private void VirtualScrollBar_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _currentTopRow = (long)e.NewValue;
        RenderVisibleRows();
        UpdateMinimapViewport();
        UpdatePositionIndicator();
    }

    private void UpdatePositionIndicator()
    {
        var offset = _currentTopRow * BytesPerRow;
        var endOffset = Math.Min(offset + _visibleRows * BytesPerRow, _fileSize);
        PositionIndicator.Text = $"Offset: 0x{offset:X8} - 0x{endOffset:X8} ({offset:N0} - {endOffset:N0})";
    }

    private void HexDisplayArea_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var isCtrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);
        var delta = e.GetCurrentPoint(HexDisplayArea).Properties.MouseWheelDelta;
        var scrollAmount = isCtrl ? 10 : 1;
        ScrollByRows(delta > 0 ? -scrollAmount : scrollAmount);
        e.Handled = true;
    }

    private void HexViewerControl_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End or VirtualKey.Up
            or VirtualKey.Down)
            HexViewerControl_KeyDown(sender, e);
    }

    private void HexViewerControl_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.PageUp:
                ScrollByRows(-_visibleRows);
                e.Handled = true;
                break;
            case VirtualKey.PageDown:
                ScrollByRows(_visibleRows);
                e.Handled = true;
                break;
            case VirtualKey.Home:
                ScrollToRow(0);
                e.Handled = true;
                break;
            case VirtualKey.End:
                ScrollToRow(Math.Max(0, _totalRows - _visibleRows));
                e.Handled = true;
                break;
            case VirtualKey.Up:
                ScrollByRows(-1);
                e.Handled = true;
                break;
            case VirtualKey.Down:
                ScrollByRows(1);
                e.Handled = true;
                break;
        }
    }

    private void CopyHexMenuItem_Click(object sender, RoutedEventArgs e) => CopySelectedText(HexTextBlock);
    private void CopyAsciiMenuItem_Click(object sender, RoutedEventArgs e) => CopySelectedText(AsciiTextBlock);

    private static void TextBlock_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.C &&
            InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down) &&
            sender is TextBlock tb)
        {
            e.Handled = true;
            CopySelectedText(tb);
        }
    }

    private static void CopySelectedText(TextBlock textBlock)
    {
        var text = textBlock.SelectedText;
        if (!string.IsNullOrEmpty(text))
            ClipboardHelper.CopyText(textBlock.Name.Contains("Ascii")
                ? text.Replace("\n", "").Replace("\r", "")
                : text);
    }

    private void ScrollByRows(long delta)
    {
        var newValue = Math.Clamp(_currentTopRow + delta, 0, (long)VirtualScrollBar.Maximum);
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
        if (_fileSize == 0 || _filePath == null || _rowRenderer == null) return;

        var endRow = Math.Min(_currentTopRow + _visibleRows, _totalRows);
        var startOffset = _currentTopRow * BytesPerRow;
        var endOffset = Math.Min(endRow * BytesPerRow, _fileSize);
        var bytesToRead = (int)(endOffset - startOffset);
        if (bytesToRead <= 0)
        {
            _rowRenderer.Clear();
            return;
        }

        var buffer = new byte[bytesToRead];
        ReadBytes(startOffset, buffer);
        _rowRenderer.RenderRows(buffer, _currentTopRow, endRow, startOffset, _fileSize);
    }

    private void ReadBytes(long offset, byte[] buffer)
    {
        if (_accessor != null) _accessor.ReadArray(offset, buffer, 0, buffer.Length);
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

    #region Minimap

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
        if (_minimapRenderer != null)
        {
            _minimapRenderer.Zoom = e.NewValue;
            if (_analysisResult != null)
            {
                RenderMinimap();
                UpdateMinimapViewport();
            }
        }
    }

    private void RenderMinimap() =>
        _minimapRenderer?.Render(_fileSize, MinimapContainer.ActualWidth - 8, MinimapContainer.ActualHeight - 8);

    private void UpdateMinimapViewport() =>
        _minimapRenderer?.UpdateViewport(_fileSize, _totalRows, _currentTopRow, _visibleRows, BytesPerRow);

    private void Minimap_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _minimapRenderer?.HandlePointerPressed(e);
        NavigateToMinimapPosition(e.GetCurrentPoint(MinimapCanvas).Position.Y);
    }

    private void Minimap_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_minimapRenderer?.IsDragging == true)
            NavigateToMinimapPosition(e.GetCurrentPoint(MinimapCanvas).Position.Y);
    }

    private void Minimap_PointerReleased(object sender, PointerRoutedEventArgs e) =>
        _minimapRenderer?.HandlePointerReleased(e);

    private void NavigateToMinimapPosition(double y)
    {
        var targetRow = _minimapRenderer?.GetRowFromPosition(y, _totalRows, _visibleRows);
        if (targetRow.HasValue)
        {
            _currentTopRow = targetRow.Value;
            VirtualScrollBar.Value = targetRow.Value;
            RenderVisibleRows();
            UpdateMinimapViewport();
        }
    }

    #endregion
}
