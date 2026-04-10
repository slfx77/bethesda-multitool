using System.Collections.ObjectModel;
using Windows.Storage.Pickers;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Semantic;
using FalloutXbox360Utils.Core.Formats.Subtitles;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace FalloutXbox360Utils;

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
                Records = existing.Records
            }));
    }

    internal static async Task<LoadOrderDialogResult> ShowAsync(
        XamlRoot xamlRoot,
        ObservableCollection<LoadOrderEntry> workingEntries,
        LoadOrderDialogOptions options)
    {
        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(new TextBlock
        {
            Text = options.IntroText,
            TextWrapping = TextWrapping.Wrap,
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });

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
            Text = "No files added. Click \"Add Files\" to get started.",
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Visibility = workingEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed,
            Margin = new Thickness(0, -4, 0, 0)
        };
        workingEntries.CollectionChanged += (_, _) =>
            emptyText.Visibility = workingEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        panel.Children.Add(emptyText);

        var addButton = new Button
        {
            Content = "Add Files...",
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
                if (!string.IsNullOrEmpty(options.PrimaryFilePath) &&
                    string.Equals(options.PrimaryFilePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (workingEntries.Any(entry =>
                        string.Equals(entry.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var fileType = FileTypeDetector.Detect(path);
                if (fileType == AnalysisFileType.Unknown)
                {
                    continue;
                }

                workingEntries.Add(new LoadOrderEntry
                {
                    FilePath = path,
                    FileType = fileType
                });
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

            panel.Children.Add(new TextBlock
            {
                Text = options.SubtitleLabel
                       ?? "Subtitles CSV (optional — provides dialogue text, speaker, quest names):",
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

            csvPathBox = new TextBox
            {
                PlaceholderText = options.SubtitlePlaceholder ?? "Path to transcriber CSV export",
                Text = options.SubtitleCsvPath ?? "",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            Grid.SetColumn(csvPathBox, 0);
            csvRow.Children.Add(csvPathBox);

            var csvBrowse = new Button { Content = "Browse...", Margin = new Thickness(8, 0, 0, 0) };
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
            PrimaryButtonText = "Load",
            SecondaryButtonText = hasExistingData ? "Clear All" : null,
            CloseButtonText = "Cancel",
            XamlRoot = xamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => new LoadOrderDialogResult(
                LoadOrderDialogAction.Apply,
                workingEntries,
                csvPathBox?.Text?.Trim()),
            ContentDialogResult.Secondary => new LoadOrderDialogResult(
                LoadOrderDialogAction.ClearAll,
                workingEntries,
                null),
            _ => new LoadOrderDialogResult(LoadOrderDialogAction.Cancel, workingEntries, null)
        };
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
            }
        }

        var hasCsv = !string.IsNullOrWhiteSpace(csvPath) && File.Exists(csvPath);
        SubtitleIndex? subtitles = null;
        if (hasCsv)
        {
            updateStatus?.Invoke("Loading subtitles CSV...");
            subtitles = await Task.Run(() => SubtitleIndex.LoadFromCsv(csvPath!), cancellationToken);
        }

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

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));

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

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));

        var files = await picker.PickMultipleFilesAsync();
        if (files == null || files.Count == 0)
        {
            return null;
        }

        return files.Select(file => file.Path).ToList();
    }
}
