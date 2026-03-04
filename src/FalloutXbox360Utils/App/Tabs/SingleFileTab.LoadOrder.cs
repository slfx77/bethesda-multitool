using System.Collections.ObjectModel;
using System.IO.MemoryMappedFiles;
using Windows.Storage.Pickers;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Subtitles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace FalloutXbox360Utils;

/// <summary>
///     Load order management: dialog for adding/reordering supplementary ESM/ESP/DMP files
///     and the loading pipeline that resolves records in load order.
/// </summary>
public sealed partial class SingleFileTab
{
    private async void LoadOrderButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_session.IsAnalyzed) return;

        // Create working copy of current load order (preserves already-loaded data)
        var workingEntries = new ObservableCollection<LoadOrderEntry>(
            _session.LoadOrder.Entries.Select(existing => new LoadOrderEntry
            {
                FilePath = existing.FilePath,
                FileType = existing.FileType,
                Resolver = existing.Resolver,
                Records = existing.Records
            }));

        // Build dialog content
        var panel = new StackPanel { Spacing = 12 };

        // Info note
        panel.Children.Add(new TextBlock
        {
            Text = "Files later in the list override records from earlier files.",
            TextWrapping = TextWrapping.Wrap,
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });

        // Load order ListView
        var listView = new ListView
        {
            ItemsSource = workingEntries,
            CanReorderItems = true,
            AllowDrop = true,
            SelectionMode = ListViewSelectionMode.None,
            MinHeight = 80,
            MaxHeight = 300
        };

        // Item template: # | filename | [Remove] button
        listView.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Grid ColumnDefinitions="Auto,*,Auto" Margin="0,2">
                    <TextBlock Grid.Column="0" VerticalAlignment="Center"
                               Margin="0,0,12,0" Opacity="0.6"
                               Text="&#x2261;" FontSize="16" />
                    <TextBlock Grid.Column="1" VerticalAlignment="Center"
                               Text="{Binding DisplayName}" TextTrimming="CharacterEllipsis" />
                    <Button Grid.Column="2" Content="&#xE711;" FontFamily="Segoe MDL2 Assets"
                            FontSize="10" Padding="6,4" Margin="8,0,0,0"
                            Background="Transparent" Tag="{Binding}" />
                </Grid>
            </DataTemplate>
            """);

        // Wire up remove buttons via ContainerContentChanging
        listView.ContainerContentChanging += (_, args) =>
        {
            if (args.Phase != 0) return;
            var root = args.ItemContainer.ContentTemplateRoot as Grid;
            var removeBtn = root?.Children.OfType<Button>().FirstOrDefault();
            if (removeBtn != null)
            {
                removeBtn.Click -= RemoveEntryClick;
                removeBtn.Click += RemoveEntryClick;
            }
        };

        void RemoveEntryClick(object s, RoutedEventArgs _)
        {
            if (s is Button btn && btn.Tag is LoadOrderEntry entry)
                workingEntries.Remove(entry);
        }

        panel.Children.Add(listView);

        // Empty state text
        var emptyText = new TextBlock
        {
            Text = "No files added. Click \"Add Files\" to get started.",
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Visibility = workingEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed,
            Margin = new Thickness(0, -4, 0, 0)
        };
        workingEntries.CollectionChanged += (_, _) =>
            emptyText.Visibility = workingEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        panel.Children.Add(emptyText);

        // Add Files button
        var addButton = new Button
        {
            Content = "Add Files...",
            Margin = new Thickness(0, 4, 0, 0)
        };
        addButton.Click += async (_, _) =>
        {
            var paths = await PickMultipleFilesAsync([".esm", ".esp", ".dmp"]);
            if (paths == null) return;

            foreach (var path in paths)
            {
                // Skip the primary file (already loaded)
                if (string.Equals(_session.FilePath, path, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip duplicates within the working list
                if (workingEntries.Any(e => string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var fileType = FileTypeDetector.Detect(path);
                if (fileType == AnalysisFileType.Unknown) continue;

                workingEntries.Add(new LoadOrderEntry
                {
                    FilePath = path,
                    FileType = fileType
                });
            }
        };
        panel.Children.Add(addButton);

        // Separator
        panel.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 4, 0, 4),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"]
        });

        // Subtitles CSV row
        panel.Children.Add(new TextBlock
        {
            Text = "Subtitles CSV (optional — provides dialogue text, speaker, quest names):",
            TextWrapping = TextWrapping.Wrap
        });
        var csvRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        var csvPathBox = new TextBox
        {
            PlaceholderText = "Path to transcriber CSV export",
            Text = _session.LoadOrder.SubtitleCsvPath ?? "",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetColumn(csvPathBox, 0);
        csvRow.Children.Add(csvPathBox);

        var csvBrowse = new Button { Content = "Browse...", Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(csvBrowse, 1);
        csvBrowse.Click += async (_, _) =>
        {
            var path = await PickFileAsync([".csv"]);
            if (path != null) csvPathBox.Text = path;
        };
        csvRow.Children.Add(csvBrowse);
        panel.Children.Add(csvRow);

        // Show dialog
        var dialog = new ContentDialog
        {
            Title = "Load Order",
            Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            },
            PrimaryButtonText = "Load",
            SecondaryButtonText = _session.LoadOrder.HasData ? "Clear All" : null,
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Secondary)
        {
            // Clear all load order data
            _session.LoadOrder.Dispose();
            await OnLoadOrderChanged();
            return;
        }

        if (result != ContentDialogResult.Primary) return;

        var csvPath = csvPathBox.Text?.Trim();
        var hasEntries = workingEntries.Count > 0;
        var hasCsv = !string.IsNullOrEmpty(csvPath) && File.Exists(csvPath);

        if (!hasEntries && !hasCsv) return;

        try
        {
            SetPipelinePhase(AnalysisPipelinePhase.Parsing);
            StatusTextBlock.Text = "Loading load order data...";
            AnalysisProgressBar.IsIndeterminate = true;

            await LoadEntriesAsync(workingEntries, csvPath);
            await OnLoadOrderChanged();
            StatusTextBlock.Text = "Load order data loaded.";
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("Load Failed",
                $"Failed to load load order data:\n{ex.GetType().Name}: {ex.Message}", true);
        }
        finally
        {
            SetPipelinePhase(AnalysisPipelinePhase.Idle);
            AnalysisProgressBar.IsIndeterminate = false;
        }
    }

    private async Task LoadEntriesAsync(ObservableCollection<LoadOrderEntry> entries, string? csvPath)
    {
        // Load new entries that haven't been loaded yet
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.IsLoaded) continue;

            StatusTextBlock.Text = $"Loading {entry.DisplayName} ({i + 1}/{entries.Count})...";
            await LoadSingleEntryAsync(entry);
        }

        // Replace session load order entries with working copy
        _session.LoadOrder.Dispose();
        foreach (var entry in entries)
            _session.LoadOrder.Entries.Add(entry);

        // Handle CSV subtitles
        var hasCsv = !string.IsNullOrEmpty(csvPath) && File.Exists(csvPath);
        if (hasCsv)
        {
            StatusTextBlock.Text = "Loading subtitles CSV...";
            _session.LoadOrder.Subtitles = await Task.Run(() => SubtitleIndex.LoadFromCsv(csvPath!));
            _session.LoadOrder.SubtitleCsvPath = csvPath;
        }
    }

    private async Task LoadSingleEntryAsync(LoadOrderEntry entry)
    {
        var path = entry.FilePath;
        var fileType = entry.FileType;

        if (fileType != AnalysisFileType.EsmFile && fileType != AnalysisFileType.Minidump)
        {
            await ShowDialogAsync("Load Failed", $"Only ESM, ESP, and DMP files are supported: {entry.DisplayName}", true);
            return;
        }

        // Run analysis
        var progress = new Progress<AnalysisProgress>(p =>
            DispatcherQueue.TryEnqueue(() => StatusTextBlock.Text = $"{entry.DisplayName}: {p.Phase}..."));

        AnalysisResult analysisResult;
        if (fileType == AnalysisFileType.EsmFile)
        {
            analysisResult = await EsmFileAnalyzer.AnalyzeAsync(path, progress);
        }
        else
        {
            analysisResult = await new Core.Minidump.MinidumpAnalyzer().AnalyzeAsync(path, progress);
        }

        if (analysisResult.EsmRecords == null)
        {
            await ShowDialogAsync("Load Failed", $"No ESM records found in: {entry.DisplayName}", true);
            return;
        }

        // Parse records
        StatusTextBlock.Text = $"Parsing {entry.DisplayName}...";

        var reconProgress = new Progress<(int percent, string phase)>(p =>
            DispatcherQueue.TryEnqueue(() => StatusTextBlock.Text = $"{entry.DisplayName}: {p.phase}"));

        var fileSize = new FileInfo(path).Length;
        using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.Read);

        var records = await Task.Run(() =>
        {
            var parser = new RecordParser(
                analysisResult.EsmRecords,
                analysisResult.FormIdMap,
                accessor,
                fileSize,
                analysisResult.MinidumpInfo);
            return parser.ParseAll(reconProgress);
        });

        entry.Resolver = records.CreateResolver();
        entry.Records = records;
    }

    private async Task OnLoadOrderChanged()
    {
        UpdateLoadOrderStatusText();

        // Reset data browser so it rebuilds with new resolver
        DataBrowserContent.Visibility = Visibility.Collapsed;
        DataBrowserPlaceholder.Visibility = Visibility.Visible;
        _esmBrowserTree = null;

        // Reset world map
        _session.WorldMapPopulated = false;
        _session.WorldViewData = null;

        // Reset dialogue viewer so it rebuilds with new resolver/subtitles
        _session.DialogueViewerPopulated = false;
        _session.DialogueTree = null;
        _session.TopicsBySpeaker = null;
        _session.DialogueFormIdIndex = null;

        // Reset reports so they regenerate with new resolver
        _reportEntries.Clear();

        // Re-trigger the currently selected tab
        var selected = SubTabView.SelectedItem;
        if (selected != null)
        {
            SubTabView_SelectionChanged(this,
                new SelectionChangedEventArgs([], [selected]));
        }

        await Task.CompletedTask;
    }

    private void UpdateLoadOrderStatusText()
    {
        var lo = _session.LoadOrder;
        if (!lo.HasData)
        {
            LoadOrderStatusText.Text = "";
            return;
        }

        var parts = new List<string>();
        if (lo.Entries.Count > 0)
            parts.Add($"{lo.Entries.Count} file{(lo.Entries.Count == 1 ? "" : "s")}");
        if (lo.SubtitleCsvPath != null)
            parts.Add("+ subtitles");

        LoadOrderStatusText.Text = string.Join(" ", parts);
    }

    private static async Task<string?> PickFileAsync(string[] extensions)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var ext in extensions)
            picker.FileTypeFilter.Add(ext);
        InitializeWithWindow.Initialize(picker,
            WindowNative.GetWindowHandle(App.Current.MainWindow));

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private static async Task<IReadOnlyList<string>?> PickMultipleFilesAsync(string[] extensions)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var ext in extensions)
            picker.FileTypeFilter.Add(ext);
        InitializeWithWindow.Initialize(picker,
            WindowNative.GetWindowHandle(App.Current.MainWindow));

        var files = await picker.PickMultipleFilesAsync();
        if (files == null || files.Count == 0) return null;
        return files.Select(f => f.Path).ToList();
    }
}
