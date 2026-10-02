using System.Collections.ObjectModel;
using Windows.Storage.Pickers;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.FileFormat;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Semantic;
using BethesdaMultitool.Core.Formats.Subtitles;
using BethesdaMultitool.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace BethesdaMultitool;

/// <summary>Builds and shows the load-order picker dialog and applies its result, loading each entry's records.</summary>
internal static class LoadOrderDialogService
{
    internal static ObservableCollection<LoadOrderEntry> CreateWorkingEntries(IEnumerable<LoadOrderEntry> entries)
    {
        return new ObservableCollection<LoadOrderEntry>(
            entries.Select(existing => new LoadOrderEntry
            {
                FilePath = existing.FilePath,
                FileType = existing.FileType,
                Resolver = existing.Resolver,
                Records = existing.Records,
                SelectionEvidence = existing.SelectionEvidence
            }));
    }

    internal static async Task<LoadOrderDialogResult> ShowAsync(
        XamlRoot xamlRoot,
        ObservableCollection<LoadOrderEntry> workingEntries,
        LoadOrderDialogOptions options)
    {
        var panel = new StackPanel { Spacing = 12 };
        var primaryFilePath = string.IsNullOrWhiteSpace(options.PrimaryFilePath)
            ? null
            : options.PrimaryFilePath;
        var recordCandidates = options.RecordCandidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        ComboBox? primaryPicker = null;
        ComboBox? discoveredPicker = null;
        Button? addDiscoveredButton = null;

        panel.Children.Add(new TextBlock
        {
            Text = options.IntroText,
            TextWrapping = TextWrapping.Wrap,
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });

        if (options.RecordCandidates.Count > 0)
        {
            var primaryCandidates = recordCandidates.ToList();
            if (primaryFilePath is not null)
            {
                // Preserve the exact current path, including its spelling, as the default.
                primaryCandidates.RemoveAll(path => SamePath(path, primaryFilePath));
                primaryCandidates.Insert(0, primaryFilePath);
            }
            primaryPicker = new ComboBox
            {
                Header = Strings.Get("LoadOrder_PrimaryPlugin"),
                PlaceholderText = Strings.Get("LoadOrder_ChoosePrimaryPlugin"),
                ItemsSource = primaryCandidates,
                SelectedItem = primaryFilePath,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(primaryPicker, Strings.Get("LoadOrder_PrimaryPlugin"));
            panel.Children.Add(primaryPicker);

            var discoveredRow = new Grid
            {
                ColumnSpacing = 8,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                }
            };
            discoveredPicker = new ComboBox
            {
                Header = Strings.Get("LoadOrder_DiscoveredPlugins"),
                PlaceholderText = Strings.Get("LoadOrder_ChooseSupplementaryPlugin"),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(discoveredPicker, Strings.Get("LoadOrder_DiscoveredPlugins"));
            addDiscoveredButton = new Button
            {
                Content = Strings.Get("LoadOrder_AddSelected"),
                VerticalAlignment = VerticalAlignment.Bottom,
                IsEnabled = false
            };
            AutomationProperties.SetName(addDiscoveredButton, Strings.Get("LoadOrder_AddDiscoveredPlugin"));
            discoveredPicker.SelectionChanged += (_, _) =>
                addDiscoveredButton.IsEnabled = discoveredPicker.SelectedItem is string;
            addDiscoveredButton.Click += (_, _) =>
            {
                if (discoveredPicker.SelectedItem is string path)
                {
                    AddEntry(workingEntries, path, primaryFilePath);
                }
            };
            discoveredRow.Children.Add(discoveredPicker);
            Grid.SetColumn(addDiscoveredButton, 1);
            discoveredRow.Children.Add(addDiscoveredButton);
            panel.Children.Add(discoveredRow);
            RemovePrimaryEntry();
            RefreshDiscoveredChoices();
        }

        void RemovePrimaryEntry()
        {
            for (var index = workingEntries.Count - 1; index >= 0; index--)
            {
                if (SamePath(workingEntries[index].FilePath, primaryFilePath))
                {
                    workingEntries.RemoveAt(index);
                }
            }
        }

        void RefreshDiscoveredChoices()
        {
            if (discoveredPicker is null || addDiscoveredButton is null)
            {
                return;
            }
            var selected = discoveredPicker.SelectedItem as string;
            var available = recordCandidates
                .Where(path => !SamePath(path, primaryFilePath) &&
                               !workingEntries.Any(entry => SamePath(entry.FilePath, path)))
                .ToList();
            discoveredPicker.ItemsSource = available;
            discoveredPicker.SelectedItem = available.FirstOrDefault(path => SamePath(path, selected));
            addDiscoveredButton.IsEnabled = discoveredPicker.SelectedItem is string;
        }

        var listView = new ListView
        {
            ItemsSource = workingEntries,
            CanReorderItems = true,
            AllowDrop = true,
            SelectionMode = ListViewSelectionMode.None,
            MinHeight = 80,
            MaxHeight = 300
        };

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

        listView.ContainerContentChanging += (_, args) =>
        {
            if (args.Phase != 0)
            {
                return;
            }

            var root = args.ItemContainer.ContentTemplateRoot as Grid;
            var removeBtn = root?.Children.OfType<Button>().FirstOrDefault();
            if (removeBtn == null)
            {
                return;
            }

            removeBtn.Click -= RemoveEntryClick;
            removeBtn.Click += RemoveEntryClick;
        };

        void RemoveEntryClick(object sender, RoutedEventArgs _)
        {
            if (sender is Button btn && btn.Tag is LoadOrderEntry entry)
            {
                workingEntries.Remove(entry);
            }
        }

        panel.Children.Add(listView);

        var emptyText = new TextBlock
        {
            Text = Strings.Get("LoadOrder_NoSupplementaryFiles"),
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Visibility = workingEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed,
            Margin = new Thickness(0, -4, 0, 0)
        };
        workingEntries.CollectionChanged += (_, _) =>
        {
            emptyText.Visibility = workingEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshDiscoveredChoices();
        };
        panel.Children.Add(emptyText);

        var addButton = new Button
        {
            Content = Strings.Get("LoadOrder_AddFiles"),
            Margin = new Thickness(0, 4, 0, 0)
        };
        addButton.Click += async (_, _) =>
        {
            var paths = await PickMultipleFilesAsync(options.AllowedExtensions);
            if (paths == null)
            {
                return;
            }

            foreach (var path in paths)
            {
                AddEntry(workingEntries, path, primaryFilePath);
            }
        };
        panel.Children.Add(addButton);

        TextBox? csvPathBox = null;
        if (options.AllowSubtitleCsv)
        {
            panel.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(0, 4, 0, 4),
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                    "DividerStrokeColorDefaultBrush"]
            });

            var csvLabel = new TextBlock
            {
                Text = options.SubtitleLabel
                       ?? Strings.Get("LoadOrder_SubtitleCsv"),
                TextWrapping = TextWrapping.Wrap
            };
            panel.Children.Add(csvLabel);

            var csvRow = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                }
            };

            csvPathBox = new TextBox
            {
                PlaceholderText = options.SubtitlePlaceholder ?? Strings.Get("LoadOrder_SubtitlePlaceholder"),
                Text = options.SubtitleCsvPath ?? "",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            Grid.SetColumn(csvPathBox, 0);
            csvRow.Children.Add(csvPathBox);

            AutomationProperties.SetLabeledBy(csvPathBox, csvLabel);
            var csvBrowse = new Button
            {
                Content = Strings.Get("Button_Browse.Content"),
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(csvBrowse, 1);
            csvBrowse.Click += async (_, _) =>
            {
                var path = await PickFileAsync([".csv"]);
                if (path != null)
                {
                    csvPathBox.Text = path;
                }
            };
            csvRow.Children.Add(csvBrowse);
            panel.Children.Add(csvRow);
        }

        var hasExistingData = workingEntries.Count > 0 || !string.IsNullOrEmpty(options.SubtitleCsvPath);
        var dialog = new ContentDialog
        {
            Title = options.Title,
            Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            },
            // "Apply", not "Load": the main file button is now labeled "Load" and this dialog opens
            // from a "Load Order..." button — a third "Load" affordance would be ambiguous.
            PrimaryButtonText = Strings.Get("LoadOrder_Apply"),
            IsPrimaryButtonEnabled = primaryPicker is null || primaryPicker.SelectedItem is string,
            SecondaryButtonText = hasExistingData ? Strings.Get("LoadOrder_ClearAll") : null,
            CloseButtonText = Strings.Get("Button_Cancel.Content"),
            XamlRoot = xamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };

        if (primaryPicker is not null)
        {
            primaryPicker.SelectionChanged += (_, _) =>
            {
                primaryFilePath = primaryPicker.SelectedItem as string;
                RemovePrimaryEntry();
                RefreshDiscoveredChoices();
                dialog.IsPrimaryButtonEnabled = primaryFilePath is not null;
            };
        }

        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => new LoadOrderDialogResult(
                LoadOrderDialogAction.Apply,
                workingEntries,
                csvPathBox?.Text?.Trim(),
                primaryFilePath),
            ContentDialogResult.Secondary => new LoadOrderDialogResult(
                LoadOrderDialogAction.ClearAll,
                workingEntries,
                null,
                primaryFilePath),
            _ => new LoadOrderDialogResult(LoadOrderDialogAction.Cancel, workingEntries, null, primaryFilePath)
        };
    }

    /// <summary>Compares the existing Windows load-order path identities without filename-only matching.</summary>
    private static bool SamePath(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>Adds one explicitly selected supplementary file, retaining the existing format admission.</summary>
    private static void AddEntry(ObservableCollection<LoadOrderEntry> entries, string path, string? primaryFilePath)
    {
        if (SamePath(path, primaryFilePath) || entries.Any(entry => SamePath(entry.FilePath, path)))
        {
            return;
        }
        var fileType = FileTypeDetector.Detect(path);
        if (fileType != AnalysisFileType.Unknown)
        {
            entries.Add(new LoadOrderEntry { FilePath = path, FileType = fileType });
        }
    }

    internal static async Task ApplyAsync(
        LoadOrder target,
        IEnumerable<LoadOrderEntry> entries,
        string? csvPath,
        Action<string>? updateStatus = null,
        CancellationToken cancellationToken = default)
    {
        var entryList = entries.ToList();
        var unloadedEntries = entryList
            .Where(entry => !entry.IsLoaded)
            .ToList();

        if (unloadedEntries.Count > 0)
        {
            var loadedSources = await SemanticSourceSetBuilder.LoadSourcesAsync(
                unloadedEntries.Select(entry => new SemanticSourceRequest
                {
                    FilePath = entry.FilePath,
                    FileType = entry.FileType
                }),
                (index, total, request) => new Progress<AnalysisProgress>(progress =>
                    updateStatus?.Invoke(
                        $"Loading {Path.GetFileName(request.FilePath)} ({index + 1}/{total}): {progress.Phase}...")),
                (index, total, request) => new Progress<(int percent, string phase)>(progress =>
                    updateStatus?.Invoke(
                        $"Parsing {Path.GetFileName(request.FilePath)} ({index + 1}/{total}): {progress.phase}")),
                cancellationToken);

            for (var i = 0; i < unloadedEntries.Count; i++)
            {
                var source = loadedSources.Sources[i];
                unloadedEntries[i].Resolver = source.Resolver;
                unloadedEntries[i].Records = source.Records;
                if (source.Records.Game is Core.Games.BethesdaGame.Fallout3 or Core.Games.BethesdaGame.FalloutNewVegas &&
                    source.RawResult?.EsmRecords is { } scan)
                {
                    unloadedEntries[i].SelectionEvidence = new Core.Formats.Esm.Records.EsmRecordScanResult
                    {
                        Game = scan.Game, MainRecords = scan.MainRecords, EditorIds = scan.EditorIds,
                        PlacementGroups = scan.PlacementGroups, LandRecords = scan.LandRecords
                    };
                }
            }
        }

        var hasCsv = !string.IsNullOrWhiteSpace(csvPath) && File.Exists(csvPath);
        SubtitleIndex? subtitles = null;
        if (hasCsv)
        {
            updateStatus?.Invoke("Loading subtitles CSV...");
            subtitles = await Task.Run(() => SubtitleIndex.LoadFromCsv(csvPath!), cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        target.Dispose();
        foreach (var entry in entryList)
        {
            target.Entries.Add(entry);
        }

        target.Subtitles = subtitles;
        target.SubtitleCsvPath = hasCsv ? csvPath : null;
    }

    private static async Task<string?> PickFileAsync(string[] extensions)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var ext in extensions)
        {
            picker.FileTypeFilter.Add(ext);
        }

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private static async Task<IReadOnlyList<string>?> PickMultipleFilesAsync(string[] extensions)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var ext in extensions)
        {
            picker.FileTypeFilter.Add(ext);
        }

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));

        var files = await picker.PickMultipleFilesAsync();
        if (files == null || files.Count == 0)
        {
            return null;
        }

        return files.Select(file => file.Path).ToList();
    }
}
