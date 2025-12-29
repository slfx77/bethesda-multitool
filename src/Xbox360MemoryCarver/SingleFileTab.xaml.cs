using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;
using Xbox360MemoryCarver.Core;

namespace Xbox360MemoryCarver.App;

/// <summary>
///     Single file analysis and extraction tab.
/// </summary>
public sealed partial class SingleFileTab : UserControl
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
    private readonly List<CarvedFileEntry> _allCarvedFiles = [];
    private readonly ObservableCollection<CarvedFileEntry> _carvedFiles = [];
    private readonly Dictionary<string, CheckBox> _fileTypeCheckboxes = [];
    private AnalysisResult? _analysisResult;
    private CarvedFileEntry? _contextMenuTarget;
    private SortColumn _currentSortColumn = SortColumn.None;
    private string? _lastInputPath;
    private bool _sortAscending = true;

    public SingleFileTab()
    {
        InitializeComponent();
        ResultsListView.ItemsSource = _carvedFiles;
        InitializeFileTypeCheckboxes();
        Loaded += SingleFileTab_Loaded;
    }

    // Win32 Clipboard interop
    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

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

        // Auto-update output path when input changes to a valid file
        if (!string.IsNullOrEmpty(currentPath) &&
            currentPath != _lastInputPath &&
            File.Exists(currentPath) &&
            currentPath.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
        {
            UpdateOutputPathFromInput(currentPath);
            _lastInputPath = currentPath;

            // Clear previous analysis when input file changes
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
        var directory = Path.GetDirectoryName(inputPath) ?? "";
        var fileName = Path.GetFileNameWithoutExtension(inputPath);
        OutputPathTextBox.Text = Path.Combine(directory, $"{fileName}_extracted");
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
        // Output path is auto-updated by TextChanged handler

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
            ResetSortState();

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
                _allCarvedFiles.Add(new CarvedFileEntry
                {
                    Offset = entry.Offset, Length = entry.Length, FileType = entry.FileType, FileName = entry.FileName
                });
                _carvedFiles.Add(_allCarvedFiles[^1]);
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
                OutputPath = outputPath, ConvertDdx = ConvertDdxCheckBox.IsChecked == true,
                SaveAtlas = SaveAtlasCheckBox.IsChecked == true, Verbose = VerboseCheckBox.IsChecked == true,
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

    private enum SortColumn
    {
        None,
        Offset,
        Length,
        Type,
        Filename
    }

    private void SortByOffset_Click(object sender, RoutedEventArgs e) => ApplySort(SortColumn.Offset);
    private void SortByLength_Click(object sender, RoutedEventArgs e) => ApplySort(SortColumn.Length);
    private void SortByType_Click(object sender, RoutedEventArgs e) => ApplySort(SortColumn.Type);
    private void SortByFilename_Click(object sender, RoutedEventArgs e) => ApplySort(SortColumn.Filename);

    private void ResetSortState()
    {
        _currentSortColumn = SortColumn.None;
        _sortAscending = true;
        UpdateSortIcons();
    }

    private void ApplySort(SortColumn col)
    {
        if (_currentSortColumn == col)
        {
            if (_sortAscending) _sortAscending = false;
            else
            {
                _currentSortColumn = SortColumn.None;
                _sortAscending = true;
            }
        }
        else
        {
            _currentSortColumn = col;
            _sortAscending = true;
        }

        UpdateSortIcons();
        RefreshSortedList();
    }

    private void UpdateSortIcons()
    {
        OffsetSortIcon.Visibility = LengthSortIcon.Visibility =
            TypeSortIcon.Visibility = FilenameSortIcon.Visibility = Visibility.Collapsed;
        var icon = _currentSortColumn switch
        {
            SortColumn.Offset => OffsetSortIcon, SortColumn.Length => LengthSortIcon,
            SortColumn.Type => TypeSortIcon, SortColumn.Filename => FilenameSortIcon, _ => null
        };
        if (icon != null)
        {
            icon.Visibility = Visibility.Visible;
            icon.Glyph = _sortAscending ? "\uE70E" : "\uE70D";
        }
    }

    private void RefreshSortedList()
    {
        // Remember the currently selected item
        var selectedItem = ResultsListView.SelectedItem as CarvedFileEntry;

        var sorted = _currentSortColumn switch
        {
            SortColumn.Offset => _sortAscending
                ? _allCarvedFiles.OrderBy(f => f.Offset)
                : _allCarvedFiles.OrderByDescending(f => f.Offset),
            SortColumn.Length => _sortAscending
                ? _allCarvedFiles.OrderBy(f => f.Length)
                : _allCarvedFiles.OrderByDescending(f => f.Length),
            SortColumn.Type => _sortAscending
                ? _allCarvedFiles.OrderBy(f => f.FileType, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Offset)
                : _allCarvedFiles.OrderByDescending(f => f.FileType, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.Offset),
            SortColumn.Filename => _sortAscending
                ? _allCarvedFiles.OrderBy(f => string.IsNullOrEmpty(f.FileName) ? 1 : 0)
                    .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Offset)
                : _allCarvedFiles.OrderBy(f => string.IsNullOrEmpty(f.FileName) ? 1 : 0)
                    .ThenByDescending(f => f.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(f => f.Offset),
            _ => _allCarvedFiles.OrderBy(f => f.Offset)
        };
        _carvedFiles.Clear();
        foreach (var f in sorted) _carvedFiles.Add(f);

        // Restore selection and scroll to the item's new position
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
        // Get the item that was right-clicked
        if (e.OriginalSource is FrameworkElement element && element.DataContext is CarvedFileEntry entry)
        {
            _contextMenuTarget = entry;

            // Update menu item states
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
        if (target != null)
        {
            HexViewer.NavigateToOffset(target.Offset);
        }
    }

    private void GoToEnd_Click(object sender, RoutedEventArgs e)
    {
        var target = _contextMenuTarget ?? ResultsListView.SelectedItem as CarvedFileEntry;
        if (target != null)
        {
            // Navigate to the end of the file (offset + length - some bytes to see the boundary)
            var endOffset = target.Offset + target.Length - 16;
            if (endOffset > 0)
            {
                HexViewer.NavigateToOffset(endOffset);
            }
        }
    }

    private void CopyOffset_Click(object sender, RoutedEventArgs e)
    {
        var target = _contextMenuTarget ?? ResultsListView.SelectedItem as CarvedFileEntry;
        if (target == null)
        {
            Console.WriteLine("[Clipboard] CopyOffset: No target selected");
            return;
        }

        var text = $"0x{target.Offset:X8}";
        Console.WriteLine($"[Clipboard] CopyOffset: Copying '{text}'");
        CopyTextToClipboardWin32(text);
    }

    private void CopyFilename_Click(object sender, RoutedEventArgs e)
    {
        var target = _contextMenuTarget ?? ResultsListView.SelectedItem as CarvedFileEntry;
        if (target == null)
        {
            Console.WriteLine("[Clipboard] CopyFilename: No target selected");
            return;
        }

        if (string.IsNullOrEmpty(target.FileName))
        {
            Console.WriteLine("[Clipboard] CopyFilename: No filename available");
            return;
        }

        Console.WriteLine($"[Clipboard] CopyFilename: Copying '{target.FileName}'");
        CopyTextToClipboardWin32(target.FileName);
    }

    /// <summary>
    ///     Copy text to clipboard using Win32 API (more reliable than WinUI 3 Clipboard).
    /// </summary>
    private static void CopyTextToClipboardWin32(string text)
    {
        try
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Console.WriteLine("[Clipboard] Failed to open clipboard");
                return;
            }

            try
            {
                EmptyClipboard();

                // Allocate global memory for the text (Unicode, null-terminated)
                var bytes = (text.Length + 1) * 2; // UTF-16 + null terminator
                var hGlobal = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                if (hGlobal == IntPtr.Zero)
                {
                    Console.WriteLine("[Clipboard] Failed to allocate global memory");
                    return;
                }

                var pGlobal = GlobalLock(hGlobal);
                if (pGlobal == IntPtr.Zero)
                {
                    Console.WriteLine("[Clipboard] Failed to lock global memory");
                    return;
                }

                try
                {
                    Marshal.Copy(text.ToCharArray(), 0, pGlobal, text.Length);
                    // Add null terminator
                    Marshal.WriteInt16(pGlobal + text.Length * 2, 0);
                }
                finally
                {
                    GlobalUnlock(hGlobal);
                }

                if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero)
                {
                    Console.WriteLine("[Clipboard] Failed to set clipboard data");
                    return;
                }

                Console.WriteLine($"[Clipboard] Successfully copied: '{text}'");
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Clipboard] Exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    #endregion
}
