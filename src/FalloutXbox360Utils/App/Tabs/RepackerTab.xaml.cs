// Copyright (c) 2026 FalloutXbox360Utils Contributors
// Licensed under the MIT License.

#if WINDOWS_GUI
using System.Collections.ObjectModel;
using System.ComponentModel;
using Windows.Storage.Pickers;
using FalloutXbox360Utils.Core.Utils;
using FalloutXbox360Utils.Repack;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace FalloutXbox360Utils;

/// <summary>
///     Repacker tab for converting Xbox 360 Fallout: New Vegas to PC format.
/// </summary>
public sealed partial class RepackerTab : UserControl, IDisposable
{
    private readonly ObservableCollection<RepackCategory> _categories = [];
    private CancellationTokenSource? _cts;
    private string? _outputPath;

    private string? _sourcePath;
    private bool _sourceValid;

    public RepackerTab()
    {
        InitializeComponent();

        // Initialize categories
        _categories.Add(new RepackCategory
        {
            Name = "Video Files",
            Description = "Copy BIK video files (no conversion needed)",
            Phase = RepackPhase.Video
        });

        _categories.Add(new RepackCategory
        {
            Name = "Music Files",
            Description = "Convert XMA audio to MP3 (192kbps, 48kHz)",
            Phase = RepackPhase.Music
        });

        _categories.Add(new RepackCategory
        {
            Name = "BSA Archives",
            Description = "Extract, convert contents, and repackage BSA files",
            Phase = RepackPhase.Bsa
        });

        _categories.Add(new RepackCategory
        {
            Name = "ESM Master Files",
            Description = "Convert big-endian ESM files to PC little-endian",
            Phase = RepackPhase.Esm
        });

        _categories.Add(new RepackCategory
        {
            Name = "ESP Plugin Files",
            Description = "Convert big-endian ESP files to PC little-endian",
            Phase = RepackPhase.Esp
        });

        _categories.Add(new RepackCategory
        {
            Name = "INI Configuration",
            Description = "Generate hybrid Fallout_default.ini with PC-compatible settings",
            Phase = RepackPhase.Ini
        });

        // Subscribe to property changes to update stats when checkboxes change
        foreach (var category in _categories)
        {
            category.PropertyChanged += Category_PropertyChanged;
        }

        CategoriesListView.ItemsSource = _categories;
        UpdateEmptyState();
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }

    private void Category_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RepackCategory.IsEnabled))
        {
            UpdateStats();
            UpdateConvertButtonState();
        }
    }

    private void UpdateEmptyState()
    {
        var hasSource = _sourceValid;
        NoSourcePanel.Visibility = hasSource ? Visibility.Collapsed : Visibility.Visible;
        SourceInfoCard.Visibility = hasSource ? Visibility.Visible : Visibility.Collapsed;
        StatsCard.Visibility = hasSource ? Visibility.Visible : Visibility.Collapsed;
        UpdateConvertButtonState();
        UpdateStats();
    }

    private void UpdateConvertButtonState()
    {
        ConvertButton.IsEnabled = _sourceValid &&
                                  !string.IsNullOrEmpty(_outputPath) &&
                                  _categories.Any(c => c.IsEnabled && c.FileCount > 0);
    }

    private void UpdateStats()
    {
        var enabledCount = _categories.Count(c => c.IsEnabled);
        var totalFiles = _categories.Where(c => c.IsEnabled).Sum(c => c.FileCount);

        SelectedCountText.Text = $"{enabledCount} categories selected";
        SelectedFilesText.Text = $"{totalFiles:N0} files";
    }

    private async void SelectSourceButton_Click(object sender, RoutedEventArgs e)
    {
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

        await ValidateSourceFolderAsync(folder.Path);
    }

    private async Task ValidateSourceFolderAsync(string path)
    {
        _sourcePath = path;
        SourcePathText.Text = Path.GetFileName(path);
        SourcePathText.SetValue(ToolTipService.ToolTipProperty, path);

        var validation = RepackerService.ValidateSourceFolder(path);
        _sourceValid = validation.IsValid;

        if (validation.IsValid)
        {
            ValidationIcon.Glyph = "\uE73E"; // Checkmark
            ValidationIcon.Foreground = new SolidColorBrush(Colors.Green);
            ValidationText.Text = "Valid Xbox 360 installation";
            ValidationText.Foreground = new SolidColorBrush(Colors.Green);

            // Get source info
            var sourceInfo = RepackerService.GetSourceInfo(path);

            // Update category file counts
            var hasIni = File.Exists(Path.Combine(path, "Fallout.ini")) ? 1 : 0;
            foreach (var category in _categories)
            {
                category.FileCount = category.Phase switch
                {
                    RepackPhase.Video => sourceInfo.VideoFiles,
                    RepackPhase.Music => sourceInfo.MusicFiles,
                    RepackPhase.Bsa => sourceInfo.BsaFiles,
                    RepackPhase.Esm => sourceInfo.EsmFiles,
                    RepackPhase.Esp => sourceInfo.EspFiles,
                    RepackPhase.Ini => hasIni,
                    _ => 0
                };
            }

            TotalFilesText.Text = $"{sourceInfo.TotalFiles:N0}";
            StatusText.Text = "Ready to convert";
            UpdateDependencyStatus();

            // Populate BSA file list
            PopulateBsaFiles(path);
        }
        else
        {
            ValidationIcon.Glyph = "\uE711"; // Error
            ValidationIcon.Foreground = new SolidColorBrush(Colors.OrangeRed);
            ValidationText.Text = validation.Message;
            ValidationText.Foreground = new SolidColorBrush(Colors.OrangeRed);

            // Reset file counts
            foreach (var category in _categories)
            {
                category.FileCount = 0;
            }
        }

        UpdateEmptyState();
    }

    private void UpdateDependencyStatus()
    {
        if (FfmpegLocator.IsAvailable)
        {
            FfmpegStatusText.Text = "Available";
            FfmpegStatusText.Foreground = new SolidColorBrush(Colors.Green);
        }
        else
        {
            FfmpegStatusText.Text = "Not found";
            FfmpegStatusText.Foreground = new SolidColorBrush(Colors.OrangeRed);
        }
    }

    private void PopulateBsaFiles(string sourcePath)
    {
        var bsaCategory = _categories.FirstOrDefault(c => c.Phase == RepackPhase.Bsa);
        if (bsaCategory is null)
        {
            return;
        }

        bsaCategory.SubItems.Clear();

        var dataPath = Path.Combine(sourcePath, "Data");
        if (!Directory.Exists(dataPath))
        {
            return;
        }

        var bsaFiles = Directory.GetFiles(dataPath, "*.bsa", SearchOption.TopDirectoryOnly)
            .OrderBy(f => Path.GetFileName(f))
            .ToArray();

        foreach (var bsaPath in bsaFiles)
        {
            bsaCategory.SubItems.Add(new RepackBsaEntry
            {
                FileName = Path.GetFileName(bsaPath),
                FullPath = bsaPath
            });
        }

        // Auto-expand if there are sub-items
        bsaCategory.IsExpanded = bsaCategory.SubItems.Count > 0;
    }

    private async void SelectOutputButton_Click(object sender, RoutedEventArgs e)
    {
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

        _outputPath = folder.Path;
        OutputPathText.Text = Path.GetFileName(folder.Path);
        OutputPathText.SetValue(ToolTipService.ToolTipProperty, folder.Path);
        OutputPathText.Foreground = new SolidColorBrush(Colors.White);
        UpdateConvertButtonState();
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_sourcePath) || string.IsNullOrEmpty(_outputPath))
        {
            return;
        }

        // Reset category statuses
        foreach (var category in _categories)
        {
            category.Status = category.IsEnabled && category.FileCount > 0
                ? RepackCategoryStatus.Pending
                : RepackCategoryStatus.Skipped;
            category.StatusMessage = null;
        }

        // Build selected BSA files set (only if not all are selected)
        HashSet<string>? selectedBsaFiles = null;
        var bsaCategory = _categories.FirstOrDefault(c => c.Phase == RepackPhase.Bsa);
        if (bsaCategory is not null && bsaCategory.SubItems.Count > 0)
        {
            var allBsaSelected = bsaCategory.SubItems.All(b => b.IsSelected);
            if (!allBsaSelected)
            {
                selectedBsaFiles = bsaCategory.SubItems
                    .Where(b => b.IsSelected)
                    .Select(b => b.FileName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        }

        // Build options
        var options = new RepackerOptions
        {
            SourceFolder = _sourcePath,
            OutputFolder = _outputPath,
            ProcessVideo = _categories.First(c => c.Phase == RepackPhase.Video).IsEnabled,
            ProcessMusic = _categories.First(c => c.Phase == RepackPhase.Music).IsEnabled,
            ProcessBsa = _categories.First(c => c.Phase == RepackPhase.Bsa).IsEnabled,
            ProcessEsm = _categories.First(c => c.Phase == RepackPhase.Esm).IsEnabled,
            ProcessEsp = _categories.First(c => c.Phase == RepackPhase.Esp).IsEnabled,
            ProcessIni = _categories.First(c => c.Phase == RepackPhase.Ini).IsEnabled,
            SelectedBsaFiles = selectedBsaFiles,
            UpdateUserIni = UpdateUserIniCheckbox.IsChecked == true
        };

        // Setup UI for conversion
        _cts = new CancellationTokenSource();
        ConvertButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        ConversionProgress.Visibility = Visibility.Visible;
        ConversionProgress.Value = 0;
        ConversionProgress.IsIndeterminate = true;
        ProgressDetailText.Text = "Starting conversion...";

        try
        {
            var progress = new Progress<RepackerProgress>(OnProgressUpdate);
            var result = await RepackerService.RepackAsync(options, progress, _cts.Token);

            // Show result
            var message = result.Success
                ? $"Conversion complete!\n\n" +
                  $"Video files: {result.VideoFilesProcessed}\n" +
                  $"Music files: {result.MusicFilesProcessed}\n" +
                  $"BSA files: {result.BsaFilesProcessed}\n" +
                  $"ESM files: {result.EsmFilesProcessed}\n" +
                  $"ESP files: {result.EspFilesProcessed}\n" +
                  $"INI files: {result.IniFilesProcessed}\n\n" +
                  $"Total: {result.TotalFilesProcessed} files processed"
                : $"Conversion failed: {result.Error}";

            var dialog = new ContentDialog
            {
                Title = result.Success ? "Conversion Complete" : "Conversion Failed",
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Conversion cancelled";
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title = "Conversion Error",
                Content = $"An error occurred:\n{ex.Message}",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _cts = null;
            ConvertButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            ConversionProgress.Visibility = Visibility.Collapsed;
            ConversionProgress.IsIndeterminate = false;
            ProgressDetailText.Text = "";
            UpdateConvertButtonState();
        }
    }

    private void OnProgressUpdate(RepackerProgress progress)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                // Update progress bar
                if (progress.TotalItems > 0)
                {
                    ConversionProgress.IsIndeterminate = false;
                    ConversionProgress.Value = (double)progress.ItemsProcessed / progress.TotalItems * 100;
                }

                // Update detail text
                ProgressDetailText.Text = progress.CurrentItem ?? progress.Message;
                StatusText.Text = progress.Message;

                // Update category status
                var category = _categories.FirstOrDefault(c => c.Phase == progress.Phase);
                if (category is not null)
                {
                    if (progress.IsComplete)
                    {
                        category.Status = progress.Success
                            ? RepackCategoryStatus.Complete
                            : RepackCategoryStatus.Failed;
                        category.StatusMessage = progress.Success
                            ? $"{progress.ItemsProcessed} done"
                            : progress.Error;
                    }
                    else
                    {
                        category.Status = RepackCategoryStatus.Processing;
                        category.StatusMessage = progress.TotalItems > 0
                            ? $"{progress.ItemsProcessed}/{progress.TotalItems}"
                            : "Processing...";
                    }
                }
            }
            catch
            {
                // Ignore UI update errors
            }
        });
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }
}

#endif
