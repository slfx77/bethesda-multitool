using System.Collections.ObjectModel;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils;

/// <summary>
///     Display methods: Update*, Build*, Show*, PopulateTreeView, UI updates
/// </summary>
public sealed partial class SingleFileTab
{
    /// <summary>
    ///     Minimal HyperlinkButton style that strips button chrome for inline-link appearance.
    /// </summary>
    private static Style? _inlineLinkStyle;

    private static Style InlineLinkStyle => _inlineLinkStyle ??= BuildInlineLinkStyle();

    /// <summary>
    ///     Creates a HyperlinkButton styled as an inline underlined link (no button chrome).
    /// </summary>
    private HyperlinkButton CreateFormIdLink(string text, uint formId, int fontSize, bool monospace = false)
    {
        // Foreground must be set on the TextBlock directly — HyperlinkButton visual state
        // animations override Foreground on the control itself, but don't affect child local values.
        var linkColor = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            ActualTheme == ElementTheme.Light
                ? Windows.UI.Color.FromArgb(0xFF, 0x00, 0x66, 0xCC) // Dark blue for light mode
                : Windows.UI.Color.FromArgb(0xFF, 0x75, 0xBE, 0xFF)); // Vivid sky blue for dark mode
        var textBlock = new TextBlock
        {
            Text = text,
            TextDecorations = TextDecorations.Underline,
            FontSize = fontSize,
            Foreground = linkColor,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };

        var link = new HyperlinkButton
        {
            Content = textBlock,
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            Style = InlineLinkStyle
        };
        link.Click += (_, _) => NavigateToFormId(formId);
        return link;
    }

    private static Style BuildInlineLinkStyle()
    {
        var style = new Style(typeof(HyperlinkButton));
        style.Setters.Add(new Setter(HyperlinkButton.BackgroundProperty,
            new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent)));
        style.Setters.Add(new Setter(HyperlinkButton.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(HyperlinkButton.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(HyperlinkButton.MinWidthProperty, 0.0));
        style.Setters.Add(new Setter(HyperlinkButton.MinHeightProperty, 0.0));
        // Use a ControlTemplate that renders only the ContentPresenter (no BackgroundElement)
        return style;
    }

    #region Coverage Tab

    private void PopulateCoverageTab()
    {
        var coverage = _session.CoverageResult;
        if (coverage == null) return;

        CoverageSummaryText.Text = ResultsFormatter.BuildCoverageSummaryText(coverage);
        CoverageClassificationText.Text = ResultsFormatter.BuildCoverageClassificationText(coverage);

        _session.CoverageGaps = ResultsFormatter.BuildCoverageGapEntries(coverage, FormatSize);

        _coverageGapSortColumn = CoverageGapSortColumn.Index;
        _coverageGapSortAscending = true;
        RefreshCoverageGapList();
        CoverageGapListView.ItemTemplate = CreateCoverageGapItemTemplate();
        _session.CoveragePopulated = true;
    }

    #endregion

    #region File Info Card

    private void UpdateFileInfoCard()
    {
        var display = PipelinePhaseHelper.ComputeFileInfoDisplay(
            _session.AnalysisResult, _session.IsEsmFile, FormatSize);
        if (display == null) return;

        InfoFileName.Text = display.FileName;
        InfoFileSize.Text = display.FileSize;
        InfoFormat.Text = display.Format;
        InfoEndianness.Text = display.Endianness;

        if (display.ShowBuildPanel)
        {
            InfoModuleName.Text = display.ModuleName ?? "";
            InfoCompileDate.Text = display.CompileDate ?? "";
            InfoBuildPanel.Visibility = Visibility.Visible;
        }
        else
        {
            InfoBuildPanel.Visibility = Visibility.Collapsed;
        }
    }

    #endregion

    #region Record Breakdown

    private void PopulateRecordBreakdown()
    {
        if (_session.SemanticResult == null) return;

        var r = _session.SemanticResult;
        RecordBreakdownPanel.Children.Clear();

        // Totals header
        var totalsText = new TextBlock
        {
            Text = PipelinePhaseHelper.BuildRecordTotalsText(r, _session.IsEsmFile),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        };
        RecordBreakdownPanel.Children.Add(totalsText);

        // Build category data and render 3-column card layout
        var categories = ResultsFormatter.BuildRecordBreakdownCategories(r);
        RecordBreakdownPanel.Children.Add(PropertyPanelBuilder.BuildThreeColumnCardLayout(categories));

        // Unparsed record types
        if (r.UnparsedTypeCounts.Count > 0)
        {
            var otherLabel = _session.IsEsmFile ? "Other (not parsed)" : "Other (not reconstructed)";
            var otherRecords = ResultsFormatter.GetUnparsedRecords(r.UnparsedTypeCounts);
            RecordBreakdownPanel.Children.Add(PropertyPanelBuilder.BuildCategoryCard(otherLabel, otherRecords));
        }

        SummaryRecordPanel.Visibility = Visibility.Visible;
        _session.RecordBreakdownPopulated = true;
    }

    #endregion

    #region Property Panel Building

    /// <summary>
    ///     Lazily-initialized callbacks that connect PropertyPanelBuilder to instance navigation methods.
    /// </summary>
    private PropertyPanelBuilder.Callbacks? _propertyPanelCallbacks;

    private PropertyPanelBuilder.Callbacks GetPropertyPanelCallbacks()
    {
        return _propertyPanelCallbacks ??= new PropertyPanelBuilder.Callbacks
        {
            IsFormIdNavigable = IsFormIdNavigable,
            CreateFormIdLink = CreateFormIdLink,
            NavigateToCellInWorldMap = async cellFormId =>
            {
                await PopulateWorldMapAsync();
                NavigateToCellInWorldMap(cellFormId);
                SubTabView.SelectedItem = WorldMapTab;
            }
        };
    }

    private void BuildPropertyPanel(List<EsmPropertyEntry> properties)
    {
        PropertyPanel.Children.Clear();
        var mainGrid = PropertyPanelBuilder.BuildGrid(properties, GetPropertyPanelCallbacks());
        PropertyPanel.Children.Add(mainGrid);
    }

    #endregion

    #region Pipeline Phase State

    /// <summary>
    ///     Represents the current phase of the analysis pipeline.
    ///     Used by <see cref="SetPipelinePhase" /> to centralize all UI control state transitions.
    /// </summary>
    internal enum AnalysisPipelinePhase
    {
        Idle,
        Scanning,
        Parsing,
        LoadingMap,
        Coverage,
        Extracting
    }

    /// <summary>
    ///     Centralized UI state transition for the analysis/extraction pipeline.
    ///     Sets all control enabled/disabled/visibility states based on the current phase.
    ///     Dynamic content (progress values, status text) remains in the pipeline callbacks.
    /// </summary>
    private void SetPipelinePhase(AnalysisPipelinePhase phase)
    {
        _pipelinePhase = phase;
        var isBusy = phase != AnalysisPipelinePhase.Idle;

        // Input controls
        MinidumpPathTextBox.IsEnabled = !isBusy;
        OutputPathTextBox.IsEnabled = !isBusy;
        SubTabView.IsEnabled = !isBusy;

        // Progress bar
        var pbState = PipelinePhaseHelper.GetProgressBarState(phase);
        AnalysisProgressBar.Visibility = pbState.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        AnalysisProgressBar.IsIndeterminate = pbState.IsIndeterminate;

        // Buttons (must be last — UpdateButtonStates reads _pipelinePhase)
        UpdateButtonStates();
    }

    #endregion

    #region Button State Updates

    private void UpdateButtonStates()
    {
        var (analyzeEnabled, extractEnabled) = PipelinePhaseHelper.ComputeButtonStates(
            _pipelinePhase, MinidumpPathTextBox.Text, OutputPathTextBox.Text, _analysisResult != null);
        AnalyzeButton.IsEnabled = analyzeEnabled;
        ExtractButton.IsEnabled = extractEnabled;
    }

    private void UpdateOutputPathFromInput(string inputPath)
    {
        OutputPathTextBox.Text = PipelinePhaseHelper.ComputeOutputPath(inputPath);
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
        var filtered = ApplyResultsFilter(_allCarvedFiles);
        var sorted = _sorter.Sort(filtered);
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
        var sorted = ResultsFormatter.SortCoverageGaps(
            _session.CoverageGaps, _coverageGapSortColumn, _coverageGapSortAscending);
        CoverageGapListView.ItemsSource = new ObservableCollection<CoverageGapEntry>(sorted);
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
        (_coverageGapSortColumn, _coverageGapSortAscending) =
            ResultsFormatter.CycleCoverageSortState(_coverageGapSortColumn, _coverageGapSortAscending, column);

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

    #region Results Type Filter

    private readonly Dictionary<string, CheckBox> _resultsFilterCheckboxes = [];

    private void BuildResultsFilterCheckboxes()
    {
        _resultsFilterCheckboxes.Clear();

        var typeCounts = _allCarvedFiles
            .GroupBy(f => f.DisplayType)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (typeCounts.Count == 0)
        {
            FilterDropDown.Visibility = Visibility.Collapsed;
            return;
        }

        ResultsFilterPanel.Children.Clear();
        foreach (var group in typeCounts)
        {
            var cb = new CheckBox
            {
                Content = $"{group.Key} ({group.Count()})",
                IsChecked = true,
                IsThreeState = false,
                FontSize = 11,
                MinWidth = 0,
                Tag = group.Key
            };
            cb.Checked += ResultsFilterCheckbox_Changed;
            cb.Unchecked += ResultsFilterCheckbox_Changed;
            _resultsFilterCheckboxes[group.Key] = cb;
            ResultsFilterPanel.Children.Add(cb);
        }

        UpdateFilterButtonText();
        FilterDropDown.Visibility = Visibility.Visible;
    }

    private List<CarvedFileEntry> ApplyResultsFilter(List<CarvedFileEntry> source)
    {
        if (_resultsFilterCheckboxes.Count == 0)
        {
            return source;
        }

        return source
            .Where(f => _resultsFilterCheckboxes.TryGetValue(f.DisplayType, out var cb) && cb.IsChecked == true)
            .ToList();
    }

    private void UpdateFilterButtonText()
    {
        var total = _resultsFilterCheckboxes.Count;
        var checkedCount = _resultsFilterCheckboxes.Values.Count(cb => cb.IsChecked == true);
        FilterDropDown.Content = ResultsFormatter.ComputeFilterButtonText(checkedCount, total);
    }

    private void ResultsFilterCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateFilterButtonText();
        RefreshSortedList();
    }

    private void FilterSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var cb in _resultsFilterCheckboxes.Values)
        {
            cb.IsChecked = true;
        }
    }

    private void FilterSelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var cb in _resultsFilterCheckboxes.Values)
        {
            cb.IsChecked = false;
        }
    }

    #endregion

    #region Reset/Initialize

    /// <summary>
    ///     Resets all sub-tab state when a new file is loaded or analysis restarts.
    ///     Each tab has its own reset method (in the corresponding partial class file)
    ///     so all controls for a given tab are easy to locate and maintain.
    /// </summary>
    private void ResetSubTabs()
    {
        _session.Dispose();
        _semanticParseTask = null;
        LoadOrderStatusText.Text = "";

        ResetMemoryMapTab();
        ResetDataBrowser();
        ResetDialogueViewer();
        ResetWorldMap();
        ResetNpcBrowser();
        ResetReportsTab();
        ResetSummaryTab();
        ResetCoverageTab();
        ResetNavigation();
    }

    private void ResetMemoryMapTab()
    {
        _resultsFilterCheckboxes.Clear();
        ResultsFilterPanel.Children.Clear();
        FilterDropDown.Visibility = Visibility.Collapsed;
    }

    private void ResetDataBrowser()
    {
        DataBrowserPlaceholder.Visibility = Visibility.Visible;
        DataBrowserContent.Visibility = Visibility.Collapsed;
        ParseStatusText.Text = Strings.Empty_RunAnalysisForEsm;
        EsmTreeView.RootNodes.Clear();
        _flatListBuilt = false;
        _esmBrowserTree = null;
        _placementIndex = null;
        _factionMembersIndex = null;
        _raceLookup = null;
        _usageIndex = null;
        _currentSearchQuery = "";
        EsmSearchBox.Text = "";
        EsmSortComboBox.SelectedIndex = 0;
        PropertyPanel.Children.Clear();
        SelectedRecordTitle.Text = Strings.Empty_SelectARecord;
        GoToOffsetButton.Visibility = Visibility.Collapsed;
        ViewWorldspaceButton.Visibility = Visibility.Collapsed;
        ViewNpcButton.Visibility = Visibility.Collapsed;
    }

    private void ResetSummaryTab()
    {
        SummaryRecordPanel.Visibility = Visibility.Collapsed;
        RecordBreakdownPanel.Children.Clear();
    }

    private void ResetCoverageTab()
    {
        CoverageSummaryText.Text = "Run analysis to see coverage data.";
        CoverageClassificationText.Text = "";
        CoverageGapListView.ItemsSource = null;
        _coverageGapSortColumn = CoverageGapSortColumn.Index;
        _coverageGapSortAscending = true;
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

    #region Browser Node Selection

    private void SelectBrowserNode(EsmBrowserNode browserNode)
    {
        // Push to unified navigation history when user clicks a different record
        if (!_isNavigating && _selectedBrowserNode?.NodeType == "Record" && browserNode.NodeType == "Record"
            && _selectedBrowserNode != browserNode)
        {
            PushUnifiedNav();
        }

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

            ViewWorldspaceButton.Visibility = browserNode.DataObject is WorldspaceRecord
                ? Visibility.Visible
                : Visibility.Collapsed;

            // Show "View in World" for NPC/Creature records that have world placements
            ViewInWorldButton.Visibility = HasWorldPlacements(browserNode.DataObject)
                ? Visibility.Visible
                : Visibility.Collapsed;

            // Show "View NPC" for NPC_ records when NPC browser is available
            ViewNpcButton.Visibility = browserNode.DataObject is NpcRecord && _session.HasEsmRecords
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        else
        {
            SelectedRecordTitle.Text = browserNode.DisplayName;
            PropertyPanel.Children.Clear();
            GoToOffsetButton.Visibility = Visibility.Collapsed;
            ViewWorldspaceButton.Visibility = Visibility.Collapsed;
            ViewInWorldButton.Visibility = Visibility.Collapsed;
            ViewNpcButton.Visibility = Visibility.Collapsed;
        }
    }

    private void GoToRecordOffset_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedBrowserNode?.FileOffset is > 0)
        {
            PushUnifiedNav();
            SubTabView.SelectedIndex = 0; // Switch to Memory Map
            HexViewer.NavigateToOffset(_selectedBrowserNode.FileOffset.Value);
        }
    }

    /// <summary>
    ///     Returns true if the record has world placements in the placement index.
    /// </summary>
    private bool HasWorldPlacements(object? dataObject)
    {
        if (_placementIndex == null || dataObject == null)
        {
            return false;
        }

        var formId = dataObject switch
        {
            NpcRecord npc => npc.FormId,
            CreatureRecord crea => crea.FormId,
            _ => 0u
        };

        return formId != 0 && _placementIndex.ContainsKey(formId);
    }

    #endregion
}
