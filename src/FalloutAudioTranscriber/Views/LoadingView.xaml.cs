using FalloutAudioTranscriber.Models;
using FalloutAudioTranscriber.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace FalloutAudioTranscriber.Views;

#pragma warning disable CA1001 // WinUI 3 UserControls don't implement IDisposable
public sealed partial class LoadingView : UserControl
{
    private string? _selectedPath;
    private CancellationTokenSource? _cts;

    public LoadingView()
    {
        InitializeComponent();
    }

    /// <summary>Fires when a build is successfully loaded.</summary>
    public event EventHandler? BuildLoaded;

    /// <summary>The loaded build result (available after BuildLoaded fires).</summary>
    public BuildLoadResult? LoadResult { get; private set; }

    /// <summary>The selected data directory path.</summary>
    public string? DataDirectory => _selectedPath;

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add("*");

        // Initialize the picker with the window handle (required for WinUI 3 unpackaged)
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance!);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            _selectedPath = folder.Path;
            FolderPathBox.Text = folder.Path;
            LoadButton.IsEnabled = true;
            ResultsPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPath == null)
        {
            return;
        }

        // Disable controls during loading
        BrowseButton.IsEnabled = false;
        LoadButton.IsEnabled = false;
        LoadProgressBar.Opacity = 1;
        LoadProgressBar.Value = 0;
        ResultsPanel.Visibility = Visibility.Collapsed;

        _cts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<(string message, double percent)>(p =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    MainWindow.Instance?.SetStatus(p.message);
                    LoadProgressBar.Value = p.percent;
                });
            });

            var result = await BuildDirectoryLoader.LoadAsync(_selectedPath, progress, _cts.Token);

            LoadResult = result;
            var entries = result.Entries;

            // Show results
            var transcribed = entries.Count(e => e.HasSubtitle);
            var untranscribed = entries.Count - transcribed;
            var voiceTypes = entries.Select(e => e.VoiceType).Distinct().Count();

            ResultsHeader.Text = $"Loaded {entries.Count:N0} voice files";

            var detailLines = $"{transcribed:N0} with subtitles, {untranscribed:N0} untranscribed\n"
                              + $"{voiceTypes} voice types across {entries.Select(e => e.BsaFilePath).Distinct().Count()} BSAs";

            // ESM enrichment heuristics
            if (result.EsmInfoCount > 0)
            {
                static string Pct(int n, int total) => total > 0 ? $"{100.0 * n / total:F0}%" : "N/A";

                detailLines += $"\nESM: {result.EsmInfoCount:N0} INFOs, {result.EsmNpcCount:N0} NPCs, "
                               + $"{result.EsmQuestCount:N0} quests, {result.EsmTopicCount:N0} topics";
                detailLines += $"\nMatch: {result.EnrichedSubtitleCount:N0} subtitles ({Pct(result.EnrichedSubtitleCount, entries.Count)}), "
                               + $"{result.EnrichedSpeakerCount:N0} speakers ({Pct(result.EnrichedSpeakerCount, entries.Count)}), "
                               + $"{result.EnrichedQuestCount:N0} quests ({Pct(result.EnrichedQuestCount, entries.Count)})";
            }
            else
            {
                detailLines += "\nNo ESM file found — speaker/quest data unavailable";
            }

            ResultsDetails.Text = detailLines;
            ResultsPanel.Visibility = Visibility.Visible;

            MainWindow.Instance?.SetStatus($"Loaded {entries.Count:N0} voice files from {_selectedPath}");

            BuildLoaded?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            MainWindow.Instance?.SetStatus("Cancelled.");
        }
        catch (Exception ex)
        {
            ResultsHeader.Text = "Error loading build";
            ResultsDetails.Text = ex.Message;
            ResultsPanel.Visibility = Visibility.Visible;
        }
        finally
        {
            BrowseButton.IsEnabled = true;
            LoadButton.IsEnabled = true;
            LoadProgressBar.Opacity = 0;
            _cts?.Dispose();
            _cts = null;
        }
    }
}
