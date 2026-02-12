using Windows.Storage.Pickers;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.RuntimeBuffer;
using FalloutXbox360Utils.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;

namespace FalloutXbox360Utils;

/// <summary>
///     File operations: Save*, Export*, Load*, file handling, report generation
/// </summary>
public sealed partial class SingleFileTab
{
    #region Report Generation

    private async Task GenerateReportsAsync()
    {
        ReportsProgressBar.Visibility = Visibility.Visible;
        ReportsProgressBar.IsIndeterminate = true;

        try
        {
            await EnsureSemanticReconstructionAsync();
            if (_session.SemanticResult == null) return;

            StatusTextBlock.Text = "Generating reports...";

            // Extract string pool data for minidump files (requires coverage + accessor)
            if (_session.StringPool == null && !_session.IsEsmFile
                                            && _session.CoverageResult != null
                                            && _session.Accessor != null
                                            && _session.AnalysisResult?.MinidumpInfo != null)
            {
                _session.StringPool = await Task.Run(() =>
                {
                    var bufferAnalyzer = new RuntimeBufferAnalyzer(
                        _session.Accessor, _session.FileSize,
                        _session.AnalysisResult.MinidumpInfo, _session.CoverageResult, null);
                    var sp = bufferAnalyzer.ExtractStringPoolOnly();
                    RuntimeBufferAnalyzer.CrossReferenceWithCarvedFiles(
                        sp, _session.AnalysisResult.CarvedFiles);
                    return sp;
                });
            }

            var sources = new ReportDataSources(
                _session.SemanticResult,
                _session.AnalysisResult?.FormIdMap,
                _session.AnalysisResult?.EsmRecords?.AssetStrings,
                _session.AnalysisResult?.EsmRecords?.RuntimeEditorIds,
                _session.StringPool);

            var reports = await Task.Run(() =>
                GeckReportGenerator.GenerateAllReports(sources));

            _reportEntries.Clear();
            foreach (var (filename, content) in reports.OrderBy(kvp => kvp.Key))
            {
                _reportEntries.Add(new ReportEntry
                {
                    FileName = filename,
                    Content = content,
                    ReportType = filename.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? "CSV" : "TXT"
                });
            }

            ExportAllReportsButton.IsEnabled = _reportEntries.Count > 0;
            StatusTextBlock.Text = $"Generated {_reportEntries.Count} reports.";
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("Report Generation Failed",
                $"{ex.GetType().Name}: {ex.Message}", true);
        }
        finally
        {
            ReportsProgressBar.IsIndeterminate = false;
            ReportsProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    #endregion

    #region Report Export

    private async void ExportAllReports_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker,
            WindowNative.GetWindowHandle(App.Current.MainWindow));

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        var count = 0;
        foreach (var report in _reportEntries)
        {
            var filePath = Path.Combine(folder.Path, report.FileName);
            await File.WriteAllTextAsync(filePath, report.Content);
            count++;
        }

        StatusTextBlock.Text = $"Exported {count} reports to {folder.Path}";
    }

    private async void ExportSelectedReport_Click(object sender, RoutedEventArgs e)
    {
        if (ReportListView.SelectedItem is not ReportEntry report) return;

        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeChoices.Add("Text file", [".txt", ".csv"]);
        picker.SuggestedFileName = report.FileName;
        InitializeWithWindow.Initialize(picker,
            WindowNative.GetWindowHandle(App.Current.MainWindow));

        var file = await picker.PickSaveFileAsync();
        if (file != null)
        {
            await File.WriteAllTextAsync(file.Path, report.Content);
            StatusTextBlock.Text = $"Saved: {file.Path}";
        }
    }

    #endregion

    #region Report Viewer

    private void ReportListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReportListView.SelectedItem is ReportEntry report)
        {
            SetReportContent(report.Content);
            ExportSelectedReportButton.IsEnabled = true;
            _reportSearchMatches = [];
            _reportSearchIndex = 0;
            ReportSearchStatus.Text = "";
        }
        else
        {
            SetReportContent("");
            ExportSelectedReportButton.IsEnabled = false;
        }
    }

    /// <summary>
    ///     Sets up virtualized display of report content. Only the visible
    ///     viewport of lines is loaded into the TextBox for memory efficiency.
    /// </summary>
    private void SetReportContent(string content)
    {
        _reportFullContent = content.Replace("\r\n", "\n").Replace("\r", "\n");
        _reportLines = _reportFullContent.Length > 0
            ? _reportFullContent.Split('\n')
            : [];

        // Build cumulative character offset per line for search navigation
        _reportLineOffsets = new int[_reportLines.Length];
        var offset = 0;
        for (var i = 0; i < _reportLines.Length; i++)
        {
            _reportLineOffsets[i] = offset;
            offset += _reportLines[i].Length + 1; // +1 for \n
        }

        // Configure scrollbar
        var maxTop = Math.Max(0, _reportLines.Length - _reportViewportLineCount);
        ReportViewerScrollBar.Maximum = maxTop;
        ReportViewerScrollBar.ViewportSize = _reportViewportLineCount;
        ReportViewerScrollBar.LargeChange = Math.Max(1, _reportViewportLineCount - 2);
        ReportViewerScrollBar.Value = 0;

        UpdateReportViewport();
    }

    /// <summary>
    ///     Updates the TextBox with only the lines visible in the current viewport.
    /// </summary>
    private void UpdateReportViewport()
    {
        if (_reportLines.Length == 0)
        {
            ReportPreviewTextBox.Text = "";
            return;
        }

        var topLine = Math.Max(0, (int)ReportViewerScrollBar.Value);
        var endLine = Math.Min(topLine + _reportViewportLineCount, _reportLines.Length);
        ReportPreviewTextBox.Text = string.Join("\n", _reportLines[topLine..endLine]);
    }

    private void ReportViewerScrollBar_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateReportViewport();
    }

    private void ReportPreviewTextBox_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height <= 0) return;

        // Measure actual line height from font metrics (cached after first call)
        if (_measuredLineHeight <= 0)
        {
            var measure = new Microsoft.UI.Xaml.Controls.TextBlock
            {
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                FontSize = 11,
                Text = "Xg" // Characters with ascenders and descenders
            };
            measure.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            _measuredLineHeight = measure.DesiredSize.Height;
        }

        _reportViewportLineCount = Math.Max(10, (int)(e.NewSize.Height / _measuredLineHeight) - 1);

        // Update scrollbar to reflect new viewport size
        var maxTop = Math.Max(0, _reportLines.Length - _reportViewportLineCount);
        ReportViewerScrollBar.Maximum = maxTop;
        ReportViewerScrollBar.ViewportSize = _reportViewportLineCount;
        ReportViewerScrollBar.LargeChange = Math.Max(1, _reportViewportLineCount - 2);

        if (ReportViewerScrollBar.Value > maxTop)
        {
            ReportViewerScrollBar.Value = maxTop;
        }

        UpdateReportViewport();
    }

    private void ReportPreviewTextBox_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(ReportPreviewTextBox).Properties.MouseWheelDelta;
        if (delta == 0) return;

        var linesToScroll = delta > 0 ? -3 : 3;
        ReportViewerScrollBar.Value = Math.Clamp(
            ReportViewerScrollBar.Value + linesToScroll,
            0, ReportViewerScrollBar.Maximum);
        e.Handled = true;
    }

    #endregion

    #region Report Search

    private void ReportSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            PerformReportSearch();
            e.Handled = true;
        }
    }

    private void PerformReportSearch()
    {
        var query = ReportSearchBox.Text?.Trim() ?? "";

        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(_reportFullContent))
        {
            _reportSearchMatches = [];
            _reportSearchIndex = 0;
            ReportSearchStatus.Text = "";
            return;
        }

        _reportSearchQuery = query;
        _reportSearchMatches = [];

        // Search the full content, not just the visible viewport
        var idx = 0;
        while (true)
        {
            var found = _reportFullContent.IndexOf(query, idx, StringComparison.OrdinalIgnoreCase);
            if (found < 0) break;
            _reportSearchMatches.Add(found);
            idx = found + 1;
        }

        if (_reportSearchMatches.Count == 0)
        {
            ReportSearchStatus.Text = "No matches";
            _reportSearchIndex = 0;
            return;
        }

        _reportSearchIndex = 0;
        NavigateToReportMatch();
    }

    private void ReportSearchNext_Click(object sender, RoutedEventArgs e)
    {
        if (_reportSearchMatches.Count == 0)
        {
            PerformReportSearch();
            return;
        }

        if (_reportSearchMatches.Count > 0)
        {
            _reportSearchIndex = (_reportSearchIndex + 1) % _reportSearchMatches.Count;
            NavigateToReportMatch();
        }
    }

    private void ReportSearchPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_reportSearchMatches.Count == 0)
        {
            PerformReportSearch();
            return;
        }

        if (_reportSearchMatches.Count > 0)
        {
            _reportSearchIndex = (_reportSearchIndex - 1 + _reportSearchMatches.Count) % _reportSearchMatches.Count;
            NavigateToReportMatch();
        }
    }

    private void NavigateToReportMatch()
    {
        if (_reportSearchMatches.Count == 0) return;

        var pos = _reportSearchMatches[_reportSearchIndex];
        var matchLine = FindLineForCharOffset(pos);

        // Scroll viewport to show the match line (one-third from top for context)
        var targetTop = Math.Max(0, matchLine - _reportViewportLineCount / 3);
        targetTop = Math.Min(targetTop, (int)ReportViewerScrollBar.Maximum);
        ReportViewerScrollBar.Value = targetTop;

        // Calculate match position within the viewport text and select it
        var topLine = (int)ReportViewerScrollBar.Value;
        if (topLine < _reportLineOffsets.Length)
        {
            var viewportStartOffset = _reportLineOffsets[topLine];
            var posInViewport = pos - viewportStartOffset;
            if (posInViewport >= 0 && posInViewport + _reportSearchQuery.Length <= ReportPreviewTextBox.Text.Length)
            {
                ReportPreviewTextBox.Select(posInViewport, _reportSearchQuery.Length);
            }
        }

        ReportSearchStatus.Text = $"{_reportSearchIndex + 1} of {_reportSearchMatches.Count}";
    }

    #endregion
}
