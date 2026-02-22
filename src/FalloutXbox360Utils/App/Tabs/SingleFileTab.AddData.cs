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
///     Supplementary data loading: "Add Data..." dialog and enrichment pipeline.
/// </summary>
public sealed partial class SingleFileTab
{
    private async void AddDataButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_session.IsAnalyzed) return;

        // Build dialog content dynamically based on file type
        var isSave = _session.IsSaveFile;
        var panel = new StackPanel { Spacing = 12, MinWidth = 480 };

        // ESM/DMP file row (save files only)
        TextBox? esmPathBox = null;
        if (isSave)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "ESM / DMP file (provides record names, world map terrain):",
                TextWrapping = TextWrapping.Wrap
            });
            var esmRow = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                }
            };
            esmPathBox = new TextBox
            {
                PlaceholderText = "Path to .esm, .esp, or .dmp file",
                Text = _session.Supplementary?.EsmFilePath ?? ""
            };
            Grid.SetColumn(esmPathBox, 0);
            esmRow.Children.Add(esmPathBox);

            var esmBrowse = new Button { Content = "Browse...", Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(esmBrowse, 1);
            var capturedEsmBox = esmPathBox;
            esmBrowse.Click += async (_, _) =>
            {
                var path = await PickFileAsync([".esm", ".esp", ".dmp"]);
                if (path != null) capturedEsmBox.Text = path;
            };
            esmRow.Children.Add(esmBrowse);
            panel.Children.Add(esmRow);
        }

        // Subtitles CSV row (all file types)
        panel.Children.Add(new TextBlock
        {
            Text = "Subtitles CSV (provides dialogue text, speaker, quest names):",
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
            Text = _session.Supplementary?.SubtitleCsvPath ?? ""
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

        var dialog = new ContentDialog
        {
            Title = "Add Data",
            Content = panel,
            PrimaryButtonText = "Load",
            SecondaryButtonText = _session.Supplementary?.HasData == true ? "Clear" : null,
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Secondary)
        {
            // Clear supplementary data
            _session.Supplementary?.Dispose();
            _session.Supplementary = null;
            await OnSupplementaryDataChanged();
            return;
        }

        if (result != ContentDialogResult.Primary) return;

        var esmPath = esmPathBox?.Text?.Trim();
        var csvPath = csvPathBox.Text?.Trim();
        var hasEsm = !string.IsNullOrEmpty(esmPath) && File.Exists(esmPath);
        var hasCsv = !string.IsNullOrEmpty(csvPath) && File.Exists(csvPath);

        if (!hasEsm && !hasCsv) return;

        try
        {
            _session.Supplementary ??= new SupplementaryData();

            SetPipelinePhase(AnalysisPipelinePhase.Reconstructing);
            StatusTextBlock.Text = "Loading supplementary data...";
            AnalysisProgressBar.IsIndeterminate = true;

            if (hasEsm)
            {
                await LoadSupplementaryEsmAsync(esmPath!);
            }

            if (hasCsv)
            {
                await LoadSupplementaryCsvAsync(csvPath!);
            }

            await OnSupplementaryDataChanged();
            StatusTextBlock.Text = "Supplementary data loaded.";
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("Load Failed",
                $"Failed to load supplementary data:\n{ex.GetType().Name}: {ex.Message}", true);
        }
        finally
        {
            SetPipelinePhase(AnalysisPipelinePhase.Idle);
            AnalysisProgressBar.IsIndeterminate = false;
        }
    }

    private async Task LoadSupplementaryEsmAsync(string path)
    {
        StatusTextBlock.Text = "Analyzing supplementary ESM/DMP...";

        var fileType = FileTypeDetector.Detect(path);
        if (fileType == AnalysisFileType.Unknown)
        {
            await ShowDialogAsync("Load Failed", $"Unknown file type: {path}", true);
            return;
        }

        // Run ESM/DMP analysis
        var progress = new Progress<AnalysisProgress>(p =>
            DispatcherQueue.TryEnqueue(() => StatusTextBlock.Text = $"Supplementary: {p.Phase}..."));

        AnalysisResult analysisResult;
        if (fileType == AnalysisFileType.EsmFile)
        {
            analysisResult = await EsmFileAnalyzer.AnalyzeAsync(path, progress);
        }
        else if (fileType == AnalysisFileType.Minidump)
        {
            analysisResult = await new Core.Minidump.MinidumpAnalyzer().AnalyzeAsync(path, progress);
        }
        else
        {
            await ShowDialogAsync("Load Failed", "Only ESM, ESP, and DMP files are supported.", true);
            return;
        }

        if (analysisResult.EsmRecords == null)
        {
            await ShowDialogAsync("Load Failed", "No ESM records found in supplementary file.", true);
            return;
        }

        // Reconstruct records
        StatusTextBlock.Text = "Reconstructing supplementary records...";

        var reconProgress = new Progress<(int percent, string phase)>(p =>
            DispatcherQueue.TryEnqueue(() => StatusTextBlock.Text = $"Supplementary: {p.phase}"));

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
            return parser.ReconstructAll(reconProgress);
        });

        var resolver = records.CreateResolver();

        _session.Supplementary!.EsmFilePath = path;
        _session.Supplementary.EsmRecords = records;
        _session.Supplementary.EsmResolver = resolver;
    }

    private async Task LoadSupplementaryCsvAsync(string path)
    {
        StatusTextBlock.Text = "Loading subtitles CSV...";

        var subtitles = await Task.Run(() => SubtitleIndex.LoadFromCsv(path));

        _session.Supplementary!.SubtitleCsvPath = path;
        _session.Supplementary.Subtitles = subtitles;
    }

    private async Task OnSupplementaryDataChanged()
    {
        UpdateAddDataStatusText();

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
            // Simulate tab selection to auto-populate
            SubTabView_SelectionChanged(this,
                new SelectionChangedEventArgs([], [selected]));
        }

        await Task.CompletedTask;
    }

    private void UpdateAddDataStatusText()
    {
        var supp = _session.Supplementary;
        if (supp == null || !supp.HasData)
        {
            AddDataStatusText.Text = "";
            return;
        }

        var parts = new List<string>();
        if (supp.EsmFilePath != null)
            parts.Add($"ESM: {Path.GetFileName(supp.EsmFilePath)}");
        if (supp.SubtitleCsvPath != null)
            parts.Add($"Subtitles: {Path.GetFileName(supp.SubtitleCsvPath)}");

        AddDataStatusText.Text = string.Join(" | ", parts);
    }

    private async Task<string?> PickFileAsync(string[] extensions)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var ext in extensions)
            picker.FileTypeFilter.Add(ext);
        InitializeWithWindow.Initialize(picker,
            WindowNative.GetWindowHandle(App.Current.MainWindow));

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }
}
