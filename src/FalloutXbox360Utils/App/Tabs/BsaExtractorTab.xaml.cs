// Copyright (c) 2026 FalloutXbox360Utils Contributors
// Licensed under the MIT License.

#if WINDOWS_GUI
using System.Collections.ObjectModel;
using System.ComponentModel;
using Windows.Storage.Pickers;
using FalloutXbox360Utils.Core.Formats.Bsa;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace FalloutXbox360Utils;

/// <summary>
///     BSA Extractor tab for extracting files from Bethesda archives.
/// </summary>
public sealed partial class BsaExtractorTab : UserControl, IDisposable, IHasSettingsDrawer
{
    public void ToggleSettingsDrawer() => SettingsDrawerHelper.Toggle(SettingsDrawer);
    public void CloseSettingsDrawer() => SettingsDrawerHelper.Close(SettingsDrawer);

    private readonly ObservableCollection<BsaFileEntry> _allFiles = [];
    private readonly ObservableCollection<BsaFileEntry> _filteredFiles = [];

    private BsaArchive? _archive;
    private CancellationTokenSource? _cts;

    private string _currentSortColumn = "Path";
    private BsaExtractor? _extractor;
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

    public void Dispose()
    {
        _cts?.Dispose();
        _extractor?.Dispose();
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

        var hwnd = WindowNative.GetWindowHandle(App.Current.MainWindow);
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
                .Where(ext => !string.IsNullOrEmpty(ext))
                .Distinct()
                .OrderBy(ext => ext)
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
            "Folder" => _sortAscending
                ? filtered.OrderBy(f => f.FolderPath)
                : filtered.OrderByDescending(f => f.FolderPath),
            "Size" => _sortAscending ? filtered.OrderBy(f => f.Size) : filtered.OrderByDescending(f => f.Size),
            "Compressed" => _sortAscending
                ? filtered.OrderBy(f => f.IsCompressed)
                : filtered.OrderByDescending(f => f.IsCompressed),
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
        SelectedSizeText.Text = BsaExtractionEngine.FormatSize(selectedSize);
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

    private void SortByPath_Click(object sender, RoutedEventArgs e)
    {
        ToggleSort("Path", PathSortIcon);
    }

    private void SortByFolder_Click(object sender, RoutedEventArgs e)
    {
        ToggleSort("Folder", FolderSortIcon);
    }

    private void SortBySize_Click(object sender, RoutedEventArgs e)
    {
        ToggleSort("Size", SizeSortIcon);
    }

    private void SortByCompressed_Click(object sender, RoutedEventArgs e)
    {
        ToggleSort("Compressed", CompressedSortIcon);
    }

    private void SortByStatus_Click(object sender, RoutedEventArgs e)
    {
        ToggleSort("Status", StatusSortIcon);
    }

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

        var hwnd = WindowNative.GetWindowHandle(App.Current.MainWindow);
        InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
        {
            return;
        }

        var selectedEntries = _filteredFiles.Where(f => f.IsSelected).ToList();
        var convertFiles = ConvertFilesCheckbox.IsChecked == true;

        // Enable conversions if requested
        var ddxConversionAvailable = false;
        var xmaConversionAvailable = false;
        var nifConversionAvailable = false;
        if (convertFiles)
        {
            ddxConversionAvailable = true; // DDXConv is compiled-in, always available
            xmaConversionAvailable = _extractor.EnableXmaConversion(true);
            nifConversionAvailable = _extractor.EnableNifConversion(true);

            var unavailable = BsaExtractionEngine.CheckConversionAvailability(xmaConversionAvailable);
            await ShowConversionWarningsAsync(unavailable);
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

        var options = new BsaExtractionEngine.ExtractionOptions
        {
            OutputDir = folder.Path,
            ConvertFiles = convertFiles,
            DdxConversionAvailable = ddxConversionAvailable,
            XmaConversionAvailable = xmaConversionAvailable,
            NifConversionAvailable = nifConversionAvailable
        };

        var counters = new BsaExtractionEngine.ExtractionCounters();
        var callbacks = CreateProgressCallbacks();

        try
        {
            // Run extraction (parallel file extraction + XMA/NIF conversion workers)
            var ddxEntries = await BsaExtractionEngine.RunExtractionAsync(
                selectedEntries, _extractor, options, counters, callbacks, _cts.Token);

            // Batch DDX conversion (much faster than per-file subprocess spawning)
            if (convertFiles && ddxConversionAvailable)
            {
                await BsaExtractionEngine.RunDdxBatchConversionAsync(
                    folder.Path, ddxEntries, counters, callbacks, _cts.Token);
            }

            // Summary
            var message = BsaExtractionEngine.BuildSummaryMessage(counters);
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
            foreach (var entry in selectedEntries.Where(e =>
                         e.Status == BsaExtractionStatus.Pending || e.Status == BsaExtractionStatus.Extracting))
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

    private BsaExtractionEngine.ProgressCallbacks CreateProgressCallbacks()
    {
        return new BsaExtractionEngine.ProgressCallbacks
        {
            OnStatusChanged = (entry, status) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try { entry.Status = status; }
                    catch { /* ignore UI errors */ }
                });
            },
            OnProgress = (current, total, fileName) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        ExtractionProgress.Value = (double)current / total * 100;
                        FilterStatusText.Text = $"Extracting: {fileName} ({current}/{total})";
                    }
                    catch { /* ignore UI errors */ }
                });
            },
            OnStatusMessage = message =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try { FilterStatusText.Text = message; }
                    catch { /* ignore UI errors */ }
                });
            },
            OnFileComplete = (entry, succeeded, statusMessage, pendingConversion) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        if (!pendingConversion)
                        {
                            entry.Status = succeeded ? BsaExtractionStatus.Done : BsaExtractionStatus.Failed;
                            entry.StatusMessage = statusMessage;
                        }
                    }
                    catch { /* ignore UI errors */ }
                });
            }
        };
    }

    private async Task ShowConversionWarningsAsync(List<string> unavailable)
    {
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

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }
}

#endif
