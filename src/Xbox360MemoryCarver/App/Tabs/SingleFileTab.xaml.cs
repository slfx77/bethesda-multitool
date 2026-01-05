using System.Collections.ObjectModel;
using Windows.Storage.Pickers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;



using Xbox360MemoryCarver.Core;

namespace Xbox360MemoryCarver;

/// <summary>
///     Single file analysis and extraction tab.
/// </summary>
public sealed partial class SingleFileTab : UserControl
{
    private readonly List<CarvedFileEntry> _allCarvedFiles = [];
    private readonly ObservableCollection<CarvedFileEntry> _carvedFiles = [];
    private readonly Dictionary<string, CheckBox> _fileTypeCheckboxes = [];
    private readonly CarvedFilesSorter _sorter = new();
    private AnalysisResult? _analysisResult;
    private CarvedFileEntry? _contextMenuTarget;
    private string? _lastInputPath;

    public SingleFileTab()
    {
        InitializeComponent();
        ResultsListView.ItemsSource = _carvedFiles;
        InitializeFileTypeCheckboxes();
        Loaded += SingleFileTab_Loaded;
    }

    private async void SingleFileTab_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= SingleFileTab_Loaded;
        var autoLoadFile = Program.AutoLoadFile;
        if (string.IsNullOrEmpty(autoLoadFile) || !File.Exists(autoLoadFile)) return;

        MinidumpPathTextBox.Text = autoLoadFile;
        UpdateOutputPathFromInput(autoLoadFile);
        UpdateButtonStates();
        await Task.Delay(500);
        if (AnalyzeButton.IsEnabled) AnalyzeButton_Click(this, new RoutedEventArgs());
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

    private void MinidumpPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var currentPath = MinidumpPathTextBox.Text;
        if (!string.IsNullOrEmpty(currentPath) && currentPath != _lastInputPath &&
            File.Exists(currentPath) && currentPath.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
        {
            UpdateOutputPathFromInput(currentPath);
            _lastInputPath = currentPath;
            if (_analysisResult != null)
            {
                _analysisResult = null;
                _carvedFiles.Clear();
                _allCarvedFiles.Clear();
                HexViewer.Clear();
            }
        }

        UpdateButtonStates();
    }

    private void OutputPathTextBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateButtonStates();

    private void UpdateOutputPathFromInput(string inputPath)
    {
        var dir = Path.GetDirectoryName(inputPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(inputPath);
        OutputPathTextBox.Text = Path.Combine(dir, $"{name}_extracted");
    }

    private void UpdateButtonStates()
    {
        var valid = !string.IsNullOrEmpty(MinidumpPathTextBox.Text) && File.Exists(MinidumpPathTextBox.Text) &&
                    MinidumpPathTextBox.Text.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase);
        AnalyzeButton.IsEnabled = valid;
        ExtractButton.IsEnabled = valid && _analysisResult != null && !string.IsNullOrEmpty(OutputPathTextBox.Text);
    }

    private async Task ShowDialogAsync(string title, string message) => await new ContentDialog
        { Title = title, Content = message, CloseButtonText = "OK", XamlRoot = XamlRoot }.ShowAsync();

    private void ResultsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsListView.SelectedItem is CarvedFileEntry f) HexViewer.NavigateToOffset(f.Offset);
    }

    private async void OpenMinidumpButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".dmp");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        MinidumpPathTextBox.Text = file.Path;
        _analysisResult = null;
        _carvedFiles.Clear();
        _allCarvedFiles.Clear();
        HexViewer.Clear();
        UpdateButtonStates();
    }

    private async void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            OutputPathTextBox.Text = folder.Path;
            UpdateButtonStates();
        }
    }

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        var filePath = MinidumpPathTextBox.Text;
        if (string.IsNullOrEmpty(filePath)) return;
        try
        {
            AnalyzeButton.IsEnabled = false;
            AnalysisProgressBar.Visibility = Visibility.Visible;
            _carvedFiles.Clear();
            _allCarvedFiles.Clear();
            _sorter.Reset();
            UpdateSortIcons();

            if (!File.Exists(filePath))
            {
                await ShowDialogAsync("Analysis Failed", $"File not found: {filePath}");
                return;
            }

            var progress = new Progress<AnalysisProgress>(p => DispatcherQueue.TryEnqueue(() =>
            {
                AnalysisProgressBar.IsIndeterminate = false;
                AnalysisProgressBar.Value = p.PercentComplete;
            }));
            _analysisResult = await Task.Run(() => new MemoryDumpAnalyzer().Analyze(filePath, progress));

            foreach (var entry in _analysisResult.CarvedFiles)
            {
                var item = new CarvedFileEntry
                {
                    Offset = entry.Offset, Length = entry.Length, FileType = entry.FileType, FileName = entry.FileName
                };
                _allCarvedFiles.Add(item);
                _carvedFiles.Add(item);
            }

            HexViewer.LoadData(filePath, _analysisResult);
            UpdateButtonStates();
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("Analysis Failed", $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}");
        }
        finally
        {
            AnalyzeButton.IsEnabled = true;
            AnalysisProgressBar.Visibility = Visibility.Collapsed;
            AnalysisProgressBar.IsIndeterminate = true;
        }
    }

    private async void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        var filePath = MinidumpPathTextBox.Text;
        var outputPath = OutputPathTextBox.Text;
        if (_analysisResult == null || string.IsNullOrEmpty(outputPath)) return;
        try
        {
            ExtractButton.IsEnabled = false;
            AnalysisProgressBar.Visibility = Visibility.Visible;
            var types = FileTypeMapping
                .GetSignatureIds(_fileTypeCheckboxes.Where(kvp => kvp.Value.IsChecked == true).Select(kvp => kvp.Key))
                .ToList();
            var opts = new ExtractionOptions
            {
                OutputPath = outputPath,
                ConvertDdx = ConvertDdxCheckBox.IsChecked == true,
                SaveAtlas = SaveAtlasCheckBox.IsChecked == true,
                Verbose = VerboseCheckBox.IsChecked == true,
                FileTypes = types
            };
            var progress = new Progress<ExtractionProgress>(p => DispatcherQueue.TryEnqueue(() =>
            {
                AnalysisProgressBar.IsIndeterminate = false;
                AnalysisProgressBar.Value = p.PercentComplete;
            }));
            var summary = await Task.Run(() => MemoryDumpExtractor.Extract(filePath, opts, progress));

            foreach (var entry in _allCarvedFiles.Where(x => summary.ExtractedOffsets.Contains(x.Offset)))
                entry.Status = ExtractionStatus.Extracted;

            var msg = $"Extraction complete!\n\nFiles extracted: {summary.TotalExtracted}\n";
            if (summary.ModulesExtracted > 0) msg += $"Modules extracted: {summary.ModulesExtracted}\n";
            if (summary.ScriptsExtracted > 0)
                msg +=
                    $"Scripts extracted: {summary.ScriptsExtracted} ({summary.ScriptQuestsGrouped} quests grouped)\n";
            if (summary.DdxConverted > 0 || summary.DdxFailed > 0)
                msg += $"\nDDX conversion: {summary.DdxConverted} ok, {summary.DdxFailed} failed";
            await ShowDialogAsync("Extraction Complete", msg + $"\n\nOutput: {outputPath}");
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("Extraction Failed", $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}");
        }
        finally
        {
            ExtractButton.IsEnabled = true;
            AnalysisProgressBar.Visibility = Visibility.Collapsed;
            AnalysisProgressBar.IsIndeterminate = true;
        }
    }

    #region Sorting

    private void SortByOffset_Click(object sender, RoutedEventArgs e) => ApplySort(CarvedFilesSorter.SortColumn.Offset);
    private void SortByLength_Click(object sender, RoutedEventArgs e) => ApplySort(CarvedFilesSorter.SortColumn.Length);
    private void SortByType_Click(object sender, RoutedEventArgs e) => ApplySort(CarvedFilesSorter.SortColumn.Type);

    private void SortByFilename_Click(object sender, RoutedEventArgs e) =>
        ApplySort(CarvedFilesSorter.SortColumn.Filename);

    private void ApplySort(CarvedFilesSorter.SortColumn col)
    {
        _sorter.CycleSortState(col);
        UpdateSortIcons();
        RefreshSortedList();
    }

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

    #endregion

    #region Context Menu

    private void ResultsListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: CarvedFileEntry entry })
        {
            _contextMenuTarget = entry;
            CopyFilenameMenuItem.IsEnabled = !string.IsNullOrEmpty(entry.FileName);
        }
        else
        {
            _contextMenuTarget = ResultsListView.SelectedItem as CarvedFileEntry;
            CopyFilenameMenuItem.IsEnabled =
                _contextMenuTarget != null && !string.IsNullOrEmpty(_contextMenuTarget.FileName);
        }
    }

    private void GoToStart_Click(object sender, RoutedEventArgs e)
    {
        var target = _contextMenuTarget ?? ResultsListView.SelectedItem as CarvedFileEntry;
        if (target != null) HexViewer.NavigateToOffset(target.Offset);
    }

    private void GoToEnd_Click(object sender, RoutedEventArgs e)
    {
        var target = _contextMenuTarget ?? ResultsListView.SelectedItem as CarvedFileEntry;
        if (target != null)
        {
            var endOffset = target.Offset + target.Length - 16;
            if (endOffset > 0) HexViewer.NavigateToOffset(endOffset);
        }
    }

    private void CopyOffset_Click(object sender, RoutedEventArgs e)
    {
        var target = _contextMenuTarget ?? ResultsListView.SelectedItem as CarvedFileEntry;
        if (target != null) ClipboardHelper.CopyText($"0x{target.Offset:X8}");
    }

    private void CopyFilename_Click(object sender, RoutedEventArgs e)
    {
        var target = _contextMenuTarget ?? ResultsListView.SelectedItem as CarvedFileEntry;
        if (target != null && !string.IsNullOrEmpty(target.FileName)) ClipboardHelper.CopyText(target.FileName);
    }

    #endregion
}
