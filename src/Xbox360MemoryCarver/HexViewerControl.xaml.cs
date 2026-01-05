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
/// </summary>
public sealed partial class HexViewerControl : UserControl, IDisposable
{
    private const int BytesPerRow = 16;

    private readonly HexDataManager _dataManager = new();
    private long _currentTopRow;
    private bool _disposed;
    private bool _hasData;
    private double _lastMinimapContainerHeight;
    private HexMinimapRenderer? _minimapRenderer;
    private double _rowHeight;
    private HexRowRenderer? _rowRenderer;
    private HexSearcher? _searcher;
    private long _totalRows;
    private int _visibleRows;

    public HexViewerControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        BuildLegend();
        HexDisplayArea.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(HexDisplayArea_PointerWheelChanged),
            true);
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
        _dataManager.Dispose();
    }

    private void BuildLegend()
    {
        foreach (var category in FileTypeColors.LegendCategories)
        {
            var itemPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            itemPanel.Children.Add(new Border
            {
                Width = 12,
                Height = 12,
                Background = new SolidColorBrush(category.Color),
                CornerRadius = new CornerRadius(2)
            });
            itemPanel.Children.Add(new TextBlock
            {
                Text = category.Name,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 204, 204, 204))
            });
            LegendPanel.Children.Add(itemPanel);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _minimapRenderer = new HexMinimapRenderer(MinimapCanvas, ViewportIndicator, MinimapScrollViewer,
            _dataManager.FindRegionForOffset);
        _rowRenderer =
            new HexRowRenderer(OffsetTextBlock, HexTextBlock, AsciiTextBlock, _dataManager.FindRegionForOffset);
        _searcher = new HexSearcher(() => _dataManager.Accessor, () => _dataManager.FileSize);
        DispatcherQueue.TryEnqueue(() =>
        {
            CalculateRowHeight();
            UpdateVisibleRowCount();
            if (_hasData) RenderAll();
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
            if (_hasData) RenderVisibleRows();
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

    public void Clear()
    {
        _dataManager.Clear();
        _hasData = false;
        _currentTopRow = _totalRows = 0;
        _rowRenderer?.Clear();
        MinimapCanvas.Children.Clear();
        ViewportIndicator.Visibility = Visibility.Collapsed;
        VirtualScrollBar.Maximum = VirtualScrollBar.Value = 0;
    }

    public void NavigateToOffset(long offset)
    {
        if (_dataManager.FileSize == 0 || _totalRows == 0) return;
        var targetRow = Math.Clamp(offset / BytesPerRow, 0, Math.Max(0, _totalRows - _visibleRows));
        _currentTopRow = targetRow;
        VirtualScrollBar.Value = targetRow;
        RenderVisibleRows();
        UpdateMinimapViewport();
    }

    public void LoadData(string filePath, AnalysisResult analysisResult)
    {
        _dataManager.Load(filePath, analysisResult);
        _hasData = true;
        _totalRows = (_dataManager.FileSize + BytesPerRow - 1) / BytesPerRow;
        _currentTopRow = 0;
        if (_rowHeight <= 0) CalculateRowHeight();
        UpdateVisibleRowCount();
        VirtualScrollBar.Minimum = 0;
        VirtualScrollBar.Maximum = Math.Max(0, _totalRows - _visibleRows);
        VirtualScrollBar.Value = 0;
        RenderAll();
        DispatcherQueue.TryEnqueue(UpdateMinimapViewport);
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
        var endOffset = Math.Min(offset + _visibleRows * BytesPerRow, _dataManager.FileSize);
        PositionIndicator.Text = $"Offset: 0x{offset:X8} - 0x{endOffset:X8} ({offset:N0} - {endOffset:N0})";
    }

    private void RenderAll()
    {
        RenderVisibleRows();
        RenderMinimap();
        UpdateMinimapViewport();
    }

    private void RenderVisibleRows()
    {
        if (_dataManager.FileSize == 0 || _rowRenderer == null) return;
        var endRow = Math.Min(_currentTopRow + _visibleRows, _totalRows);
        var startOffset = _currentTopRow * BytesPerRow;
        var bytesToRead = (int)(Math.Min(endRow * BytesPerRow, _dataManager.FileSize) - startOffset);
        if (bytesToRead <= 0)
        {
            _rowRenderer.Clear();
            return;
        }

        var buffer = new byte[bytesToRead];
        _dataManager.ReadBytes(startOffset, buffer);
        _rowRenderer.RenderRows(buffer, _currentTopRow, endRow, startOffset, _dataManager.FileSize);
    }

    private void ScrollByRows(long delta)
    {
        var newVal = Math.Clamp(_currentTopRow + delta, 0, (long)VirtualScrollBar.Maximum);
        if (newVal != _currentTopRow)
        {
            _currentTopRow = newVal;
            VirtualScrollBar.Value = newVal;
            RenderVisibleRows();
            UpdateMinimapViewport();
            UpdatePositionIndicator();
        }
    }

    private void ScrollToRow(long row)
    {
        var newVal = Math.Clamp(row, 0, (long)VirtualScrollBar.Maximum);
        if (newVal != _currentTopRow)
        {
            _currentTopRow = newVal;
            VirtualScrollBar.Value = newVal;
            RenderVisibleRows();
            UpdateMinimapViewport();
            UpdatePositionIndicator();
        }
    }

    #region Input Handling

    private void HexDisplayArea_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var isCtrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);
        ScrollByRows((e.GetCurrentPoint(HexDisplayArea).Properties.MouseWheelDelta > 0 ? -1 : 1) * (isCtrl ? 10 : 1));
        e.Handled = true;
    }

    private void HexViewerControl_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var isCtrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);
        if (isCtrl && e.Key == VirtualKey.F)
        {
            OpenSearch();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.F3)
        {
            if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down))
                FindPrevious();
            else FindNext();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Escape && SearchPanel.Visibility == Visibility.Visible)
        {
            CloseSearch();
            e.Handled = true;
            return;
        }

        if (e.Key is VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.Home or VirtualKey.End or VirtualKey.Up
            or VirtualKey.Down) HexViewerControl_KeyDown(sender, e);
    }

    private void HexViewerControl_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.PageUp: ScrollByRows(-_visibleRows); break;
            case VirtualKey.PageDown: ScrollByRows(_visibleRows); break;
            case VirtualKey.Home: ScrollToRow(0); break;
            case VirtualKey.End: ScrollToRow(Math.Max(0, _totalRows - _visibleRows)); break;
            case VirtualKey.Up: ScrollByRows(-1); break;
            case VirtualKey.Down: ScrollByRows(1); break;
            default: return;
        }

        e.Handled = true;
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

    private static void CopySelectedText(TextBlock tb)
    {
        var text = tb.SelectedText;
        if (!string.IsNullOrEmpty(text))
            ClipboardHelper.CopyText(tb.Name.Contains("Ascii") ? text.Replace("\n", "").Replace("\r", "") : text);
    }

    #endregion

    #region Minimap

    private void MinimapContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Height - _lastMinimapContainerHeight) > 1)
        {
            _lastMinimapContainerHeight = e.NewSize.Height;
            if (_hasData) RenderAll();
        }
    }

    private void MinimapZoomSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_minimapRenderer != null)
        {
            _minimapRenderer.Zoom = e.NewValue;
            if (_hasData) RenderAll();
        }
    }

    private void RenderMinimap() => _minimapRenderer?.Render(_dataManager.FileSize, MinimapContainer.ActualWidth - 8,
        MinimapContainer.ActualHeight - 8);

    private void UpdateMinimapViewport() => _minimapRenderer?.UpdateViewport(_dataManager.FileSize, _totalRows,
        _currentTopRow, _visibleRows, BytesPerRow);

    private void Minimap_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var y = e.GetCurrentPoint(MinimapCanvas).Position.Y;
        _minimapRenderer?.HandlePointerPressed(e, y);
        NavigateToMinimapPosition(y);
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
        var row = _minimapRenderer?.GetRowFromPositionWithOffset(y, _totalRows, _visibleRows);
        if (row.HasValue)
        {
            _currentTopRow = row.Value;
            VirtualScrollBar.Value = row.Value;
            RenderVisibleRows();
            UpdateMinimapViewport();
        }
    }

    #endregion

    #region Search

    private void SearchToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (SearchPanel.Visibility == Visibility.Collapsed) OpenSearch();
        else CloseSearch();
    }

    private void OpenSearch()
    {
        SearchPanel.Visibility = Visibility.Visible;
        OffsetPanel.CornerRadius = new CornerRadius(0, 0, 4, 4);
        SearchToggleButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0x88, 0x88, 0x88));
        SearchTextBox.Focus(FocusState.Programmatic);
        SearchTextBox.SelectAll();
    }

    private void CloseSearch()
    {
        SearchPanel.Visibility = Visibility.Collapsed;
        OffsetPanel.CornerRadius = new CornerRadius(4);
        SearchToggleButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x00, 0x00, 0x00, 0x00));
        _searcher?.Clear();
        SearchResultsText.Text = "";
        if (_rowRenderer != null)
        {
            _rowRenderer.HighlightStart = -1;
            _rowRenderer.HighlightEnd = -1;
            RenderVisibleRows();
        }

        Focus(FocusState.Programmatic);
    }

    private void SearchTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            var isShift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
                .HasFlag(CoreVirtualKeyStates.Down);
            if (isShift) FindPrevious();
            else PerformSearch();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            CloseSearch();
            e.Handled = true;
        }
    }

    private void SearchPrevButton_Click(object sender, RoutedEventArgs e) => FindPrevious();
    private void SearchNextButton_Click(object sender, RoutedEventArgs e) => FindNext();
    private void SearchButton_Click(object sender, RoutedEventArgs e) => PerformSearch();

    private void PerformSearch()
    {
        if (_searcher == null) return;
        var result = _searcher.Search(SearchTextBox.Text, SearchModeHex.IsChecked == true,
            SearchCaseSensitive.IsChecked == true);
        SearchResultsText.Text = result.IsInvalidHex ? "Invalid hex" : _searcher.GetResultsText();
        if (result.HasResults && result.MatchOffset.HasValue) NavigateToSearchResult(result.MatchOffset.Value);
    }

    private void FindNext()
    {
        if (_searcher == null) return;
        if (_searcher.SearchResults.Count == 0 && _searcher.LastSearchPattern != null)
        {
            PerformSearch();
            return;
        }

        var offset = _searcher.FindNext();
        if (offset.HasValue)
        {
            SearchResultsText.Text = _searcher.GetResultsText();
            NavigateToSearchResult(offset.Value);
        }
    }

    private void FindPrevious()
    {
        if (_searcher == null) return;
        if (_searcher.SearchResults.Count == 0 && _searcher.LastSearchPattern != null)
        {
            PerformSearch();
            return;
        }

        var offset = _searcher.FindPrevious();
        if (offset.HasValue)
        {
            SearchResultsText.Text = _searcher.GetResultsText();
            NavigateToSearchResult(offset.Value);
        }
    }

    private void NavigateToSearchResult(long offset)
    {
        if (_rowRenderer != null && _searcher?.LastSearchPattern != null)
        {
            _rowRenderer.HighlightStart = offset;
            _rowRenderer.HighlightEnd = offset + _searcher.LastSearchPattern.Length;
        }

        NavigateToOffset(offset);
    }

    #endregion
}
