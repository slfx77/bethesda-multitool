using System.Collections.ObjectModel;
using System.ComponentModel;
using Windows.Storage.Pickers;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Xbox360MemoryCarver.Core;

namespace Xbox360MemoryCarver.App;

/// <summary>
///     Single file analysis and extraction tab.
/// </summary>
public sealed partial class SingleFileTab : UserControl
{
    // Known file types that can be extracted - maps display name to signature key(s)
    private static readonly Dictionary<string, string[]> FileTypeMapping = new()
    {
        ["DDS"] = ["dds"],
        ["DDX (3XDO)"] = ["ddx_3xdo"],
        ["DDX (3XDR)"] = ["ddx_3xdr"],
        ["PNG"] = ["png"],
        ["XMA"] = ["xma"],
        ["NIF"] = ["nif"],
        ["Module"] = ["xex"], // Module maps to XEX executables
        ["XDBF"] = ["xdbf"],
        ["XUI"] = ["xui_scene", "xui_binary"], // XUI has two variants
        ["ESP"] = ["esp"],
        ["LIP"] = ["lip"],
        ["ObScript"] = ["script_scn"]
    };

    // Display names for checkboxes (keys from the mapping)
    private static readonly string[] KnownFileTypes = [.. FileTypeMapping.Keys];
    private readonly List<CarvedFileEntry> _allCarvedFiles = []; // Original unsorted list

    private readonly ObservableCollection<CarvedFileEntry> _carvedFiles = [];
    private readonly Dictionary<string, CheckBox> _fileTypeCheckboxes = [];
    private AnalysisResult? _analysisResult;

    private SortColumn _currentSortColumn = SortColumn.None;
    private bool _sortAscending = true;

    public SingleFileTab()
    {
        try
        {
            Console.WriteLine("[SingleFileTab] Constructor starting...");
            InitializeComponent();
            Console.WriteLine("[SingleFileTab] InitializeComponent complete");
            ResultsListView.ItemsSource = _carvedFiles;
            InitializeFileTypeCheckboxes();
            Console.WriteLine("[SingleFileTab] Constructor complete");

            // Check for auto-load file from command line
            Loaded += SingleFileTab_Loaded;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CRASH] SingleFileTab constructor failed: {ex}");
            throw;
        }
    }

    private async void SingleFileTab_Loaded(object sender, RoutedEventArgs e)
    {
        // Only run once
        Loaded -= SingleFileTab_Loaded;

        // Check if we should auto-load a file
        var autoLoadFile = Program.AutoLoadFile;
        if (!string.IsNullOrEmpty(autoLoadFile) && File.Exists(autoLoadFile))
        {
            Console.WriteLine($"[SingleFileTab] Auto-loading file: {autoLoadFile}");
            MinidumpPathTextBox.Text = autoLoadFile;

            // Set default output path
            var directory = Path.GetDirectoryName(autoLoadFile);
            var fileName = Path.GetFileNameWithoutExtension(autoLoadFile);
            OutputPathTextBox.Text = Path.Combine(directory ?? "", $"{fileName}_extracted");

            UpdateButtonStates();

            // Auto-start analysis after a short delay to let UI settle
            await Task.Delay(500);
            if (AnalyzeButton.IsEnabled)
            {
                Console.WriteLine("[SingleFileTab] Auto-starting analysis...");
                AnalyzeButton_Click(this, new RoutedEventArgs());
            }
        }
    }

    private void InitializeFileTypeCheckboxes()
    {
        FileTypeCheckboxPanel.Children.Clear();
        _fileTypeCheckboxes.Clear();

        foreach (var fileType in KnownFileTypes)
        {
            var checkbox = new CheckBox
            {
                Content = fileType,
                IsChecked = true,
                Margin = new Thickness(0, 0, 8, 0)
            };
            _fileTypeCheckboxes[fileType] = checkbox;
            FileTypeCheckboxPanel.Children.Add(checkbox);
        }
    }

#pragma warning disable RCS1163 // Unused parameter - required for event handler signature
    private void MinidumpPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateButtonStates();
    }
#pragma warning restore RCS1163

    private void UpdateButtonStates()
    {
        var hasValidPath = !string.IsNullOrEmpty(MinidumpPathTextBox.Text)
                           && File.Exists(MinidumpPathTextBox.Text)
                           && MinidumpPathTextBox.Text.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase);

        AnalyzeButton.IsEnabled = hasValidPath;
        ExtractButton.IsEnabled =
            hasValidPath && _analysisResult != null && !string.IsNullOrEmpty(OutputPathTextBox.Text);
    }

    private async Task ShowDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

#pragma warning disable RCS1163 // Unused parameter - required for event handler signature
    private void ResultsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // When a file is selected in the list, navigate the hex viewer to that offset
        if (ResultsListView.SelectedItem is CarvedFileEntry selectedFile)
            HexViewer.NavigateToOffset(selectedFile.Offset);
    }
#pragma warning restore RCS1163

    // Sorting state
    private enum SortColumn
    {
        None,
        Offset,
        Length,
        Type,
        Filename
    }

    #region Sorting

    private void SortByOffset_Click(object sender, RoutedEventArgs e)
    {
        ApplySort(SortColumn.Offset);
    }

    private void SortByLength_Click(object sender, RoutedEventArgs e)
    {
        ApplySort(SortColumn.Length);
    }

    private void SortByType_Click(object sender, RoutedEventArgs e)
    {
        ApplySort(SortColumn.Type);
    }

    private void SortByFilename_Click(object sender, RoutedEventArgs e)
    {
        ApplySort(SortColumn.Filename);
    }

    private void ApplySort(SortColumn column)
    {
        // Cycle through: Ascending -> Descending -> Default (by offset)
        if (_currentSortColumn == column)
        {
            if (_sortAscending)
            {
                _sortAscending = false;
            }
            else
            {
                // Reset to default sort (by offset ascending)
                _currentSortColumn = SortColumn.None;
                _sortAscending = true;
            }
        }
        else
        {
            _currentSortColumn = column;
            _sortAscending = true;
        }

        UpdateSortIcons();
        RefreshSortedList();
    }

    private void UpdateSortIcons()
    {
        // Hide all icons first
        OffsetSortIcon.Visibility = Visibility.Collapsed;
        LengthSortIcon.Visibility = Visibility.Collapsed;
        TypeSortIcon.Visibility = Visibility.Collapsed;
        FilenameSortIcon.Visibility = Visibility.Collapsed;

        // Show the active sort icon
        var activeIcon = _currentSortColumn switch
        {
            SortColumn.Offset => OffsetSortIcon,
            SortColumn.Length => LengthSortIcon,
            SortColumn.Type => TypeSortIcon,
            SortColumn.Filename => FilenameSortIcon,
            _ => null
        };

        if (activeIcon != null)
        {
            activeIcon.Visibility = Visibility.Visible;
            activeIcon.Glyph = _sortAscending ? "\uE70E" : "\uE70D"; // Up or Down arrow
        }
    }

    private void RefreshSortedList()
    {
        IEnumerable<CarvedFileEntry> sorted = _currentSortColumn switch
        {
            SortColumn.Offset => _sortAscending
                ? _allCarvedFiles.OrderBy(f => f.Offset)
                : _allCarvedFiles.OrderByDescending(f => f.Offset),
            SortColumn.Length => _sortAscending
                ? _allCarvedFiles.OrderBy(f => f.Length)
                : _allCarvedFiles.OrderByDescending(f => f.Length),
            // Sort by FileType string for contiguous grouping
            SortColumn.Type => _sortAscending
                ? _allCarvedFiles.OrderBy(f => f.FileType, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Offset)
                : _allCarvedFiles.OrderByDescending(f => f.FileType, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.Offset),
            // Sort by Filename, with nulls/empty at the end
            SortColumn.Filename => _sortAscending
                ? _allCarvedFiles.OrderBy(f => string.IsNullOrEmpty(f.FileName) ? 1 : 0)
                    .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.Offset)
                : _allCarvedFiles.OrderBy(f => string.IsNullOrEmpty(f.FileName) ? 1 : 0)
                    .ThenByDescending(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.Offset),
            _ => _allCarvedFiles.OrderBy(f => f.Offset) // Default sort by offset
        };

        _carvedFiles.Clear();
        foreach (var file in sorted) _carvedFiles.Add(file);
    }

    #endregion

#pragma warning disable RCS1163 // Unused parameter - required for event handler signature
    private async void OpenMinidumpButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add(".dmp");

        // Get the window handle for the picker
        var hwnd = WindowNative.GetWindowHandle(App.Current.MainWindow);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            MinidumpPathTextBox.Text = file.Path;

            // Auto-set output path if not set
            if (string.IsNullOrEmpty(OutputPathTextBox.Text))
            {
                var directory = Path.GetDirectoryName(file.Path);
                var fileName = Path.GetFileNameWithoutExtension(file.Path);
                OutputPathTextBox.Text = Path.Combine(directory ?? "", $"{fileName}_extracted");
            }

            // Reset analysis state
            _analysisResult = null;
            _carvedFiles.Clear();
            _allCarvedFiles.Clear();
            HexViewer.Clear();
            UpdateButtonStates();
        }
    }

    private async void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add("*");

        var hwnd = WindowNative.GetWindowHandle(App.Current.MainWindow);
        InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            OutputPathTextBox.Text = folder.Path;
            UpdateButtonStates();
        }
    }

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(MinidumpPathTextBox.Text)) return;

        try
        {
            AnalyzeButton.IsEnabled = false;
            AnalysisProgressBar.Visibility = Visibility.Visible;
            _carvedFiles.Clear();
            _allCarvedFiles.Clear();

            // Reset sort state
            _currentSortColumn = SortColumn.None;
            _sortAscending = true;
            UpdateSortIcons();

            var filePath = MinidumpPathTextBox.Text;

            // Verify file exists and is accessible
            if (!File.Exists(filePath))
            {
                await ShowDialogAsync("Analysis Failed", $"File not found: {filePath}");
                return;
            }

            Console.WriteLine($"[Analysis] Starting analysis of {filePath}");
            var analyzer = new MemoryDumpAnalyzer();
            var progress = new Progress<AnalysisProgress>(p =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    AnalysisProgressBar.IsIndeterminate = false;
                    AnalysisProgressBar.Value = p.PercentComplete;
                });
            });

            _analysisResult = await Task.Run(() =>
                analyzer.Analyze(filePath, progress));

            Console.WriteLine(
                $"[Analysis] Complete: {_analysisResult.CarvedFiles.Count} files found in {_analysisResult.AnalysisTime.TotalSeconds:F2}s");

            // Populate the results table
            foreach (var entry in _analysisResult.CarvedFiles)
            {
                var carvedEntry = new CarvedFileEntry
                {
                    Offset = entry.Offset,
                    Length = entry.Length,
                    FileType = entry.FileType,
                    FileName = entry.FileName,
                    IsExtracted = false
                };
                _allCarvedFiles.Add(carvedEntry);
                _carvedFiles.Add(carvedEntry);
            }

            // Update the hex viewer
            HexViewer.LoadData(filePath, _analysisResult);

            UpdateButtonStates();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Analysis] Error: {ex.Message}");
            var fullError = $"{ex.GetType().Name}: {ex.Message}";
            if (ex.InnerException != null) fullError += $"\n\nInner: {ex.InnerException.Message}";

            fullError += $"\n\nStack trace:\n{ex.StackTrace}";
            await ShowDialogAsync("Analysis Failed", fullError);
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
        if (_analysisResult == null || string.IsNullOrEmpty(OutputPathTextBox.Text)) return;

        try
        {
            ExtractButton.IsEnabled = false;
            AnalysisProgressBar.Visibility = Visibility.Visible;

            // Get selected file types and map display names to signature keys
            var selectedTypes = _fileTypeCheckboxes
                .Where(kvp => kvp.Value.IsChecked == true)
                .SelectMany(kvp => FileTypeMapping.TryGetValue(kvp.Key, out var sigKeys) ? sigKeys : [])
                .ToList();

            var inputPath = MinidumpPathTextBox.Text;
            var outputPath = OutputPathTextBox.Text;

            Console.WriteLine("[Extraction] Starting extraction");
            Console.WriteLine($"[Extraction] Input: {inputPath}");
            Console.WriteLine($"[Extraction] Output: {outputPath}");
            Console.WriteLine($"[Extraction] Selected types (signature keys): {string.Join(", ", selectedTypes)}");
            Console.WriteLine(
                $"[Extraction] ConvertDdx: {ConvertDdxCheckBox.IsChecked}, SaveAtlas: {SaveAtlasCheckBox.IsChecked}, Verbose: {VerboseCheckBox.IsChecked}");

            var options = new ExtractionOptions
            {
                OutputPath = outputPath,
                ConvertDdx = ConvertDdxCheckBox.IsChecked == true,
                SaveAtlas = SaveAtlasCheckBox.IsChecked == true,
                Verbose = VerboseCheckBox.IsChecked == true,
                FileTypes = selectedTypes
            };

            var progress = new Progress<ExtractionProgress>(p =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    AnalysisProgressBar.IsIndeterminate = false;
                    AnalysisProgressBar.Value = p.PercentComplete;
                });
            });

            var summary = await Task.Run(() => MemoryDumpExtractor.Extract(
                inputPath,
                options,
                progress));

            Console.WriteLine($"[Extraction] Complete: {summary.TotalExtracted} files");
            Console.WriteLine($"[Extraction] DDX converted: {summary.DdxConverted}, failed: {summary.DdxFailed}");

            // Update file status in the table based on extraction results
            foreach (var entry in _allCarvedFiles.Where(e => summary.ExtractedOffsets.Contains(e.Offset)))
                entry.Status = ExtractionStatus.Extracted;

            var summaryMessage = $"Extraction complete!\n\n" +
                                 $"Files extracted: {summary.TotalExtracted}\n";

            if (summary.ModulesExtracted > 0)
                summaryMessage += $"Modules extracted: {summary.ModulesExtracted}\n";

            if (summary.DdxConverted > 0 || summary.DdxFailed > 0)
                summaryMessage += $"\nDDX to DDS conversion:\n" +
                                  $"  - Converted: {summary.DdxConverted}\n" +
                                  $"  - Failed: {summary.DdxFailed}";

            summaryMessage += $"\n\nOutput: {outputPath}";

            await ShowDialogAsync("Extraction Complete", summaryMessage);
        }
        catch (Exception ex)
        {
            var fullError = $"{ex.GetType().Name}: {ex.Message}";
            if (ex.InnerException != null) fullError += $"\n\nInner: {ex.InnerException.Message}";

            fullError += $"\n\nStack trace:\n{ex.StackTrace}";
            await ShowDialogAsync("Extraction Failed", fullError);
        }
        finally
        {
            ExtractButton.IsEnabled = true;
            AnalysisProgressBar.Visibility = Visibility.Collapsed;
            AnalysisProgressBar.IsIndeterminate = true;
        }
    }
#pragma warning restore RCS1163
}

/// <summary>
///     Represents a carved file entry in the results table.
/// </summary>
public partial class CarvedFileEntry : INotifyPropertyChanged
{
    private ExtractionStatus _status = ExtractionStatus.NotExtracted;
    public long Offset { get; set; }
    public long Length { get; set; }
    public string FileType { get; set; } = "";
    public string? FileName { get; set; }

    /// <summary>
    ///     Gets a display name - filename if available, otherwise the file type.
    /// </summary>
    public string DisplayName => !string.IsNullOrEmpty(FileName) ? FileName : FileType;

    /// <summary>
    ///     Gets the filename for display, or empty string if none.
    /// </summary>
    public string FileNameDisplay => FileName ?? "";

    public ExtractionStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExtractedGlyph)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExtractedColor)));
            }
        }
    }

    // Legacy property for compatibility
    public bool IsExtracted
    {
        get => _status == ExtractionStatus.Extracted;
        set => Status = value ? ExtractionStatus.Extracted : ExtractionStatus.NotExtracted;
    }

    public string OffsetHex => $"0x{Offset:X8}";

    public string LengthFormatted
    {
        get
        {
            if (Length >= 1024 * 1024) return $"{Length / (1024.0 * 1024.0):F2} MB";

            if (Length >= 1024) return $"{Length / 1024.0:F2} KB";

            return $"{Length} B";
        }
    }

    public string ExtractedGlyph => _status switch
    {
        ExtractionStatus.Extracted => "\uE73E", // Checkmark
        ExtractionStatus.Failed => "\uE711", // X
        _ => "\uE8FB" // More (horizontal dots) - pending/not extracted
    };

    public Brush ExtractedColor => _status switch
    {
        ExtractionStatus.Extracted => new SolidColorBrush(Colors.Green),
        ExtractionStatus.Failed => new SolidColorBrush(Colors.Red),
        _ => new SolidColorBrush(Colors.Gray)
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}

public enum ExtractionStatus
{
    NotExtracted,
    Extracted,
    Failed
}
