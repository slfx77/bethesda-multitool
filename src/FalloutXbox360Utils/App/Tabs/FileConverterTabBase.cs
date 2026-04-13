using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FalloutXbox360Utils;

/// <summary>
///     Base class for converter tabs (NIF, DDX) that share file scanning, sorting,
///     selection, and progress patterns. Each subclass keeps its own XAML and
///     provides UI element references via abstract properties.
/// </summary>
public abstract class FileConverterTabBase<TEntry> : UserControl, IDisposable, IHasSettingsDrawer
    where TEntry : class, IConvertibleFileEntry
{
    private readonly List<TEntry> _allFiles = [];
    private readonly ConvertibleFileSorter<TEntry> _sorter = new();

    protected List<TEntry> AllFiles => _allFiles;
    protected ConvertibleFileSorter<TEntry> Sorter => _sorter;
    protected CancellationTokenSource? ConversionCts { get; set; }
    protected List<TEntry> Files { get; set; } = [];
    protected CancellationTokenSource? ScanCts { get; set; }

    // Abstract UI element accessors — each concrete tab returns its XAML-declared elements
    protected abstract ListView FilesListView { get; }
    protected abstract ProgressBar ConversionProgressBar { get; }
    protected abstract Button ConvertButtonElement { get; }
    protected abstract Button CancelButtonElement { get; }
    protected abstract TextBox InputDirectoryTextBox { get; }
    protected abstract TextBox OutputDirectoryTextBox { get; }
    protected abstract FontIcon FilePathSortIcon { get; }
    protected abstract FontIcon SizeSortIcon { get; }
    protected abstract FontIcon FormatSortIcon { get; }
    protected abstract FontIcon StatusSortIcon { get; }
    protected abstract Border SettingsDrawerElement { get; }

#pragma warning disable CA1822, S2325
    protected StatusTextHelper StatusTextBlock => new();
#pragma warning restore CA1822, S2325

    public void ToggleSettingsDrawer() => SettingsDrawerHelper.Toggle(SettingsDrawerElement);
    public void CloseSettingsDrawer() => SettingsDrawerHelper.Close(SettingsDrawerElement);

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            ConversionCts?.Dispose();
            ScanCts?.Dispose();
        }
    }

    #region State Management

    protected void UpdateButtonStates()
    {
        var hasOutput = !string.IsNullOrEmpty(OutputDirectoryTextBox.Text);
        var hasSelected = Files.Any(f => f.IsSelected);
        ConvertButtonElement.IsEnabled =
            hasOutput && hasSelected && (ConversionCts == null || ConversionCts.IsCancellationRequested);
        CancelButtonElement.IsEnabled = ConversionCts != null && !ConversionCts.IsCancellationRequested;
    }

    protected void UpdateFileCount()
    {
        var total = Files.Count;
        var selected = Files.Count(f => f.IsSelected);
        StatusTextBlock.Text = $"{selected} of {total} files selected";
    }

    protected async Task ShowDialogAsync(string title, string message)
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

    #endregion

    #region Scanning

    protected void InitializeScanProgress(int fileCount)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ConversionProgressBar.IsIndeterminate = false;
            ConversionProgressBar.Maximum = fileCount;
            ConversionProgressBar.Value = 0;
            StatusTextBlock.Text = $"Scanning {fileCount} files...";
        });
    }

    protected void ClearFileList()
    {
        Files = [];
        AllFiles.Clear();
        AllFiles.TrimExcess();
        FilesListView.ItemsSource = null;
        Sorter.Reset();
        UpdateSortIcons();
        UpdateFileCount();
        UpdateButtonStates();
    }

    protected void OnScanComplete(TEntry[] entries)
    {
        AllFiles.Clear();
        AllFiles.Capacity = entries.Length;
        AllFiles.AddRange(entries);
        Files = new List<TEntry>(AllFiles);
        FilesListView.ItemsSource = Files;
        UpdateFileCount();
        UpdateButtonStates();
    }

    #endregion

    #region Selection

    protected void SelectAll()
    {
        foreach (var file in Files)
        {
            file.IsSelected = true;
        }

        UpdateFileCount();
        UpdateButtonStates();
    }

    protected void SelectNone()
    {
        foreach (var file in Files)
        {
            file.IsSelected = false;
        }

        UpdateFileCount();
        UpdateButtonStates();
    }

    #endregion

    #region Sorting

    protected void ApplySort(ConvertibleSortColumn column)
    {
        Sorter.CycleSortState(column);
        UpdateSortIcons();
        RefreshSortedList();
    }

    protected void UpdateSortIcons()
    {
        FilePathSortIcon.Visibility = SizeSortIcon.Visibility =
            FormatSortIcon.Visibility = StatusSortIcon.Visibility = Visibility.Collapsed;

        var icon = Sorter.CurrentColumn switch
        {
            ConvertibleSortColumn.FilePath => FilePathSortIcon,
            ConvertibleSortColumn.Size => SizeSortIcon,
            ConvertibleSortColumn.Format => FormatSortIcon,
            ConvertibleSortColumn.Status => StatusSortIcon,
            _ => null
        };

        if (icon != null)
        {
            icon.Visibility = Visibility.Visible;
            icon.Glyph = Sorter.IsAscending ? "\uE70E" : "\uE70D";
        }
    }

    protected void RefreshSortedList()
    {
        var selectedItem = FilesListView.SelectedItem as TEntry;
        var sorted = Sorter.Sort(AllFiles);

        Files = sorted.ToList();
        FilesListView.ItemsSource = Files;

        if (selectedItem != null && Files.Contains(selectedItem))
        {
            FilesListView.SelectedItem = selectedItem;
            FilesListView.ScrollIntoView(selectedItem, ScrollIntoViewAlignment.Leading);
        }
    }

    #endregion

    #region Browse Helpers

    protected async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker,
            WindowNative.GetWindowHandle(App.Current.MainWindow));

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    protected void SetupTextBoxContextMenus()
    {
        TextBoxContextMenuHelper.AttachContextMenu(InputDirectoryTextBox);
        TextBoxContextMenuHelper.AttachContextMenu(OutputDirectoryTextBox);
    }

    #endregion
}

/// <summary>
///     Non-generic intermediate base for NifConverterTab.
///     Required because WinUI XAML codegen cannot use generic types as root elements.
/// </summary>
public abstract class NifFileConverterBase : FileConverterTabBase<NifFileEntry>
{
}

/// <summary>
///     Non-generic intermediate base for DdxConverterTab.
///     Required because WinUI XAML codegen cannot use generic types as root elements.
/// </summary>
public abstract class DdxFileConverterBase : FileConverterTabBase<DdxFileEntry>
{
}
