using System.Collections.ObjectModel;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Models;
using FalloutXbox360Utils.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FalloutXbox360Utils;

/// <summary>
/// Display methods: Update*, Build*, Show*, PopulateTreeView, UI updates
/// </summary>
public sealed partial class SingleFileTab
{
    #region Button State Updates

    private void UpdateButtonStates()
    {
        var valid = !string.IsNullOrEmpty(MinidumpPathTextBox.Text) && File.Exists(MinidumpPathTextBox.Text) &&
                    FileTypeDetector.IsSupportedExtension(MinidumpPathTextBox.Text);
        AnalyzeButton.IsEnabled = valid;
        ExtractButton.IsEnabled = valid && _analysisResult != null && !string.IsNullOrEmpty(OutputPathTextBox.Text);
    }

    private void UpdateOutputPathFromInput(string inputPath)
    {
        var dir = Path.GetDirectoryName(inputPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(inputPath);
        OutputPathTextBox.Text = Path.Combine(dir, $"{name}_extracted");
    }

    #endregion

    #region Sort Icons

    private void UpdateSortIcons()
    {
        OffsetSortIcon.Visibility = LengthSortIcon.Visibility =
            TypeSortIcon.Visibility = FilenameSortIcon.Visibility = Visibility.Collapsed;
        var icon = _sorter.CurrentColumn switch
        {
            CarvedFilesSorter.SortColumn.Offset => OffsetSortIcon,
            CarvedFilesSorter.SortColumn.Length => LengthSortIcon,
            CarvedFilesSorter.SortColumn.Type => TypeSortIcon,
            CarvedFilesSorter.SortColumn.Filename => FilenameSortIcon,
            _ => null
        };
        if (icon != null)
        {
            icon.Visibility = Visibility.Visible;
            icon.Glyph = _sorter.IsAscending ? "\uE70E" : "\uE70D";
        }
    }

    private void UpdateCoverageSortIcons()
    {
        CoverageIndexSortIcon.Visibility = CoverageOffsetSortIcon.Visibility =
            CoverageSizeSortIcon.Visibility = CoverageClassSortIcon.Visibility = Visibility.Collapsed;

        var icon = _coverageGapSortColumn switch
        {
            CoverageGapSortColumn.Index => CoverageIndexSortIcon,
            CoverageGapSortColumn.Offset => CoverageOffsetSortIcon,
            CoverageGapSortColumn.Size => CoverageSizeSortIcon,
            CoverageGapSortColumn.Classification => CoverageClassSortIcon,
            _ => null
        };

        if (icon != null)
        {
            icon.Visibility = Visibility.Visible;
            icon.Glyph = _coverageGapSortAscending ? "\uE70E" : "\uE70D";
        }
    }

    #endregion

    #region List Refresh

    private void RefreshSortedList()
    {
        var selectedItem = ResultsListView.SelectedItem as CarvedFileEntry;
        var sorted = _sorter.Sort(_allCarvedFiles);
        _carvedFiles.Clear();
        foreach (var f in sorted) _carvedFiles.Add(f);
        if (selectedItem != null && _carvedFiles.Contains(selectedItem))
        {
            ResultsListView.SelectedItem = selectedItem;
            ResultsListView.ScrollIntoView(selectedItem, ScrollIntoViewAlignment.Leading);
        }
    }

    private void RefreshCoverageGapList()
    {
        IEnumerable<CoverageGapEntry> sorted = _coverageGapSortColumn switch
        {
            CoverageGapSortColumn.Offset => _coverageGapSortAscending
                ? _allCoverageGapEntries.OrderBy(g => g.RawFileOffset)
                : _allCoverageGapEntries.OrderByDescending(g => g.RawFileOffset),
            CoverageGapSortColumn.Size => _coverageGapSortAscending
                ? _allCoverageGapEntries.OrderBy(g => g.RawSize)
                : _allCoverageGapEntries.OrderByDescending(g => g.RawSize),
            CoverageGapSortColumn.Classification => _coverageGapSortAscending
                ? _allCoverageGapEntries.OrderBy(g => g.Classification, StringComparer.OrdinalIgnoreCase)
                : _allCoverageGapEntries.OrderByDescending(g => g.Classification, StringComparer.OrdinalIgnoreCase),
            _ => _coverageGapSortAscending
                ? _allCoverageGapEntries.OrderBy(g => g.Index)
                : _allCoverageGapEntries.OrderByDescending(g => g.Index)
        };

        CoverageGapListView.ItemsSource = new ObservableCollection<CoverageGapEntry>(sorted);
    }

    #endregion

    #region Coverage Tab

    private void PopulateCoverageTab()
    {
        var coverage = _session.CoverageResult;
        if (coverage == null) return;

        var totalRegion = coverage.TotalRegionBytes;

        // Summary text
        var summary = $"File size:           {coverage.FileSize,15:N0} bytes\n" +
                      $"Memory regions:      {coverage.TotalMemoryRegions,6:N0}   (total: {totalRegion:N0} bytes)\n" +
                      $"Minidump overhead:   {coverage.MinidumpOverhead,15:N0} bytes\n\n" +
                      $"Recognized data:     {coverage.TotalRecognizedBytes,15:N0} bytes  ({coverage.RecognizedPercent:F1}%)\n";

        foreach (var (cat, bytes) in coverage.CategoryBytes.OrderByDescending(kv => kv.Value))
        {
            var pct = totalRegion > 0 ? bytes * 100.0 / totalRegion : 0;
            var label = cat switch
            {
                CoverageCategory.Header => "Minidump header",
                CoverageCategory.Module => "Modules",
                CoverageCategory.CarvedFile => "Carved files",
                CoverageCategory.EsmRecord => "ESM records",
                CoverageCategory.ScdaScript => "SCDA scripts",
                _ => cat.ToString()
            };
            summary += $"  {label + ":",-19}{bytes,15:N0} bytes  ({pct,5:F1}%)\n";
        }

        summary += $"\nUncovered:           {coverage.TotalGapBytes,15:N0} bytes  ({coverage.GapPercent:F1}%)";
        CoverageSummaryText.Text = summary;

        // Classification summary - use friendly display names
        var totalGap = coverage.TotalGapBytes;
        var classText = "";
        if (totalGap > 0)
        {
            var byClass = coverage.Gaps
                .GroupBy(g => g.Classification)
                .Select(g => new { Classification = g.Key, TotalBytes = g.Sum(x => x.Size), Count = g.Count() })
                .OrderByDescending(x => x.TotalBytes);

            foreach (var entry in byClass)
            {
                var pct = totalGap > 0 ? entry.TotalBytes * 100.0 / totalGap : 0;
                var displayName = FileTypeColors.GapDisplayNames.GetValueOrDefault(
                    entry.Classification, entry.Classification.ToString());
                classText +=
                    $"{displayName + ":",-18}{entry.TotalBytes,15:N0} bytes  ({pct,5:F1}%)  - {entry.Count:N0} regions\n";
            }
        }
        else
        {
            classText = "No gaps detected - 100% coverage!";
        }

        CoverageClassificationText.Text = classText;

        // Build full gap details list (no limit)
        _allCoverageGapEntries = [];
        for (var i = 0; i < coverage.Gaps.Count; i++)
        {
            var gap = coverage.Gaps[i];
            var gapDisplayName = FileTypeColors.GapDisplayNames.GetValueOrDefault(
                gap.Classification, gap.Classification.ToString());
            _allCoverageGapEntries.Add(new CoverageGapEntry
            {
                Index = i + 1,
                FileOffset = $"0x{gap.FileOffset:X8}",
                Size = FormatSize(gap.Size),
                Classification = gapDisplayName,
                Context = gap.Context,
                RawFileOffset = gap.FileOffset,
                RawSize = gap.Size
            });
        }

        _coverageGapSortColumn = CoverageGapSortColumn.Index;
        _coverageGapSortAscending = true;
        RefreshCoverageGapList();
        CoverageGapListView.ItemTemplate = CreateCoverageGapItemTemplate();
        _coveragePopulated = true;
    }

    #endregion

    #region Coverage Tab Events

    private void CoverageSortByIndex_Click(object sender, RoutedEventArgs e)
    {
        CycleCoverageSort(CoverageGapSortColumn.Index);
    }

    private void CoverageSortByOffset_Click(object sender, RoutedEventArgs e)
    {
        CycleCoverageSort(CoverageGapSortColumn.Offset);
    }

    private void CoverageSortBySize_Click(object sender, RoutedEventArgs e)
    {
        CycleCoverageSort(CoverageGapSortColumn.Size);
    }

    private void CoverageSortByClassification_Click(object sender, RoutedEventArgs e)
    {
        CycleCoverageSort(CoverageGapSortColumn.Classification);
    }

    private void CycleCoverageSort(CoverageGapSortColumn column)
    {
        if (_coverageGapSortColumn == column)
        {
            if (_coverageGapSortAscending)
            {
                _coverageGapSortAscending = false;
            }
            else
            {
                _coverageGapSortColumn = CoverageGapSortColumn.Index;
                _coverageGapSortAscending = true;
            }
        }
        else
        {
            _coverageGapSortColumn = column;
            _coverageGapSortAscending = true;
        }

        UpdateCoverageSortIcons();
        RefreshCoverageGapList();
    }

    private void CoverageGapListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CoverageGapListView.SelectedItem is CoverageGapEntry gap)
        {
            SubTabView.SelectedIndex = 0; // Switch to Memory Map tab
            HexViewer.NavigateToOffset(gap.RawFileOffset);
        }
    }

    #endregion

    #region Reset/Initialize

    private void ResetSubTabs()
    {
        _session.Dispose();
        CoverageTab.IsEnabled = false;
        DataBrowserTab.IsEnabled = false;
        ReportsTab.IsEnabled = false;
        _coveragePopulated = false;
        _allCoverageGapEntries = [];
        _reportEntries.Clear();
        ReportPreviewTextBox.Text = "";
        _reportLines = [];
        _reportLineOffsets = [];
        _reportFullContent = "";
        ReportViewerScrollBar.Maximum = 0;
        ReportViewerScrollBar.Value = 0;
        _reportSearchMatches = [];
        _reportSearchIndex = 0;
        _reportSearchQuery = "";
        DataBrowserPlaceholder.Visibility = Visibility.Visible;
        DataBrowserContent.Visibility = Visibility.Collapsed;
        ReconstructStatusText.Text = Strings.Empty_RunAnalysisForEsm;
        EsmTreeView.RootNodes.Clear();
        _flatListBuilt = false;
        _esmBrowserTree = null;
        _currentSearchQuery = "";
        EsmSearchBox.Text = "";
        PropertyPanel.Children.Clear();
        SelectedRecordTitle.Text = Strings.Empty_SelectARecord;
        GoToOffsetButton.Visibility = Visibility.Collapsed;
        ExportAllReportsButton.IsEnabled = false;
        ExportSelectedReportButton.IsEnabled = false;
    }

    private void InitializeFileTypeCheckboxes()
    {
        FileTypeCheckboxPanel.Children.Clear();
        _fileTypeCheckboxes.Clear();
        foreach (var fileType in FileTypeMapping.DisplayNames)
        {
            var cb = new CheckBox { Content = fileType, IsChecked = true, Margin = new Thickness(0, 0, 8, 0) };
            _fileTypeCheckboxes[fileType] = cb;
            FileTypeCheckboxPanel.Children.Add(cb);
        }
    }

    private void SetupTextBoxContextMenus()
    {
        TextBoxContextMenuHelper.AttachContextMenu(MinidumpPathTextBox);
        TextBoxContextMenuHelper.AttachContextMenu(OutputPathTextBox);
    }

    #endregion

    #region Property Panel Building

    private void BuildPropertyPanel(List<EsmPropertyEntry> properties)
    {
        PropertyPanel.Children.Clear();

        // Use a single Grid so all rows share the same column widths (dynamic table alignment)
        // 5 columns: icon, name, value col1 (FullName/EditorID), value col2 (EditorID), value col3 (FormID)
        var mainGrid = new Grid();
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // icon
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // name
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // value col1
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // value col2
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // value col3

        var currentRow = 0;
        var propertyRowIndex = 0; // For alternating row colors (excludes category headers)
        string? lastCategory = null;

        // Use theme-aware foreground with low opacity for subtle alternating rows
        // This adapts to both light and dark modes automatically
        var foregroundBrush = (Microsoft.UI.Xaml.Media.SolidColorBrush)
            Application.Current.Resources["TextFillColorPrimaryBrush"];
        var altRowBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(foregroundBrush.Color) { Opacity = 0.05 };

        foreach (var prop in properties)
        {
            // Add category header when category changes
            if (prop.Category != null && prop.Category != lastCategory)
            {
                lastCategory = prop.Category;
                propertyRowIndex = 0; // Reset alternating for each category
                mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                // Add more visible background for category headers (distinct from row stripes)
                var categoryBgBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(foregroundBrush.Color) { Opacity = 0.12 };
                var categoryBg = new Border { Background = categoryBgBrush };
                Grid.SetRow(categoryBg, currentRow);
                Grid.SetColumnSpan(categoryBg, 5); // Spans all 5 columns
                mainGrid.Children.Add(categoryBg);

                var categoryHeader = new TextBlock
                {
                    Text = prop.Category,
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground =
                        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    Margin = new Thickness(8, 5, 0, 7) // Vertically centered (up 1px)
                };
                Grid.SetRow(categoryHeader, currentRow);
                Grid.SetColumnSpan(categoryHeader, 5); // Spans all 5 columns
                mainGrid.Children.Add(categoryHeader);
                currentRow++;
            }

            if (prop.IsExpandable && prop.SubItems?.Count > 0)
            {
                // Expandable entry - header row
                mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                // Add alternating row background
                if (propertyRowIndex % 2 == 1)
                {
                    var bgBorder = new Border { Background = altRowBrush };
                    Grid.SetRow(bgBorder, currentRow);
                    Grid.SetColumnSpan(bgBorder, 5);
                    mainGrid.Children.Add(bgBorder);
                }

                var expandIcon = new TextBlock
                {
                    Text = "\u25B6",
                    FontSize = 10,
                    Width = 18,
                    Padding = new Thickness(4, 3, 0, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground =
                        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                };
                Grid.SetRow(expandIcon, currentRow);
                Grid.SetColumn(expandIcon, 0);
                mainGrid.Children.Add(expandIcon);

                var nameText = new TextBlock
                {
                    Text = prop.Name,
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Padding = new Thickness(0, 3, 16, 2),
                    IsTextSelectionEnabled = true
                };
                Grid.SetRow(nameText, currentRow);
                Grid.SetColumn(nameText, 1);
                mainGrid.Children.Add(nameText);

                var countText = new TextBlock
                {
                    Text = prop.Value,
                    FontSize = 12,
                    Padding = new Thickness(0, 3, 4, 2),
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    Foreground =
                        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                };
                Grid.SetRow(countText, currentRow);
                Grid.SetColumn(countText, 2);
                Grid.SetColumnSpan(countText, 3); // Spans value columns
                mainGrid.Children.Add(countText);

                var headerRow = currentRow;
                currentRow++;

                // Create a separate grid for sub-items (isolates column widths from mainGrid)
                var subItemsGrid = new Grid { Visibility = Visibility.Collapsed };
                subItemsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Col1: EditorID
                subItemsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Col2: FullName
                subItemsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Col3: FormID
                subItemsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Col4: Qty

                var subRow = 0;
                foreach (var sub in prop.SubItems)
                {
                    subItemsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    // 4-column sub-item (Inventory, Factions)
                    if (sub.Col1 != null || sub.Col2 != null || sub.Col3 != null || sub.Col4 != null)
                    {
                        var col1Text = new TextBlock
                        {
                            Text = sub.Col1 ?? "",
                            FontSize = 11,
                            Padding = new Thickness(0, 1, 12, 1),
                            IsTextSelectionEnabled = true
                        };
                        Grid.SetRow(col1Text, subRow);
                        Grid.SetColumn(col1Text, 0);
                        subItemsGrid.Children.Add(col1Text);

                        var col2Text = new TextBlock
                        {
                            Text = sub.Col2 ?? "",
                            FontSize = 11,
                            Padding = new Thickness(0, 1, 12, 1),
                            IsTextSelectionEnabled = true
                        };
                        Grid.SetRow(col2Text, subRow);
                        Grid.SetColumn(col2Text, 1);
                        subItemsGrid.Children.Add(col2Text);

                        var col3Text = new TextBlock
                        {
                            Text = sub.Col3 ?? "",
                            FontSize = 11,
                            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                            Padding = new Thickness(0, 1, 12, 1),
                            IsTextSelectionEnabled = true
                        };
                        Grid.SetRow(col3Text, subRow);
                        Grid.SetColumn(col3Text, 2);
                        subItemsGrid.Children.Add(col3Text);

                        var col4Text = new TextBlock
                        {
                            Text = sub.Col4 ?? "",
                            FontSize = 11,
                            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                            Padding = new Thickness(0, 1, 4, 1),
                            IsTextSelectionEnabled = true
                        };
                        Grid.SetRow(col4Text, subRow);
                        Grid.SetColumn(col4Text, 3);
                        subItemsGrid.Children.Add(col4Text);
                    }
                    else if (string.IsNullOrEmpty(sub.Name))
                    {
                        // Value-only sub-item (FaceGen hex blocks)
                        var valText = new TextBlock
                        {
                            Text = sub.Value,
                            FontSize = 11,
                            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                            TextWrapping = TextWrapping.Wrap,
                            IsTextSelectionEnabled = true,
                            Padding = new Thickness(0, 1, 0, 1)
                        };
                        Grid.SetRow(valText, subRow);
                        Grid.SetColumnSpan(valText, 4);
                        subItemsGrid.Children.Add(valText);
                    }
                    else
                    {
                        // Name + Value sub-item (Skills)
                        var subNameText = new TextBlock
                        {
                            Text = sub.Name,
                            FontSize = 11,
                            Padding = new Thickness(0, 1, 16, 1),
                            IsTextSelectionEnabled = true
                        };
                        Grid.SetRow(subNameText, subRow);
                        Grid.SetColumnSpan(subNameText, 2);
                        subItemsGrid.Children.Add(subNameText);

                        var valText = new TextBlock
                        {
                            Text = sub.Value,
                            FontSize = 11,
                            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                            Padding = new Thickness(0, 1, 4, 1),
                            IsTextSelectionEnabled = true
                        };
                        Grid.SetRow(valText, subRow);
                        Grid.SetColumn(valText, 2);
                        Grid.SetColumnSpan(valText, 2);
                        subItemsGrid.Children.Add(valText);
                    }
                    subRow++;
                }

                // Add sub-items grid as a single row in mainGrid (spans all columns, isolated)
                mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var subItemsContainer = new Border
                {
                    Child = subItemsGrid,
                    Margin = new Thickness(18, 0, 0, 0) // Indent
                };
                Grid.SetRow(subItemsContainer, currentRow);
                Grid.SetColumn(subItemsContainer, 1);
                Grid.SetColumnSpan(subItemsContainer, 4);
                mainGrid.Children.Add(subItemsContainer);
                currentRow++;

                // Click handler - make header row clickable
                expandIcon.PointerPressed += (_, _) => ToggleSubItems();
                nameText.PointerPressed += (_, _) => ToggleSubItems();
                countText.PointerPressed += (_, _) => ToggleSubItems();

                void ToggleSubItems()
                {
                    var isCollapsed = subItemsGrid.Visibility == Visibility.Collapsed;
                    subItemsGrid.Visibility = isCollapsed ? Visibility.Visible : Visibility.Collapsed;
                    expandIcon.Text = isCollapsed ? "\u25BC" : "\u25B6";
                }

                propertyRowIndex++;
            }
            else
            {
                // Normal property row
                mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                // Add alternating row background
                if (propertyRowIndex % 2 == 1)
                {
                    var bgBorder = new Border { Background = altRowBrush };
                    Grid.SetRow(bgBorder, currentRow);
                    Grid.SetColumnSpan(bgBorder, 5);
                    mainGrid.Children.Add(bgBorder);
                }

                // Empty spacer for icon column alignment
                var spacer = new TextBlock { Width = 18, Padding = new Thickness(4, 3, 0, 2) };
                Grid.SetRow(spacer, currentRow);
                Grid.SetColumn(spacer, 0);
                mainGrid.Children.Add(spacer);

                var nameText = new TextBlock
                {
                    Text = prop.Name,
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Padding = new Thickness(0, 3, 16, 2),
                    IsTextSelectionEnabled = true
                };
                Grid.SetRow(nameText, currentRow);
                Grid.SetColumn(nameText, 1);
                mainGrid.Children.Add(nameText);

                // Single value column (spans all value columns)
                var valueText = new TextBlock
                {
                    Text = prop.Value,
                    FontSize = 12,
                    Padding = new Thickness(0, 3, 4, 2),
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                };
                Grid.SetRow(valueText, currentRow);
                Grid.SetColumn(valueText, 2);
                Grid.SetColumnSpan(valueText, 3);
                mainGrid.Children.Add(valueText);

                propertyRowIndex++;
                currentRow++;
            }
        }

        PropertyPanel.Children.Add(mainGrid);
    }

    #endregion

    #region Browser Node Selection

    private void SelectBrowserNode(EsmBrowserNode browserNode)
    {
        _selectedBrowserNode = browserNode;

        if (browserNode.NodeType == "Record")
        {
            SelectedRecordTitle.Text = browserNode.DisplayName;
            BuildPropertyPanel(browserNode.Properties);

            if (browserNode.FileOffset.HasValue && browserNode.FileOffset.Value > 0)
            {
                GoToOffsetButton.Visibility = Visibility.Visible;
            }
            else
            {
                GoToOffsetButton.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            SelectedRecordTitle.Text = browserNode.DisplayName;
            PropertyPanel.Children.Clear();
            GoToOffsetButton.Visibility = Visibility.Collapsed;
        }
    }

    private void GoToRecordOffset_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedBrowserNode?.FileOffset is > 0)
        {
            SubTabView.SelectedIndex = 0; // Switch to Memory Map
            HexViewer.NavigateToOffset(_selectedBrowserNode.FileOffset.Value);
        }
    }

    #endregion
}
