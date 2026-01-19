// Copyright (c) 2026 Xbox360MemoryCarver Contributors
// Licensed under the MIT License.

#if WINDOWS_GUI

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Channels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Xbox360MemoryCarver.Core.Converters;
using Xbox360MemoryCarver.Core.Formats.Bsa;

namespace Xbox360MemoryCarver;

/// <summary>
/// Status of a file during BSA extraction.
/// </summary>
public enum BsaExtractionStatus
{
    Pending,
    Extracting,
    Converting,
    Done,
    Skipped,
    Failed
}

/// <summary>
/// Data model for BSA file entries in the list.
/// </summary>
public sealed class BsaFileEntry : INotifyPropertyChanged
{
    private static SolidColorBrush? _grayBrush;
    private static SolidColorBrush? _greenBrush;
    private static SolidColorBrush? _yellowBrush;
    private static SolidColorBrush? _blueBrush;
    private static SolidColorBrush? _redBrush;

    private bool _isSelected = true;
    private BsaExtractionStatus _status = BsaExtractionStatus.Pending;
    private string? _statusMessage;

    private static SolidColorBrush GrayBrush => _grayBrush ??= new SolidColorBrush(Colors.Gray);
    private static SolidColorBrush GreenBrush => _greenBrush ??= new SolidColorBrush(Colors.Green);
    private static SolidColorBrush YellowBrush => _yellowBrush ??= new SolidColorBrush(Colors.Yellow);
    private static SolidColorBrush BlueBrush => _blueBrush ??= new SolidColorBrush(Colors.DodgerBlue);
    private static SolidColorBrush RedBrush => _redBrush ??= new SolidColorBrush(Colors.OrangeRed);

    public required BsaFileRecord Record { get; init; }

    public string FullPath => Record.FullPath;
    public string FileName => Record.Name ?? $"unknown_{Record.NameHash:X16}";
    public string FolderPath => Record.Folder?.Name ?? "";
    public long Size => Record.Size;
    public bool IsCompressed { get; init; }

    public string SizeDisplay => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:F1} KB",
        _ => $"{Size / (1024.0 * 1024.0):F2} MB"
    };

    public string CompressedDisplay => IsCompressed ? "Yes" : "";
    public SolidColorBrush CompressedColor => IsCompressed ? GreenBrush : GrayBrush;

    public string Extension => Path.GetExtension(FileName).ToLowerInvariant();

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

    public BsaExtractionStatus Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(StatusDisplay));
                OnPropertyChanged(nameof(StatusColor));
            }
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (_statusMessage != value)
            {
                _statusMessage = value;
                OnPropertyChanged(nameof(StatusMessage));
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }
    }

    public string StatusDisplay => _status switch
    {
        BsaExtractionStatus.Pending => "",
        BsaExtractionStatus.Extracting => "Extracting...",
        BsaExtractionStatus.Converting => "Converting...",
        BsaExtractionStatus.Done => _statusMessage ?? "Done",
        BsaExtractionStatus.Skipped => _statusMessage ?? "Skipped",
        BsaExtractionStatus.Failed => _statusMessage ?? "Failed",
        _ => ""
    };

    public SolidColorBrush StatusColor => _status switch
    {
        BsaExtractionStatus.Pending => GrayBrush,
        BsaExtractionStatus.Extracting => BlueBrush,
        BsaExtractionStatus.Converting => YellowBrush,
        BsaExtractionStatus.Done => GreenBrush,
        BsaExtractionStatus.Skipped => GrayBrush,
        BsaExtractionStatus.Failed => RedBrush,
        _ => GrayBrush
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// BSA Extractor tab for extracting files from Bethesda archives.
/// </summary>
public sealed partial class BsaExtractorTab : UserControl
{
    private readonly ObservableCollection<BsaFileEntry> _allFiles = [];
    private readonly ObservableCollection<BsaFileEntry> _filteredFiles = [];

    private BsaArchive? _archive;
    private BsaExtractor? _extractor;
    private string? _bsaFilePath;
    private CancellationTokenSource? _cts;

    private string _currentSortColumn = "Path";
    private bool _sortAscending = true;

    public BsaExtractorTab()
    {
        InitializeComponent();
        FilesListView.ItemsSource = _filteredFiles;

        // Subscribe to selection changes
        foreach (var file in _allFiles)
        {
            file.PropertyChanged += File_PropertyChanged;
        }

        UpdateEmptyState();
    }

    private void File_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BsaFileEntry.IsSelected))
        {
            UpdateSelectionStats();
        }
    }

    private void UpdateEmptyState()
    {
        var hasArchive = _archive is not null;
        NoArchivePanel.Visibility = hasArchive ? Visibility.Collapsed : Visibility.Visible;
        ArchiveInfoCard.Visibility = hasArchive ? Visibility.Visible : Visibility.Collapsed;
        StatsCard.Visibility = hasArchive ? Visibility.Visible : Visibility.Collapsed;
        ExtractButton.IsEnabled = hasArchive && _filteredFiles.Any(f => f.IsSelected);
    }

    private async void SelectBsaButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".bsa");
        picker.FileTypeFilter.Add(".ba2");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        var hwnd = WindowNative.GetWindowHandle(global::Xbox360MemoryCarver.App.Current.MainWindow);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        await LoadBsaAsync(file.Path);
    }

    private async Task LoadBsaAsync(string path)
    {
        try
        {
            // Clean up previous extractor
            _extractor?.Dispose();

            _bsaFilePath = path;
            _archive = BsaParser.Parse(path);
            _extractor = new BsaExtractor(_archive, File.OpenRead(path));

            // Update UI with archive info
            ArchiveNameText.Text = Path.GetFileName(path);
            ArchivePlatformText.Text = _archive.Header.IsXbox360 ? "Xbox 360" : "PC";
            ArchivePlatformText.Foreground = _archive.Header.IsXbox360
                ? new SolidColorBrush(Colors.Yellow)
                : new SolidColorBrush(Colors.Green);
            ArchiveFolderCountText.Text = _archive.Header.FolderCount.ToString("N0");
            ArchiveFileCountText.Text = _archive.Header.FileCount.ToString("N0");
            ArchiveCompressedText.Text = _archive.Header.DefaultCompressed ? "Yes" : "No";

            // Build content types string
            var contentTypes = new List<string>();
            if (_archive.Header.FileFlags.HasFlag(BsaFileFlags.Meshes)) contentTypes.Add("Meshes");
            if (_archive.Header.FileFlags.HasFlag(BsaFileFlags.Textures)) contentTypes.Add("Textures");
            if (_archive.Header.FileFlags.HasFlag(BsaFileFlags.Sounds)) contentTypes.Add("Sounds");
            if (_archive.Header.FileFlags.HasFlag(BsaFileFlags.Voices)) contentTypes.Add("Voices");
            if (_archive.Header.FileFlags.HasFlag(BsaFileFlags.Menus)) contentTypes.Add("Menus");
            if (_archive.Header.FileFlags.HasFlag(BsaFileFlags.Misc)) contentTypes.Add("Misc");
            ArchiveContentText.Text = contentTypes.Count > 0 ? string.Join(", ", contentTypes) : "Unknown";

            // Load files
            _allFiles.Clear();
            var defaultCompressed = _archive.Header.DefaultCompressed;

            foreach (var file in _archive.AllFiles)
            {
                var entry = new BsaFileEntry
                {
                    Record = file,
                    IsCompressed = defaultCompressed != file.CompressionToggle
                };
                entry.PropertyChanged += File_PropertyChanged;
                _allFiles.Add(entry);
            }

            // Populate extension filter
            var extensions = _allFiles
                .Select(f => f.Extension)
                .Where(e => !string.IsNullOrEmpty(e))
                .Distinct()
                .OrderBy(e => e)
                .ToList();

            ExtensionFilterCombo.Items.Clear();
            ExtensionFilterCombo.Items.Add(new ComboBoxItem { Content = "All types", Tag = "" });
            foreach (var ext in extensions)
            {
                var count = _allFiles.Count(f => f.Extension == ext);
                ExtensionFilterCombo.Items.Add(new ComboBoxItem { Content = $"{ext} ({count:N0})", Tag = ext });
            }
            ExtensionFilterCombo.SelectedIndex = 0;

            ApplyFilters();
            UpdateEmptyState();
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title = "Error Loading BSA",
                Content = $"Failed to load BSA archive:\n{ex.Message}",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            };
            await dialog.ShowAsync();
        }
    }

    private void ApplyFilters()
    {
        var extensionFilter = (ExtensionFilterCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        var folderFilter = FolderFilterBox.Text?.Trim() ?? "";

        var filtered = _allFiles.AsEnumerable();

        if (!string.IsNullOrEmpty(extensionFilter))
        {
            filtered = filtered.Where(f => f.Extension.Equals(extensionFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(folderFilter))
        {
            filtered = filtered.Where(f => f.FolderPath.Contains(folderFilter, StringComparison.OrdinalIgnoreCase));
        }

        // Apply sorting
        filtered = _currentSortColumn switch
        {
            "Path" => _sortAscending ? filtered.OrderBy(f => f.FullPath) : filtered.OrderByDescending(f => f.FullPath),
            "Folder" => _sortAscending ? filtered.OrderBy(f => f.FolderPath) : filtered.OrderByDescending(f => f.FolderPath),
            "Size" => _sortAscending ? filtered.OrderBy(f => f.Size) : filtered.OrderByDescending(f => f.Size),
            "Compressed" => _sortAscending ? filtered.OrderBy(f => f.IsCompressed) : filtered.OrderByDescending(f => f.IsCompressed),
            "Status" => _sortAscending ? filtered.OrderBy(f => f.Status) : filtered.OrderByDescending(f => f.Status),
            _ => filtered
        };

        _filteredFiles.Clear();
        foreach (var file in filtered)
        {
            _filteredFiles.Add(file);
        }

        // Update filter status
        if (string.IsNullOrEmpty(extensionFilter) && string.IsNullOrEmpty(folderFilter))
        {
            FilterStatusText.Text = $"Showing all {_filteredFiles.Count:N0} files";
        }
        else
        {
            FilterStatusText.Text = $"Showing {_filteredFiles.Count:N0} of {_allFiles.Count:N0} files";
        }

        UpdateSelectionStats();
    }

    private void UpdateSelectionStats()
    {
        var selectedFiles = _filteredFiles.Where(f => f.IsSelected).ToList();
        var selectedCount = selectedFiles.Count;
        var selectedSize = selectedFiles.Sum(f => f.Size);

        SelectedCountText.Text = $"{selectedCount:N0} selected";
        SelectedSizeText.Text = FormatSize(selectedSize);
        ExtractButton.IsEnabled = selectedCount > 0;
    }

    private void ExtensionFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyFilters();
    }

    private void FolderFilterBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ApplyFilters();
        }
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var file in _filteredFiles)
        {
            file.IsSelected = true;
        }
    }

    private void SelectNoneButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var file in _filteredFiles)
        {
            file.IsSelected = false;
        }
    }

    private void SortByPath_Click(object sender, RoutedEventArgs e) => ToggleSort("Path", PathSortIcon);
    private void SortByFolder_Click(object sender, RoutedEventArgs e) => ToggleSort("Folder", FolderSortIcon);
    private void SortBySize_Click(object sender, RoutedEventArgs e) => ToggleSort("Size", SizeSortIcon);
    private void SortByCompressed_Click(object sender, RoutedEventArgs e) => ToggleSort("Compressed", CompressedSortIcon);
    private void SortByStatus_Click(object sender, RoutedEventArgs e) => ToggleSort("Status", StatusSortIcon);

    private void ToggleSort(string column, FontIcon icon)
    {
        if (_currentSortColumn == column)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _currentSortColumn = column;
            _sortAscending = true;
        }

        // Reset all icons
        PathSortIcon.Glyph = "";
        FolderSortIcon.Glyph = "";
        SizeSortIcon.Glyph = "";
        CompressedSortIcon.Glyph = "";
        StatusSortIcon.Glyph = "";

        // Set active icon
        icon.Glyph = _sortAscending ? "\uE70D" : "\uE70E";

        ApplyFilters();
    }

    private async void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        if (_extractor is null || _archive is null)
        {
            return;
        }

        // Pick output folder
        var picker = new FolderPicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add("*");

        var hwnd = WindowNative.GetWindowHandle(global::Xbox360MemoryCarver.App.Current.MainWindow);
        InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        var selectedEntries = _filteredFiles.Where(f => f.IsSelected).ToList();
        var convertFiles = ConvertFilesCheckbox.IsChecked == true;

        // Enable DDX, XMA, and NIF conversion if requested
        var ddxConversionAvailable = false;
        var xmaConversionAvailable = false;
        var nifConversionAvailable = false;
        if (convertFiles)
        {
            ddxConversionAvailable = _extractor.EnableDdxConversion(true, verbose: false);
            xmaConversionAvailable = _extractor.EnableXmaConversion(true);
            nifConversionAvailable = _extractor.EnableNifConversion(true, verbose: false);

            // Check what's unavailable (NIF is always available as it's built-in)
            var unavailable = new List<string>();
            if (!ddxConversionAvailable) unavailable.Add("DDX->DDS (DDXConv not found)");
            if (!xmaConversionAvailable) unavailable.Add("XMA->OGG (FFmpeg not found)");

            if (unavailable.Count == 2)
            {
                var warningDialog = new ContentDialog
                {
                    Title = "Limited Conversion Support",
                    Content = "External conversion tools unavailable:\n" +
                              string.Join("\n", unavailable.Select(u => "- " + u)) + "\n\n" +
                              "NIF conversion (Xbox 360 to PC) is available.\n" +
                              "DDX and XMA files will be extracted without conversion.",
                    CloseButtonText = "Continue",
                    XamlRoot = XamlRoot
                };
                await warningDialog.ShowAsync();
            }
            else if (unavailable.Count > 0)
            {
                var warningDialog = new ContentDialog
                {
                    Title = "Partial Conversion Support",
                    Content = "Some external tools are unavailable:\n" +
                              string.Join("\n", unavailable.Select(u => "- " + u)) + "\n\n" +
                              "NIF conversion is always available.\n" +
                              "Other available conversions will be applied.",
                    CloseButtonText = "Continue",
                    XamlRoot = XamlRoot
                };
                await warningDialog.ShowAsync();
            }
        }

        // Reset all statuses
        foreach (var entry in selectedEntries)
        {
            entry.Status = BsaExtractionStatus.Pending;
            entry.StatusMessage = null;
        }

        // Start extraction
        _cts = new CancellationTokenSource();
        ExtractButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        ExtractionProgress.Visibility = Visibility.Visible;
        ExtractionProgress.Value = 0;

        var succeeded = 0;
        var failed = 0;
        var converted = 0;
        long totalSize = 0;

        try
        {
            // Create a channel for files that need conversion
            var conversionChannel = Channel.CreateBounded<(BsaFileEntry entry, byte[] data, string outputPath, string conversionType)>(
                new BoundedChannelOptions(10) { FullMode = BoundedChannelFullMode.Wait });

            var total = selectedEntries.Count;
            var processed = 0;

            // Start conversion workers (run concurrently with extraction)
            var conversionTask = Task.CompletedTask;
            if (convertFiles && (ddxConversionAvailable || xmaConversionAvailable || nifConversionAvailable))
            {
                conversionTask = RunConversionWorkersAsync(
                    conversionChannel.Reader,
                    _extractor,
                    () => Interlocked.Increment(ref converted),
                    _cts.Token);
            }

            // Extract files - can run multiple extractions in parallel
            var extractionTasks = new List<Task>();
            var semaphore = new SemaphoreSlim(4); // Limit concurrent extractions

            foreach (var entry in selectedEntries)
            {
                _cts.Token.ThrowIfCancellationRequested();

                await semaphore.WaitAsync(_cts.Token);

                var task = Task.Run(async () =>
                {
                    var extractionSucceeded = false;
                    var statusMessage = "Extracted";
                    try
                    {
                        // Update status on UI thread
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            try { entry.Status = BsaExtractionStatus.Extracting; } catch { /* ignore UI errors */ }
                        });

                        var extension = Path.GetExtension(entry.FileName).ToLowerInvariant();
                        var needsDdxConversion = convertFiles && ddxConversionAvailable && extension == ".ddx";
                        var needsXmaConversion = convertFiles && xmaConversionAvailable && extension == ".xma";
                        var needsNifConversion = convertFiles && nifConversionAvailable && extension == ".nif";
                        var needsConversion = needsDdxConversion || needsXmaConversion || needsNifConversion;
                        var outputPath = Path.Combine(folder.Path, entry.FullPath);

                        if (needsDdxConversion)
                        {
                            outputPath = Path.ChangeExtension(outputPath, ".dds");
                        }
                        else if (needsXmaConversion)
                        {
                            outputPath = Path.ChangeExtension(outputPath, ".ogg");
                        }
                        // NIF keeps same extension

                        // Extract to memory
                        var data = _extractor.ExtractFile(entry.Record);

                        if (needsConversion)
                        {
                            // Queue for conversion
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                try { entry.Status = BsaExtractionStatus.Converting; } catch { /* ignore UI errors */ }
                            });
                            var conversionType = needsDdxConversion ? "ddx" : needsXmaConversion ? "xma" : "nif";
                            await conversionChannel.Writer.WriteAsync((entry, data, outputPath, conversionType), _cts.Token);
                            // Note: conversion worker will set final status
                            extractionSucceeded = true; // Don't update status here, conversion worker handles it
                        }
                        else
                        {
                            // Write directly
                            var dir = Path.GetDirectoryName(outputPath)!;
                            Directory.CreateDirectory(dir);
                            await File.WriteAllBytesAsync(outputPath, data, _cts.Token);

                            Interlocked.Add(ref totalSize, data.Length);
                            Interlocked.Increment(ref succeeded);
                            extractionSucceeded = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        statusMessage = ex.Message;
                        extractionSucceeded = false;
                    }

                    // Update progress (do this outside try-catch so extraction isn't marked failed due to UI errors)
                    var current = Interlocked.Increment(ref processed);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            ExtractionProgress.Value = (double)current / total * 100;
                            FilterStatusText.Text = $"Extracting: {entry.FileName} ({current}/{total})";

                            // Only update status for non-conversion files (conversion worker handles those)
                            var ext = Path.GetExtension(entry.FileName).ToLowerInvariant();
                            var wasQueued = (convertFiles && ddxConversionAvailable && ext == ".ddx") ||
                                            (convertFiles && xmaConversionAvailable && ext == ".xma") ||
                                            (convertFiles && nifConversionAvailable && ext == ".nif");
                            if (!wasQueued)
                            {
                                entry.Status = extractionSucceeded ? BsaExtractionStatus.Done : BsaExtractionStatus.Failed;
                                entry.StatusMessage = statusMessage;
                            }
                        }
                        catch { /* ignore UI update errors */ }
                    });

                    semaphore.Release();
                }, _cts.Token);

                extractionTasks.Add(task);
            }

            // Wait for all extractions to complete
            await Task.WhenAll(extractionTasks);

            // Signal completion to conversion workers
            conversionChannel.Writer.Complete();

            // Wait for conversions to finish
            await conversionTask;

            // Update stats from conversion
            succeeded += converted;

            // Summary
            var message = $"Successfully extracted {succeeded:N0} files ({FormatSize(totalSize)})";
            if (converted > 0)
            {
                message += $"\n{converted:N0} files converted (DDX->DDS, XMA->OGG, NIF endian swap).";
            }
            if (failed > 0)
            {
                message += $"\n{failed:N0} files failed.";
            }

            var dialog = new ContentDialog
            {
                Title = "Extraction Complete",
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            };
            await dialog.ShowAsync();

            FilterStatusText.Text = $"Showing {_filteredFiles.Count:N0} files";
        }
        catch (OperationCanceledException)
        {
            FilterStatusText.Text = "Extraction cancelled";

            // Mark remaining as skipped
            foreach (var entry in selectedEntries.Where(e => e.Status == BsaExtractionStatus.Pending || e.Status == BsaExtractionStatus.Extracting))
            {
                entry.Status = BsaExtractionStatus.Skipped;
                entry.StatusMessage = "Cancelled";
            }
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title = "Extraction Error",
                Content = $"An error occurred during extraction:\n{ex.Message}",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _cts = null;
            ExtractButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            ExtractionProgress.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Run conversion workers that process DDX, XMA, and NIF files from the channel.
    /// </summary>
    private async Task RunConversionWorkersAsync(
        ChannelReader<(BsaFileEntry entry, byte[] data, string outputPath, string conversionType)> reader,
        BsaExtractor extractor,
        Action onConverted,
        CancellationToken cancellationToken)
    {
        // Run multiple conversion workers in parallel
        const int workerCount = 2;
        var workers = new Task[workerCount];

        for (int i = 0; i < workerCount; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                await foreach (var (entry, data, outputPath, conversionType) in reader.ReadAllAsync(cancellationToken))
                {
                    var conversionSucceeded = false;
                    var statusMessage = "Converted";

                    try
                    {
                        DdxConversionResult result;
                        string originalExtension;

                        switch (conversionType)
                        {
                            case "ddx":
                                result = await extractor.ConvertDdxAsync(data);
                                originalExtension = ".ddx";
                                break;
                            case "xma":
                                result = await extractor.ConvertXmaAsync(data);
                                originalExtension = ".xma";
                                break;
                            case "nif":
                                result = await extractor.ConvertNifAsync(data);
                                originalExtension = ".nif";
                                break;
                            default:
                                result = new DdxConversionResult { Success = false, Notes = $"Unknown conversion type: {conversionType}" };
                                originalExtension = "";
                                break;
                        }

                        var dir = Path.GetDirectoryName(outputPath)!;
                        Directory.CreateDirectory(dir);

                        if (result.Success && result.DdsData != null)
                        {
                            await File.WriteAllBytesAsync(outputPath, result.DdsData, cancellationToken);
                            onConverted();
                            conversionSucceeded = true;
                            statusMessage = "Converted";
                        }
                        else
                        {
                            // Conversion failed - save original file
                            var fallbackPath = conversionType == "nif"
                                ? outputPath // NIF keeps same extension
                                : Path.ChangeExtension(outputPath, originalExtension);
                            await File.WriteAllBytesAsync(fallbackPath, data, cancellationToken);
                            conversionSucceeded = true; // File was saved, just not converted
                            statusMessage = $"Saved as {originalExtension.ToUpperInvariant().TrimStart('.')} ({result.Notes})";
                        }
                    }
                    catch (Exception ex)
                    {
                        conversionSucceeded = false;
                        statusMessage = ex.Message;
                    }

                    // Update UI status (outside try-catch to not fail extraction due to UI errors)
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            entry.Status = conversionSucceeded ? BsaExtractionStatus.Done : BsaExtractionStatus.Failed;
                            entry.StatusMessage = statusMessage;
                        }
                        catch { /* ignore UI update errors */ }
                    });
                }
            }, cancellationToken);
        }

        await Task.WhenAll(workers);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
    };
}

#endif
