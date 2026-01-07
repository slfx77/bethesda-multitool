using System.Collections.ObjectModel;
using System.ComponentModel;
using Windows.Storage.Pickers;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Xbox360MemoryCarver.Core.Formats.Nif;

namespace Xbox360MemoryCarver;

/// <summary>
///     Data model for NIF file entries in the list.
/// </summary>
public sealed class NifFileEntry : INotifyPropertyChanged
{
    private bool _isSelected = true;
    private string _status = "Pending";
    private string _formatDescription = "";
    private SolidColorBrush _statusColor = new(Colors.Gray);
    private SolidColorBrush _formatColor = new(Colors.Gray);

    public required string FullPath { get; init; }
    public required string RelativePath { get; init; }
    public required long FileSize { get; init; }

    public string FileSizeDisplay => FileSize switch
    {
        < 1024 => $"{FileSize} B",
        < 1024 * 1024 => $"{FileSize / 1024.0:F1} KB",
        _ => $"{FileSize / (1024.0 * 1024.0):F2} MB"
    };

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged(nameof(Status));
            }
        }
    }

    public SolidColorBrush StatusColor
    {
        get => _statusColor;
        set
        {
            if (_statusColor != value)
            {
                _statusColor = value;
                OnPropertyChanged(nameof(StatusColor));
            }
        }
    }

    public string FormatDescription
    {
        get => _formatDescription;
        set
        {
            if (_formatDescription != value)
            {
                _formatDescription = value;
                OnPropertyChanged(nameof(FormatDescription));
            }
        }
    }

    public SolidColorBrush FormatColor
    {
        get => _formatColor;
        set
        {
            if (_formatColor != value)
            {
                _formatColor = value;
                OnPropertyChanged(nameof(FormatColor));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
///     Tab for batch converting Xbox 360 NIF files to PC format.
/// </summary>
public sealed partial class NifConverterTab : UserControl, IDisposable
{
    private readonly ObservableCollection<NifFileEntry> _nifFiles = [];
    private readonly NifConverter _converter = new(verbose: false);
    private CancellationTokenSource? _cts;

    public NifConverterTab()
    {
        InitializeComponent();
        NifFilesListView.ItemsSource = _nifFiles;
    }

    public void Dispose() => _cts?.Dispose();

    private void UpdateButtonStates()
    {
        var hasOutput = !string.IsNullOrEmpty(OutputDirectoryTextBox.Text);
        var hasSelected = _nifFiles.Any(f => f.IsSelected);
        ConvertButton.IsEnabled = hasOutput && hasSelected && (_cts == null || _cts.IsCancellationRequested);
        CancelButton.IsEnabled = _cts != null && !_cts.IsCancellationRequested;
    }

    private void UpdateFileCount()
    {
        var total = _nifFiles.Count;
        var selected = _nifFiles.Count(f => f.IsSelected);
        FileCountTextBlock.Text = $"({selected}/{total} selected)";
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

    private async void BrowseInputButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null)
        {
            return;
        }

        InputDirectoryTextBox.Text = folder.Path;
        if (string.IsNullOrEmpty(OutputDirectoryTextBox.Text))
        {
            OutputDirectoryTextBox.Text = Path.Combine(folder.Path, "converted_pc");
        }

        await ScanForNifFilesAsync(folder.Path);
    }

    private async void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            OutputDirectoryTextBox.Text = folder.Path;
            UpdateButtonStates();
        }
    }

    private async Task ScanForNifFilesAsync(string directory)
    {
        _nifFiles.Clear();
        StatusTextBlock.Text = "Scanning for NIF files...";

        await Task.Run(() =>
        {
            var nifFiles = Directory.EnumerateFiles(directory, "*.nif", SearchOption.AllDirectories)
                .ToList();

            foreach (var filePath in nifFiles)
            {
                var relativePath = Path.GetRelativePath(directory, filePath);
                var fileInfo = new FileInfo(filePath);

                // Quick check for Xbox 360 format - determine format on background thread
                string formatDesc;
                byte endianByte = 255; // Invalid default
                try
                {
                    var headerBytes = new byte[50];
                    using var fs = File.OpenRead(filePath);
                    _ = fs.Read(headerBytes, 0, 50);

                    var newlinePos = Array.IndexOf(headerBytes, (byte)0x0A, 0, 50);
                    if (newlinePos > 0 && newlinePos + 5 < 50)
                    {
                        endianByte = headerBytes[newlinePos + 5];
                        formatDesc = endianByte switch
                        {
                            0 => "Xbox 360 (BE)",
                            1 => "PC (LE)",
                            _ => "Unknown"
                        };
                    }
                    else
                    {
                        formatDesc = "Invalid";
                    }
                }
                catch
                {
                    formatDesc = "Error";
                }

                var fileSize = fileInfo.Length;
                var isXbox360 = formatDesc == "Xbox 360 (BE)";

                // Create UI objects on UI thread
                DispatcherQueue.TryEnqueue(() =>
                {
                    var formatColor = formatDesc switch
                    {
                        "Xbox 360 (BE)" => new SolidColorBrush(Colors.Orange),
                        "PC (LE)" => new SolidColorBrush(Colors.Green),
                        "Invalid" or "Error" => new SolidColorBrush(Colors.Red),
                        _ => new SolidColorBrush(Colors.Gray)
                    };

                    var entry = new NifFileEntry
                    {
                        FullPath = filePath,
                        RelativePath = relativePath,
                        FileSize = fileSize,
                        FormatDescription = formatDesc,
                        FormatColor = formatColor,
                        IsSelected = isXbox360
                    };

                    _nifFiles.Add(entry);
                });
            }
        });

        UpdateFileCount();
        UpdateButtonStates();
        StatusTextBlock.Text = $"Found {_nifFiles.Count} NIF files. {_nifFiles.Count(f => f.FormatDescription == "Xbox 360 (BE)")} require conversion.";
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var file in _nifFiles)
        {
            file.IsSelected = true;
        }
        UpdateFileCount();
        UpdateButtonStates();
    }

    private void SelectNoneButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var file in _nifFiles)
        {
            file.IsSelected = false;
        }
        UpdateFileCount();
        UpdateButtonStates();
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedFiles = _nifFiles.Where(f => f.IsSelected).ToList();
        if (selectedFiles.Count == 0)
        {
            await ShowDialogAsync("No Files Selected", "Please select at least one NIF file to convert.");
            return;
        }

        var outputDir = OutputDirectoryTextBox.Text;
        var inputDir = InputDirectoryTextBox.Text;
        var preserveStructure = PreserveStructureCheckBox.IsChecked == true;
        var overwrite = OverwriteExistingCheckBox.IsChecked == true;

        _cts = new CancellationTokenSource();
        UpdateButtonStates();

        ConversionProgressBar.Visibility = Visibility.Visible;
        ConversionProgressBar.Maximum = selectedFiles.Count;
        ConversionProgressBar.Value = 0;

        var converted = 0;
        var skipped = 0;
        var failed = 0;

        try
        {
            Directory.CreateDirectory(outputDir);

            for (var i = 0; i < selectedFiles.Count; i++)
            {
                if (_cts.Token.IsCancellationRequested)
                {
                    break;
                }

                var file = selectedFiles[i];
                StatusTextBlock.Text = $"Converting {i + 1}/{selectedFiles.Count}: {file.RelativePath}";
                file.Status = "Converting...";
                file.StatusColor = new SolidColorBrush(Colors.Yellow);

                try
                {
                    // Determine output path
                    string outputPath;
                    if (preserveStructure)
                    {
                        var relativePath = Path.GetRelativePath(inputDir, file.FullPath);
                        outputPath = Path.Combine(outputDir, relativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    }
                    else
                    {
                        outputPath = Path.Combine(outputDir, Path.GetFileName(file.FullPath));
                    }

                    // Check if output exists
                    if (File.Exists(outputPath) && !overwrite)
                    {
                        file.Status = "Skipped (exists)";
                        file.StatusColor = new SolidColorBrush(Colors.Gray);
                        skipped++;
                        continue;
                    }

                    // Read and convert
                    var inputData = await File.ReadAllBytesAsync(file.FullPath, _cts.Token);
                    var result = await Task.Run(() => _converter.Convert(inputData), _cts.Token);

                    if (result.Success && result.OutputData != null)
                    {
                        await File.WriteAllBytesAsync(outputPath, result.OutputData, _cts.Token);

                        file.Status = "Converted";
                        file.StatusColor = new SolidColorBrush(Colors.Green);
                        converted++;
                    }
                    else
                    {
                        file.Status = result.ErrorMessage ?? "Failed";
                        file.StatusColor = new SolidColorBrush(Colors.Red);
                        failed++;
                    }
                }
                catch (OperationCanceledException)
                {
                    file.Status = "Cancelled";
                    file.StatusColor = new SolidColorBrush(Colors.Orange);
                    throw;
                }
                catch (Exception ex)
                {
                    file.Status = $"Error: {ex.Message}";
                    file.StatusColor = new SolidColorBrush(Colors.Red);
                    failed++;
                }

                ConversionProgressBar.Value = i + 1;
            }

            StatusTextBlock.Text = $"Conversion complete. Converted: {converted}, Skipped: {skipped}, Failed: {failed}";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Conversion cancelled.";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            ConversionProgressBar.Visibility = Visibility.Collapsed;
            UpdateButtonStates();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusTextBlock.Text = "Cancelling...";
    }
}
