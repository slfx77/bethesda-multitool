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
    // Known file types that can be extracted
    private static readonly string[] KnownFileTypes =
    [
        "DDS", "DDX (3XDO)", "DDX (3XDR)", "PNG", "XMA", "NIF",
        "Module", "XDBF", "XUI", "ESP", "LIP", "SDT", "ObScript"
    ];

    private readonly ObservableCollection<CarvedFileEntry> _carvedFiles = [];
    private readonly Dictionary<string, CheckBox> _fileTypeCheckboxes = [];
    private AnalysisResult? _analysisResult;

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
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CRASH] SingleFileTab constructor failed: {ex}");
            throw;
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
                _carvedFiles.Add(new CarvedFileEntry
                {
                    Offset = entry.Offset,
                    Length = entry.Length,
                    FileType = entry.FileType,
                    IsExtracted = false
                });

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

            // Get selected file types
            var selectedTypes = _fileTypeCheckboxes
                .Where(kvp => kvp.Value.IsChecked == true)
                .Select(kvp => kvp.Key)
                .ToList();

            var inputPath = MinidumpPathTextBox.Text;
            var outputPath = OutputPathTextBox.Text;

            Console.WriteLine("[Extraction] Starting extraction");
            Console.WriteLine($"[Extraction] Input: {inputPath}");
            Console.WriteLine($"[Extraction] Output: {outputPath}");
            Console.WriteLine($"[Extraction] Selected types: {string.Join(", ", selectedTypes)}");
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

                    // Update extracted status in the table
                    if (p.CurrentFile != null)
                    {
                        var entry = _carvedFiles.FirstOrDefault(f => f.Offset == p.CurrentFile.Offset);
                        if (entry != null) entry.IsExtracted = p.CurrentFile.IsExtracted;
                    }
                });
            });

            var summary = await Task.Run(() => MemoryDumpExtractor.Extract(
                inputPath,
                options,
                progress));

            Console.WriteLine($"[Extraction] Complete: {summary.TotalExtracted} files");
            Console.WriteLine($"[Extraction] DDX converted: {summary.DdxConverted}, failed: {summary.DdxFailed}");

            var summaryMessage = $"Extraction complete!\n\n" +
                                 $"Files extracted: {summary.TotalExtracted}\n";

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
    private bool _isExtracted;
    public long Offset { get; set; }
    public long Length { get; set; }
    public string FileType { get; set; } = "";

    public bool IsExtracted
    {
        get => _isExtracted;
        set
        {
            if (_isExtracted != value)
            {
                _isExtracted = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExtracted)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExtractedGlyph)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExtractedColor)));
            }
        }
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

    public string ExtractedGlyph => IsExtracted ? "\uE73E" : "\uE711"; // Checkmark or X

    public Brush ExtractedColor => IsExtracted
        ? new SolidColorBrush(Colors.Green)
        : new SolidColorBrush(Colors.Gray);

    public event PropertyChangedEventHandler? PropertyChanged;
}
